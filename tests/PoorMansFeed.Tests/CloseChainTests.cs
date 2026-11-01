using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class CloseChainTests
{
    private readonly List<string> _log = [];
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly LoggingRecords _records;

    public CloseChainTests()
    {
        _records = new LoggingRecords(_log);
        _ui.OnDialogOpen = () => _log.Add(_gate.IsHeld ? "feed-write asked (held)" : "feed-write asked");
    }

    [Fact]
    public async Task AsksEveryQuestionThenStopsEveryParticipantInOrder()
    {
        var settings = CloseChain.AskOnly(() => true, () => Logged("settings asked", true));
        var abuse = new LoggingParticipant("abuse", _log);
        var saved = new LoggingParticipant("saved", _log);
        var update = CloseChain.AskOnly(() => true, UpdateAskAsync);
        _records.InFlight = 1;
        var chain = new CloseChain([settings, abuse, saved], [update], _records, _ui, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["settings asked", "abuse asked", "abuse hold", "saved asked", "saved hold",
                      "feed-write asked (held)", "update asked", "update cancelled", "update stopped",
                      "abuse stop", "saved stop", "writes stopped"], _log);
        Assert.False(_gate.IsHeld);
        Assert.Equal(0, abuse.HoldsReleased);
        Assert.Equal(0, saved.HoldsReleased);

        async Task<bool> UpdateAskAsync()
        {
            _log.Add("update asked");
            _log.Add("update cancelled");
            await Task.Yield();
            _log.Add("update stopped");
            return true;
        }
    }

    [Fact]
    public async Task APageThatAsksSkipsTheFeedWriteQuestionEvenWhenItsWorkEndsDuringItsQuestion()
    {
        var abuse = new LoggingParticipant("abuse", _log) { Asks = true };
        abuse.OnAsk = () => abuse.Asks = false;
        _records.InFlight = 1;
        var chain = new CloseChain([abuse], [], _records, _ui, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Empty(_ui.Confirms);
        Assert.Equal(["abuse asked", "abuse hold", "abuse stop", "writes stopped"], _log);
    }

    [Fact]
    public async Task AParticipantStoppedAtItsYesIsNotStoppedAgainEvenWhenItsFlagChangesAfterTheStop()
    {
        var refresh = new LoggingParticipant("refresh", _log) { StopsWhenAgreed = true };
        refresh.OnStop = () => refresh.StopsWhenAgreed = false;
        var abuse = new LoggingParticipant("abuse", _log);
        var chain = new CloseChain([refresh, abuse], [], _records, _ui, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["refresh asked", "refresh hold", "refresh stop", "abuse asked", "abuse hold", "abuse stop",
                      "writes stopped"], _log);
    }

    [Fact]
    public async Task NoWriteInFlightAsksNoFeedWriteQuestion()
    {
        var chain = new CloseChain([new LoggingParticipant("abuse", _log)], [], _records, _ui, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Empty(_ui.Confirms);
    }

    [Fact]
    public async Task TheFeedWriteQuestionFailingToShowKeepsTheWindowOpen()
    {
        var abuse = new LoggingParticipant("abuse", _log);
        _records.InFlight = 1;
        _ui.ConfirmException = new InvalidOperationException("no dialog");
        var chain = new CloseChain([abuse], [], _records, _ui, _gate);

        Assert.False(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["abuse asked", "abuse hold"], _log);
        Assert.Equal(1, abuse.HoldsReleased);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task WithNoUiTheFeedWriteQuestionCountsAsYes()
    {
        _records.InFlight = 1;
        var chain = new CloseChain([], [], _records, null, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["writes stopped"], _log);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ANoFromAnyQuestionReturnsFalseAsksNothingAfterAndReleasesTheHolds(int refusing)
    {
        var first = new LoggingParticipant("first", _log);
        var second = new LoggingParticipant("second", _log);
        var last = new LoggingParticipant("last", _log);
        LoggingParticipant?[] byPosition = [first, second, null, last];
        if (byPosition[refusing] is { } participant)
            participant.Answer = false;
        else
            _ui.ConfirmResult = false;
        _records.InFlight = 1;
        var chain = new CloseChain([first, second], [last], _records, _ui, _gate);

        Assert.False(await chain.ConfirmAndStopAsync(() => false));

        string[] asked = ["first asked", "first hold", "second asked", "second hold", "feed-write asked (held)",
                          "last asked", "last hold"];
        int askedCount = refusing switch { 0 => 1, 1 => 3, 2 => 5, _ => 6 };
        Assert.Equal(asked.Take(askedCount), _log);
        Assert.Equal(refusing > 0 ? 1 : 0, first.HoldsReleased);
        Assert.Equal(refusing > 1 ? 1 : 0, second.HoldsReleased);
        Assert.Equal(0, last.HoldsTaken);
    }

    [Theory]
    [InlineData("first asked")]
    [InlineData("second asked")]
    [InlineData("feed-write asked (held)")]
    [InlineData("last asked")]
    [InlineData("first stop")]
    [InlineData("second stop")]
    [InlineData("last stop")]
    [InlineData("writes stopped")]
    public async Task TheWindowClosingUnderAnyStepEndsTheChainThere(string closingStep)
    {
        var first = new LoggingParticipant("first", _log);
        var second = new LoggingParticipant("second", _log);
        var last = new LoggingParticipant("last", _log);
        _records.InFlight = 1;
        var chain = new CloseChain([first, second], [last], _records, _ui, _gate);

        Assert.False(await chain.ConfirmAndStopAsync(() => _log.Contains(closingStep)));

        Assert.Equal(closingStep, _log[^1]);
        Assert.Equal(first.HoldsTaken, first.HoldsReleased);
        Assert.Equal(second.HoldsTaken, second.HoldsReleased);
        Assert.Equal(last.HoldsTaken, last.HoldsReleased);
    }

    [Fact]
    public void HoldsCloseWhileAnyParticipantHoldsOrAWriteIsInFlight()
    {
        var first = new LoggingParticipant("first", _log);
        var last = new LoggingParticipant("last", _log);
        var chain = new CloseChain([first], [last], _records, _ui, _gate);
        Assert.False(chain.HoldsClose);

        first.Holds = true;
        Assert.True(chain.HoldsClose);
        first.Holds = false;
        last.Holds = true;
        Assert.True(chain.HoldsClose);
        last.Holds = false;
        _records.InFlight = 1;
        Assert.True(chain.HoldsClose);
        _records.InFlight = 0;
        Assert.False(chain.HoldsClose);
        Assert.False(new CloseChain([], [], null, null, null).HoldsClose);
    }

    [Fact]
    public async Task IsRunningFromTheCallsStartUntilItEnds()
    {
        var answer = new TaskCompletionSource<bool>();
        var first = new LoggingParticipant("first", _log) { Pending = answer.Task };
        var chain = new CloseChain([first], [], _records, _ui, _gate);
        Assert.False(chain.IsRunning);

        Task<bool> running = chain.ConfirmAndStopAsync(() => false);
        Assert.True(chain.IsRunning);
        answer.SetResult(true);

        Assert.True(await running);
        Assert.False(chain.IsRunning);
    }

    [Fact]
    public async Task IsRunningClearsAfterAThrowAndTheHoldsAreReleased()
    {
        var first = new LoggingParticipant("first", _log);
        var second = new LoggingParticipant("second", _log) { Failure = new InvalidOperationException("broken") };
        var chain = new CloseChain([first, second], [], _records, _ui, _gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => chain.ConfirmAndStopAsync(() => false));

        Assert.False(chain.IsRunning);
        Assert.Equal(1, first.HoldsReleased);
    }

    [Fact]
    public async Task AnAskOnlyParticipantAsksAndHoldsNothingAndStopsNothing()
    {
        int asks = 0;
        bool busy = true;
        ICloseParticipant participant = CloseChain.AskOnly(() => busy, () => { asks++; return Task.FromResult(true); });

        Assert.True(participant.HoldsClose);
        busy = false;
        Assert.False(participant.HoldsClose);
        Assert.False(participant.AsksBeforeClose);
        Assert.False(participant.StopsWhenAgreed);
        Assert.True(CloseChain.AskOnly(() => true, () => Task.FromResult(true), stopsWhenAgreed: true).StopsWhenAgreed);
        Assert.Null(participant.HoldRunsForClose());
        Assert.True(participant.StopForCloseAsync().IsCompletedSuccessfully);
        Assert.Equal(0, asks);
        Assert.True(await participant.AskBeforeCloseAsync());
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task AParticipantThatStopsWhenAgreedIsStoppedBeforeTheNextIsAskedAndNotAgain()
    {
        var refresh = new LoggingParticipant("refresh", _log) { Holds = true, Asks = true, StopsWhenAgreed = true };
        var abuse = new LoggingParticipant("abuse", _log);
        var chain = new CloseChain([refresh, abuse], [], _records, _ui, _gate);

        Assert.True(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["refresh asked", "refresh hold", "refresh stop", "abuse asked", "abuse hold", "abuse stop",
                      "writes stopped"], _log);
    }

    [Fact]
    public async Task AParticipantStoppedWhenAgreedStaysStoppedAfterALaterNoButItsHoldIsReleased()
    {
        var refresh = new LoggingParticipant("refresh", _log) { StopsWhenAgreed = true };
        var abuse = new LoggingParticipant("abuse", _log) { Answer = false };
        var chain = new CloseChain([refresh, abuse], [], _records, _ui, _gate);

        Assert.False(await chain.ConfirmAndStopAsync(() => false));

        Assert.Equal(["refresh asked", "refresh hold", "refresh stop", "abuse asked"], _log);
        Assert.Equal(1, refresh.HoldsReleased);
    }

    [Fact]
    public async Task TheWindowClosingDuringAStopWhenAgreedEndsTheChainThere()
    {
        bool closed = false;
        var refresh = new LoggingParticipant("refresh", _log) { StopsWhenAgreed = true, OnStop = () => closed = true };
        var abuse = new LoggingParticipant("abuse", _log);
        var chain = new CloseChain([refresh, abuse], [], _records, _ui, _gate);

        Assert.False(await chain.ConfirmAndStopAsync(() => closed));

        Assert.Equal(["refresh asked", "refresh hold", "refresh stop"], _log);
    }

    private Task<bool> Logged(string entry, bool answer)
    {
        _log.Add(entry);
        return Task.FromResult(answer);
    }

    private sealed class LoggingParticipant(string name, List<string> log) : ICloseParticipant
    {
        public bool Holds { get; set; }
        public bool Asks { get; set; }
        public bool Answer { get; set; } = true;
        public Action? OnAsk { get; set; }
        public Task<bool>? Pending { get; set; }
        public Exception? Failure { get; set; }
        public int HoldsTaken { get; private set; }
        public int HoldsReleased { get; private set; }

        public bool HoldsClose => Holds;
        public bool AsksBeforeClose => Asks;
        public bool StopsWhenAgreed { get; set; }

        public async Task<bool> AskBeforeCloseAsync()
        {
            log.Add($"{name} asked");
            OnAsk?.Invoke();
            if (Failure is not null)
                throw Failure;
            return Pending is null ? Answer : await Pending;
        }

        public IDisposable? HoldRunsForClose()
        {
            log.Add($"{name} hold");
            HoldsTaken++;
            return new Release(() => HoldsReleased++);
        }

        public Action? OnStop { get; set; }

        public Task StopForCloseAsync()
        {
            log.Add($"{name} stop");
            OnStop?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed class LoggingRecords(List<string> log) : IFeedRecords
    {
        public int InFlight { get; set; }

        public FeedRecordResult Record(FeedRecordRequest request) => throw new NotSupportedException();
        public IDisposable EnterWrite(string feedKey) => throw new NotSupportedException();
        public int WritesInFlight => InFlight;

        public Task StopWritesAsync(CancellationToken ct)
        {
            log.Add("writes stopped");
            return Task.CompletedTask;
        }

        public Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<HistoryState> ClearHistoryAsync(CancellationToken ct) => throw new NotSupportedException();
        public SavedFeedsRead ReadSavedFeeds() => new(false, []);
        public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException();
        IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps) =>
            throw new NotSupportedException();
        bool IFeedRecords.SaveRefusedThisSession(string feedKey) => false;
    }
}
