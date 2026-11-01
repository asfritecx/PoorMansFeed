using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>
/// Starts Refresh all on its schedule while the application is open: one interval after launch, a switch-on or the
/// last run's end. UI thread only, except the timer's callback, which only posts a tick.
/// </summary>
public sealed class RefreshTimer : IRefreshSchedule, IDisposable
{
    // A gap this long between two ticks means the machine slept; the run then waits for the network to come back
    private const int WakeGapPolls = 3;
    private const int WakeHoldPolls = 2;
    // Ticks that keep arriving late would hold a run for good, so a wake arms the hold again only this many held polls
    private const int MaxWakeHeldPolls = 6;

    private readonly RefreshAllRunner _runner;
    private readonly Func<UserSettings> _settings;
    private readonly Func<bool> _pagesHoldOpenChecks;
    private readonly IUiServices _ui;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private int _tickPosted;
    private bool _started;
    private bool _stopped;
    private bool _disposed;
    private bool _on;
    private TimeSpan _interval;
    private DateTimeOffset _baseline;
    private DateTimeOffset _lastTick;
    private int _runsEnded;
    private int _wakeHold;
    private int _wakeHeldPolls;
    private DateTimeOffset? _nextDueUtc;
    private bool _isWaiting;
    private int _published;

    public RefreshTimer(RefreshAllRunner runner, Func<UserSettings> settings, Func<bool> pagesHoldOpenChecks,
                        IUiServices ui, TimeProvider time)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _pagesHoldOpenChecks = pagesHoldOpenChecks ?? throw new ArgumentNullException(nameof(pagesHoldOpenChecks));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    internal static TimeSpan PollEvery { get; } = TimeSpan.FromMinutes(1);

    /// <summary>How long past its due time open checks on the cloud pages may hold a run back.</summary>
    internal static TimeSpan OpenCheckLimit { get; } = TimeSpan.FromMinutes(60);

    /// <summary>Null while off, stopped, disposed or a run is out; in the past while a due run waits.</summary>
    public DateTimeOffset? NextDueUtc => _nextDueUtc;

    /// <summary>A run is due but waits for a wake, open checks on the cloud pages or other work.</summary>
    public bool IsWaiting => _isWaiting;

    /// <summary>NextDueUtc or IsWaiting changed; raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Counts the first interval from now; later calls do nothing.</summary>
    public void Start()
    {
        if (_started || _disposed)
            return;

        _started = true;
        DateTimeOffset now = _time.GetUtcNow();
        _baseline = now;
        _lastTick = now;
        _runsEnded = _runner.RunsEnded;
        ReadSettings(now);
        _runner.Changed += RunnerChanged;
        // .NET's timers don't count sleep on macOS, so each tick compares the wall clock instead of one long due time
        _timer = _time.CreateTimer(_ => PostTick(), null, PollEvery, PollEvery);
        Publish(DueTime(), false);
    }

    /// <summary>A switch-on counts the interval from now; a new interval keeps the start it counts from.</summary>
    public void SettingsSaved()
    {
        if (!_started || _stopped || _disposed)
            return;

        DateTimeOffset now = _time.GetUtcNow();
        ReadSettings(now);
        if (_runner.IsRunning)
            Publish(null, false);
        else
            Publish(DueTime(), _on && _isWaiting && now >= _baseline + _interval);
    }

    /// <summary>A tick already posted does nothing once disposed; raises nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopPolling();
        _nextDueUtc = null;
        _isWaiting = false;
    }

    // Ticks the timer fires while one is still posted are dropped, so a long sleep wakes to one tick, not hundreds
    private void PostTick()
    {
        if (Interlocked.Exchange(ref _tickPosted, 1) == 0)
            _ui.PostToUiThread(Tick);
    }

    private void Tick()
    {
        Volatile.Write(ref _tickPosted, 0);
        if (_disposed || _stopped)
            return;

        DateTimeOffset now = _time.GetUtcNow();
        bool newDay = now.UtcDateTime.Date != _lastTick.UtcDateTime.Date;
        int published = _published;
        TickAt(now);
        // A due time shown for tomorrow is shown for today once the UTC day turns, though the time itself didn't move
        if (newDay && _on && !_disposed && !_stopped && _published == published)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TickAt(DateTimeOffset now)
    {
        ReadSettings(now);
        NoteRunsEnded(now);
        if (now < _baseline)
            _baseline = now;

        if (now - _lastTick > WakeGapPolls * PollEvery && _wakeHeldPolls < MaxWakeHeldPolls)
            _wakeHold = WakeHoldPolls;
        else if (_wakeHold > 0)
            _wakeHold--;
        _lastTick = now;

        // A run that is out, pressed or timed, names no due time: its end moves the baseline
        if (!_on || _runner.IsRunning)
        {
            Publish(null, false);
            return;
        }

        DateTimeOffset due = _baseline + _interval;
        if (now < due)
        {
            _wakeHeldPolls = 0;
            Publish(due, false);
            return;
        }

        if (_wakeHold > 0)
            _wakeHeldPolls++;
        if (_wakeHold > 0 || (_pagesHoldOpenChecks() && now < due + OpenCheckLimit))
        {
            Publish(due, true);
            return;
        }

        switch (_runner.TryStart(RefreshTrigger.Timer))
        {
            case RefreshStartRefusal.None:
                _wakeHeldPolls = 0;
                Publish(null, false);
                break;
            case RefreshStartRefusal.Unavailable:
                _stopped = true;
                StopPolling();
                Publish(null, false);
                break;
            case RefreshStartRefusal.Closing:
                _wakeHeldPolls = 0;
                break;
            default:
                // A run already out moves the baseline when it ends; the gate and a page's work free up by themselves
                _wakeHeldPolls = 0;
                Publish(due, true);
                break;
        }
    }

    // The count, never an edge of IsRunning: a run started from another run's end handler hides that edge
    private void RunnerChanged(object? sender, EventArgs e)
    {
        if (_disposed || _stopped)
            return;

        bool ended = NoteRunsEnded(_time.GetUtcNow());
        if (_runner.IsRunning)
            Publish(null, false);
        else if (ended)
            Publish(DueTime(), false);
    }

    private bool NoteRunsEnded(DateTimeOffset now)
    {
        int ended = _runner.RunsEnded;
        if (ended == _runsEnded)
            return false;

        _runsEnded = ended;
        _baseline = now;
        return true;
    }

    private void ReadSettings(DateTimeOffset now)
    {
        UserSettings settings = _settings();
        if (settings.RefreshAllOnTimer && !_on)
            _baseline = now;
        _on = settings.RefreshAllOnTimer;
        _interval = TimeSpan.FromHours(Math.Clamp(settings.RefreshAllEveryHours, RefreshAllLimits.MinHours,
                                                  RefreshAllLimits.MaxHours));
    }

    private DateTimeOffset? DueTime() => _on ? _baseline + _interval : null;

    private void StopPolling()
    {
        _timer?.Dispose();
        _timer = null;
        _runner.Changed -= RunnerChanged;
    }

    private void Publish(DateTimeOffset? nextDueUtc, bool isWaiting)
    {
        if (nextDueUtc == _nextDueUtc && isWaiting == _isWaiting)
            return;

        _nextDueUtc = nextDueUtc;
        _isWaiting = isWaiting;
        _published++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
