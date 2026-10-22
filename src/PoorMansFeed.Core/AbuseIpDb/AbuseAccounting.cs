namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// The AbuseIPDB download accounting: the stored rate limit and the UTC day's count of 2xx answers.
/// <see cref="RateLimit"/> is null when AbuseIPDB never said what is left of the limit; <see cref="DownloadsOnDay"/> is
/// the number of 2xx answers on <see cref="DownloadsDay"/>, and 0 while there is no day.
/// </summary>
internal sealed record AbuseAccounting(AbuseRateLimit? RateLimit, DateOnly? DownloadsDay, int DownloadsOnDay)
{
    public static AbuseAccounting Empty { get; } = new(null, null, 0);

    /// <summary>The accounting after one more answer: with countDownload the count for answeredAt's UTC day goes up by one
    /// (a new day starts at 1, and the count stops at int.MaxValue); the limit is replaced only when rateLimit.Remaining is
    /// not null.</summary>
    internal AbuseAccounting After(AbuseRateLimit rateLimit, DateTimeOffset answeredAt, bool countDownload)
    {
        ArgumentNullException.ThrowIfNull(rateLimit);
        // The one place these rules live, so the store and a session copy kept after a failed write can't disagree
        DateOnly? day = DownloadsDay;
        int count = DownloadsOnDay;
        if (countDownload)
        {
            var today = DateOnly.FromDateTime(answeredAt.UtcDateTime);
            count = DownloadsDay == today && DownloadsOnDay < int.MaxValue ? DownloadsOnDay + 1
                  : DownloadsDay == today ? int.MaxValue
                  : 1;
            day = today;
        }

        AbuseRateLimit? limit = rateLimit.Remaining is not null
            ? new AbuseRateLimit(rateLimit.Limit, rateLimit.Remaining, rateLimit.ResetAt)
            : RateLimit;
        return new AbuseAccounting(limit, day, count);
    }
}

/// <summary>Where the download accounting is kept: read from memory, and written one answer at a time.</summary>
internal interface IAbuseAccounting
{
    /// <summary>The accounting as last committed. Never throws.</summary>
    AbuseAccounting Current { get; }

    /// <summary>False while no config store is open; the accounting is then unknown, and no download may be sent.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// One transaction. With countDownload the count for answeredAt's UTC day goes up by one (a new day starts at 1).
    /// The limit is replaced only when rateLimit.Remaining is not null. Current changes only after the commit; false when
    /// the write failed, and Current is then unchanged.
    /// </summary>
    bool TryRecordAnswer(AbuseRateLimit rateLimit, DateTimeOffset answeredAt, bool countDownload);
}
