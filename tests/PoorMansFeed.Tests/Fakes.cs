using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using GraphiteUi;

namespace PoorMansFeed.Tests;

/// <summary>
/// Scripted core service. Every result is settable. A gate (CheckGate, UpdateGate, SearchGate, ExportGate) holds the
/// NEXT call of that kind until its TaskCompletionSource completes, and honours the call's cancellation token.
/// </summary>
internal sealed class FakeIpInfoService : IIpInfoService
{
    public string DataFolder { get; set; } = Path.Combine(Path.GetTempPath(), "ipinfo-fake-data");
    private UserSettings _settings = new();

    /// <summary>
    /// The settings; each read of the getter is counted in <see cref="SettingsReads"/>, and throws
    /// <see cref="SettingsFailure"/> when it is set.
    /// </summary>
    public UserSettings Settings
    {
        get
        {
            SettingsReads++;
            if (SettingsFailure is not null)
                throw SettingsFailure;
            return _settings;
        }
        set => _settings = value;
    }

    public int SettingsReads { get; private set; }

    /// <summary>When set, what the <see cref="Settings"/> getter throws.</summary>
    public Exception? SettingsFailure { get; set; }
    public bool HasToken { get; set; }
    public DatasetInfo? Dataset { get; set; }
    public ExceptionList Exceptions { get; set; } = ExceptionList.Empty;

    public StartupResult StartupResult { get; set; } = new(StartupFailure.None, null, false);
    public StartupResult StartFreshResult { get; set; } = new(StartupFailure.None, null, false);
    public StartupResult StartWithoutOldExceptionsResult { get; set; } = new(StartupFailure.None, null, false);
    /// <summary>Runs inside a Start fresh, before it answers; a test can read the view model's state from here.</summary>
    public Action? OnStartFresh { get; set; }
    /// <summary>When set, what a Start fresh throws.</summary>
    public Exception? StartFreshException { get; set; }
    public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckOutcome.UpToDate);
    public UpdateResult UpdateResult { get; set; } = new(UpdateFailure.None, false, null);
    public Func<string, int, SearchResult> SearchFunc { get; set; } = (_, _) => new SearchResult(SearchOutcome.NoResults, null, [], 0);
    public ExportPlan ExportPlan { get; set; } = new(0, "", []);
    public ExportResult ExportResult { get; set; } = new(ExportFailure.None, [], 0, 0);
    public string ResultsText { get; set; } = "";
    public bool SaveTokenResult { get; set; } = true;
    public bool ClearTokenResult { get; set; } = true;
    public bool SaveSettingsResult { get; set; } = true;
    public ExceptionsSaveOutcome SaveExceptionsResult { get; set; } = ExceptionsSaveOutcome.Saved;
    /// <summary>When set, an exceptions save waits for it before it returns, to test what happens meanwhile.</summary>
    public TaskCompletionSource? SaveExceptionsPending { get; set; }

    /// <summary>Thrown by the next check or search, to test defensive handling.</summary>
    public Exception? CheckException { get; set; }
    public Exception? SearchException { get; set; }

    public TaskCompletionSource? CheckGate { get; set; }
    public TaskCompletionSource? UpdateGate { get; set; }
    public TaskCompletionSource? SearchGate { get; set; }
    public TaskCompletionSource? ExportGate { get; set; }
    public TaskCompletionSource? SaveTokenGate { get; set; }
    public TaskCompletionSource? ClearTokenGate { get; set; }
    /// <summary>When set, the next start-up step (a launch, a Start fresh or a Start without exceptions) is held until it completes.</summary>
    public TaskCompletionSource? InitializeGate { get; set; }
    public TaskCompletionSource? StartFreshGate { get; set; }
    public TaskCompletionSource? StartWithoutOldExceptionsGate { get; set; }

    /// <summary>Runs when an update starts, before its gate; a test can push progress from here.</summary>
    public Action<IProgress<UpdateProgress>?>? OnUpdateStarted { get; set; }

    /// <summary>The progress object the view model passed to the most recent update.</summary>
    public IProgress<UpdateProgress>? LastProgress { get; private set; }

    public int CheckCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int SearchCalls { get; private set; }
    public int PrepareExportCalls { get; private set; }
    public int ExportCalls { get; private set; }
    public int ResultsTextCalls { get; private set; }
    public int SaveTokenCalls { get; private set; }
    public int ClearTokenCalls { get; private set; }
    public int SaveSettingsCalls { get; private set; }
    public int SaveExceptionsCalls { get; private set; }
    public int OverlapCalls { get; private set; }
    public int StartFreshCalls { get; private set; }
    public int StartWithoutOldExceptionsCalls { get; private set; }

    public Func<IReadOnlyList<CidrBlock>, OverlapResult>? OverlapFunc { get; set; }

    public IReadOnlyList<CidrBlock>? LastOverlapBlocks { get; private set; }

    public string? LastSearchInput { get; private set; }
    public int LastSearchLimit { get; private set; }
    public SearchQuery? LastExportQuery { get; private set; }
    public IReadOnlyCollection<string>? LastIncluded { get; private set; }
    public SearchQuery? LastResultsQuery { get; private set; }
    public string? SavedToken { get; private set; }
    public UserSettings? SavedSettings { get; private set; }
    public IReadOnlyList<ExceptionEntry>? SavedExceptions { get; private set; }

    public int InitializeCalls { get; private set; }

    /// <summary>Whether the token each start-up step was given had been cancelled by the time the step returned.</summary>
    public List<bool> StartStepTokensCancelled { get; } = [];

    public Task<StartupResult> InitializeAsync(CancellationToken ct)
    {
        InitializeCalls++;
        TaskCompletionSource? gate = InitializeGate;
        InitializeGate = null;
        return gate is null ? Task.FromResult(StartupResult) : HeldAsync(gate, () => StartupResult, ct);
    }

    public Task<StartupResult> StartFreshAsync(CancellationToken ct)
    {
        StartFreshCalls++;
        OnStartFresh?.Invoke();
        if (StartFreshException is { } failure)
            throw failure;
        TaskCompletionSource? gate = StartFreshGate;
        StartFreshGate = null;
        return gate is null ? Task.FromResult(StartFreshResult) : HeldAsync(gate, () => StartFreshResult, ct);
    }

    public Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct)
    {
        StartWithoutOldExceptionsCalls++;
        TaskCompletionSource? gate = StartWithoutOldExceptionsGate;
        StartWithoutOldExceptionsGate = null;
        return gate is null
            ? Task.FromResult(StartWithoutOldExceptionsResult)
            : HeldAsync(gate, () => StartWithoutOldExceptionsResult, ct);
    }

    /// <summary>
    /// When set, a held step whose token was cancelled by its release throws OperationCanceledException, as the real
    /// start methods do for a cancel that lands before they observe the token on entry under their gate.
    /// </summary>
    public bool HeldStepThrowsWhenCancelled { get; set; }

    /// <summary>How many held steps threw OperationCanceledException.</summary>
    public int HeldStepsCancelled { get; private set; }

    // The real start methods observe the token once, on entry under their gate: a cancel before that throws, and a
    // cancel after it lets the step run to its end. Released, a held step does the one or the other.
    private async Task<StartupResult> HeldAsync(TaskCompletionSource gate, Func<StartupResult> result, CancellationToken ct)
    {
        await gate.Task;
        StartStepTokensCancelled.Add(ct.IsCancellationRequested);
        if (HeldStepThrowsWhenCancelled && ct.IsCancellationRequested)
        {
            HeldStepsCancelled++;
            throw new OperationCanceledException(ct);
        }

        return result();
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        CheckCalls++;
        if (CheckException is { } failure)
        {
            CheckException = null;
            throw failure;
        }

        TaskCompletionSource? gate = CheckGate;
        CheckGate = null;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }

        return CheckResult;
    }

    public async Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        UpdateCalls++;
        LastProgress = progress;
        OnUpdateStarted?.Invoke(progress);
        TaskCompletionSource? gate = UpdateGate;
        UpdateGate = null;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        if (UpdateResult.Failure == UpdateFailure.None && !UpdateResult.AlreadyCurrent && UpdateResult.Dataset is not null)
        {
            Dataset = UpdateResult.Dataset;
        }

        return UpdateResult;
    }

    public async Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct)
    {
        SearchCalls++;
        LastSearchInput = input;
        LastSearchLimit = displayLimit;
        if (SearchException is { } failure)
        {
            SearchException = null;
            throw failure;
        }

        TaskCompletionSource? gate = SearchGate;
        SearchGate = null;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }

        return SearchFunc(input, displayLimit);
    }

    public Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct)
    {
        OverlapCalls++;
        LastOverlapBlocks = blocks.ToArray();
        if (Dataset is null)
            return Task.FromResult(new OverlapResult(OverlapOutcome.NoDataset, [], null));

        if (OverlapFunc is not null)
            return Task.FromResult(OverlapFunc(blocks));

        IEnumerable<CidrBlock> asked = Settings.IPv6Enabled ? blocks : blocks.Where(block => !block.IsIpv6);
        var overlaps = asked.Select(block => new NetworkOverlap(block, [])).ToList();
        return Task.FromResult(new OverlapResult(OverlapOutcome.Found, overlaps, Dataset));
    }

    public Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct)
    {
        PrepareExportCalls++;
        return Task.FromResult(ExportPlan);
    }

    public async Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct)
    {
        ExportCalls++;
        LastExportQuery = query;
        LastIncluded = includedLargeNetworks.ToArray();
        TaskCompletionSource? gate = ExportGate;
        ExportGate = null;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }

        return ExportResult;
    }

    public Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct)
    {
        ResultsTextCalls++;
        LastResultsQuery = query;
        return Task.FromResult(ResultsText);
    }

    public async Task<bool> SaveTokenAsync(string token)
    {
        SaveTokenCalls++;
        SavedToken = token;
        TaskCompletionSource? gate = SaveTokenGate;
        SaveTokenGate = null;
        if (gate is not null)
        {
            await gate.Task;
        }

        if (SaveTokenResult)
        {
            HasToken = true;
        }

        return SaveTokenResult;
    }

    public async Task<bool> ClearTokenAsync()
    {
        ClearTokenCalls++;
        TaskCompletionSource? gate = ClearTokenGate;
        ClearTokenGate = null;
        if (gate is not null)
        {
            await gate.Task;
        }

        if (ClearTokenResult)
        {
            HasToken = false;
        }

        return ClearTokenResult;
    }

    public Task<bool> SaveSettingsAsync(UserSettings settings)
    {
        SaveSettingsCalls++;
        SavedSettings = settings;
        if (SaveSettingsResult)
        {
            Settings = settings;
        }

        return Task.FromResult(SaveSettingsResult);
    }

    public Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries)
    {
        SaveExceptionsCalls++;
        SavedExceptions = entries;
        return SaveExceptionsPending is { } pending ? SaveExceptionsAfterAsync(pending) : Task.FromResult(FinishSaveExceptions(entries));

        async Task<ExceptionsSaveOutcome> SaveExceptionsAfterAsync(TaskCompletionSource gate)
        {
            await gate.Task;
            return FinishSaveExceptions(entries);
        }
    }

    private ExceptionsSaveOutcome FinishSaveExceptions(IReadOnlyList<ExceptionEntry> entries)
    {
        if (SaveExceptionsResult != ExceptionsSaveOutcome.Failed)
        {
            Exceptions = ExceptionList.Create(entries);
        }

        return SaveExceptionsResult;
    }

    public void Dispose()
    {
    }
}

/// <summary>Scripted window services. Records what the view model asked for; never opens a window.</summary>
internal sealed class FakeUi : IUiServices
{
    /// <summary>The answer to every confirmation.</summary>
    public bool ConfirmResult { get; set; } = true;
    /// <summary>Thrown by the next confirmation, after it is recorded, as a dialog that fails to show.</summary>
    public Exception? ConfirmException { get; set; }

    /// <summary>The token the token dialog returns; null = cancelled.</summary>
    public string? TokenToReturn { get; set; }

    /// <summary>When set, answers the token dialog in place of TokenToReturn, so a test can hold it or open a real window.</summary>
    public Func<TokenPromptReason, Task<string?>>? TokenPrompt { get; set; }

    /// <summary>The keys the AbuseIPDB key dialog returns, one per prompt; once empty, every prompt is cancelled.</summary>
    public Queue<string?> AbuseKeyAnswers { get; } = new();

    /// <summary>Answers the large-range dialog; the default cancels it.</summary>
    public Func<LargeRangePrompt, IReadOnlyList<string>?> LargeRangeAnswer { get; set; } = _ => null;

    /// <summary>When set, runs as the new addresses dialog, so a test can hold it open or make it fail; unset, it closes at once.</summary>
    public Func<NewAddressesPrompt, Task>? NewAddressesAnswer { get; set; }

    /// <summary>Runs while a dialog is "open", before it answers.</summary>
    public Action? OnDialogOpen { get; set; }

    /// <summary>When set, the AbuseIPDB key dialog fails to open after the prompt is recorded.</summary>
    public bool FailAbuseKeyPrompt { get; set; }

    /// <summary>The folder the folder picker returns; null = cancelled.</summary>
    public string? FolderToReturn { get; set; }

    /// <summary>When set, the folder picker fails to open after the prompt is recorded.</summary>
    public bool FailFolderPicker { get; set; }

    /// <summary>The start folder of each folder picker, in order.</summary>
    public List<string?> FolderPrompts { get; } = [];

    public bool FailClipboard { get; set; }
    public bool FailOpenFolder { get; set; }
    public bool FailOpenWebPage { get; set; }

    public string? ClipboardText { get; private set; }
    public List<string> OpenedFolders { get; } = [];
    public List<string> OpenedFiles { get; } = [];
    public List<WebPage> OpenedPages { get; } = [];
    public List<(string Title, string Message)> ClosingNotices { get; } = [];
    public int CloseCalls { get; private set; }
    public List<(string Title, string Message, string ConfirmText)> Confirms { get; } = [];
    /// <summary>When set, the next confirmation stays open until it completes, then answers ConfirmResult.</summary>
    public TaskCompletionSource? ConfirmGate { get; set; }
    public List<TokenPromptReason> TokenPrompts { get; } = [];

    /// <summary>Why each AbuseIPDB key prompt was asked, in order.</summary>
    public List<AbuseKeyAsk> AbuseKeyPrompts { get; } = [];
    public List<LargeRangePrompt> LargeRangePrompts { get; } = [];
    public List<NewAddressesPrompt> NewAddressesPrompts { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirms.Add((title, message, confirmText));
        if (ConfirmException is { } failure)
        {
            ConfirmException = null;
            throw failure;
        }

        OnDialogOpen?.Invoke();
        TaskCompletionSource? gate = ConfirmGate;
        ConfirmGate = null;
        return gate is null ? Task.FromResult(ConfirmResult) : AnswerWhenReleasedAsync(gate);
    }

    private async Task<bool> AnswerWhenReleasedAsync(TaskCompletionSource gate)
    {
        await gate.Task;
        return ConfirmResult;
    }

    public Task CopyTextAsync(string text)
    {
        if (FailClipboard)
        {
            throw new InvalidOperationException("clipboard unavailable");
        }

        ClipboardText = text;
        return Task.CompletedTask;
    }

    public Task OpenFolderAsync(string path)
    {
        if (FailOpenFolder)
        {
            throw new InvalidOperationException("no folder");
        }

        OpenedFolders.Add(path);
        return Task.CompletedTask;
    }

    public Task OpenFileAsync(string path)
    {
        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }

    public Task OpenWebPageAsync(WebPage page)
    {
        if (FailOpenWebPage)
        {
            throw new InvalidOperationException("no browser");
        }

        OpenedPages.Add(page);
        return Task.CompletedTask;
    }

    public Task ShowClosingNoticeAsync(string title, string message)
    {
        ClosingNotices.Add((title, message));
        OnDialogOpen?.Invoke();
        return Task.CompletedTask;
    }

    public void CloseApplication() => CloseCalls++;

    public Task<string?> PromptForTokenAsync(TokenPromptReason reason)
    {
        TokenPrompts.Add(reason);
        OnDialogOpen?.Invoke();
        return TokenPrompt is { } prompt ? prompt(reason) : Task.FromResult(TokenToReturn);
    }

    public Task<string?> PromptForAbuseIpDbKeyAsync(AbuseKeyAsk why)
    {
        AbuseKeyPrompts.Add(why);
        if (FailAbuseKeyPrompt)
        {
            throw new InvalidOperationException("dialog failed");
        }

        OnDialogOpen?.Invoke();
        return Task.FromResult(AbuseKeyAnswers.TryDequeue(out string? key) ? key : null);
    }

    public Task<string?> ChooseFolderAsync(string? startFolder)
    {
        FolderPrompts.Add(startFolder);
        if (FailFolderPicker)
        {
            throw new InvalidOperationException("picker failed");
        }

        OnDialogOpen?.Invoke();
        return Task.FromResult(FolderToReturn);
    }

    public Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt)
    {
        LargeRangePrompts.Add(prompt);
        OnDialogOpen?.Invoke();
        return Task.FromResult(LargeRangeAnswer(prompt));
    }

    public Task ShowNewAddressesAsync(NewAddressesPrompt prompt)
    {
        NewAddressesPrompts.Add(prompt);
        OnDialogOpen?.Invoke();
        return NewAddressesAnswer is { } answer ? answer(prompt) : Task.CompletedTask;
    }
}

/// <summary>
/// The download accounting in memory. Every call to TryRecordAnswer is recorded in Writes, a failed one too; with
/// FailWrites it returns false and Current stays as it was, otherwise Current becomes Current.After(...).
/// </summary>
internal sealed class FakeAbuseAccounting : IAbuseAccounting
{
    public AbuseAccounting Current { get; set; } = AbuseAccounting.Empty;

    public bool IsAvailable { get; set; } = true;

    public bool FailWrites { get; set; }

    public List<(AbuseRateLimit RateLimit, DateTimeOffset AnsweredAt, bool CountDownload)> Writes { get; } = [];

    public bool TryRecordAnswer(AbuseRateLimit rateLimit, DateTimeOffset answeredAt, bool countDownload)
    {
        Writes.Add((rateLimit, answeredAt, countDownload));
        if (FailWrites)
            return false;

        Current = Current.After(rateLimit, answeredAt, countDownload);
        return true;
    }
}

/// <summary>
/// Scripted AbuseIPDB service. Each check takes the next queued result (none queued throws); CheckGate holds the NEXT
/// check until it completes, and honours the call's cancellation token; CheckHoldIgnoringCancel holds it without the
/// token, so a check called off meanwhile still returns its answer, as a real one whose answer was already in does. A
/// save takes the next queued result (none = Saved); SaveGate holds the NEXT save, after its key is recorded, until it
/// completes.
/// </summary>
internal sealed class FakeAbuseIpDbService : IAbuseIpDbService
{
    public bool HasKey { get; set; }

    public Queue<AbuseCheckResult> CheckResults { get; } = new();
    public List<SearchQuery> CheckQueries { get; } = [];
    public int CheckCalls => CheckQueries.Count;
    public TaskCompletionSource? CheckGate { get; set; }
    public TaskCompletionSource? CheckHoldIgnoringCancel { get; set; }

    /// <summary>Thrown once by the next check, to test defensive handling.</summary>
    public Exception? CheckException { get; set; }

    public Queue<AbuseKeySave> SaveResults { get; } = new();
    public List<string> SavedKeys { get; } = [];
    public TaskCompletionSource? SaveGate { get; set; }

    public async Task<AbuseKeySave> SaveKeyAsync(string key)
    {
        SavedKeys.Add(key);
        Calls.Add(nameof(SaveKeyAsync));
        TaskCompletionSource? gate = SaveGate;
        SaveGate = null;
        if (gate is not null)
        {
            await gate.Task;
        }

        AbuseKeySave result = SaveResults.TryDequeue(out AbuseKeySave queued) ? queued : AbuseKeySave.Saved;
        if (result == AbuseKeySave.Saved)
        {
            HasKey = true;
        }

        return result;
    }

    /// <summary>Dequeued by each clear; none queued means the key is removed.</summary>
    public Queue<bool> ClearKeyResults { get; } = new();
    public int ClearKeyCalls { get; private set; }

    public Task<bool> ClearKeyAsync()
    {
        ClearKeyCalls++;
        Calls.Add(nameof(ClearKeyAsync));
        bool result = !ClearKeyResults.TryDequeue(out bool queued) || queued;
        HasKey = !result;
        return Task.FromResult(result);
    }

    public async Task<AbuseCheckResult> CheckAsync(SearchQuery query, CancellationToken ct)
    {
        CheckQueries.Add(query);
        Calls.Add(nameof(CheckAsync));
        if (CheckException is { } failure)
        {
            CheckException = null;
            throw failure;
        }

        TaskCompletionSource? gate = CheckGate;
        CheckGate = null;
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }

        TaskCompletionSource? hold = CheckHoldIgnoringCancel;
        CheckHoldIgnoringCancel = null;
        if (hold is not null)
        {
            await hold.Task;
        }

        return CheckResults.TryDequeue(out AbuseCheckResult? result)
            ? result
            : throw new InvalidOperationException("no check result queued");
    }

    /// <summary>Member names in call order: the list calls, the check and the key calls.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Each held once: the next call of its member awaits it with its own token, then the gate is cleared.</summary>
    public TaskCompletionSource? StatusGate { get; set; }
    public TaskCompletionSource? DownloadGate { get; set; }

    /// <summary>When set, the next download awaits DownloadGate without its token, as a merge already under way does;
    /// then it is cleared.</summary>
    public bool DownloadIgnoresToken { get; set; }
    public TaskCompletionSource? ClearGate { get; set; }
    public TaskCompletionSource? FeedGate { get; set; }
    public TaskCompletionSource? EmptyGate { get; set; }
    public TaskCompletionSource? NetworksGate { get; set; }

    /// <summary>The token each call was given, in call order.</summary>
    public List<CancellationToken> StatusTokens { get; } = [];
    public List<CancellationToken> DownloadTokens { get; } = [];
    public List<CancellationToken> ClearTokens { get; } = [];
    public List<CancellationToken> FeedTokens { get; } = [];
    public List<CancellationToken> EmptyTokens { get; } = [];
    public List<CancellationToken> NetworksTokens { get; } = [];

    private static async Task TakeGateAsync(TaskCompletionSource? gate, CancellationToken ct)
    {
        if (gate is not null)
        {
            await gate.Task.WaitAsync(ct);
        }
    }

    /// <summary>Dequeued by each status call; an empty queue gives a missing list.</summary>
    public Queue<AbuseListStatus> StatusResults { get; } = new();
    public int StatusCalls { get; private set; }

    /// <summary>Thrown once by the next status call, after its gate, to test defensive handling.</summary>
    public Exception? StatusException { get; set; }

    public Queue<AbuseDownloadResult> DownloadResults { get; } = new();
    public int DownloadCalls { get; private set; }

    /// <summary>Dequeued by each clear; an empty queue gives None.</summary>
    public Queue<AbuseIpDbFailure> ClearResults { get; } = new();
    public int ClearCalls { get; private set; }

    public async Task<AbuseListStatus> GetStatusAsync(CancellationToken ct)
    {
        StatusCalls++;
        StatusTokens.Add(ct);
        Calls.Add(nameof(GetStatusAsync));
        TaskCompletionSource? gate = StatusGate;
        StatusGate = null;
        await TakeGateAsync(gate, ct);
        if (StatusException is { } failure)
        {
            StatusException = null;
            throw failure;
        }

        return StatusResults.TryDequeue(out AbuseListStatus? result)
            ? result
            : new AbuseListStatus(AbuseListState.Missing, 0, 0, null, null, null, 0, null);
    }

    /// <summary>What each new-address call gives: by default the set isn't known and holds nothing.</summary>
    public AbuseNewAddresses NewAddresses { get; set; } = new(AbuseNewAddressesFailure.NotKnown, [], null);

    /// <summary>Held once, like StatusGate: the next new-address call awaits it with its own token, then it is cleared.</summary>
    public TaskCompletionSource? NewAddressesGate { get; set; }
    public List<CancellationToken> NewAddressesTokens { get; } = [];

    /// <summary>When set, every new-address call throws it after its gate.</summary>
    public Exception? NewAddressesException { get; set; }

    public async Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct)
    {
        NewAddressesTokens.Add(ct);
        Calls.Add(nameof(GetNewAddressesAsync));
        TaskCompletionSource? gate = NewAddressesGate;
        NewAddressesGate = null;
        await TakeGateAsync(gate, ct);
        if (NewAddressesException is { } failure)
        {
            throw failure;
        }

        return NewAddresses;
    }

    public async Task<AbuseDownloadResult> DownloadBlacklistAsync(CancellationToken ct)
    {
        DownloadCalls++;
        DownloadTokens.Add(ct);
        Calls.Add(nameof(DownloadBlacklistAsync));
        TaskCompletionSource? gate = DownloadGate;
        DownloadGate = null;
        bool ignoresToken = DownloadIgnoresToken;
        DownloadIgnoresToken = false;
        await TakeGateAsync(gate, ignoresToken ? CancellationToken.None : ct);
        return DownloadResults.TryDequeue(out AbuseDownloadResult? result)
            ? result
            : throw new InvalidOperationException("no download result queued");
    }

    public async Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct)
    {
        ClearCalls++;
        ClearTokens.Add(ct);
        Calls.Add(nameof(ClearListAsync));
        TaskCompletionSource? gate = ClearGate;
        ClearGate = null;
        await TakeGateAsync(gate, ct);
        return ClearResults.TryDequeue(out AbuseIpDbFailure result) ? result : AbuseIpDbFailure.None;
    }

    /// <summary>Dequeued by each feed write; an empty queue throws, like a download with nothing queued.</summary>
    public Queue<AbuseFeedWriteResult> FeedResults { get; } = new();
    public int FeedCalls { get; private set; }

    public async Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct)
    {
        FeedCalls++;
        FeedTokens.Add(ct);
        Calls.Add(nameof(WriteFeedAsync));
        TaskCompletionSource? gate = FeedGate;
        FeedGate = null;
        await TakeGateAsync(gate, ct);
        return FeedResults.TryDequeue(out AbuseFeedWriteResult? result)
            ? result
            : throw new InvalidOperationException("no feed result queued");
    }

    /// <summary>Dequeued by each Empty feed; an empty queue throws, like a feed write with nothing queued.</summary>
    public Queue<AbuseFeedWriteResult> EmptyResults { get; } = new();
    public int EmptyCalls { get; private set; }

    public async Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct)
    {
        EmptyCalls++;
        EmptyTokens.Add(ct);
        Calls.Add(nameof(EmptyFeedAsync));
        TaskCompletionSource? gate = EmptyGate;
        EmptyGate = null;
        await TakeGateAsync(gate, ct);
        return EmptyResults.TryDequeue(out AbuseFeedWriteResult? result)
            ? result
            : throw new InvalidOperationException("no empty result queued");
    }

    /// <summary>What each offending-networks call gives when NetworksResults is empty; a list with no download until it is set.</summary>
    public AbuseNetworksResult NetworksResult { get; set; } =
        new(AbuseNetworksFailure.NoDownload, [], 0, [], 0, 0, 0, true, 30, null, null, 0);
    public int NetworksCalls { get; private set; }

    /// <summary>Dequeued by each offending-networks call first; NetworksResult when empty.</summary>
    public Queue<AbuseNetworksResult> NetworksResults { get; } = new();

    /// <summary>Reported to the call's progress, in order, before NetworksGate is awaited.</summary>
    public List<GroupingProgress> NetworksProgress { get; } = [];

    public async Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct)
    {
        NetworksCalls++;
        NetworksTokens.Add(ct);
        Calls.Add(nameof(FindOffendingNetworksAsync));
        foreach (GroupingProgress step in NetworksProgress)
        {
            progress?.Report(step);
        }

        TaskCompletionSource? gate = NetworksGate;
        NetworksGate = null;
        await TakeGateAsync(gate, ct);
        return NetworksResults.TryDequeue(out AbuseNetworksResult? result) ? result : NetworksResult;
    }
}

/// <summary>A progress sink that runs the handler inline, so a report has taken effect when Report returns.</summary>
internal sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>
/// Scripted stand-in for the Azure feed service. The cache, the update check and its gate are scripted; checking,
/// planning and writing go to a real AzureFeedService so a test gets real rows and real files.
/// </summary>
internal sealed class FakeAzureFeedService(AzureFeedService inner) : IAzureFeedService
{
    public AzureTagCatalogue? Cached { get; set; }

    public Func<AzureTagCatalogue?, AzureLoadResult> UpdateFunc { get; set; } =
        _ => throw new InvalidOperationException("No update was scripted.");

    /// <summary>Holds the next update check until it completes, and honours the call's cancellation token.</summary>
    public TaskCompletionSource? UpdateGate { get; set; }

    public int LoadCachedCalls { get; private set; }

    public int UpdateCalls { get; private set; }

    public int CheckCalls { get; private set; }

    /// <summary>"cache" and "update", in the order the view model called them.</summary>
    public List<string> Calls { get; } = [];

    public AzureTagCatalogue? LastCurrent { get; private set; }

    public AzureTagSelection? LastSelection { get; private set; }

    public Task<AzureTagCatalogue?> LoadCachedAsync(CancellationToken ct)
    {
        LoadCachedCalls++;
        Calls.Add("cache");
        return Task.FromResult(Cached);
    }

    public async Task<AzureLoadResult> CheckForUpdateAsync(AzureTagCatalogue? current, CancellationToken ct)
    {
        UpdateCalls++;
        Calls.Add("update");
        LastCurrent = current;
        if (UpdateGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return UpdateFunc(current);
    }

    public Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct)
    {
        CheckCalls++;
        LastSelection = selection;
        return inner.CheckAsync(catalogue, selection, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) => inner.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        inner.WriteAsync(plan, includedLargeRanges, ct);
}

/// <summary>
/// A scripted AWS service for view model tests. The load and update steps are scripted; checking, planning and
/// writing go to a real AwsFeedService so a test gets real rows and real files.
/// </summary>
internal sealed class FakeAwsFeedService(AwsFeedService inner) : IAwsFeedService
{
    public AwsRangeCatalogue? Cached { get; set; }

    public Func<AwsRangeCatalogue?, AwsLoadResult> UpdateFunc { get; set; } =
        _ => throw new InvalidOperationException("No update was scripted.");

    /// <summary>Holds the next update check until it completes, and honours the call's cancellation token.</summary>
    public TaskCompletionSource? UpdateGate { get; set; }

    public int LoadCachedCalls { get; private set; }

    public int UpdateCalls { get; private set; }

    public int CheckCalls { get; private set; }

    /// <summary>"cache" and "update", in the order the view model called them.</summary>
    public List<string> Calls { get; } = [];

    public AwsRangeCatalogue? LastCurrent { get; private set; }

    public AwsSelection? LastSelection { get; private set; }

    public Task<AwsRangeCatalogue?> LoadCachedAsync(CancellationToken ct)
    {
        LoadCachedCalls++;
        Calls.Add("cache");
        return Task.FromResult(Cached);
    }

    public async Task<AwsLoadResult> CheckForUpdateAsync(AwsRangeCatalogue? current, CancellationToken ct)
    {
        UpdateCalls++;
        Calls.Add("update");
        LastCurrent = current;
        if (UpdateGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return UpdateFunc(current);
    }

    public Task<CheckResult> CheckAsync(AwsRangeCatalogue catalogue, AwsSelection selection, CancellationToken ct)
    {
        CheckCalls++;
        LastSelection = selection;
        return inner.CheckAsync(catalogue, selection, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) => inner.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        inner.WriteAsync(plan, includedLargeRanges, ct);
}
