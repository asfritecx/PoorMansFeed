using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class RefreshTimerTests : IDisposable
{
    private static readonly DateTimeOffset Launch = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly FakeTimeProvider _fake = new(Launch);
    private readonly ShiftedTime _time;
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly FakeFeedRefresher _refresher = new();
    private readonly RefreshAllRunner _runner;
    private UserSettings _settings = new() { RefreshAllOnTimer = true, RefreshAllEveryHours = 6 };
    private bool _openChecks;
    private int _changes;
    private RefreshTimer? _timer;

    public RefreshTimerTests()
    {
        _time = new ShiftedTime(_fake);
        _gate.SetStarted(true);
        _runner = NewRunner(_refresher);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _runner.Dispose();
    }

    private RefreshAllRunner NewRunner(IFeedRefresher? refresher) =>
        new(refresher, _gate, _ui) { ProgressFactory = handler => new ImmediateProgress<RefreshProgress>(handler) };

    private RefreshTimer Started(RefreshAllRunner? runner = null)
    {
        var timer = new RefreshTimer(runner ?? _runner, () => _settings, () => _openChecks, _ui, _time);
        timer.Changed += (_, _) => _changes++;
        timer.Start();
        _timer = timer;
        return timer;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Poll(int polls = 1)
    {
        for (int i = 0; i < polls; i++)
        {
            _fake.Advance(RefreshTimer.PollEvery);
            _ui.RunPosted();
        }
    }

    // One poll at a time, as the timer ticks, until the wall clock reaches the time
    private void PollUntil(DateTimeOffset time)
    {
        while (Now < time)
            Poll();
    }

    // The run under way, taken before it ends: an end handler may start the next run and replace PendingRun
    private async Task FinishRunAsync()
    {
        Task run = _runner.PendingRun!;
        _refresher.Finish(FakeFeedRefresher.Of());
        await run;
    }

    // ----- when a run is due -----

    [Fact]
    public void Start_sets_the_first_run_one_interval_after_launch()
    {
        RefreshTimer timer = Started();

        Assert.Equal(Launch + Interval, timer.NextDueUtc);
        Assert.False(timer.IsWaiting);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void No_run_starts_before_the_interval_and_a_poll_that_changes_nothing_raises_nothing()
    {
        RefreshTimer timer = Started();
        int changes = _changes;

        PollUntil(Launch + Interval - RefreshTimer.PollEvery);

        Assert.Equal(0, _refresher.Calls);
        Assert.False(_runner.IsRunning);
        Assert.Equal(Launch + Interval, timer.NextDueUtc);
        Assert.False(timer.IsWaiting);
        Assert.Equal(changes, _changes);
    }

    [Fact]
    public void The_run_starts_at_the_poll_that_reaches_the_due_time_exactly()
    {
        RefreshTimer timer = Started();

        PollUntil(Launch + Interval);

        Assert.Equal(Launch + Interval, Now);
        Assert.Equal(1, _refresher.Calls);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
        Assert.False(timer.IsWaiting);
    }

    [Fact]
    public async Task A_timed_runs_end_sets_the_next_due_one_interval_after_it()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);
        Poll(30);
        DateTimeOffset ended = Now;
        int changes = _changes;

        await FinishRunAsync();

        Assert.Equal(ended + Interval, timer.NextDueUtc);
        Assert.True(_changes > changes);
        PollUntil(ended + Interval - RefreshTimer.PollEvery);
        Assert.Equal(1, _refresher.Calls);
        Poll();
        Assert.Equal(2, _refresher.Calls);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
    }

    [Fact]
    public async Task A_pressed_runs_end_moves_the_next_due()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(2));
        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart());
        PollUntil(Launch + TimeSpan.FromHours(3));

        await FinishRunAsync();

        DateTimeOffset due = Launch + TimeSpan.FromHours(3) + Interval;
        Assert.Equal(due, timer.NextDueUtc);
        PollUntil(due - RefreshTimer.PollEvery);
        Assert.Equal(1, _refresher.Calls);
        Poll();
        Assert.Equal(2, _refresher.Calls);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
    }

    [Fact]
    public async Task A_run_started_from_another_runs_end_handler_still_moves_the_next_due()
    {
        // Hooked before the timer, so the second run is out before the timer hears of the first run's end
        bool restarted = false;
        _runner.Changed += (_, _) =>
        {
            if (!restarted && _runner.RunsEnded == 1)
            {
                restarted = true;
                _runner.TryStart();
            }
        };
        RefreshTimer timer = Started();
        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart());
        PollUntil(Launch + TimeSpan.FromHours(1));

        await FinishRunAsync();

        Assert.True(restarted);
        Assert.True(_runner.IsRunning);
        Assert.Null(timer.NextDueUtc);
        PollUntil(Launch + TimeSpan.FromHours(2));
        await FinishRunAsync();
        Assert.Equal(Launch + TimeSpan.FromHours(2) + Interval, timer.NextDueUtc);
        Assert.Equal(2, _refresher.Calls);
    }

    [Fact]
    public void Two_polls_while_a_timed_run_is_out_start_no_second_run()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);

        Assert.Null(timer.NextDueUtc);
        Poll(2);

        Assert.Equal(1, _refresher.Calls);
        Assert.True(_runner.IsRunning);
        Assert.False(timer.IsWaiting);
        Assert.Null(timer.NextDueUtc);
    }

    // ----- the setting -----

    [Fact]
    public void Off_at_start_has_no_due_time_and_never_runs()
    {
        _settings = _settings with { RefreshAllOnTimer = false };
        RefreshTimer timer = Started();

        PollUntil(Launch + Interval + Interval);

        Assert.Null(timer.NextDueUtc);
        Assert.False(timer.IsWaiting);
        Assert.Equal(0, _refresher.Calls);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void A_switch_on_saved_counts_the_interval_from_the_save()
    {
        _settings = _settings with { RefreshAllOnTimer = false };
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(10));
        _settings = _settings with { RefreshAllOnTimer = true };

        timer.SettingsSaved();

        DateTimeOffset due = Launch + TimeSpan.FromHours(10) + Interval;
        Assert.Equal(due, timer.NextDueUtc);
        Assert.Equal(1, _changes);
        PollUntil(due - RefreshTimer.PollEvery);
        Assert.Equal(0, _refresher.Calls);
        Poll();
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void A_switch_off_saved_clears_the_due_time_and_stops_runs()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(1));
        int changes = _changes;
        _settings = _settings with { RefreshAllOnTimer = false };

        timer.SettingsSaved();

        Assert.Null(timer.NextDueUtc);
        Assert.Equal(changes + 1, _changes);
        PollUntil(Launch + Interval + Interval);
        Assert.Equal(0, _refresher.Calls);
        Assert.Null(timer.NextDueUtc);
    }

    [Fact]
    public void An_interval_changed_keeps_the_baseline()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(2));
        _settings = _settings with { RefreshAllEveryHours = 3 };

        timer.SettingsSaved();

        Assert.Equal(Launch + TimeSpan.FromHours(3), timer.NextDueUtc);
        PollUntil(Launch + TimeSpan.FromHours(3) - RefreshTimer.PollEvery);
        Assert.Equal(0, _refresher.Calls);
        Poll();
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void A_shorter_interval_already_past_runs_at_the_next_poll_not_at_the_save()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(4));
        _settings = _settings with { RefreshAllEveryHours = 2 };

        timer.SettingsSaved();

        Assert.Equal(Launch + TimeSpan.FromHours(2), timer.NextDueUtc);
        Assert.Equal(0, _refresher.Calls);
        Poll();
        Assert.Equal(1, _refresher.Calls);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
    }

    [Fact]
    public void An_interval_off_the_limits_is_clamped()
    {
        _settings = _settings with { RefreshAllEveryHours = 0 };
        RefreshTimer timer = Started();

        Assert.Equal(Launch + TimeSpan.FromHours(RefreshAllLimits.MinHours), timer.NextDueUtc);

        _settings = _settings with { RefreshAllEveryHours = 99 };
        timer.SettingsSaved();
        Assert.Equal(Launch + TimeSpan.FromHours(RefreshAllLimits.MaxHours), timer.NextDueUtc);
    }

    // ----- the clock -----

    [Fact]
    public void A_clock_set_back_behind_the_baseline_counts_from_the_new_time()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(1));
        _time.Shift = TimeSpan.FromHours(-3);

        Poll();

        DateTimeOffset now = Now;
        Assert.True(now < Launch);
        Assert.Equal(now + Interval, timer.NextDueUtc);
        PollUntil(now + Interval - RefreshTimer.PollEvery);
        Assert.Equal(0, _refresher.Calls);
        Poll();
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void After_a_wake_a_due_run_waits_two_more_polls()
    {
        RefreshTimer timer = Started();
        int posted = _ui.PostedCount;

        _fake.Advance(TimeSpan.FromHours(10));
        _ui.RunPosted();

        // One tick waits however many periods the timer saw pass while asleep
        Assert.Equal(posted + 1, _ui.PostedCount);
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        Assert.Equal(Launch + Interval, timer.NextDueUtc);
        Poll();
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        Poll();
        Assert.Equal(1, _refresher.Calls);
        Assert.False(timer.IsWaiting);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
    }

    [Fact]
    public void Ticks_that_keep_arriving_late_still_start_the_run_after_eight()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval - RefreshTimer.PollEvery);

        for (int late = 1; late <= 7; late++)
        {
            _fake.Advance(TimeSpan.FromMinutes(4));
            _ui.RunPosted();
            Assert.Equal(0, _refresher.Calls);
            Assert.True(timer.IsWaiting);
        }
        _fake.Advance(TimeSpan.FromMinutes(4));
        _ui.RunPosted();

        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void A_second_sleep_while_a_wake_holds_the_run_holds_it_two_more_polls()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);
        _refresher.Finish(FakeFeedRefresher.Of());
        _ui.RunPosted();
        int calls = _refresher.Calls;

        _fake.Advance(Interval + TimeSpan.FromHours(1));
        _ui.RunPosted();
        Poll();
        _fake.Advance(TimeSpan.FromHours(2));
        _ui.RunPosted();
        Assert.Equal(calls, _refresher.Calls);
        Poll();
        Assert.Equal(calls, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        Poll();

        Assert.Equal(calls + 1, _refresher.Calls);
    }

    [Fact]
    public void A_wake_after_starts_refused_through_earlier_wakes_still_holds_the_run()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval - RefreshTimer.PollEvery);
        using (_gate.Hold())
        {
            for (int sleep = 1; sleep <= 4; sleep++)
            {
                _fake.Advance(TimeSpan.FromHours(1));
                _ui.RunPosted();
                Poll(2);
            }
            Assert.Equal(0, _refresher.Calls);
        }

        _fake.Advance(TimeSpan.FromHours(1));
        _ui.RunPosted();
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        Poll(2);

        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void A_gap_of_three_polls_is_no_wake()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval - TimeSpan.FromMinutes(3));

        _fake.Advance(TimeSpan.FromMinutes(3));
        _ui.RunPosted();

        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void A_wake_before_the_due_time_holds_nothing_once_its_polls_have_passed()
    {
        RefreshTimer timer = Started();

        _fake.Advance(TimeSpan.FromHours(2));
        _ui.RunPosted();

        Assert.False(timer.IsWaiting);
        PollUntil(Launch + Interval);
        Assert.Equal(1, _refresher.Calls);
    }

    // ----- open checks on the cloud pages -----

    [Fact]
    public void A_save_that_moves_a_waiting_run_past_now_ends_the_wait()
    {
        _openChecks = true;
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);
        Assert.True(timer.IsWaiting);

        _settings = _settings with { RefreshAllEveryHours = 12 };
        timer.SettingsSaved();

        Assert.False(timer.IsWaiting);
        Assert.Equal(Launch + TimeSpan.FromHours(12), timer.NextDueUtc);
    }

    [Fact]
    public void Open_checks_hold_a_due_run_until_they_close()
    {
        _openChecks = true;
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        PollUntil(Launch + Interval + TimeSpan.FromMinutes(10));
        Assert.Equal(0, _refresher.Calls);

        _openChecks = false;
        Poll();

        Assert.Equal(1, _refresher.Calls);
        Assert.False(timer.IsWaiting);
    }

    [Fact]
    public void Open_checks_hold_a_due_run_at_most_sixty_minutes()
    {
        _openChecks = true;
        RefreshTimer timer = Started();

        PollUntil(Launch + Interval + TimeSpan.FromMinutes(59));
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);
        Poll();

        Assert.Equal(1, _refresher.Calls);
        Assert.Equal(RefreshTrigger.Timer, _runner.RunningTrigger);
        Assert.False(timer.IsWaiting);
    }

    [Fact]
    public void Open_checks_before_the_due_time_hold_nothing()
    {
        _openChecks = true;
        RefreshTimer timer = Started();

        PollUntil(Launch + Interval - RefreshTimer.PollEvery);

        Assert.False(timer.IsWaiting);
    }

    // ----- refusals -----

    [Fact]
    public void Not_started_retries_at_the_next_poll()
    {
        _gate.SetStarted(false);
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval);
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);

        _gate.SetStarted(true);
        Poll();

        Assert.Equal(1, _refresher.Calls);
        Assert.False(timer.IsWaiting);
    }

    [Fact]
    public void A_busy_gate_retries_at_the_next_poll()
    {
        RefreshTimer timer = Started();
        IDisposable hold = _gate.Hold();
        PollUntil(Launch + Interval + RefreshTimer.PollEvery);
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);

        hold.Dispose();
        Poll();

        Assert.Equal(1, _refresher.Calls);
        Assert.False(timer.IsWaiting);
    }

    [Fact]
    public void A_working_page_retries_at_the_next_poll()
    {
        RefreshTimer timer = Started();
        IDisposable work = _gate.EnterPageWork();
        PollUntil(Launch + Interval);
        Assert.Equal(0, _refresher.Calls);
        Assert.True(timer.IsWaiting);

        work.Dispose();
        Poll();

        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public async Task A_pressed_run_still_out_at_the_due_time_waits_for_its_end()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(5));
        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart());
        PollUntil(Launch + Interval + RefreshTimer.PollEvery);
        Assert.Equal(1, _refresher.Calls);
        // The press's own run is the refresh, so the caption names no due time and no wait
        Assert.False(timer.IsWaiting);
        Assert.Null(timer.NextDueUtc);
        Assert.Equal(RefreshTrigger.Pressed, _runner.RunningTrigger);
        DateTimeOffset ended = Now;

        await FinishRunAsync();

        Assert.False(timer.IsWaiting);
        Assert.Equal(ended + Interval, timer.NextDueUtc);
        Poll();
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void Closing_does_nothing_and_the_timer_keeps_polling()
    {
        RefreshTimer timer = Started();
        IDisposable? closeHold = _runner.HoldRunsForClose();
        PollUntil(Launch + Interval - RefreshTimer.PollEvery);
        int changes = _changes;

        Poll(3);

        Assert.Equal(0, _refresher.Calls);
        Assert.False(timer.IsWaiting);
        Assert.Equal(Launch + Interval, timer.NextDueUtc);
        Assert.Equal(changes, _changes);
        closeHold!.Dispose();
        Poll();
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void Unavailable_stops_the_timer_for_the_session()
    {
        using RefreshAllRunner unavailable = NewRunner(null);
        RefreshTimer timer = Started(unavailable);
        PollUntil(Launch + Interval - RefreshTimer.PollEvery);
        int changes = _changes;

        Poll();

        Assert.Null(timer.NextDueUtc);
        Assert.False(timer.IsWaiting);
        Assert.Equal(changes + 1, _changes);
        int posted = _ui.PostedCount;
        Poll(10);
        timer.SettingsSaved();
        Assert.Equal(posted, _ui.PostedCount);
        Assert.Null(timer.NextDueUtc);
        Assert.Equal(changes + 1, _changes);
    }

    // ----- dispose -----

    [Fact]
    public void A_tick_posted_before_dispose_and_run_after_it_does_nothing()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + Interval - RefreshTimer.PollEvery);
        _fake.Advance(RefreshTimer.PollEvery);
        int posted = _ui.PostedCount;

        timer.Dispose();
        _ui.RunPosted();

        Assert.Equal(0, _refresher.Calls);
        Assert.Null(timer.NextDueUtc);
        Poll(5);
        Assert.Equal(posted, _ui.PostedCount);
        Assert.Equal(0, _refresher.Calls);
    }

    [Fact]
    public async Task Dispose_twice_is_harmless_and_unhooks_the_runner()
    {
        RefreshTimer timer = Started();
        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart());
        int changes = _changes;

        timer.Dispose();
        timer.Dispose();
        await FinishRunAsync();
        timer.SettingsSaved();
        timer.Start();

        Assert.Null(timer.NextDueUtc);
        Assert.False(timer.IsWaiting);
        Assert.Equal(changes, _changes);
        Poll(5);
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void Start_twice_counts_from_the_first()
    {
        RefreshTimer timer = Started();
        PollUntil(Launch + TimeSpan.FromHours(1));
        int posted = _ui.PostedCount;

        timer.Start();

        Assert.Equal(Launch + Interval, timer.NextDueUtc);
        Poll();
        Assert.Equal(posted + 1, _ui.PostedCount);
    }

    // The wall clock with an offset the test sets, over the fake's timers, so the clock can be set back
    private sealed class ShiftedTime(FakeTimeProvider inner) : TimeProvider
    {
        public TimeSpan Shift { get; set; }

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Shift;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
    // ----- the UTC day -----

    private static readonly DateTimeOffset Midnight = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_poll_that_crosses_a_UTC_midnight_while_on_raises_Changed_once_and_the_next_raises_nothing()
    {
        _settings = new UserSettings { RefreshAllOnTimer = true, RefreshAllEveryHours = 24 };
        RefreshTimer timer = Started();
        PollUntil(Midnight - RefreshTimer.PollEvery);
        int changes = _changes;

        Poll();
        Assert.Equal(Midnight, Now);
        Assert.Equal(changes + 1, _changes);
        Assert.Equal(Launch + TimeSpan.FromHours(24), timer.NextDueUtc);
        Assert.False(timer.IsWaiting);

        Poll();
        Assert.Equal(changes + 1, _changes);
    }

    [Fact]
    public void A_poll_that_crosses_a_UTC_midnight_while_off_raises_nothing()
    {
        _settings = new UserSettings { RefreshAllOnTimer = false, RefreshAllEveryHours = 24 };
        RefreshTimer timer = Started();
        PollUntil(Midnight - RefreshTimer.PollEvery);
        int changes = _changes;

        Poll(2);
        Assert.Equal(changes, _changes);
        Assert.Null(timer.NextDueUtc);
    }
}
