using System.Reflection;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

/// <summary>The one AbuseIPDB key prompt that Check AbuseIPDB and the Settings page share.</summary>
public sealed class AbuseKeyPromptTests
{
    // A made-up key: nothing but the service's save may hold it
    private const string SentinelKey = "abuseipdb-sentinel-key-0123456789";

    private static (AbuseKeyPrompt Prompt, FakeUi Ui, FakeAbuseIpDbService Abuse, WorkGate Gate) NewPrompt()
    {
        var ui = new FakeUi();
        var abuse = new FakeAbuseIpDbService();
        var gate = new WorkGate();
        gate.SetStarted(true);
        return (new AbuseKeyPrompt(ui, abuse, gate), ui, abuse, gate);
    }

    [Fact]
    public async Task ASecondPromptWhileOneIsOpenIsBusyAndOpensNothing()
    {
        var (prompt, ui, abuse, _) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        AbuseKeyOutcome? second = null;
        bool openWhileShown = false;
        ui.OnDialogOpen = () =>
        {
            openWhileShown = prompt.IsOpen;
            Task<AbuseKeyOutcome> nested = prompt.AskAndSaveAsync(AbuseKeyAsk.Replace);
            Assert.True(nested.IsCompleted);
            second = nested.Result;
        };

        AbuseKeyOutcome first = await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing);

        Assert.Equal(AbuseKeyOutcome.Saved, first);
        Assert.Equal(AbuseKeyOutcome.Busy, second);
        Assert.True(openWhileShown);
        Assert.Equal([AbuseKeyAsk.Missing], ui.AbuseKeyPrompts);
        Assert.Single(abuse.SavedKeys);
        Assert.False(prompt.IsOpen);
    }

    [Fact]
    public async Task APromptAskedWhenTheFirstSaysItIsOpenIsBusy()
    {
        // IsOpenChanged is raised before the gate is held, so only IsOpen stops a handler opening a second prompt
        var (prompt, ui, _, _) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var nested = new List<AbuseKeyOutcome>();
        prompt.IsOpenChanged += async (_, _) =>
        {
            if (prompt.IsOpen && nested.Count == 0)
            {
                nested.Add(AbuseKeyOutcome.Saved);
                nested[0] = await prompt.AskAndSaveAsync(AbuseKeyAsk.Replace);
            }
        };

        Assert.Equal(AbuseKeyOutcome.Saved, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));

        Assert.Equal([AbuseKeyOutcome.Busy], nested);
        Assert.Equal([AbuseKeyAsk.Missing], ui.AbuseKeyPrompts);
    }

    [Fact]
    public async Task ABusyGateOpensNothing()
    {
        var (prompt, ui, abuse, gate) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);

        using (gate.Hold())
        {
            Assert.Equal(AbuseKeyOutcome.Busy, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));
        }

        gate.SetBusy(true);
        Assert.Equal(AbuseKeyOutcome.Busy, await prompt.AskAndSaveAsync(AbuseKeyAsk.Rejected));
        gate.SetBusy(false);
        gate.SetStarted(false);
        Assert.Equal(AbuseKeyOutcome.Busy, await prompt.AskAndSaveAsync(AbuseKeyAsk.Replace));

        Assert.Empty(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
        Assert.False(prompt.IsOpen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ACancelledOrBlankAnswerSavesNothing(string? answer)
    {
        var (prompt, ui, abuse, gate) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(answer);

        Assert.Equal(AbuseKeyOutcome.Cancelled, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));

        Assert.Single(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
        Assert.False(abuse.HasKey);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public async Task ASavedKeyIsNeverReturnedOrRaised()
    {
        var (prompt, ui, abuse, _) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var raised = new List<object?>();
        prompt.IsOpenChanged += (sender, e) => raised.Add(e);

        AbuseKeyOutcome outcome = await prompt.AskAndSaveAsync(AbuseKeyAsk.Replace);

        Assert.Equal(AbuseKeyOutcome.Saved, outcome);
        Assert.True(abuse.HasKey);
        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Equal(2, raised.Count);
        Assert.All(raised, e => Assert.Same(EventArgs.Empty, e));
        Assert.True(KeyNotHeldBy(prompt), "The prompt holds the typed key.");
    }

    [Fact]
    public async Task ADialogThatThrowsIsDialogFailed()
    {
        var (prompt, ui, abuse, gate) = NewPrompt();
        ui.FailAbuseKeyPrompt = true;
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);

        Assert.Equal(AbuseKeyOutcome.DialogFailed, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));

        Assert.Empty(abuse.SavedKeys);
        Assert.False(prompt.IsOpen);
        Assert.True(gate.CanStart);
    }

    [Theory]
    [InlineData(AbuseKeySave.NotAKey)]
    [InlineData(AbuseKeySave.Failed)]
    public async Task NotAKeyAndSaveFailedAreReported(AbuseKeySave save)
    {
        AbuseKeyOutcome expected = save == AbuseKeySave.NotAKey ? AbuseKeyOutcome.NotAKey : AbuseKeyOutcome.SaveFailed;
        var (prompt, ui, abuse, gate) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        abuse.SaveResults.Enqueue(save);

        Assert.Equal(expected, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));

        Assert.False(abuse.HasKey);
        Assert.False(prompt.IsOpen);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public async Task ASaveThatThrowsIsSaveFailed()
    {
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var gate = new WorkGate();
        gate.SetStarted(true);
        var prompt = new AbuseKeyPrompt(ui, new ThrowingSaveService(), gate);

        Assert.Equal(AbuseKeyOutcome.SaveFailed, await prompt.AskAndSaveAsync(AbuseKeyAsk.Missing));

        Assert.False(prompt.IsOpen);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public async Task TheGateIsHeldWhileThePromptIsOpen()
    {
        var (prompt, ui, abuse, gate) = NewPrompt();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        bool canStartInDialog = true;
        ui.OnDialogOpen = () => canStartInDialog = gate.CanStart;
        var saveGate = new TaskCompletionSource();
        abuse.SaveGate = saveGate;
        var openChanges = new List<bool>();
        prompt.IsOpenChanged += (_, _) => openChanges.Add(prompt.IsOpen);

        Task<AbuseKeyOutcome> asked = prompt.AskAndSaveAsync(AbuseKeyAsk.Missing);

        // Still saving: the gate stays held and the prompt open
        Assert.False(canStartInDialog);
        Assert.False(asked.IsCompleted);
        Assert.False(gate.CanStart);
        Assert.True(prompt.IsOpen);

        saveGate.SetResult();
        Assert.Equal(AbuseKeyOutcome.Saved, await asked);
        Assert.True(gate.CanStart);
        Assert.Equal([true, false], openChanges);
    }

    [Theory]
    [InlineData(AbuseKeyAsk.Missing)]
    [InlineData(AbuseKeyAsk.Rejected)]
    [InlineData(AbuseKeyAsk.Replace)]
    public async Task EachAskOpensItsOwnPromptText(AbuseKeyAsk why)
    {
        var (prompt, ui, _, _) = NewPrompt();

        await prompt.AskAndSaveAsync(why);

        Assert.Equal([why], ui.AbuseKeyPrompts);
        SecretPrompt shown = SecretPrompt.ForAbuseIpDb(why);
        (string title, string body) = why switch
        {
            AbuseKeyAsk.Missing => (AppText.AbuseKeyFirstTitle, AppText.AbuseKeyBody),
            AbuseKeyAsk.Rejected => (AppText.AbuseKeyReplaceTitle, AppText.AbuseKeyReplaceBody),
            _ => (AppText.AbuseKeySetTitle, AppText.AbuseKeySetBody),
        };
        Assert.Equal(new SecretPrompt(title, body, AppText.AbuseKeyBoxName, AppText.ShowKey, AppText.SaveKey), shown);
    }

    // No field or property of the prompt is a string, so the key can't be kept in one
    private static bool KeyNotHeldBy(object instance) =>
        instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .All(field => field.FieldType != typeof(string) || field.GetValue(instance) is not string text || !text.Contains(SentinelKey, StringComparison.Ordinal));

    private sealed class ThrowingSaveService : IAbuseIpDbService
    {
        public bool HasKey => false;
        public Task<AbuseKeySave> SaveKeyAsync(string key) => throw new InvalidOperationException("save failed");
        public Task<bool> ClearKeyAsync() => throw new NotSupportedException();
        public Task<AbuseCheckResult> CheckAsync(PoorMansFeed.Core.SearchQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseListStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseDownloadResult> DownloadBlacklistAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
