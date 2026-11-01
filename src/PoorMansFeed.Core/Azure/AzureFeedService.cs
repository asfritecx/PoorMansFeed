using System.Diagnostics;
using System.Security.Cryptography;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core.Azure;

public sealed class AzureFeedService : IAzureFeedService
{
    public event EventHandler<CloudSourceUpdated<AzureTagCatalogue>>? SourceUpdated;

    public const string MicrosoftDomain = "microsoft.com";

    private const string GlobalScope = "global";

    private readonly AzureServiceTagsClient _client;
    private readonly AzureTagCache _cache;
    private readonly CloudFeedEngine _engine;
    private readonly TimeProvider _time;

    /// <param name="paths">Where the saved file lives. Null keeps nothing between runs.</param>
    public AzureFeedService(IIpInfoService ipinfo, HttpClient http, CoreOptions options, TimeProvider time,
                            AppPaths? paths, string defaultExportFolder)
    {
        ArgumentNullException.ThrowIfNull(ipinfo);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentNullException.ThrowIfNull(defaultExportFolder);
        _client = new AzureServiceTagsClient(http, options);
        _cache = new AzureTagCache(paths, options.AzureMaxFileBytes);
        _engine = new CloudFeedEngine(ipinfo, time, defaultExportFolder);
    }

    public Task<AzureTagCatalogue?> LoadCachedAsync(CancellationToken ct) => Task.Run(() =>
    {
        CachedAzureTags? cached = _cache.TryRead();
        return cached is null ? null : ParseSaved(cached);
    }, ct);

    public Task<SavedRanges<AzureTagCatalogue>> LoadSavedAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (!_cache.AnyFileExists())
                return new SavedRanges<AzureTagCatalogue>(SavedRangesState.NotSaved, null, null);

            CachedAzureTags? cached = _cache.TryRead();
            AzureTagCatalogue? catalogue = cached is null ? null : ParseSaved(cached);
            return catalogue is null
                ? new SavedRanges<AzureTagCatalogue>(SavedRangesState.Unusable, null, null)
                : new SavedRanges<AzureTagCatalogue>(SavedRangesState.Read, catalogue, catalogue.Source.DownloadedUtc);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            Trace.TraceWarning("The saved Azure service tags could not be read: " + ex.GetType().Name);
            return new SavedRanges<AzureTagCatalogue>(SavedRangesState.Unusable, null, null);
        }
    }, ct);

    public async Task<AzureLoadResult> CheckForUpdateAsync(AzureTagCatalogue? current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AzurePageResult page = await _client.GetPublishedFileAsync(ct);
        if (page.File is null)
            return Failed(page.Failure, current);

        if (current is not null && current.Source.Url == page.File.Url)
            return new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, _time.GetUtcNow());

        AzureFileResult download = await _client.DownloadAsync(page.File, ct);
        if (download.Body is null)
            return Failed(download.Failure, current);

        byte[] json = download.Body;
        AzureTagCatalogue? fresh = await Task.Run(() => BuildDownloaded(page.File, json), ct);
        if (fresh is null)
        {
            Trace.TraceWarning("The downloaded Azure file was not usable Public cloud service tags.");
            return Failed(AzureSourceFailure.FileUnreadable, current);
        }

        if (current is not null && fresh.Source.ChangeNumber < current.Source.ChangeNumber)
            Trace.TraceWarning("The published Azure file has a lower change number than the saved one.");

        bool saved = await Task.Run(() => _cache.TryWrite(json, fresh.Source), ct);
        var result = new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, fresh, _time.GetUtcNow());
        // A new address serving the same file replaced nothing a listener has, so only other bytes are told
        if (saved && !string.Equals(current?.Source.Sha256, fresh.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            CloudSourceEvents.Tell(this, SourceUpdated,
                                   new CloudSourceUpdated<AzureTagCatalogue>(fresh, fresh.Source.DownloadedUtc));
        return result;
    }

    public Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();

        // Gone before the dataset is asked, so a pick that is gone is told apart from no dataset
        AzureServiceTag? tag = catalogue.Find(selection.TagName);
        if (tag is null)
            return Task.FromResult(new CheckResult(CheckFailure.PickGone, null));

        string scope = tag.Region.Length > 0 ? tag.Region : GlobalScope;
        var ordered = new List<FeedPrefix>(tag.Prefixes.Count);
        foreach (CidrBlock block in tag.Prefixes)
            ordered.Add(new FeedPrefix(block, scope));

        return _engine.CheckAsync(Describe(catalogue.Source, tag.Name), ordered,
                                  new CloudFeedPick(FeedKind.Azure, null, false, tag.Name, null, null), ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
        _engine.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        _engine.WriteAsync(plan, includedLargeRanges, ct);

    Task<CloudFeedRefresh> ICloudFeedService.RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc,
                                                         CancellationToken ct) =>
        _engine.RefreshAsync(check, checkStartedUtc, ct);

    private AzureLoadResult Failed(AzureSourceFailure failure, AzureTagCatalogue? current) => new(
        current is null ? AzureUpdateStatus.Unavailable : AzureUpdateStatus.CheckFailed,
        failure,
        current,
        _time.GetUtcNow());

    private AzureTagCatalogue? BuildDownloaded(AzurePublishedFile file, byte[] json)
    {
        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(json);
        if (parsed is null)
            return null;

        var source = new AzureTagFileSource(
            file.Url, file.FileName, file.FileDate, parsed.ChangeNumber, parsed.Cloud, _time.GetUtcNow(),
            Convert.ToHexStringLower(SHA256.HashData(json)), json.Length);
        return new AzureTagCatalogue(source, parsed.Services, parsed.SkippedTags, parsed.SkippedPrefixes);
    }

    private static AzureTagCatalogue? ParseSaved(CachedAzureTags cached)
    {
        AzureTagCatalogue? catalogue = Build(cached.Source, cached.Json);
        if (catalogue is null)
            Trace.TraceWarning("The saved Azure service tags could not be parsed, so they are not used.");
        return catalogue;
    }

    private static AzureTagCatalogue? Build(AzureTagFileSource source, byte[] json)
    {
        ParsedAzureTags? parsed = AzureServiceTagsParser.TryParse(json);
        return parsed is null
            ? null
            : new AzureTagCatalogue(source, parsed.Services, parsed.SkippedTags, parsed.SkippedPrefixes);
    }

    private static FeedDescription Describe(AzureTagFileSource source, string tagName) => new(
        MicrosoftDomain,
        [
            $"Ranges: Azure service tags, {source.FileName} (change {source.ChangeNumber})",
            $"Service tag: {tagName}",
        ],
        "Azure_" + tagName.Replace('.', '_'));
}
