using System.Diagnostics;
using PoorMansFeed.Core;

namespace PoorMansFeed;

public enum RefreshStartRefusal { None, Unavailable, NotStarted, Running, Closing, GateBusy, PageWorking }

/// <summary>What started a run: a press of Refresh all, or the timer while the application is open.</summary>
public enum RefreshTrigger { Pressed, Timer }

/// <summary>
/// Runs Refresh all one run at a time, keeps the whole window's gate busy while it runs, and keeps its progress and the
/// last run's results. UI thread only.
/// </summary>
public sealed class RefreshAllRunner : ICloseParticipant, IDisposable
{
    private static readonly IReadOnlyDictionary<string, FeedRefreshResult> NoResults =
        new Dictionary<string, FeedRefreshResult>(StringComparer.OrdinalIgnoreCase).AsReadOnly();

    private readonly IFeedRefresher? _refresher;
    private readonly WorkGate _gate;
    private readonly IUiServices? _ui;
    private CancellationTokenSource? _cts;
    private object? _run;
    private int _closeHolds;
    private bool _stopping;
    private bool _disposed;

    public RefreshAllRunner(IFeedRefresher? refresher, WorkGate gate, IUiServices? ui)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _refresher = refresher;
        _gate = gate;
        _ui = ui;
    }

    public bool IsRunning { get; private set; }

    /// <summary>A Cancel or the close's stop asked the run under way to stop; false when idle.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>IsRunning, Progress or the results changed; raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The latest report of the run under way, null otherwise.</summary>
    public RefreshProgress? Progress { get; private set; }

    /// <summary>The last finished run's feeds, keys ignoring case; empty before one.</summary>
    public IReadOnlyDictionary<string, FeedRefreshResult> LastResults { get; private set; } = NoResults;

    public RefreshRunResult? LastRun { get; private set; }

    /// <summary>What started the run under way; null when idle.</summary>
    public RefreshTrigger? RunningTrigger { get; private set; }

    /// <summary>What started the run <see cref="LastRun"/> holds; a run that threw leaves both as they were.</summary>
    public RefreshTrigger? LastRunTrigger { get; private set; }

    /// <summary>Runs ended, a throw or a cancel included; counted before the end's Changed and the gate's.</summary>
    public int RunsEnded { get; private set; }

    /// <summary>The run under way, or the last one; it ends only after the run's bookkeeping.</summary>
    internal Task? PendingRun { get; private set; }

    internal Func<Action<RefreshProgress>, IProgress<RefreshProgress>> ProgressFactory { get; set; } =
        handler => new Progress<RefreshProgress>(handler);

    /// <summary>None: the run has started.</summary>
    public RefreshStartRefusal TryStart(RefreshTrigger trigger = RefreshTrigger.Pressed)
    {
        if (_refresher is not { } refresher || _disposed)
            return RefreshStartRefusal.Unavailable;
        if (!_gate.IsStarted)
            return RefreshStartRefusal.NotStarted;
        if (IsRunning)
            return RefreshStartRefusal.Running;
        if (_closeHolds > 0)
            return RefreshStartRefusal.Closing;
        if (!_gate.CanStart)
            return RefreshStartRefusal.GateBusy;
        if (_gate.IsPageWorking)
            return RefreshStartRefusal.PageWorking;

        // Every field before any event, so a handler that stops or starts again sees this run whole
        var run = new object();
        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource();
        IProgress<RefreshProgress> progress = ProgressFactory(report => Report(run, report));
        _run = run;
        _cts = cts;
        IsRunning = true;
        RunningTrigger = trigger;
        CancelRequested = false;
        Progress = null;
        PendingRun = done.Task;
        _gate.SetRefreshing(true);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunAsync(refresher, run, trigger, progress, cts, done);
        return RefreshStartRefusal.None;
    }

    // A Progress<T> post can land after its run ended, so a report counts only while its own run is the current one
    private void Report(object run, RefreshProgress report)
    {
        if (!ReferenceEquals(_run, run))
            return;
        Progress = report;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(IFeedRefresher refresher, object run, RefreshTrigger trigger,
                                IProgress<RefreshProgress> progress, CancellationTokenSource cts, TaskCompletionSource done)
    {
        try
        {
            RefreshRunResult result = await refresher.RunAsync(progress, cts.Token);
            var results = new Dictionary<string, FeedRefreshResult>(StringComparer.OrdinalIgnoreCase);
            foreach (FeedRefreshResult feed in result.Feeds)
                results[feed.Key] = feed;
            LastRun = result;
            LastRunTrigger = trigger;
            LastResults = results.AsReadOnly();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Refresh all failed unexpectedly: {ex.GetType().Name}");
        }
        finally
        {
            // A handler of an earlier event may already have started the next run; its fields are left alone
            if (ReferenceEquals(_run, run))
            {
                _run = null;
                _cts = null;
                Progress = null;
                IsRunning = false;
                RunningTrigger = null;
                CancelRequested = false;
            }

            RunsEnded++;
            cts.Dispose();
            try
            {
                _gate.SetRefreshing(false);
                Changed?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                done.TrySetResult();
            }
        }
    }

    /// <summary>Stays true until the close's stop has seen the run end, so a second close meanwhile holds.</summary>
    public bool HoldsClose => IsRunning || _stopping;

    public bool AsksBeforeClose => IsRunning;

    /// <summary>True from the close's stop until the run has ended, its last Changed included.</summary>
    internal bool StoppingForClose => _stopping;

    bool ICloseParticipant.StopsWhenAgreed => true;

    /// <summary>Holds the gate while the question is open; a dialog that fails to show is a no.</summary>
    public async Task<bool> AskBeforeCloseAsync()
    {
        if (!IsRunning || _ui is not { } ui)
            return true;

        using IDisposable hold = _gate.Hold();
        try
        {
            return await ui.ConfirmAsync(AppText.CloseRefreshTitle, AppText.CloseRefreshBody, AppText.CloseRefreshButton);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IDisposable? HoldRunsForClose()
    {
        _closeHolds++;
        return new CloseHold(this);
    }

    /// <summary>Cancels the run and waits for it, its feed in flight and its one save included. Never throws.</summary>
    public async Task StopForCloseAsync()
    {
        if (!IsRunning || PendingRun is not { } run)
            return;

        _stopping = true;
        try
        {
            Cancel();
            await run;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Refresh all's stop failed unexpectedly: {ex.GetType().Name}");
        }
        finally
        {
            _stopping = false;
        }
    }

    /// <summary>Asks the run under way to stop and raises Changed, once per run; does nothing when idle.</summary>
    public void Cancel()
    {
        if (!IsRunning || CancelRequested)
            return;

        CancelRequested = true;
        CancelToken();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancels a run without waiting or raising Changed; the run frees the gate when it returns.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (IsRunning)
            CancelRequested = true;
        CancelToken();
    }

    // A callback registered on the token may throw from Cancel; the close and the window's end must go on
    private void CancelToken()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (AggregateException ex)
        {
            Trace.TraceWarning($"Refresh all's cancel failed: {ex.GetType().Name}");
        }
    }

    private sealed class CloseHold(RefreshAllRunner runner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            runner._closeHolds--;
        }
    }
}
