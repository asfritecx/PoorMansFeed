namespace PoorMansFeed;

/// <summary>
/// The gate the IPInfo Lookup and Settings commands wait on, and that their dialogs, confirmations, prompts and saves
/// hold: work may start only once the app has started, while no update runs and while nothing holds it. The cloud
/// pages' runs are outside it on purpose: each keeps its own IsWorking, and their service serialises their work; their
/// large-range question holds it like every other dialog. That question, and the AbuseIPDB page's Clear list, Empty feed
/// and New addresses, test IsHeld at the moment of showing and show nothing while something holds it; the close's
/// questions don't, because the close must be asked. Used on the UI thread only.
/// </summary>
public interface IWorkGate
{
    /// <summary>Started, not busy and not held.</summary>
    bool CanStart { get; }

    /// <summary>
    /// The app has started: the service is open and the settings and exceptions it read are the saved ones. Stays true
    /// through an update and every hold; until then nothing shown can be edited, because start-up replaces it.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Something holds the gate: a dialog, confirmation, prompt or save is out. An update alone holds nothing. Raises
    /// nothing when it flips: <see cref="Changed"/> follows only <see cref="CanStart"/> and <see cref="IsStarted"/>.
    /// </summary>
    bool IsHeld { get; }

    /// <summary>Holds the gate until the returned value is disposed. Holds nest; a second Dispose does nothing.</summary>
    IDisposable Hold();

    /// <summary>Raised only when <see cref="CanStart"/> or <see cref="IsStarted"/> flips; never for <see cref="IsHeld"/>
    /// alone.</summary>
    event EventHandler? Changed;
}

/// <summary>The gate itself. Only the main view model reports start-up and the update into it.</summary>
public sealed class WorkGate : IWorkGate
{
    private bool _started;
    private bool _busy;
    private int _holds;

    public bool CanStart => _started && !_busy && _holds == 0;

    public bool IsStarted => _started;

    public bool IsHeld => _holds > 0;

    public event EventHandler? Changed;

    public IDisposable Hold()
    {
        Change(() => _holds++);
        return new Release(this);
    }

    internal void SetStarted(bool started) => Change(() => _started = started);

    internal void SetBusy(bool busy) => Change(() => _busy = busy);

    private void Change(Action change)
    {
        bool before = CanStart;
        bool startedBefore = _started;
        change();
        if (CanStart != before || _started != startedBefore)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Release(WorkGate gate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            gate.Change(() => gate._holds--);
        }
    }
}
