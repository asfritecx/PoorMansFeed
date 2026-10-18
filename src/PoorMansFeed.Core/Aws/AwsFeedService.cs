using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.Aws;

public sealed class AwsFeedService : IAwsFeedService
{
    /// <summary>
    /// The IPinfo as_domain values that count as AWS. Outside China nearly every AWS range is amazon.com. The two
    /// China regions are operated by Sinnet (cn-north-1) and Ningxia Western Cloud Data (cn-northwest-1), and the
    /// AWS European Sovereign Cloud region by aws.eu, so each of those domains is an AWS operator here too.
    /// </summary>
    public static IReadOnlyList<string> ConfirmationDomains { get; } =
        ["amazon.com", "sinnet.com.cn", "westcloudvalley.com", "aws.eu"];

    private readonly AwsRangesClient _client;
    private readonly AwsRangeCache _cache;
    private readonly CloudFeedEngine _engine;
    private readonly TimeProvider _time;

    /// <param name="paths">Where the saved file lives. Null keeps nothing between runs.</param>
    public AwsFeedService(IIpInfoService ipinfo, HttpClient http, CoreOptions options, TimeProvider time,
                          AppPaths? paths, string defaultExportFolder)
    {
        ArgumentNullException.ThrowIfNull(ipinfo);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ArgumentNullException.ThrowIfNull(defaultExportFolder);
        _client = new AwsRangesClient(http, options);
        _cache = new AwsRangeCache(paths, options.AwsMaxFileBytes);
        _engine = new CloudFeedEngine(ipinfo, time, defaultExportFolder);
    }

    public Task<AwsRangeCatalogue?> LoadCachedAsync(CancellationToken ct) => Task.Run(() =>
    {
        CachedAwsRanges? cached = _cache.TryRead();
        if (cached is null)
            return null;

        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(cached.Json);
        if (parsed is null)
        {
            Trace.TraceWarning("The saved AWS ranges could not be parsed, so they are not used.");
            return null;
        }

        return new AwsRangeCatalogue(cached.Source, parsed.Services, parsed.SkippedPrefixes);
    }, ct);

    public async Task<AwsLoadResult> CheckForUpdateAsync(AwsRangeCatalogue? current, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AwsFetchResult fetch = await _client.FetchAsync(current?.Source.ETag, ct);
        if (fetch.Failure != AwsSourceFailure.None)
            return Failed(fetch.Failure, current);

        if (fetch.NotModified)
        {
            if (current is null)
            {
                Trace.TraceWarning("AWS answered not modified although no file was saved.");
                return Failed(AwsSourceFailure.ServiceError, null);
            }

            return UpToDate(current);
        }

        byte[] json = fetch.Body!;
        AwsRangeCatalogue? fresh = await Task.Run(() => BuildDownloaded(json, fetch.ETag), ct);
        if (fresh is null)
        {
            Trace.TraceWarning("The downloaded AWS file was not usable ip-ranges.json.");
            return Failed(AwsSourceFailure.FileUnreadable, current);
        }

        if (current is not null && string.Equals(fresh.Source.Sha256, current.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            return await SameFileAsync(current, json, fetch.ETag, ct);

        if (current is not null && IsLower(fresh.Source.SyncToken, current.Source.SyncToken))
            Trace.TraceWarning("The published AWS file has a lower sync token than the saved one.");

        await Task.Run(() => _cache.TryWrite(json, fresh.Source), ct);
        return new AwsLoadResult(AwsUpdateStatus.Updated, AwsSourceFailure.None, fresh, _time.GetUtcNow());
    }

    public Task<CheckResult> CheckAsync(AwsRangeCatalogue catalogue, AwsSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(selection);
        ct.ThrowIfCancellationRequested();

        AwsService? service = catalogue.Find(selection.Service);
        IReadOnlyList<AwsPrefix>? prefixes = null;
        if (service is not null)
            prefixes = selection.Region is null ? service.AllRegionsPrefixes : service.Find(selection.Region)?.Prefixes;

        if (service is null || prefixes is null)
            return _engine.CheckAsync(new FeedDescription(ConfirmationDomains, ["Ranges: AWS ip-ranges.json"], "AWS"), [], ct);

        var ordered = new List<FeedPrefix>(prefixes.Count);
        foreach (AwsPrefix prefix in prefixes)
            ordered.Add(new FeedPrefix(prefix.Block, prefix.BorderGroup));

        return _engine.CheckAsync(Describe(catalogue.Source, service.Name, selection.Region), ordered, ct);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
        _engine.PlanWrite(check, ticked);

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct) =>
        _engine.WriteAsync(plan, includedLargeRanges, ct);

    /// <summary>
    /// The downloaded bytes are what is already saved. If the server now names them with a different ETag, the
    /// record is rewritten with it, so the next check can be answered with a 304 instead of another download.
    /// </summary>
    private async Task<AwsLoadResult> SameFileAsync(AwsRangeCatalogue current, byte[] json, string? eTag, CancellationToken ct)
    {
        if (eTag is null || string.Equals(eTag, current.Source.ETag, StringComparison.Ordinal))
            return UpToDate(current);

        AwsRangeCatalogue renamed = current.WithSource(current.Source with { ETag = eTag });
        await Task.Run(() => _cache.TryWrite(json, renamed.Source), ct);
        return UpToDate(renamed);
    }

    private AwsLoadResult UpToDate(AwsRangeCatalogue catalogue) =>
        new(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, catalogue, _time.GetUtcNow());

    private AwsLoadResult Failed(AwsSourceFailure failure, AwsRangeCatalogue? current) => new(
        current is null ? AwsUpdateStatus.Unavailable : AwsUpdateStatus.CheckFailed,
        failure,
        current,
        _time.GetUtcNow());

    private AwsRangeCatalogue? BuildDownloaded(byte[] json, string? eTag)
    {
        ParsedAwsRanges? parsed = AwsRangesParser.TryParse(json);
        if (parsed is null)
            return null;

        var source = new AwsFileSource(
            parsed.SyncToken, parsed.CreatedUtc, eTag, _time.GetUtcNow(),
            Convert.ToHexStringLower(SHA256.HashData(json)), json.Length);
        return new AwsRangeCatalogue(source, parsed.Services, parsed.SkippedPrefixes);
    }

    private static bool IsLower(string token, string saved) =>
        ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ulong newer)
        && ulong.TryParse(saved, NumberStyles.None, CultureInfo.InvariantCulture, out ulong older)
        && newer < older;

    private static FeedDescription Describe(AwsFileSource source, string service, string? region)
    {
        string token = AwsRangesParser.IsValidSyncToken(source.SyncToken) ? source.SyncToken : "unknown";
        string created = source.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new FeedDescription(
            ConfirmationDomains,
            [
                $"Ranges: AWS ip-ranges.json (sync token {token}, created {created} UTC)",
                $"Service: {service}",
                $"Region: {region ?? "all regions"}",
            ],
            $"AWS_{service}_{region ?? "all"}");
    }
}
