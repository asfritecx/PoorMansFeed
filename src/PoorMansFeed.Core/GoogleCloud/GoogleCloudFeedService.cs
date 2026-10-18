using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.GoogleCloud;

public sealed class GoogleCloudFeedService : IGoogleCloudFeedService
{
    private readonly IIpInfoService _ipinfo;
    private readonly GoogleCloudRangeClient _client;
    private readonly CloudFeedEngine _engine;

    public GoogleCloudFeedService(IIpInfoService ipinfo, HttpClient http, CoreOptions options, TimeProvider time,
                                  string defaultExportFolder)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(defaultExportFolder);
        _client = new GoogleCloudRangeClient(http, options);
        _engine = new CloudFeedEngine(ipinfo, time, defaultExportFolder);
    }

    public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => _client.FetchAsync(ct);

    public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ct.ThrowIfCancellationRequested();

        // Filter first, then sort: List.Sort is unstable, so the list that is sorted must be the one that was always sorted.
        UserSettings settings = _ipinfo.Settings;
        var selected = new List<CloudPrefix>();
        foreach (CloudPrefix prefix in catalogue.Prefixes)
        {
            if (!selection.Matches(prefix.Scope))
                continue;
            if (!settings.IPv6Enabled && prefix.Block.IsIpv6)
                continue;
            selected.Add(prefix);
        }

        selected.Sort((left, right) => ComparePrefixes(left, right, selection.Region));
        var ordered = new List<FeedPrefix>(selected.Count);
        foreach (CloudPrefix prefix in selected)
            ordered.Add(new FeedPrefix(prefix.Block, prefix.Scope.Id));

        return _engine.CheckAsync(Describe(catalogue.Source, selection), ordered, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
        _engine.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        _engine.WriteAsync(plan, includedLargeRanges, ct);

    private static FeedDescription Describe(CloudRangeSource source, RegionSelection selection)
    {
        string region = selection.AddsGlobal
            ? selection.Region.Id + " + global"
            : selection.Region.Id;
        string global = selection.AddsGlobal ? "_global" : "";
        return new FeedDescription(
            GoogleConfirmation.GoogleDomain,
            [
                $"Ranges: Google Cloud, {source.Url.AbsoluteUri} (created {SingleLine(source.CreationTime)})",
                $"Region: {region}",
            ],
            $"GoogleCloud_{selection.Region.Id}{global}");
    }

    /// <summary>The creation time comes from the downloaded file, so a line break in it must not start a new feed line.</summary>
    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static int ComparePrefixes(CloudPrefix left, CloudPrefix right, GoogleCloudScope region)
    {
        int rank = (left.Scope == region ? 0 : 1).CompareTo(right.Scope == region ? 0 : 1);
        if (rank != 0)
            return rank;

        int family = left.Block.IsIpv6.CompareTo(right.Block.IsIpv6);
        if (family != 0)
            return family;

        byte[] leftStart = left.Block.Start;
        byte[] rightStart = right.Block.Start;
        for (int i = 0; i < leftStart.Length; i++)
        {
            int byteOrder = leftStart[i].CompareTo(rightStart[i]);
            if (byteOrder != 0)
                return byteOrder;
        }

        // List.Sort is not stable, so two blocks with one start (10.0.0.0/8 and 10.0.0.0/16) need an order of their own
        return left.Block.PrefixLength.CompareTo(right.Block.PrefixLength);
    }
}
