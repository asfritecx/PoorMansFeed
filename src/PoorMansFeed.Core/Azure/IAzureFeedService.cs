using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.Azure;

/// <summary>
/// The Azure tool's Core facade. The service tags file, the cache, the confirmation and the feed all live behind
/// it. The dataset is reached only through IIpInfoService.LookUpOverlapsAsync.
/// </summary>
public interface IAzureFeedService : ICloudFeedService
{

    /// <summary>Raised after an update replaced the saved file; never for UpToDate or a save that failed.</summary>
    event EventHandler<CloudSourceUpdated<AzureTagCatalogue>>? SourceUpdated;
    /// <summary>Reads the saved file and nothing else: no network. Null when there is no usable saved file.</summary>
    Task<AzureTagCatalogue?> LoadCachedAsync(CancellationToken ct);

    /// <summary>
    /// Reads only the saved file and sends nothing. NotSaved: neither file is there; Unusable: the pair won't read.
    /// </summary>
    Task<SavedRanges<AzureTagCatalogue>> LoadSavedAsync(CancellationToken ct);

    /// <summary>
    /// Downloads nothing when the download page names the file already in current (UpToDate). Any failure leaves
    /// current and the saved file untouched: CheckFailed, or Unavailable when there was no current file.
    /// </summary>
    Task<AzureLoadResult> CheckForUpdateAsync(AzureTagCatalogue? current, CancellationToken ct);

    /// <summary>
    /// Confirms every range of the picked tag against the dataset. Drops IPv6 when IPv6Enabled is off.
    /// NothingSelected when the tag is not in the catalogue. NoDataset when there is no usable database.
    /// </summary>
    Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct);
}
