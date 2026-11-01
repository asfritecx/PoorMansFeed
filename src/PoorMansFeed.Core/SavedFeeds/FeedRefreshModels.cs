using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>How far apart the Refresh all timer's runs are, in hours.</summary>
public static class RefreshAllLimits
{
    public const int MinHours = 1;
    public const int MaxHours = 24;
    public const int DefaultHours = 6;
}

public enum FeedRefreshOutcome { Unchanged, Changed, Skipped, Failed }

/// <summary>Why a feed was skipped or failed; None for Unchanged and Changed.</summary>
public enum FeedRefreshReason
{
    None, Retired, Removed, Superseded, Cancelled, FeedsOff,
    SaveRefused, NotSaved, SavedFeedsUnavailable, NoDataset, SourceGone, PickGone, SourceUnavailable, NoFeedType,
    ExceptionsUnreadable, SettingsChanged, FolderUnavailable, WriteFailed, ListUnavailable, ClockWrong, Failed,
}

/// <summary>
/// One saved feed's refresh. Added and Removed count one feed type's copy; Changed means an entry changed, so a rewrite
/// with no entry change is Unchanged with Written true. NewUnconfirmed and NewLarge count ranges left out, unanswered.
/// </summary>
public sealed record FeedRefreshResult(string Key, FeedKind Kind, FeedRefreshOutcome Outcome, FeedRefreshReason Reason,
                                       bool Written, int Added, int Removed, int NewUnconfirmed, int NewLarge,
                                       string? FailedFile, IReadOnlyList<string> OldFilesLeft, FeedRecordResult? Record);

public enum RefreshStep { Dataset, Sources, AbuseIpDb, Feeds, Saving }

/// <summary>Dataset carries the update's own progress in the Dataset step; FeedKey names the feed just done.</summary>
public readonly record struct RefreshProgress(RefreshStep Step, int Done, int Total, string? FeedKey, UpdateProgress? Dataset);

/// <summary>Fresh: checked now. Saved: the check failed, so the saved copy was used. Unavailable: neither.</summary>
public enum SourceRefreshState { Fresh, Saved, Unavailable }

public sealed record SourceRefresh(FeedKind Provider, SourceRefreshState State);

/// <summary>Download is null when nothing was asked of the service (no saved key gives NoKey).</summary>
public sealed record AbuseRefreshDownload(AbuseIpDbFailure Failure, AbuseDownloadResult? Download);

/// <summary>
/// Dataset, Sources and AbuseIpDb hold only the steps that ran; Feeds are in ordinal key order, one per saved feed.
/// SaveFailed: the one save of the stamps failed for a feed or threw, so the result can say it once.
/// </summary>
public sealed record RefreshRunResult(bool SavedFeedsAvailable, UpdateResult? Dataset, IReadOnlyList<SourceRefresh> Sources,
                                      AbuseRefreshDownload? AbuseIpDb, IReadOnlyList<FeedRefreshResult> Feeds, bool Cancelled,
                                      bool SaveFailed);

public interface IFeedRefresher
{
    /// <summary>Only the caller's cancel stops it early, and nothing escapes: a cancel gives Cancelled true.</summary>
    Task<RefreshRunResult> RunAsync(IProgress<RefreshProgress>? progress, CancellationToken ct);
}
