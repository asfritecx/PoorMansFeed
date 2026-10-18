using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using GraphiteUi;

namespace PoorMansFeed.Tests;

/// <summary>
/// Scripted core service. Every result is settable. A gate (CheckGate, UpdateGate, SearchGate) holds the NEXT call
/// of that kind until its TaskCompletionSource completes, and honours the call's cancellation token.
/// </summary>
internal sealed class FakeIpInfoService : IIpInfoService
{
    public string DataFolder { get; set; } = Path.Combine(Path.GetTempPath(), "ipinfo-fake-data");
    public UserSettings Settings { get; set; } = new();
    public bool HasToken { get; set; }
    public DatasetInfo? Dataset { get; set; }

    public StartupResult StartupResult { get; set; } = new(StartupFailure.None, null, false);
    public UpdateCheckResult CheckResult { get; set; } = new(UpdateCheckOutcome.UpToDate);
    public UpdateResult UpdateResult { get; set; } = new(UpdateFailure.None, false, null);
    public Func<string, int, SearchResult> SearchFunc { get; set; } = (_, _) => new SearchResult(SearchOutcome.NoResults, null, [], 0);
    public ExportPlan ExportPlan { get; set; } = new(0, "", []);
    public ExportResult ExportResult { get; set; } = new(ExportFailure.None, [], 0, 0);
    public string ResultsText { get; set; } = "";
    public bool SaveTokenResult { get; set; } = true;
    public bool ClearTokenResult { get; set; } = true;
    public bool SaveSettingsResult { get; set; } = true;

    /// <summary>Thrown by the next check or search, to test defensive handling.</summary>
    public Exception? CheckException { get; set; }
    public Exception? SearchException { get; set; }

    public TaskCompletionSource? CheckGate { get; set; }
    public TaskCompletionSource? UpdateGate { get; set; }
    public TaskCompletionSource? SearchGate { get; set; }

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
    public int OverlapCalls { get; private set; }

    public Func<IReadOnlyList<CidrBlock>, OverlapResult>? OverlapFunc { get; set; }

    public IReadOnlyList<CidrBlock>? LastOverlapBlocks { get; private set; }

    public string? LastSearchInput { get; private set; }
    public int LastSearchLimit { get; private set; }
    public SearchQuery? LastExportQuery { get; private set; }
    public IReadOnlyCollection<string>? LastIncluded { get; private set; }
    public SearchQuery? LastResultsQuery { get; private set; }
    public string? SavedToken { get; private set; }
    public UserSettings? SavedSettings { get; private set; }

    public Task<StartupResult> InitializeAsync(CancellationToken ct) => Task.FromResult(StartupResult);

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

    public Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct)
    {
        ExportCalls++;
        LastExportQuery = query;
        LastIncluded = includedLargeNetworks.ToArray();
        return Task.FromResult(ExportResult);
    }

    public Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct)
    {
        ResultsTextCalls++;
        LastResultsQuery = query;
        return Task.FromResult(ResultsText);
    }

    public Task<bool> SaveTokenAsync(string token)
    {
        SaveTokenCalls++;
        SavedToken = token;
        if (SaveTokenResult)
        {
            HasToken = true;
        }

        return Task.FromResult(SaveTokenResult);
    }

    public Task<bool> ClearTokenAsync()
    {
        ClearTokenCalls++;
        if (ClearTokenResult)
        {
            HasToken = false;
        }

        return Task.FromResult(ClearTokenResult);
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

    public void Dispose()
    {
    }
}

/// <summary>Scripted window services. Records what the view model asked for; never opens a window.</summary>
internal sealed class FakeUi : IUiServices
{
    /// <summary>The answer to every confirmation.</summary>
    public bool ConfirmResult { get; set; } = true;

    /// <summary>The token the token dialog returns; null = cancelled.</summary>
    public string? TokenToReturn { get; set; }

    /// <summary>The settings the settings dialog returns; null = cancelled.</summary>
    public UserSettings? SettingsToReturn { get; set; }

    /// <summary>Answers the large-range dialog; the default cancels it.</summary>
    public Func<LargeRangePrompt, IReadOnlyList<string>?> LargeRangeAnswer { get; set; } = _ => null;

    /// <summary>Runs while a dialog is "open", before it answers.</summary>
    public Action? OnDialogOpen { get; set; }

    public bool FailClipboard { get; set; }
    public bool FailOpenFolder { get; set; }

    public string? ClipboardText { get; private set; }
    public List<string> OpenedFolders { get; } = [];
    public List<string> OpenedFiles { get; } = [];
    public List<(string Title, string Message)> ClosingNotices { get; } = [];
    public int CloseCalls { get; private set; }
    public List<(string Title, string Message, string ConfirmText)> Confirms { get; } = [];
    public List<TokenPromptReason> TokenPrompts { get; } = [];
    public List<(UserSettings Current, string DefaultFolder)> SettingsPrompts { get; } = [];
    public List<LargeRangePrompt> LargeRangePrompts { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirms.Add((title, message, confirmText));
        OnDialogOpen?.Invoke();
        return Task.FromResult(ConfirmResult);
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
        return Task.FromResult(TokenToReturn);
    }

    public Task<UserSettings?> EditSettingsAsync(UserSettings current, string defaultExportFolder)
    {
        SettingsPrompts.Add((current, defaultExportFolder));
        OnDialogOpen?.Invoke();
        return Task.FromResult(SettingsToReturn);
    }

    public Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt)
    {
        LargeRangePrompts.Add(prompt);
        OnDialogOpen?.Invoke();
        return Task.FromResult(LargeRangeAnswer(prompt));
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
