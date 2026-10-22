namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// The rate-limit headers of one AbuseIPDB answer: the daily limit, what is left of it, and when it resets
/// (<c>X-RateLimit-Limit</c>, <c>X-RateLimit-Remaining</c>, <c>X-RateLimit-Reset</c>). Each part is null when the
/// answer didn't carry it or it couldn't be read.
/// </summary>
public sealed record AbuseRateLimit(int? Limit, int? Remaining, DateTimeOffset? ResetAt);

/// <summary>One address from a blacklist download, already judged public, with what AbuseIPDB said about it.</summary>
internal sealed record BlacklistEntry(PublicAddress Address, int Score, DateTimeOffset? LastReportedAt, string? CountryCode);

/// <summary>
/// A blacklist as read from one download: when AbuseIPDB generated it (UTC), its entries with one per address, and how
/// many entries were left out because they couldn't be read (<see cref="Unreadable"/>), weren't public
/// (<see cref="NotPublic"/>) or named an address already listed (<see cref="Duplicates"/>).
/// </summary>
internal sealed record Blacklist(DateTimeOffset GeneratedAt, IReadOnlyList<BlacklistEntry> Entries,
                                 int Unreadable, int NotPublic, int Duplicates);

/// <summary>
/// What <c>abuseipdb.db</c> is now. <see cref="Missing"/>: there is no file yet, which reads as an empty list.
/// <see cref="Usable"/>: it can be read and written. <see cref="Newer"/>: a newer version wrote it.
/// <see cref="Unreadable"/>: it isn't a database, is damaged, or isn't this application's list.
/// <see cref="StorageError"/>: it couldn't be read or written now. A file that is <see cref="Newer"/> or
/// <see cref="Unreadable"/> is never written; only clearing the list removes it.
/// </summary>
public enum AbuseListState { Missing, Usable, Newer, Unreadable, StorageError }

/// <summary>
/// One address kept in the rolling list, judged public again when it was read: when a download first and last listed
/// it (the local download times, UTC), and what the last download that listed it said. The record compares
/// <see cref="Address"/> by reference, so compare its <see cref="PublicAddress.Bytes"/> or text.
/// </summary>
internal sealed record AbuseListEntry(PublicAddress Address, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Score,
                                      string? CountryCode, DateTimeOffset? LastReportedAt);

/// <summary>
/// The rolling list now. The counts are the addresses listed within the keep days, per family.
/// <see cref="LastDownloadAt"/> and <see cref="GeneratedAt"/> are when the list now held was downloaded and generated,
/// both null while no download has been merged into the file now held, and again after the list is cleared.
/// <see cref="State"/> is <see cref="AbuseListState.Missing"/> while there is no file: before a download press reaches
/// the write made before sending, which creates the file, and again after the list is cleared. With a file it is
/// <see cref="AbuseListState.Usable"/> when it can be used, with no addresses until a download is merged.
/// <see cref="RateLimit"/> is what AbuseIPDB last said about the download limit, null when it never has.
/// <see cref="DownloadsToday"/> counts the 2xx answers AbuseIPDB gave this UTC day, also one whose body couldn't be
/// used; a 4xx, 429 or 5xx answer, or no answer, isn't one. <see cref="RefusedUntil"/> is when a download may be sent
/// again, null when one may be sent now. <see cref="NewCount"/> is the rows of the last download's new-address set
/// (see <see cref="AbuseNewAddresses"/>) that pass the read judgement, or 0 when the set isn't known.
/// <see cref="ClockBehind"/> is true when this computer's clock is more than
/// <see cref="AbuseIpDbLimits.MaxClockDifferenceHours"/> hours behind <see cref="LastDownloadAt"/>; the counts are then
/// withheld as 0, which doesn't mean the list is empty, while <see cref="LastDownloadAt"/>, <see cref="GeneratedAt"/>,
/// <see cref="NewCount"/>, <see cref="RateLimit"/>, <see cref="DownloadsToday"/> and <see cref="RefusedUntil"/> are kept.
/// </summary>
public sealed record AbuseListStatus(AbuseListState State, int IPv4Count, int IPv6Count,
                                     DateTimeOffset? LastDownloadAt, DateTimeOffset? GeneratedAt,
                                     AbuseRateLimit? RateLimit, int DownloadsToday, DateTimeOffset? RefusedUntil,
                                     int NewCount = 0, bool ClockBehind = false);

/// <summary>
/// Why the last download's new addresses couldn't be given. <see cref="ListNewer"/>, <see cref="ListUnreadable"/> and
/// <see cref="StorageError"/>: the list can't be used now. <see cref="NoDownload"/>: no file, or no download merged into
/// the file now held. <see cref="NotKnown"/>: the list has a download, but the set the merge kept isn't there, isn't
/// this version's, or wasn't made by that download (a build without the set merged since, or the set couldn't be kept).
/// </summary>
public enum AbuseNewAddressesFailure { None, ListNewer, ListUnreadable, StorageError, NoDownload, NotKnown }

/// <summary>
/// The addresses the last download counted as new: listed by it and not within the keep days before it (a new
/// address, one that came back after the horizon, or one the keep days had only hidden), both families whatever the
/// IPv6 switch. <see cref="Addresses"/> holds every row of the set that passes the read judgement, IPv4 first, then by
/// bytes, and is empty on a failure. <see cref="AsOf"/> is the download that made the set, null on a failure.
/// </summary>
public sealed record AbuseNewAddresses(AbuseNewAddressesFailure Failure, IReadOnlyList<AbuseNewAddress> Addresses,
                                       DateTimeOffset? AsOf);

/// <summary>
/// One new address: the <see cref="PublicAddress"/> the read judged (not its text, so a page built from it holds only
/// checked text), its stored score, and its last report time, null when the entry had none.
/// </summary>
public sealed record AbuseNewAddress(PublicAddress Address, int Score, DateTimeOffset? LastReportedAt);

/// <summary>
/// One blacklist download. <see cref="Failure"/> is <see cref="AbuseIpDbFailure.None"/> when a list was downloaded and
/// merged; <see cref="NewGeneration"/> is false when AbuseIPDB sent a list no newer than one already merged, whose
/// addresses were still refreshed.
/// <see cref="Listed"/> is the addresses the download kept, <see cref="Skipped"/> the entries left out (unreadable or
/// not public), and <see cref="Added"/> and <see cref="Lapsed"/> what changed in the addresses within the keep days, with
/// the cutoffs the status and the feed use: before the download from the previous download, after it from this one.
/// They are counted over the rows in the list file, both families together, before a read judges them, so a row a read
/// leaves out (one changed outside the app) makes them differ from the status by that many, and the feed also follows
/// the IPv6 switch, the exceptions and the cap. Added counts the addresses within them after the download that weren't
/// before it (new addresses, and addresses listed again after dropping out of the keep days), and Lapsed those within
/// them before it that aren't after it, so over those rows the count before plus Added less Lapsed is the count after.
/// <see cref="Expired"/> counts those removed because no
/// download had listed them for <see cref="AbuseIpDbLimits.MaxKeepDays"/> days, whatever the keep days; an address can
/// be both Lapsed and Expired. <see cref="RetryAt"/> is set for <see cref="AbuseIpDbFailure.RateLimited"/> and
/// <see cref="AbuseIpDbFailure.LimitReached"/> when the time is known. <see cref="Counted"/> is true when AbuseIPDB
/// answered the press with a 2xx, so the download counts against today's allowance whatever came after it (a list that
/// couldn't be read or merged, or a clock too far from AbuseIPDB's), and false for every refusal before sending, a 401,
/// a 429, any other failure status and no answer.
/// </summary>
public sealed record AbuseDownloadResult(AbuseIpDbFailure Failure, bool NewGeneration, int Listed, int Skipped,
                                         int Added, int Lapsed, int Expired, DateTimeOffset? GeneratedAt,
                                         DateTimeOffset? RetryAt, bool Counted = false);
