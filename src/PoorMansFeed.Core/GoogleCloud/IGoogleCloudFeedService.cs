using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.GoogleCloud;

/// <summary>
/// The tool's Core facade. Network, JSON, IPv6 filtering, confirmation, large-range detection, filenames and the
/// feed header all live behind it. The dataset is reached only through IIpInfoService.LookUpOverlapsAsync.
/// </summary>
public interface IGoogleCloudFeedService : ICloudFeedService
{

    /// <summary>Raised after a load saved bytes other than the saved file's; never for a save that failed.</summary>
    event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated;
    /// <summary>Downloads and parses cloud.json. Never calls the live URL in tests (stub handler).</summary>
    Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct);

    /// <summary>
    /// Reads only the saved file and sends nothing. NotSaved: neither file is there; Unusable: the pair won't read.
    /// </summary>
    Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct);

    /// <summary>
    /// Selects the prefixes the pick matches, drops IPv6 when IPv6Enabled is off, confirms each against the
    /// dataset and classifies it. NoDataset when there is no usable database.
    /// </summary>
    Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct);
}
