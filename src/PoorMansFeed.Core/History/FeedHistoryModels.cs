namespace PoorMansFeed.Core.History;

public enum FeedKind { Asn, GoogleCloud, Azure, Aws, AbuseIpDbRolling }

public enum FeedChangeCause { PageWrite, Retire, RefreshAll }

public enum FeedRemovalReason { NotTold, Exception, SourceDropped, LeftTheList, LeftOut, FamilyOff }

public enum HistoryState { Missing, Usable, Newer, Unreadable, StorageError }

public sealed record FeedRemoval(string Text, FeedRemovalReason Reason);

/// <summary>
/// One family's entries added and removed by a write.
/// <see cref="Copy"/> is null when every copy changed alike.
/// </summary>
public sealed record FeedFamilyChange(bool IsIPv6, FeedType? Copy, FeedFileState OldFile,
                                      IReadOnlyList<string> Added, IReadOnlyList<FeedRemoval> Removed);

public sealed record FeedChangeRecord(DateTimeOffset AtUtc, string FeedKey, FeedKind Kind, FeedChangeCause Cause,
                                      DateOnly? DatasetDate, DateTimeOffset? ListDownloadedUtc,
                                      IReadOnlyList<FeedFamilyChange> Families);

/// <summary>Filters that combine with AND; a null one filters nothing.</summary>
public sealed record HistoryQuery(string? FeedKey, DateTimeOffset? SinceUtc, HistoryMatch? Match);

public sealed record HistoryRow(DateTimeOffset AtUtc, string FeedKey, FeedKind Kind, FeedChangeCause Cause, bool IsIPv6,
                                FeedType? Copy, bool Added, string Entry, FeedRemovalReason Reason, FeedFileState OldFile,
                                DateOnly? DatasetDate, DateTimeOffset? ListDownloadedUtc);

/// <summary>
/// <see cref="Total"/> counts every match, rows past the limit or left out on read included, up to a cap;
/// <see cref="TotalCapped"/> says more matched; <see cref="CapTrimmedBeforeUtc"/>: the cap's latest trimmed change.
/// </summary>
public sealed record HistoryRead(HistoryState State, IReadOnlyList<HistoryRow> Rows, int Total,
                                 IReadOnlyList<string> FeedKeys, bool TotalCapped = false,
                                 DateTimeOffset? CapTrimmedBeforeUtc = null)
{
    public static HistoryRead Empty(HistoryState state) => new(state, [], 0, []);
}

public static class HistoryLimits
{
    /// <summary>The cap on the history's pages in use, in MiB; past it the oldest whole changes are trimmed.</summary>
    public const int CapMegabytes = 256;
}
