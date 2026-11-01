using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>Asks, holds and stops the main window's close over its participants, on the UI thread.</summary>
public sealed class CloseChain
{
    private readonly IReadOnlyList<ICloseParticipant> _beforeFeedWrites;
    private readonly IReadOnlyList<ICloseParticipant> _afterFeedWrites;
    private readonly IFeedRecords? _records;
    private readonly IUiServices? _ui;
    private readonly IWorkGate? _gate;

    public CloseChain(IReadOnlyList<ICloseParticipant> beforeFeedWrites,
                      IReadOnlyList<ICloseParticipant> afterFeedWrites,
                      IFeedRecords? records, IUiServices? ui, IWorkGate? gate)
    {
        ArgumentNullException.ThrowIfNull(beforeFeedWrites);
        ArgumentNullException.ThrowIfNull(afterFeedWrites);
        _beforeFeedWrites = beforeFeedWrites;
        _afterFeedWrites = afterFeedWrites;
        _records = records;
        _ui = ui;
        _gate = gate;
    }

    public bool HoldsClose =>
        _beforeFeedWrites.Any(p => p.HoldsClose) || _afterFeedWrites.Any(p => p.HoldsClose)
        || _records is { WritesInFlight: > 0 };

    /// <summary>Set before the call's first await, so a second close meanwhile holds and asks nothing.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>True: the window may close now. False: it stays open, or it closed under the chain.</summary>
    public async Task<bool> ConfirmAndStopAsync(Func<bool> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        IsRunning = true;
        var holds = new List<IDisposable>();
        var stopped = new HashSet<ICloseParticipant>(ReferenceEqualityComparer.Instance);
        bool closing = false;
        try
        {
            bool pageAsked = false;
            foreach (ICloseParticipant participant in _beforeFeedWrites)
            {
                pageAsked |= participant.AsksBeforeClose;
                if (!await AskAsync(participant, holds, stopped, closed))
                    return false;
            }

            // Once a page asked, the feed-write question is skipped; the close still waits below for every feed write
            // in flight, not only the page's own
            if (!pageAsked && _records is { WritesInFlight: > 0 } && (!await ConfirmFeedWriteCloseAsync() || closed()))
                return false;

            foreach (ICloseParticipant participant in _afterFeedWrites)
            {
                if (!await AskAsync(participant, holds, stopped, closed))
                    return false;
            }

            // One stopped at its own yes is recorded, so a flag that changed since can't stop it twice
            foreach (ICloseParticipant participant in
                     _beforeFeedWrites.Concat(_afterFeedWrites).Where(p => !stopped.Contains(p)))
            {
                await participant.StopForCloseAsync();
                if (closed())
                    return false;
            }

            // Admits no new write and waits for those in flight, so no feed file is left half written
            if (_records is { } records)
            {
                await records.StopWritesAsync(CancellationToken.None);
                if (closed())
                    return false;
            }

            closing = true;
            return true;
        }
        finally
        {
            if (!closing)
            {
                foreach (IDisposable hold in holds)
                    hold.Dispose();
            }

            IsRunning = false;
        }
    }

    private static async Task<bool> AskAsync(ICloseParticipant participant, List<IDisposable> holds,
                                             HashSet<ICloseParticipant> stopped, Func<bool> closed)
    {
        if (!await participant.AskBeforeCloseAsync() || closed())
            return false;

        if (participant.HoldRunsForClose() is { } hold)
            holds.Add(hold);
        if (participant.StopsWhenAgreed)
        {
            stopped.Add(participant);
            await participant.StopForCloseAsync();
            if (closed())
                return false;
        }
        return true;
    }

    // A dialog that fails to show keeps the window open
    private async Task<bool> ConfirmFeedWriteCloseAsync()
    {
        if (_ui is not { } ui)
            return true;

        using IDisposable? hold = _gate?.Hold();
        try
        {
            return await ui.ConfirmAsync(AppText.CloseFeedWriteTitle, AppText.CloseFeedWriteBody,
                AppText.CloseFeedWriteButton);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static ICloseParticipant ForSettings(SettingsPageViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return AskOnly(() => settings.HasUnsavedChanges, settings.ConfirmDiscardAsync);
    }

    /// <summary>Agreeing cancels the update and waits for it inside the question, so it stops first.</summary>
    internal static ICloseParticipant ForUpdate(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return AskOnly(() => viewModel.IsBusy, viewModel.ConfirmCloseAsync, stopsWhenAgreed: true);
    }

    internal static ICloseParticipant AskOnly(Func<bool> holdsClose, Func<Task<bool>> ask, bool stopsWhenAgreed = false) =>
        new AskOnlyParticipant(holdsClose, ask, stopsWhenAgreed);

    private sealed class AskOnlyParticipant(Func<bool> holdsClose, Func<Task<bool>> ask, bool stopsWhenAgreed)
        : ICloseParticipant
    {
        public bool StopsWhenAgreed => stopsWhenAgreed;
        public bool HoldsClose => holdsClose();
        public bool AsksBeforeClose => false;
        public Task<bool> AskBeforeCloseAsync() => ask();
        public IDisposable? HoldRunsForClose() => null;
        public Task StopForCloseAsync() => Task.CompletedTask;
    }
}
