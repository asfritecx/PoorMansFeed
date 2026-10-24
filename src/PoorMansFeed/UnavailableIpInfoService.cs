using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>
/// Stands in for the real service when the data folder cannot be created. Start-up reports StorageUnavailable, so
/// nothing else is ever called by the view model; the other members return safe failures.
/// </summary>
internal sealed class UnavailableIpInfoService : IIpInfoService
{
    public string DataFolder => "";

    public UserSettings Settings { get; } = new();

    public bool HasToken => false;

    public DatasetInfo? Dataset => null;

    public ExceptionList Exceptions => ExceptionList.Unreadable;

    public Task<StartupResult> InitializeAsync(CancellationToken ct) =>
        Task.FromResult(new StartupResult(StartupFailure.StorageUnavailable, null, false));

    public Task<StartupResult> StartFreshAsync(CancellationToken ct) =>
        Task.FromResult(new StartupResult(StartupFailure.StorageUnavailable, null, false));

    public Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct) =>
        Task.FromResult(new StartupResult(StartupFailure.StorageUnavailable, null, false));

    public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct) =>
        Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.ServiceError));

    public Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct) =>
        Task.FromResult(new UpdateResult(UpdateFailure.ServiceError, false, null));

    public Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct) =>
        Task.FromResult(new SearchResult(SearchOutcome.Failed, null, [], 0));

    public Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct) =>
        Task.FromResult(new OverlapResult(OverlapOutcome.NoDataset, [], null));

    public Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct) =>
        Task.FromResult(new ExportPlan(0, "", []));

    public Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct) =>
        Task.FromResult(new ExportResult(ExportFailure.FolderUnavailable, [], 0, 0));

    public Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct) => Task.FromResult("");

    public Task<bool> SaveTokenAsync(string token) => Task.FromResult(false);

    public Task<bool> ClearTokenAsync() => Task.FromResult(false);

    public Task<bool> SaveSettingsAsync(UserSettings settings) => Task.FromResult(false);

    public Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries) => Task.FromResult(ExceptionsSaveOutcome.Failed);

    public Task<ModSecurityRuleIdsResult> AssignModSecurityRuleIdsAsync(IReadOnlyList<string> fileNames, CancellationToken ct)
        => Task.FromResult(new ModSecurityRuleIdsResult(ModSecurityRuleIdsFailure.StorageError, []));

    public void Dispose()
    {
    }
}
