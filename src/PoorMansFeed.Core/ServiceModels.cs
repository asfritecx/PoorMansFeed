using System.Numerics;

namespace PoorMansFeed.Core;

public enum StartupFailure
{
    None, Privileged, AlreadyRunning, StorageUnavailable, DatabaseNewer,
    ConfigNewer,                // config.db was made by a newer version: Start fresh offered (the key is kept when it reads)
    ConfigUnreadable,           // config.db is damaged or isn't this app's file: Start fresh offered (the key is kept when it reads)
    ConfigKeyMismatch,          // the key is gone or doesn't open config.db, or its contents changed outside the app: Start fresh offered (the key is kept when it reads)
    ConfigLocked,               // another program holds config.db's lock (BUSY): no Start fresh
    ConfigStorageError,         // config.db can't be opened or written (read-only file or folder, CANTOPEN): no Start fresh
    ConfigKeyUnavailable,       // the key can't be read or saved now (a locked keychain, Deny): nothing is touched, no Start fresh (also from a Start fresh)
    ConfigKeyStoreMissing,      // this system has no secret store, so nothing is written
    ExceptionsImportUnreadable, // the old exceptions.json can't be read at the import: Start without the old exceptions offered
    ConfigMissing,              // config.db is gone or empty though an earlier launch made one: Start fresh offered (the key is kept when it reads)
}
public sealed record StartupResult(StartupFailure Failure, DatasetInfo? Dataset, bool HasToken);

public sealed record DatasetInfo(DateOnly DownloadedOn, long RangeCount, string Checksum, DateTimeOffset ImportedUtc, bool IncludesIPv6);

public enum UpdateCheckOutcome { UpToDate, UpdateAvailable, NoToken, TokenRejected, RateLimited, Offline, ServiceError }
/// <summary>AddsIPv6 is true when UpdateAvailable is only because the dataset lacks IPv6 networks (the checksum matches).</summary>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, TimeSpan? RetryAfter = null, bool AddsIPv6 = false);

public enum UpdatePhase { Checking, Downloading, Importing, Indexing, Finishing }
public readonly record struct UpdateProgress(UpdatePhase Phase, long Done, long Total);   // Total 0 = indeterminate

public enum UpdateFailure { None, NoToken, TokenRejected, RateLimited, Offline, ChecksumMismatch,
                            NotEnoughDiskSpace, CorruptData, StorageError, ServiceError, KeptForUser }
public sealed record UpdateResult(UpdateFailure Failure, bool AlreadyCurrent, DatasetInfo? Dataset, TimeSpan? RetryAfter = null);

/// <summary>
/// A dataset update kept back for the user: each download request is sent only while at least
/// <see cref="LeftForUser"/> of IPinfo's daily downloads remain after it, and never while a refusal from IPinfo holds.
/// </summary>
public sealed record IpInfoUpdatePolicy(int LeftForUser)
{
    public static IpInfoUpdatePolicy RefreshAll { get; } = new(3);
}

public enum SearchKind { Asn, IpAddress }
public sealed partial record SearchQuery(SearchKind Kind, string Text, uint Asn, byte[]? IpBytes);  // Text is normalised: "AS15169" or "8.8.8.8"
public enum SearchOutcome { Found, NoResults, InvalidInput, NoDataset, Failed, IPv6Disabled }
public sealed record NetworkRecord(string Network, string? CountryCode, string? Country, string? Continent, string? Asn,
                                   string? AsName, string? AsDomain, bool IsLargeRange = false);
public sealed record SearchResult(SearchOutcome Outcome, SearchQuery? Query, IReadOnlyList<NetworkRecord> Rows, int TotalCount);

/// <summary>SavedIncluded is true when the saved feed of this write includes the range.</summary>
public sealed record LargeRange(string Network, bool IsIpv6, int PrefixLength, BigInteger AddressCount,
                                bool SavedIncluded = false);
public sealed record ExportPlan(int TotalCount, string AsName, IReadOnlyList<LargeRange> LargeRanges);
public enum ExportFailure { None, NothingToExport, FolderUnavailable, WriteFailed, ExceptionsUnreadable }

/// <summary>Saved: the exceptions list was saved. Failed: it couldn't be saved, and the list in use is unchanged.</summary>
public enum ExceptionsSaveOutcome { Saved, Failed }

/// <summary>
/// OldFilesLeft (failures), ModSecurityFiles, LeftOutLargeRanges, FeedChanges, EmptiedFiles (success): null for none.
/// FailedFile is set only on WriteFailed when a file's write failed; Files: those before it, report first.
/// </summary>
public sealed record ExportResult(ExportFailure Failure, IReadOnlyList<string> Files, int NetworksWritten, int LargeRangesLeftOut,
                                  int ExceptionCuts = 0, int ExceptionRemovals = 0, IReadOnlyList<string>? OldFilesLeft = null,
                                  string? FailedFile = null, IReadOnlyList<string>? ModSecurityFiles = null, bool NoFeedType = false,
                                  int WholeRangesLeftOut = 0, IReadOnlyList<string>? LeftOutLargeRanges = null,
                                  IReadOnlyList<FeedFileChange>? FeedChanges = null, FeedRecordResult? Record = null,
                                  IReadOnlyList<string>? EmptiedFiles = null);

public enum AsnRefreshOutcome { Unchanged, Written, Missing, Retired, SaveRefused, SourceGone, NoDataset, NoFeedType, AsnFeedsOff,
                               SavedFeedsUnavailable, Failed }

/// <summary>
/// One saved ASN feed's refresh. Stamp is what the caller commits with the other feeds'; NewLarge lists the large
/// networks the saved feed never answered, which the refresh left out.
/// </summary>
public sealed record AsnFeedRefresh(AsnRefreshOutcome Outcome, ExportResult? Export, SavedFeedStamp? Stamp,
                                    IReadOnlyList<string> NewLarge);

public static class IpInfoLimits { public const int DownloadsPerDay = 10; }

/// <summary>
/// IPinfo networks overlapping one block that share every stored display field. Grouped by the raw values, so a
/// caller applies its own trim and case rules.
/// </summary>
public sealed record OverlapGroup(string? CountryCode, string? Country, string? Asn, string? AsName,
                                  string? AsDomain, int NetworkCount);

/// <summary>What the dataset holds for one block. Groups is empty when nothing overlaps.</summary>
public sealed record NetworkOverlap(CidrBlock Block, IReadOnlyList<OverlapGroup> Groups);

public enum OverlapOutcome { Found, NoDataset, Failed }

/// <summary>
/// One NetworkOverlap per block asked about, in input order, except IPv6 blocks while IPv6 is off, which are left
/// out. Dataset is the dataset that answered, and is null unless Found.
/// </summary>
public sealed record OverlapResult(OverlapOutcome Outcome, IReadOnlyList<NetworkOverlap> Overlaps, DatasetInfo? Dataset);

public sealed record ChecksumFetch(UpdateCheckOutcome Outcome, string? Sha256, TimeSpan? RetryAfter = null);
public sealed record DownloadOutcome(UpdateFailure Failure, string? Sha256, TimeSpan? RetryAfter = null);
