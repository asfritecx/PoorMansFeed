using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Core;

public interface IIpInfoService : IDisposable
{
    string DataFolder { get; }
    UserSettings Settings { get; }              // current, immutable snapshot
    bool HasToken { get; }
    DatasetInfo? Dataset { get; }               // null = no usable database
    ExceptionList Exceptions { get; }           // current; IsReadable false = no feed may be written

    Task<StartupResult> InitializeAsync(CancellationToken ct);

    /// <summary>Only after ConfigNewer, ConfigUnreadable, ConfigKeyMismatch or ConfigMissing. It first opens config.db
    /// again, and when that gives any other result, that is the result and nothing is replaced.</summary>
    Task<StartupResult> StartFreshAsync(CancellationToken ct);

    /// <summary>Only after ExceptionsImportUnreadable: moves exceptions.json aside and imports with no exceptions.
    /// A failed move gives ExceptionsImportUnreadable back; in any other state it changes nothing.</summary>
    Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct);

    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct);
    Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct);

    Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct);

    /// <summary>
    /// For each block, the IPinfo networks that overlap it, read-only through the database gate on the thread pool.
    /// An unusable database gives NoDataset unqueried; only the caller's cancellation throws.
    /// </summary>
    Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct);
    Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct);
    Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct);
    Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct);

    Task<bool> SaveTokenAsync(string token);    // false = couldn't be written
    Task<bool> ClearTokenAsync();
    Task<bool> SaveSettingsAsync(UserSettings settings);
    Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries);   // Saved, or Failed when it couldn't be saved (the list in use is unchanged)

    /// <summary>One kept rule id per list file name, from the saved first rule id. Cancel is seen once, before the
    /// call; the write itself isn't cancelled. No store open gives StorageError.</summary>
    Task<ModSecurityRuleIdsResult> AssignModSecurityRuleIdsAsync(IReadOnlyList<string> fileNames, CancellationToken ct);
}
