namespace PoorMansFeed;

/// <summary>
/// Work may start only once the app has started, while no update or Refresh all runs and nothing holds the gate. UI
/// thread only.
/// </summary>
public interface IWorkGate
{
    /// <summary>Started, no update or refresh running and not held.</summary>
    bool CanStart { get; }

    /// <summary>
    /// The app has started: the service is open and the settings and exceptions it read are the saved ones. Stays true
    /// through an update and every hold; until then nothing shown can be edited, because start-up replaces it.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Something holds the gate: a dialog, confirmation, prompt or save is out. An update alone holds nothing. Raises
    /// nothing when it flips: <see cref="Changed"/> follows only <see cref="CanStart"/>, IsStarted and IsRefreshing.
    /// </summary>
    bool IsHeld { get; }

    /// <summary>Refresh all is running: a page refuses its own writes and source checks meanwhile.</summary>
    bool IsRefreshing { get; }

    /// <summary>
    /// A page's own call (a check, write, download, retire, remove, clear) is out: Refresh all doesn't start meanwhile.
    /// </summary>
    bool IsPageWorking { get; }

    /// <summary>Holds the gate until the returned value is disposed. Holds nest; a second Dispose does nothing.</summary>
    IDisposable Hold();

    /// <summary>
    /// Marks a page's own call until disposed. Calls nest; a second Dispose does nothing. Raises nothing.
    /// </summary>
    IDisposable EnterPageWork();

    /// <summary>
    /// Raised only when <see cref="CanStart"/>, <see cref="IsStarted"/> or <see cref="IsRefreshing"/> flips, so a page
    /// hears a refresh end even while a hold is out; never for IsHeld or IsPageWorking alone.
    /// </summary>
    event EventHandler? Changed;
}

/// <summary>
/// The gate itself. The main view model reports start-up and the update into it, and the Refresh all runner its run.
/// The pages' runs don't wait on CanStart, but each marks its calls as page work; their dialogs hold it.
/// </summary>
public sealed class WorkGate : IWorkGate
{
    private bool _started;
    private bool _busy;
    private bool _refreshing;
    private int _holds;
    private int _pageWork;

    public bool CanStart => _started && !_busy && !_refreshing && _holds == 0;

    public bool IsStarted => _started;

    public bool IsHeld => _holds > 0;

    public bool IsRefreshing => _refreshing;

    public bool IsPageWorking => _pageWork > 0;

    public event EventHandler? Changed;

    public IDisposable Hold()
    {
        Change(() => _holds++);
        return new Release(() => Change(() => _holds--));
    }

    public IDisposable EnterPageWork()
    {
        _pageWork++;
        return new Release(() => _pageWork--);
    }

    internal void SetStarted(bool started) => Change(() => _started = started);

    internal void SetBusy(bool busy) => Change(() => _busy = busy);

    internal void SetRefreshing(bool refreshing) => Change(() => _refreshing = refreshing);

    private void Change(Action change)
    {
        bool before = CanStart;
        bool startedBefore = _started;
        bool refreshingBefore = _refreshing;
        change();
        if (CanStart != before || _started != startedBefore || _refreshing != refreshingBefore)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            release();
        }
    }
}
