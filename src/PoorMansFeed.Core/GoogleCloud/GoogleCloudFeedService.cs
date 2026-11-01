using System.Diagnostics;
using System.Security.Cryptography;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core.GoogleCloud;

public sealed class GoogleCloudFeedService : IGoogleCloudFeedService
{
    public event EventHandler<CloudSourceUpdated<CloudRangeCatalogue>>? SourceUpdated;

    private readonly IIpInfoService _ipinfo;
    private readonly GoogleCloudRangeClient _client;
    private readonly CloudFeedEngine _engine;
    private readonly TimeProvider _time;
    private readonly GoogleRangeCache _cache;

    public GoogleCloudFeedService(IIpInfoService ipinfo, HttpClient http, CoreOptions options, TimeProvider time,
                                  AppPaths? paths, string defaultExportFolder)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(defaultExportFolder);
        _client = new GoogleCloudRangeClient(http, options);
        _engine = new CloudFeedEngine(ipinfo, time, defaultExportFolder);
        _time = time;
        _cache = new GoogleRangeCache(paths, GoogleCloudRangeClient.MaxBodyBytes);
    }

    public async Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct)
    {
        GoogleFetch fetch = await _client.FetchAsync(ct);
        CloudSourceUpdated<CloudRangeCatalogue>? updated = null;
        if (fetch.Failure == CatalogueFailure.None && fetch.Body is byte[] body && fetch.Catalogue is CloudRangeCatalogue catalogue
            && catalogue.Prefixes.Count > 0)
        {
            CloudRangeSource from = catalogue.Source;
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(body));
            var source = new GoogleRangeFileSource(from.Url, from.SyncToken, from.CreationTime, _time.GetUtcNow(),
                                                   sha256, body.Length);
            bool replaced = await Task.Run(() =>
            {
                // The same bytes keep their pair, so the record's download time stays when they were first downloaded.
                // The text is compared, because Uri's == ignores user info and the fragment.
                CachedGoogleRanges? saved = _cache.TryRead();
                bool sameBytes = saved is not null
                                 && string.Equals(saved.Source.Sha256, sha256, StringComparison.OrdinalIgnoreCase);
                if (sameBytes && string.Equals(saved?.Source.Url.AbsoluteUri, from.Url.AbsoluteUri, StringComparison.Ordinal))
                    return false;
                // Only other bytes are told to listeners; a pair that doesn't read back counts as other bytes
                return _cache.TryWrite(body, source) && !sameBytes;
            }, ct);
            if (replaced)
                updated = new CloudSourceUpdated<CloudRangeCatalogue>(catalogue, source.DownloadedUtc);
        }

        var result = new CatalogueResult(fetch.Failure, fetch.Catalogue);
        if (updated is not null)
            CloudSourceEvents.Tell(this, SourceUpdated, updated);
        return result;
    }

    public Task<SavedRanges<CloudRangeCatalogue>> LoadSavedAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (!_cache.AnyFileExists())
                return new SavedRanges<CloudRangeCatalogue>(SavedRangesState.NotSaved, null, null);

            CachedGoogleRanges? cached = _cache.TryRead();
            if (cached is not null && GoogleCloudRangeClient.TryParseSaved(cached.Json, cached.Source.Url) is CloudRangeCatalogue catalogue)
                return new SavedRanges<CloudRangeCatalogue>(SavedRangesState.Read, catalogue, cached.Source.DownloadedUtc);

            return new SavedRanges<CloudRangeCatalogue>(SavedRangesState.Unusable, null, null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            Trace.TraceWarning("The saved Google Cloud ranges could not be read: " + ex.GetType().Name);
            return new SavedRanges<CloudRangeCatalogue>(SavedRangesState.Unusable, null, null);
        }
    }, ct);

    public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ct.ThrowIfCancellationRequested();
        // Gone before the dataset is asked, so a pick that is gone is told apart from no dataset
        if (!catalogue.Regions.Any(region => region.Scope == selection.Region)
            || (selection.AddsGlobal && !catalogue.Regions.Any(region => region.Scope.IsGlobal)))
            return Task.FromResult(new CheckResult(CheckFailure.PickGone, null));

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

        var pick = new CloudFeedPick(FeedKind.GoogleCloud, selection.Region.Id, selection.AddsGlobal, null, null, null);
        return _engine.CheckAsync(Describe(catalogue.Source, selection), ordered, pick, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
        _engine.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        _engine.WriteAsync(plan, includedLargeRanges, ct);

    Task<CloudFeedRefresh> ICloudFeedService.RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc,
                                                         CancellationToken ct) =>
        _engine.RefreshAsync(check, checkStartedUtc, ct);

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
