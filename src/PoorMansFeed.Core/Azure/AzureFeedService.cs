using System.Diagnostics;
using System.Security.Cryptography;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.Azure;

public sealed class AzureFeedService : IAzureFeedService
{
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
        if (cached is null)
            return null;

        AzureTagCatalogue? catalogue = Build(cached.Source, cached.Json);
        if (catalogue is null)
            Trace.TraceWarning("The saved Azure service tags could not be parsed, so they are not used.");
        return catalogue;
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

        await Task.Run(() => _cache.TryWrite(json, fresh.Source), ct);
        return new AzureLoadResult(AzureUpdateStatus.Updated, AzureSourceFailure.None, fresh, _time.GetUtcNow());
    }

    public Task<CheckResult> CheckAsync(AzureTagCatalogue catalogue, AzureTagSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();

        AzureServiceTag? tag = catalogue.Find(selection.TagName);
        if (tag is null)
            return _engine.CheckAsync(Describe(catalogue.Source, selection.TagName), [], ct);

        string scope = tag.Region.Length > 0 ? tag.Region : GlobalScope;
        var ordered = new List<FeedPrefix>(tag.Prefixes.Count);
        foreach (CidrBlock block in tag.Prefixes)
            ordered.Add(new FeedPrefix(block, scope));

        return _engine.CheckAsync(Describe(catalogue.Source, tag.Name), ordered, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
        _engine.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        _engine.WriteAsync(plan, includedLargeRanges, ct);

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
