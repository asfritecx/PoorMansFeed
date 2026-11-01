namespace PoorMansFeed.Core;

/// <summary>
/// The IPinfo dataset download requests sent, oldest first, and the time a refusal from IPinfo holds until. IPinfo
/// counts its daily limit over a rolling 24 hours, and the checksums request isn't counted.
/// </summary>
internal sealed record IpInfoDownloads(IReadOnlyList<DateTimeOffset> Requests, DateTimeOffset? RefusedUntil)
{
    internal const int DailyLimit = IpInfoLimits.DownloadsPerDay, MaxKept = 64;

    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan ShortestRefusal = TimeSpan.FromHours(1);

    public static IpInfoDownloads Empty { get; } = new([], null);

    /// <summary>The stored times, sorted, and only the newest <see cref="MaxKept"/>.</summary>
    internal static IpInfoDownloads FromStored(IEnumerable<DateTimeOffset> requests, DateTimeOffset? refusedUntil) =>
        new(Newest(requests), refusedUntil);

    /// <summary>
    /// The requests in (now - 24 h, now + 24 h]. One ahead of now, after the clock was set back, still counts, as
    /// IPinfo's own count hasn't moved.
    /// </summary>
    internal int CountedAt(DateTimeOffset now) => Requests.Count(request => Counts(request, now));

    /// <summary>Null when the refusal has passed, or is more than 24 hours ahead, which no refusal can be.</summary>
    internal DateTimeOffset? RefusedAt(DateTimeOffset now) =>
        RefusedUntil is DateTimeOffset until && until > now && until <= now + Window ? until : null;

    /// <summary>
    /// None when a request may be sent now under the policy: RateLimited while a refusal holds, KeptForUser when it
    /// would leave fewer than the policy's downloads for the user.
    /// </summary>
    internal UpdateFailure Judge(DateTimeOffset now, IpInfoUpdatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (RefusedAt(now) is not null)
            return UpdateFailure.RateLimited;
        return CountedAt(now) + 1 <= DailyLimit - policy.LeftForUser ? UpdateFailure.None : UpdateFailure.KeptForUser;
    }

    /// <summary>One more request at now; requests no longer counted are dropped.</summary>
    internal IpInfoDownloads AfterRequest(DateTimeOffset now) =>
        new(Newest(Requests.Where(request => Counts(request, now)).Append(now)), RefusedAt(now));

    /// <summary>After IPinfo served a download: whatever refusal was stored has ended.</summary>
    internal IpInfoDownloads AfterServed() => this with { RefusedUntil = null };

    /// <summary>
    /// After a 429: Retry-After when sent, else when the oldest counted request is 24 hours old, else 24 hours; never
    /// sooner than an hour from now, and never more than 24 hours.
    /// </summary>
    internal IpInfoDownloads AfterRefusal(DateTimeOffset now, TimeSpan? retryAfter)
    {
        List<DateTimeOffset> counted = Requests.Where(request => Counts(request, now)).ToList();
        DateTimeOffset until = retryAfter is TimeSpan wait ? now + (wait < Window ? wait : Window)
                             : counted.Count > 0 ? counted.Min() + Window
                             : now + Window;
        if (until < now + ShortestRefusal)
            until = now + ShortestRefusal;
        else if (until > now + Window)
            until = now + Window;
        return new IpInfoDownloads(Newest(counted), until);
    }

    public bool Equals(IpInfoDownloads? other) =>
        other is not null && RefusedUntil == other.RefusedUntil && Requests.SequenceEqual(other.Requests);

    public override int GetHashCode() => HashCode.Combine(RefusedUntil, Requests.Count);

    private static bool Counts(DateTimeOffset request, DateTimeOffset now) => request > now - Window && request <= now + Window;

    private static DateTimeOffset[] Newest(IEnumerable<DateTimeOffset> requests)
    {
        DateTimeOffset[] sorted = [.. requests.Order()];
        return sorted.Length > MaxKept ? sorted[^MaxKept..] : sorted;
    }
}
