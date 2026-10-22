namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>The fixed values of the AbuseIPDB check and the rolling list.</summary>
public static class AbuseIpDbLimits
{
    /// <summary>How many days of reports the check counts (<c>maxAgeInDays</c>).</summary>
    public const int CheckMaxAgeInDays = 30;

    /// <summary>The highest abuse confidence score; the lowest is 0.</summary>
    public const int MaxScore = 100;

    /// <summary>How many days an address stays in the rolling list after the last download that listed it, by default.</summary>
    public const int DefaultKeepDays = 30;

    /// <summary>The fewest keep days the setting allows.</summary>
    public const int MinKeepDays = 1;

    /// <summary>The most keep days the setting allows.</summary>
    public const int MaxKeepDays = 365;

    /// <summary>
    /// Blacklist downloads a day on AbuseIPDB's free plan. The app counts downloads against it only while AbuseIPDB has
    /// never sent its <c>X-RateLimit-Remaining</c> header; once it has, the stored header values decide.
    /// </summary>
    public const int FreeBlacklistDownloadsPerDay = 5;

    /// <summary>The most entries FortiOS reads from one threat feed file.</summary>
    public const int MaxFeedEntries = 131_072;

    /// <summary>
    /// How many hours the computer's clock may be from the time AbuseIPDB's answer gives before a downloaded list is
    /// not merged: the rolling list's times come from the computer's clock.
    /// </summary>
    public const int MaxClockDifferenceHours = 24;

    /// <summary>
    /// How many hours ahead a reset or retry time may be. A later time can't be the reset and is taken as unknown:
    /// stored, it would refuse every download until then, and nothing sent could correct it. AbuseIPDB's documented
    /// reset is midnight US Eastern, so the time between two resets is 25 hours on the autumn clock change; the extra
    /// hour is margin. A reset or a <c>Retry-After</c> date is measured from its own clock (the answer's <c>Date</c> when
    /// the clock guard accepts it, else now), and a <c>Retry-After</c> delta from now. A stored reset is held to the same
    /// bound again each time it is read, from the reading clock.
    /// </summary>
    public const int MaxResetAheadHours = 26;

    /// <summary>
    /// The free space a download needs on the data folder's disk, beyond the rolling list's current size, before it is
    /// sent: the 16 MiB answer, the merge's journal and the list growing by it, with margin. With less, the download is
    /// refused here, so none is spent on a list that couldn't take it.
    /// </summary>
    public const long MinFreeBytesForDownload = 64L * 1024 * 1024;
}

/// <summary>
/// Why a check gave no score, or a blacklist download or list operation didn't complete. <see cref="None"/> means it
/// did. <see cref="NoKey"/>: no key is saved, or the saved text is not a key. <see cref="KeyUnavailable"/>: a key is
/// saved but can't be read now (a locked keychain or a denied prompt); it is kept. <see cref="NotPublic"/>: there is no
/// public address to send: the query is not an address search, or its address is private, reserved or outside global
/// unicast. <see cref="LimitReached"/>: refused here, with nothing sent, because no downloads are left until the
/// result's retry time. <see cref="ListUnreadable"/>: <c>abuseipdb.db</c> isn't a database, is damaged, or isn't this
/// application's list. <see cref="ListNewer"/>: it was written by a newer version. <see cref="StorageError"/>: it
/// couldn't be read or written now. A list file the application can't use is left as it is.
/// <see cref="ClockWrong"/>: the computer's clock is more than <see cref="AbuseIpDbLimits.MaxClockDifferenceHours"/>
/// hours from AbuseIPDB's, so the downloaded list was not merged. <see cref="DiskNearlyFull"/>: refused here, with
/// nothing written or sent, because the data folder's disk has less free space than
/// <see cref="AbuseIpDbLimits.MinFreeBytesForDownload"/> beyond the list's size. <see cref="ClockBehind"/>: refused
/// here, with nothing written, read or sent and nothing counted, because this computer's clock is more than
/// <see cref="AbuseIpDbLimits.MaxClockDifferenceHours"/> hours behind the list's last download.
/// </summary>
public enum AbuseIpDbFailure
{
    None, NoKey, KeyUnavailable, NotPublic, KeyRejected, RateLimited, Offline, ServiceError, BadResponse,
    LimitReached, ListUnreadable, ListNewer, StorageError, ClockWrong, DiskNearlyFull, ClockBehind,
}

/// <summary>What saving an AbuseIPDB key came to.</summary>
public enum AbuseKeySave { Saved, NotAKey, Failed }

/// <summary>
/// What AbuseIPDB said about one address. Only numbers, dates, flags and a checked country code are kept: no free
/// text from the answer (ISP, domain, usage type, host names).
/// </summary>
public sealed record AbuseCheck(int Score, int TotalReports, int DistinctUsers, DateTimeOffset? LastReportedAt,
                                bool? IsWhitelisted, bool? IsTor, string? CountryCode);

/// <summary>
/// One check. <see cref="Check"/> is set only when <see cref="Failure"/> is <see cref="AbuseIpDbFailure.None"/>.
/// <see cref="RetryAt"/> is set only for <see cref="AbuseIpDbFailure.RateLimited"/>, when the server said when.
/// </summary>
public sealed record AbuseCheckResult(AbuseIpDbFailure Failure, AbuseCheck? Check, DateTimeOffset? RetryAt);
