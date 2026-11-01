namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// <see cref="ClockWrong"/>: this clock is too far behind the last download, so the header would date the feed before
/// its list. <see cref="NothingToWrite"/> from Retire feed looks only in the current export folder.
/// </summary>
public enum AbuseFeedWriteFailure
{
    None,
    ExceptionsUnreadable,
    ListUnreadable,
    ListNewer,
    StorageError,
    NoDownload,
    ClockWrong,
    NothingToWrite,
    FolderUnavailable,
    WriteFailed,
    NoFeedType,
}

/// <summary>
/// Counts are over both families, one copy each, kept by every outcome after the addresses are chosen. Null means
/// none: OldFilesLeft names what a failed write left, ModSecurityFiles the copies, FeedChanges each moved file's.
/// </summary>
public sealed record AbuseFeedWriteResult(AbuseFeedWriteFailure Failure, IReadOnlyList<string> Files,
                                          int IPv4Written, int IPv6Written, int LeftOut, int ExceptionRemovals,
                                          IReadOnlyList<string> EmptiedFiles, string? FailedFile = null,
                                          IReadOnlyList<string>? OldFilesLeft = null,
                                          IReadOnlyList<string>? ModSecurityFiles = null,
                                          IReadOnlyList<FeedFileChange>? FeedChanges = null,
                                          FeedRecordResult? Record = null);

public enum RollingRefreshOutcome { Unchanged, Written, Missing, Retired, SaveRefused, SavedFeedsUnavailable, Failed }

/// <summary>The saved rolling feed's refresh. Stamp is what the caller commits with the other feeds'.</summary>
public sealed record RollingFeedRefresh(RollingRefreshOutcome Outcome, AbuseFeedWriteResult? Write, SavedFeedStamp? Stamp);
