namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>The <c>X-RateLimit-*</c> headers of one answer, each null when not sent or not readable.</summary>
public sealed record AbuseRateLimit(int? Limit, int? Remaining, DateTimeOffset? ResetAt);

/// <summary>One address from a blacklist download, already judged public, with what AbuseIPDB said about it.</summary>
internal sealed record BlacklistEntry(PublicAddress Address, int Score, DateTimeOffset? LastReportedAt, string? CountryCode);

/// <summary>GeneratedAt is AbuseIPDB's time (UTC); the three counts are the entries left out, by reason.</summary>
internal sealed record Blacklist(DateTimeOffset GeneratedAt, IReadOnlyList<BlacklistEntry> Entries,
                                 int Unreadable, int NotPublic, int Duplicates);

/// <summary>
/// <see cref="Missing"/>: no file, read as an empty list. <see cref="Unreadable"/>: not a database, damaged or not this
/// list. A <see cref="Newer"/> or <see cref="Unreadable"/> file is never written; only clearing the list removes it.
/// </summary>
public enum AbuseListState { Missing, Usable, Newer, Unreadable, StorageError }

/// <summary>
/// FirstSeen and LastSeen are local download times (UTC). The record compares <see cref="Address"/> by reference,
/// so compare its <see cref="PublicAddress.Bytes"/> or text.
/// </summary>
internal sealed record AbuseListEntry(PublicAddress Address, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Score,
                                      string? CountryCode, DateTimeOffset? LastReportedAt);

/// <summary>
/// With <see cref="ClockBehind"/> the counts are withheld as 0, which doesn't mean an empty list, and the rest is kept.
/// <see cref="DownloadsToday"/> counts this UTC day's 2xx answers, one whose body couldn't be used included.
/// </summary>
public sealed record AbuseListStatus(AbuseListState State, int IPv4Count, int IPv6Count,
                                     DateTimeOffset? LastDownloadAt, DateTimeOffset? GeneratedAt,
                                     AbuseRateLimit? RateLimit, int DownloadsToday, DateTimeOffset? RefusedUntil,
                                     int NewCount = 0, bool ClockBehind = false);

/// <summary>
/// <see cref="NotKnown"/>: the list has a download, but the set its merge kept is missing, of another shape or made
/// by another download (an earlier build merged since, or the set couldn't be kept).
/// </summary>
public enum AbuseNewAddressesFailure { None, ListNewer, ListUnreadable, StorageError, NoDownload, NotKnown }

/// <summary>
/// Listed by the last download and not within the keep days before it, both families whatever the IPv6 switch.
/// <see cref="Addresses"/> is empty and <see cref="AsOf"/> null on a failure.
/// </summary>
public sealed record AbuseNewAddresses(AbuseNewAddressesFailure Failure, IReadOnlyList<AbuseNewAddress> Addresses,
                                       DateTimeOffset? AsOf);

/// <summary>
/// One new address: the <see cref="PublicAddress"/> the read judged (not its text, so a page built from it holds only
/// checked text), its stored score, and its last report time, null when the entry had none.
/// </summary>
public sealed record AbuseNewAddress(PublicAddress Address, int Score, DateTimeOffset? LastReportedAt);

/// <summary>
/// <see cref="Added"/> and <see cref="Lapsed"/> count the file's rows before a read judges them, so they can differ
/// from the status. <see cref="Counted"/> is true after any 2xx answer, whatever stopped the merge after it.
/// </summary>
public sealed record AbuseDownloadResult(AbuseIpDbFailure Failure, bool NewGeneration, int Listed, int Skipped,
                                         int Added, int Lapsed, int Expired, DateTimeOffset? GeneratedAt,
                                         DateTimeOffset? RetryAt, bool Counted = false);
