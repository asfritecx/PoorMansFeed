using PoorMansFeed.Core;

namespace PoorMansFeed.Core.CloudFeeds;

/// <summary>
/// The part of a cloud tool's Core facade that is the same for every provider: choosing what to write and
/// writing it. Each provider's own interface adds how its ranges are loaded and checked.
/// </summary>
public interface ICloudFeedService
{
    /// <summary>Pure. Ticked blocks not in the check are ignored, so the plan is always a subset of the check.</summary>
    CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked);

    /// <summary>
    /// Writes each ticked type's {FileStem}_ipv4.txt and _ipv6.txt through a temp file and a move, returning each file's
    /// changed entries; a family with no rows writes no file. Large ranges not in includedLargeRanges are left out.
    /// </summary>
    Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                          CancellationToken ct);

    /// <summary>Writes the check's saved feed again; checkStartedUtc is taken before its source was read.</summary>
    internal Task<CloudFeedRefresh> RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc, CancellationToken ct) =>
        Task.FromResult(new CloudFeedRefresh(CloudRefreshOutcome.SavedFeedsUnavailable, null, null, [], []));
}
