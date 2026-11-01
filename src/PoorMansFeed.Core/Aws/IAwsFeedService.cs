using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.Aws;

/// <summary>
/// The AWS tool's Core facade. The ip-ranges.json file, the cache, the confirmation and the feed all live behind
/// it. The dataset is reached only through IIpInfoService.LookUpOverlapsAsync.
/// </summary>
public interface IAwsFeedService : ICloudFeedService
{

    /// <summary>Raised after an update replaced the saved file; never for UpToDate or a save that failed.</summary>
    event EventHandler<CloudSourceUpdated<AwsRangeCatalogue>>? SourceUpdated;
    /// <summary>Reads the saved file and nothing else: no network. Null when there is no usable saved file.</summary>
    Task<AwsRangeCatalogue?> LoadCachedAsync(CancellationToken ct);

    /// <summary>
    /// Reads only the saved file and sends nothing. NotSaved: neither file is there; Unusable: the pair won't read.
    /// </summary>
    Task<SavedRanges<AwsRangeCatalogue>> LoadSavedAsync(CancellationToken ct);

    /// <summary>
    /// Nothing is replaced (UpToDate) on a 304 for the saved ETag or a file identical to current. Any failure leaves
    /// current and the saved file untouched: CheckFailed, or Unavailable when there was no current file.
    /// </summary>
    Task<AwsLoadResult> CheckForUpdateAsync(AwsRangeCatalogue? current, CancellationToken ct);

    /// <summary>
    /// Confirms every range of the picked service and region (every region when Region is null). Drops IPv6 when
    /// IPv6Enabled is off; NothingSelected when the pick isn't in the catalogue, NoDataset with no usable database.
    /// </summary>
    Task<CheckResult> CheckAsync(AwsRangeCatalogue catalogue, AwsSelection selection, CancellationToken ct);
}
