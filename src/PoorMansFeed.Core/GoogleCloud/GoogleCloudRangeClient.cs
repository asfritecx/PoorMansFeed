using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoorMansFeed.Core;

namespace PoorMansFeed.Core.GoogleCloud;

/// <summary>Body is the bytes as served, set only when Failure is None, so an over-cap read is never saved.</summary>
internal sealed record GoogleFetch(CatalogueFailure Failure, CloudRangeCatalogue? Catalogue, byte[]? Body);

internal sealed class GoogleCloudRangeClient
{
    internal const int MaxBodyBytes = 8 * 1024 * 1024;
    private const string GoogleCloudService = "Google Cloud";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly CoreOptions _options;

    public GoogleCloudRangeClient(HttpClient http, CoreOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<GoogleFetch> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(_options.HttpTimeoutMinutes));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.GoogleCloudRangesUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"Google Cloud ranges request failed with HTTP {(int)response.StatusCode}.");
                return new GoogleFetch(CatalogueFailure.ServiceError, null, null);
            }

            if (response.Content.Headers.ContentLength is long length && length > MaxBodyBytes)
            {
                Trace.TraceWarning("Google Cloud ranges response was larger than the size cap.");
                return new GoogleFetch(CatalogueFailure.Unreadable, null, null);
            }

            byte[] body = await ReadCappedAsync(response, linked.Token);
            if (body.Length > MaxBodyBytes)
                return new GoogleFetch(CatalogueFailure.Unreadable, null, null);

            CatalogueResult parsed = Parse(body, new Uri(_options.GoogleCloudRangesUrl));
            return parsed.Failure == CatalogueFailure.None
                ? new GoogleFetch(CatalogueFailure.None, parsed.Catalogue, body)
                : new GoogleFetch(parsed.Failure, null, null);
        }
        catch (JsonException)
        {
            Trace.TraceWarning("Google Cloud ranges response was not valid JSON.");
            return new GoogleFetch(CatalogueFailure.Unreadable, null, null);
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning("Google Cloud ranges endpoint could not be reached.");
            return new GoogleFetch(CatalogueFailure.Offline, null, null);
        }
        catch (IOException)
        {
            Trace.TraceWarning("Google Cloud ranges response could not be read.");
            return new GoogleFetch(CatalogueFailure.Offline, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Google Cloud ranges request timed out.");
            return new GoogleFetch(CatalogueFailure.Offline, null, null);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            if (body.Length + read > MaxBodyBytes)
                return new byte[MaxBodyBytes + 1];

            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }

    /// <summary>The saved file, parsed as a fetch's body is, under its record's Url; null if it isn't one.</summary>
    internal static CloudRangeCatalogue? TryParseSaved(byte[] json, Uri url)
    {
        try
        {
            CatalogueResult parsed = Parse(json, url);
            if (parsed.Failure != CatalogueFailure.None || parsed.Catalogue is null)
                return null;

            if (parsed.Catalogue.Prefixes.Count == 0)
            {
                Trace.TraceWarning("The saved Google Cloud ranges hold no usable range, so they are not used.");
                return null;
            }

            return parsed.Catalogue;
        }
        catch (JsonException)
        {
            Trace.TraceWarning("The saved Google Cloud ranges could not be parsed, so they are not used.");
            return null;
        }
    }

    private static CatalogueResult Parse(byte[] body, Uri url)
    {
        CloudJsonFile? file = JsonSerializer.Deserialize<CloudJsonFile>(body, JsonOptions);
        if (file is null || file.Prefixes.ValueKind != JsonValueKind.Array)
            return new CatalogueResult(CatalogueFailure.Unreadable, null);

        var prefixes = new List<CloudPrefix>();
        int skipped = 0;
        foreach (JsonElement item in file.Prefixes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            CloudJsonPrefix? entry;
            try
            {
                entry = item.Deserialize<CloudJsonPrefix>(JsonOptions);
            }
            catch (JsonException)
            {
                skipped++;
                continue;
            }

            if (entry is null || !TryAccept(entry, out CloudPrefix? prefix))
            {
                skipped++;
                continue;
            }

            prefixes.Add(prefix);
        }

        var regions = prefixes
            .GroupBy(prefix => prefix.Scope)
            .Select(group => new CloudRegion(
                group.Key,
                group.Count(prefix => !prefix.Block.IsIpv6),
                group.Count(prefix => prefix.Block.IsIpv6)))
            .OrderBy(region => region.Scope.IsGlobal ? 0 : 1)
            .ThenBy(region => region.Scope.Id, StringComparer.Ordinal)
            .ToList();

        var source = new CloudRangeSource(url, file.SyncToken ?? "", file.CreationTime ?? "");
        return new CatalogueResult(CatalogueFailure.None, new CloudRangeCatalogue(source, prefixes, regions, skipped));
    }

    private static bool TryAccept(CloudJsonPrefix entry, out CloudPrefix prefix)
    {
        prefix = null!;
        if (!string.Equals(entry.Service, GoogleCloudService, StringComparison.Ordinal))
            return false;

        bool hasIpv4 = !string.IsNullOrWhiteSpace(entry.Ipv4Prefix);
        bool hasIpv6 = !string.IsNullOrWhiteSpace(entry.Ipv6Prefix);
        if (hasIpv4 == hasIpv6)
            return false;

        string text = hasIpv4 ? entry.Ipv4Prefix! : entry.Ipv6Prefix!;
        if (!CidrBlock.TryParse(text, out CidrBlock? block))
            return false;

        if (!GoogleCloudScope.TryCreate(entry.Scope, out GoogleCloudScope scope))
            return false;

        prefix = new CloudPrefix(block, scope);
        return true;
    }

    private sealed class CloudJsonFile
    {
        [JsonPropertyName("syncToken")]
        public string? SyncToken { get; set; }

        [JsonPropertyName("creationTime")]
        public string? CreationTime { get; set; }

        [JsonPropertyName("prefixes")]
        public JsonElement Prefixes { get; set; }
    }

    private sealed class CloudJsonPrefix
    {
        [JsonPropertyName("ipv4Prefix")]
        public string? Ipv4Prefix { get; set; }

        [JsonPropertyName("ipv6Prefix")]
        public string? Ipv6Prefix { get; set; }

        [JsonPropertyName("service")]
        public string? Service { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
