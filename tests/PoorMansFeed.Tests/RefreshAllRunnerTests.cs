using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>A refresher whose run ends by Finish or, with FinishWhenCancelled, by the run's own cancel.</summary>
internal sealed class FakeFeedRefresher : IFeedRefresher
{
    private TaskCompletionSource<RefreshRunResult> _pending = new();

    public int Calls { get; private set; }
    public CancellationToken Token { get; private set; }
    public IProgress<RefreshProgress>? Progress { get; private set; }
    public Exception? Failure { get; set; }
    public bool FinishWhenCancelled { get; set; }

    public Task<RefreshRunResult> RunAsync(IProgress<RefreshProgress>? progress, CancellationToken ct)
    {
        Calls++;
        Token = ct;
        Progress = progress;
        if (Failure is { } failure)
            throw failure;
        var pending = new TaskCompletionSource<RefreshRunResult>();
        _pending = pending;
        if (FinishWhenCancelled)
            ct.Register(() => pending.TrySetResult(Cancelled()));
        return pending.Task;
    }

    public void Finish(RefreshRunResult result) => _pending.SetResult(result);

    public static RefreshRunResult Cancelled() => new(true, null, [], null, [], true, false);

    public static RefreshRunResult Of(params string[] keys) => new(true, null, [], null,
        keys.Select(key => new FeedRefreshResult(key, FeedKind.Asn, FeedRefreshOutcome.Unchanged, FeedRefreshReason.None,
            false, 0, 0, 0, 0, null, [], null)).ToList(), false, false);
}

public sealed class RefreshAllRunnerTests
{
    private readonly WorkGate _gate = new();
    private readonly FakeUi _ui = new();
    private readonly FakeFeedRefresher _refresher = new();
    private int _changes;

    private RefreshAllRunner NewRunner(bool started = true, IFeedRefresher? refresher = null, bool noRefresher = false,
                                       IUiServices? ui = null, bool noUi = false)
    {
        if (started)
            _gate.SetStarted(true);
        var runner = new RefreshAllRunner(noRefresher ? null : refresher ?? _refresher, _gate, noUi ? null : ui ?? _ui)
        {
            ProgressFactory = handler => new ImmediateProgress<RefreshProgress>(handler),
        };
        runner.Changed += (_, _) => _changes++;
        return runner;
    }

    private void AssertRefusedWithNoChange(RefreshAllRunner runner, RefreshStartRefusal expected)
    {
        int changes = _changes;
        int calls = _refresher.Calls;
        bool running = runner.IsRunning;
        Task? pending = runner.PendingRun;

        Assert.Equal(expected, runner.TryStart());

        Assert.Equal(changes, _changes);
        Assert.Equal(calls, _refresher.Calls);
        Assert.Equal(running, runner.IsRunning);
        Assert.Same(pending, runner.PendingRun);
    }

    [Fact]
    public void NoRefresherIsUnavailable()
    {
        RefreshAllRunner runner = NewRunner(noRefresher: true);

        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Unavailable);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task EachRefusalComesInItsOrderAndChangesNothing()
    {
        RefreshAllRunner runner = NewRunner();
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        IDisposable pageWork = _gate.EnterPageWork();
        IDisposable gateHold = _gate.Hold();
        IDisposable closeHold = runner.HoldRunsForClose()!;
        _gate.SetStarted(false);

        AssertRefusedWithNoChange(runner, RefreshStartRefusal.NotStarted);
        _gate.SetStarted(true);
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Running);
        _refresher.Finish(FakeFeedRefresher.Of());
        await runner.PendingRun!;
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Closing);
        closeHold.Dispose();
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.GateBusy);
        gateHold.Dispose();
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.PageWorking);
        pageWork.Dispose();
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        Assert.Equal(2, _refresher.Calls);
    }

    [Fact]
    public void NotStartedRefusesBeforeAnything()
    {
        RefreshAllRunner runner = NewRunner(started: false);

        AssertRefusedWithNoChange(runner, RefreshStartRefusal.NotStarted);
        Assert.Equal(0, _refresher.Calls);
    }

    [Fact]
    public void CloseHoldsNestAndADoubleDisposeReleasesOnce()
    {
        RefreshAllRunner runner = NewRunner();
        IDisposable outer = runner.HoldRunsForClose()!;
        IDisposable inner = runner.HoldRunsForClose()!;

        inner.Dispose();
        inner.Dispose();
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Closing);
        outer.Dispose();
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
    }

    [Fact]
    public void AnUpdateOrAHoldOnTheGateRefusesAsGateBusy()
    {
        RefreshAllRunner runner = NewRunner();

        _gate.SetBusy(true);
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.GateBusy);
        _gate.SetBusy(false);
        using (_gate.Hold())
            AssertRefusedWithNoChange(runner, RefreshStartRefusal.GateBusy);

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
    }

    [Fact]
    public void PageWorkRefuses()
    {
        RefreshAllRunner runner = NewRunner();
        IDisposable pageWork = _gate.EnterPageWork();

        AssertRefusedWithNoChange(runner, RefreshStartRefusal.PageWorking);
        Assert.True(_gate.CanStart);
        pageWork.Dispose();
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
    }

    [Fact]
    public void AStartRunsMakesTheGateBusyWithoutTheUpdateFlagAndPassesAToken()
    {
        RefreshAllRunner runner = NewRunner();
        int gateChanges = 0;
        _gate.Changed += (_, _) => gateChanges++;

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());

        Assert.True(runner.IsRunning);
        Assert.False(_gate.CanStart);
        Assert.False(_gate.IsHeld);
        Assert.Equal(1, gateChanges);
        Assert.Equal(1, _changes);
        Assert.Equal(1, _refresher.Calls);
        Assert.True(_refresher.Token.CanBeCanceled);
        Assert.False(_refresher.Token.IsCancellationRequested);
        Assert.NotNull(_refresher.Progress);
        Assert.False(runner.PendingRun!.IsCompleted);
        _gate.SetBusy(false);
        Assert.False(_gate.CanStart);
    }

    [Fact]
    public void ProgressLandsAndRaisesChanged()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        var report = new RefreshProgress(RefreshStep.Feeds, 1, 3, "AS64500", null);

        _refresher.Progress!.Report(report);

        Assert.Equal(report, runner.Progress);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public async Task AReportAfterTheRunEndedIsDropped()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        IProgress<RefreshProgress> progress = _refresher.Progress!;
        _refresher.Finish(FakeFeedRefresher.Of());
        await runner.PendingRun!;
        int changes = _changes;

        progress.Report(new RefreshProgress(RefreshStep.Saving, 1, 1, null, null));

        Assert.Null(runner.Progress);
        Assert.Equal(changes, _changes);
    }

    [Fact]
    public async Task TheEndKeepsTheRunAndItsFeedsIgnoringCaseAndFreesTheGate()
    {
        RefreshAllRunner runner = NewRunner();
        Assert.Null(runner.LastRun);
        Assert.Empty(runner.LastResults);
        runner.TryStart();
        _refresher.Progress!.Report(new RefreshProgress(RefreshStep.Dataset, 0, 1, null, null));
        RefreshRunResult result = FakeFeedRefresher.Of("AS64500", "GoogleCloud_all");
        int gateChanges = 0;
        _gate.Changed += (_, _) => gateChanges++;

        _refresher.Finish(result);
        await runner.PendingRun!;

        Assert.Same(result, runner.LastRun);
        Assert.Equal(2, runner.LastResults.Count);
        Assert.Same(result.Feeds[0], runner.LastResults["as64500"]);
        Assert.Same(result.Feeds[1], runner.LastResults["GOOGLECLOUD_ALL"]);
        Assert.Null(runner.Progress);
        Assert.False(runner.IsRunning);
        Assert.True(_gate.CanStart);
        Assert.Equal(1, gateChanges);
        Assert.Equal(3, _changes);
    }

    [Fact]
    public async Task ASecondRunReplacesTheResults()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await runner.PendingRun!;

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        RefreshRunResult second = FakeFeedRefresher.Of("AWS_EC2_all");
        _refresher.Finish(second);
        await runner.PendingRun!;

        Assert.Same(second, runner.LastRun);
        Assert.False(runner.LastResults.ContainsKey("AS64500"));
        Assert.True(runner.LastResults.ContainsKey("aws_ec2_all"));
    }

    [Fact]
    public async Task ARunThatThrowsStillFreesTheGateAndKeepsTheEarlierResults()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        RefreshRunResult first = FakeFeedRefresher.Of("AS64500");
        _refresher.Finish(first);
        await runner.PendingRun!;
        _refresher.Failure = new InvalidOperationException("broken");

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        await runner.PendingRun!;

        Assert.False(runner.IsRunning);
        Assert.True(_gate.CanStart);
        Assert.Same(first, runner.LastRun);
        Assert.True(runner.LastResults.ContainsKey("AS64500"));
        Assert.Null(runner.Progress);
    }

    [Fact]
    public async Task ARunStartsPressedByDefaultAndKeepsItsTriggerUntilTheNextRunEnds()
    {
        RefreshAllRunner runner = NewRunner();
        Assert.Null(runner.RunningTrigger);
        Assert.Null(runner.LastRunTrigger);
        Assert.Equal(0, runner.RunsEnded);

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        Assert.Equal(RefreshTrigger.Pressed, runner.RunningTrigger);
        Assert.Null(runner.LastRunTrigger);
        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await runner.PendingRun!;
        Assert.Null(runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Pressed, runner.LastRunTrigger);
        Assert.Equal(1, runner.RunsEnded);

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart(RefreshTrigger.Timer));
        Assert.Equal(RefreshTrigger.Timer, runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Pressed, runner.LastRunTrigger);
        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await runner.PendingRun!;
        Assert.Null(runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Timer, runner.LastRunTrigger);
        Assert.Equal(2, runner.RunsEnded);
    }

    [Fact]
    public async Task ACancelledTimedRunIsTheLastRunWithItsTrigger()
    {
        _refresher.FinishWhenCancelled = true;
        RefreshAllRunner runner = NewRunner();

        runner.TryStart(RefreshTrigger.Timer);
        Assert.Equal(RefreshTrigger.Timer, runner.RunningTrigger);
        runner.Cancel();
        await runner.PendingRun!;

        Assert.True(runner.LastRun!.Cancelled);
        Assert.Null(runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Timer, runner.LastRunTrigger);
        Assert.Equal(1, runner.RunsEnded);
    }

    [Fact]
    public async Task ARunThatThrowsCountsAsEndedAndKeepsTheEarlierRunsTrigger()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await runner.PendingRun!;
        _refresher.Failure = new InvalidOperationException("broken");

        runner.TryStart(RefreshTrigger.Timer);
        await runner.PendingRun!;

        Assert.Null(runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Pressed, runner.LastRunTrigger);
        Assert.Equal(2, runner.RunsEnded);
    }

    [Fact]
    public async Task ARefusedStartLeavesTheTriggersAndTheCount()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();

        Assert.Equal(RefreshStartRefusal.Running, runner.TryStart(RefreshTrigger.Timer));
        Assert.Equal(RefreshTrigger.Pressed, runner.RunningTrigger);
        _refresher.Finish(FakeFeedRefresher.Of());
        await runner.PendingRun!;
        Assert.Equal(1, runner.RunsEnded);
    }

    [Fact]
    public async Task TheEndsChangedSeesTheRunCountedAndAHandlerCanStartTheNextRun()
    {
        RefreshAllRunner runner = NewRunner();
        var seen = new List<(bool Running, int Ended, RefreshTrigger? Last, RefreshTrigger? Running2)>();
        bool startAgain = true;
        runner.Changed += (_, _) =>
        {
            seen.Add((runner.IsRunning, runner.RunsEnded, runner.LastRunTrigger, runner.RunningTrigger));
            if (!runner.IsRunning && startAgain)
            {
                startAgain = false;
                Assert.Equal(RefreshStartRefusal.None, runner.TryStart(RefreshTrigger.Timer));
            }
        };
        int gateEnded = -1;
        _gate.Changed += (_, _) =>
        {
            if (!_gate.IsRefreshing && gateEnded < 0)
                gateEnded = runner.RunsEnded;
        };

        runner.TryStart();
        Task first = runner.PendingRun!;
        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await first;

        Assert.Equal(1, gateEnded);
        Assert.Contains((false, 1, (RefreshTrigger?)RefreshTrigger.Pressed, (RefreshTrigger?)null), seen);
        Assert.True(runner.IsRunning);
        Assert.Equal(RefreshTrigger.Timer, runner.RunningTrigger);
        Assert.Equal(1, runner.RunsEnded);

        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await runner.PendingRun!;
        Assert.Equal(2, runner.RunsEnded);
        Assert.Null(runner.RunningTrigger);
        Assert.Equal(RefreshTrigger.Timer, runner.LastRunTrigger);
    }

    [Fact]
    public void TheCloseAsksOnlyWhileARunIsOutAndStopsAtItsYes()
    {
        RefreshAllRunner runner = NewRunner();
        Assert.True(((ICloseParticipant)runner).StopsWhenAgreed);
        Assert.False(runner.AsksBeforeClose);
        Assert.False(runner.HoldsClose);

        runner.TryStart();

        Assert.True(runner.AsksBeforeClose);
        Assert.True(runner.HoldsClose);
    }

    [Fact]
    public async Task IdleOrWithNoDialogsTheQuestionAgreesWithoutAsking()
    {
        Assert.True(await NewRunner().AskBeforeCloseAsync());
        Assert.Empty(_ui.Confirms);

        RefreshAllRunner noUi = NewRunner(noUi: true);
        noUi.TryStart();
        Assert.True(await noUi.AskBeforeCloseAsync());
    }

    [Fact]
    public async Task TheQuestionNamesTheRefreshAndHoldsTheGateWhileOpen()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        bool? heldWhileOpen = null;
        _ui.OnDialogOpen = () => heldWhileOpen = _gate.IsHeld;

        Assert.True(await runner.AskBeforeCloseAsync());

        var (title, message, button) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.CloseRefreshTitle, title);
        Assert.Equal(AppText.CloseRefreshBody, message);
        Assert.Equal(AppText.CloseRefreshButton, button);
        Assert.True(heldWhileOpen);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task ANoOrAFailedDialogLeavesTheRunGoing()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();

        _ui.ConfirmResult = false;
        Assert.False(await runner.AskBeforeCloseAsync());
        _ui.ConfirmResult = true;
        _ui.ConfirmException = new InvalidOperationException("no dialog");
        Assert.False(await runner.AskBeforeCloseAsync());

        Assert.True(runner.IsRunning);
        Assert.False(_refresher.Token.IsCancellationRequested);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task TheRunsLastChangedDuringTheClosesStopSaysItIsStoppingForTheClose()
    {
        RefreshAllRunner runner = NewRunner();
        _refresher.FinishWhenCancelled = true;
        var seenAtEnd = new List<bool>();
        runner.Changed += (_, _) =>
        {
            if (!runner.IsRunning)
                seenAtEnd.Add(runner.StoppingForClose);
        };
        runner.TryStart();
        Assert.False(runner.StoppingForClose);

        await runner.StopForCloseAsync();

        Assert.NotEmpty(seenAtEnd);
        Assert.All(seenAtEnd, Assert.True);
        Assert.False(runner.StoppingForClose);
    }

    [Fact]
    public async Task TheStopCancelsAndReturnsOnlyOnceTheRunHasEnded()
    {
        RefreshAllRunner runner = NewRunner();
        Assert.True(runner.StopForCloseAsync().IsCompletedSuccessfully);
        runner.TryStart();

        Task stop = runner.StopForCloseAsync();

        Assert.True(_refresher.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        Assert.True(runner.HoldsClose);
        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await stop;
        Assert.False(runner.HoldsClose);
        Assert.False(runner.IsRunning);
        Assert.True(_gate.CanStart);
        Assert.True(runner.LastRun!.Cancelled);
    }

    [Fact]
    public async Task DisposeCancelsWithoutWaitingAndLaterStartsAreUnavailable()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();

        runner.Dispose();

        Assert.True(_refresher.Token.IsCancellationRequested);
        Assert.True(runner.IsRunning);
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Unavailable);
        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await runner.PendingRun!;
        Assert.False(runner.IsRunning);
        Assert.True(_gate.CanStart);
        AssertRefusedWithNoChange(runner, RefreshStartRefusal.Unavailable);
    }

    [Fact]
    public void CancelWhenIdleDoesNothing()
    {
        RefreshAllRunner runner = NewRunner();

        runner.Cancel();

        Assert.False(runner.CancelRequested);
        Assert.Equal(0, _changes);
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        Assert.False(_refresher.Token.IsCancellationRequested);
        Assert.False(runner.CancelRequested);
    }

    [Fact]
    public async Task CancelStopsTheRunOnceAndItsEndClearsTheRequest()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        int changes = _changes;

        runner.Cancel();

        Assert.True(runner.CancelRequested);
        Assert.True(_refresher.Token.IsCancellationRequested);
        Assert.True(runner.IsRunning);
        Assert.Equal(changes + 1, _changes);
        runner.Cancel();
        Assert.Equal(changes + 1, _changes);

        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await runner.PendingRun!;
        Assert.False(runner.CancelRequested);
        Assert.False(runner.IsRunning);
        Assert.True(_gate.CanStart);

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        Assert.False(runner.CancelRequested);
        Assert.False(_refresher.Token.IsCancellationRequested);
    }

    // A handler of the run's end that starts the next run must get one the close's stop can still cancel and wait for
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunStartedFromTheEndsEventsIsTheOneTheStopCancelsAndAwaits(bool fromTheGate)
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        Task first = runner.PendingRun!;
        RefreshStartRefusal? again = null;
        void Restart()
        {
            if (again is null && !runner.IsRunning)
                again = runner.TryStart();
        }

        if (fromTheGate)
            _gate.Changed += (_, _) => Restart();
        else
            runner.Changed += (_, _) => Restart();

        _refresher.Finish(FakeFeedRefresher.Of("AS64500"));
        await first;

        Assert.Equal(RefreshStartRefusal.None, again);
        Assert.Equal(2, _refresher.Calls);
        Assert.True(runner.IsRunning);
        Assert.True(_gate.IsRefreshing);
        Assert.NotSame(first, runner.PendingRun);
        CancellationToken second = _refresher.Token;
        Assert.False(second.IsCancellationRequested);

        Task stop = runner.StopForCloseAsync();

        Assert.True(second.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await stop;
        Assert.False(runner.IsRunning);
        Assert.False(_gate.IsRefreshing);
        Assert.True(_gate.CanStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopFromTheStartsEventsCancelsTheNewRunAndAwaitsItNotTheLastOne(bool fromTheGate)
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        _refresher.Finish(FakeFeedRefresher.Of());
        await runner.PendingRun!;
        Task? stop = null;
        bool? pendingAtStop = null;
        void Stop()
        {
            if (stop is null && runner.IsRunning)
            {
                pendingAtStop = runner.PendingRun is { IsCompleted: false };
                stop = runner.StopForCloseAsync();
            }
        }

        if (fromTheGate)
            _gate.Changed += (_, _) => Stop();
        else
            runner.Changed += (_, _) => Stop();

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());

        Assert.NotNull(stop);
        Assert.True(pendingAtStop);
        Assert.Equal(2, _refresher.Calls);
        Assert.True(_refresher.Token.IsCancellationRequested);
        Assert.True(runner.CancelRequested);
        Assert.False(stop!.IsCompleted);
        Assert.True(runner.HoldsClose);
        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await stop;
        Assert.False(runner.IsRunning);
        Assert.False(runner.HoldsClose);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task TheCloseStillHoldsWhenTheStoppedRunRaisesItsLastChanged()
    {
        RefreshAllRunner runner = NewRunner();
        runner.TryStart();
        bool? holdsAtTheEnd = null;
        runner.Changed += (_, _) =>
        {
            if (!runner.IsRunning)
                holdsAtTheEnd = runner.HoldsClose;
        };

        Task stop = runner.StopForCloseAsync();
        _refresher.Finish(FakeFeedRefresher.Cancelled());
        await stop;

        Assert.True(holdsAtTheEnd);
        Assert.False(runner.HoldsClose);
    }
}
