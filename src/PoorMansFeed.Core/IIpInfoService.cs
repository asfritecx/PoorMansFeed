using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core;

public interface IIpInfoService : IDisposable
{
    string DataFolder { get; }
    UserSettings Settings { get; }              // current, immutable snapshot
    bool HasToken { get; }
    DatasetInfo? Dataset { get; }               // null = no usable database
    ExceptionList Exceptions { get; }           // current; IsReadable false = no feed may be written

    Task<StartupResult> InitializeAsync(CancellationToken ct);

    /// <summary>Only after ConfigNewer, ConfigUnreadable, ConfigKeyMismatch or ConfigMissing: first opens config.db again,
    /// and when it opens now, start-up finishes with it; when it gives another failure, that is the result and nothing is
    /// replaced. Otherwise keeps config.db as config.unreadable.db (not for ConfigMissing), makes a new config.db with the
    /// default settings, no exceptions and no download accounting, then finishes start-up. Gives the same failure back
    /// when the old file couldn't be copied or deleted (it is then untouched, though a new key is already saved when the
    /// old one was missing or could never be read). The key is kept whenever it reads, after ConfigKeyMismatch too, so the
    /// copy kept as config.unreadable.db may still open with it. Gives ConfigKeyUnavailable or ConfigKeyStoreMissing, with
    /// nothing touched, when the key can't be read or a new one saved; as at launch, that offers nothing more until the app
    /// is reopened. Changes nothing in any other state.</summary>
    Task<StartupResult> StartFreshAsync(CancellationToken ct);

    /// <summary>Only after ExceptionsImportUnreadable: moves exceptions.json to exceptions.unreadable.json (over an older
    /// copy), imports with no exceptions, and finishes start-up. Gives ExceptionsImportUnreadable back when the move fails,
    /// or any other failure a launch can give, and changes nothing in any other state.</summary>
    Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct);

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
    Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries);   // Saved, or Failed when it couldn't be saved (the list in use is unchanged)
}
