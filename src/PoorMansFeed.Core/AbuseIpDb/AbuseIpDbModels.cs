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
    /// A later reset or retry time is taken as unknown: stored, it would refuse every download until then. A reset at
    /// midnight US Eastern can be 25 hours away on the autumn clock change; the extra hour is margin.
    /// </summary>
    public const int MaxResetAheadHours = 26;

    /// <summary>
    /// Free space a download needs beyond the list's size: the 16 MiB answer, the merge's journal and the list's
    /// growth, with margin. With less it is refused unsent, so none is spent on a list that can't take it.
    /// </summary>
    public const long MinFreeBytesForDownload = 64L * 1024 * 1024;
}

/// <summary>
/// <see cref="ClockWrong"/>: an answer's <c>Date</c> was too far from this clock: counted, not merged.
/// <see cref="ClockBehind"/>: refused before sending, because this clock is too far behind the last download.
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
