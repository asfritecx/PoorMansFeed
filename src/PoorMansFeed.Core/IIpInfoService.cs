namespace PoorMansFeed.Core;

public interface IIpInfoService : IDisposable
{
    string DataFolder { get; }
    UserSettings Settings { get; }              // current, immutable snapshot
    bool HasToken { get; }
    DatasetInfo? Dataset { get; }               // null = no usable database

    Task<StartupResult> InitializeAsync(CancellationToken ct);
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct);
    Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct);

    Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct);

    /// <summary>
    /// For each block, the IPinfo networks that overlap it, through the database gate on the thread pool.
    /// Read-only. A database that is not usable returns NoDataset and is not queried. Only the caller's
    /// cancellation throws.
    /// </summary>
    Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct);
    Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct);
    Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct);
    Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct);

    Task<bool> SaveTokenAsync(string token);    // false = couldn't be written
    Task<bool> ClearTokenAsync();
    Task<bool> SaveSettingsAsync(UserSettings settings);
}
