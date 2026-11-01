using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoorMansFeed.Core;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class GoogleCloudFeedServiceCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 15, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    private byte[] _file = RangesFile("1770000000000", "2026-01-15T00:00:00Z", "34.80.1.0/24");
    private HttpStatusCode _status = HttpStatusCode.OK;
    private Exception? _failure;
    private bool _hideLength;

    public GoogleCloudFeedServiceCacheTests()
    {
        _handler = new StubHttpHandler(Respond);
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_Good_Fetch_Saves_The_File_Exactly_As_Served_With_Its_Record()
    {
        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        CachedGoogleRanges cached = Cache().TryRead()!;
        Assert.NotNull(cached);
        CloudRangeSource source = result.Catalogue!.Source;
        Assert.Equal(source.Url, cached.Source.Url);
        Assert.Equal(source.SyncToken, cached.Source.SyncToken);
        Assert.Equal(source.CreationTime, cached.Source.CreationTime);
        Assert.Equal(Now, cached.Source.DownloadedUtc);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_file)), cached.Source.Sha256);
        Assert.Equal(_file.Length, cached.Source.Length);
        Assert.Equal(_file, cached.Json);
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
    }

    [Fact]
    public async Task A_Good_Fetch_Rewrites_The_Pair_Only_When_Its_Bytes_Change()
    {
        await Service().LoadCatalogueAsync(Ct);
        _file = RangesFile("1770000000999", "2026-01-16T00:00:00Z", "34.80.1.0/24", "35.190.1.0/24");
        _time.Advance(TimeSpan.FromHours(1));

        await Service().LoadCatalogueAsync(Ct);

        CachedGoogleRanges second = Cache().TryRead()!;
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        Assert.Equal("1770000000999", second.Source.SyncToken);
        Assert.Equal("2026-01-16T00:00:00Z", second.Source.CreationTime);
        Assert.Equal(_time.GetUtcNow(), second.Source.DownloadedUtc);
        byte[] body = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath);
        byte[] record = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath);

        _time.Advance(TimeSpan.FromHours(1));
        CatalogueResult again = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, again.Failure);
        Assert.Equal(2, again.Catalogue!.Prefixes.Count);
        Assert.Equal(body, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        Assert.Equal(record, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath));
        Assert.Equal(second.Source.DownloadedUtc, Cache().TryRead()!.Source.DownloadedUtc);
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
    }

    [Theory]
    [InlineData("missing-record")]
    [InlineData("missing-body")]
    [InlineData("record-length")]
    [InlineData("body")]
    [InlineData("record-http-url")]
    [InlineData("record-null-token")]
    [InlineData("record-user-info")]
    [InlineData("record-fragment")]
    public async Task A_Same_Body_Fetch_Heals_A_Damaged_Pair(string damage)
    {
        await Service().LoadCatalogueAsync(Ct);
        string body = _temp.Paths.GoogleCloudRangesPath;
        string record = _temp.Paths.GoogleCloudRangesSourcePath;
        switch (damage)
        {
            case "missing-record": File.Delete(record); break;
            case "missing-body": File.Delete(body); break;
            case "record-length": File.WriteAllText(record, File.ReadAllText(record).Replace("\"length\": ", "\"length\": 1")); break;
            case "body": File.WriteAllBytes(body, [.. _file.AsSpan(0, _file.Length - 1), (byte)' ']); break;
            case "record-http-url": Rewrite(record, source => source with { Url = new Uri("http://www.gstatic.com/ipranges/cloud.json") }); break;
            case "record-null-token": Rewrite(record, source => source with { SyncToken = null! }); break;
            case "record-user-info": Rewrite(record, source => source with { Url = new Uri("https://someone@www.gstatic.com/ipranges/cloud.json") }); break;
            case "record-fragment": Rewrite(record, source => source with { Url = new Uri("https://www.gstatic.com/ipranges/cloud.json#x") }); break;
        }
        // A record that differs from the fetch only in user info or the fragment still reads; the fetch must replace it
        if (damage is not ("record-user-info" or "record-fragment"))
            Assert.Null(Cache().TryRead());
        _time.Advance(TimeSpan.FromHours(1));

        await Service().LoadCatalogueAsync(Ct);

        CachedGoogleRanges healed = Cache().TryRead()!;
        Assert.NotNull(healed);
        Assert.Equal(_file, healed.Json);
        Assert.Equal(new Uri(new CoreOptions().GoogleCloudRangesUrl).AbsoluteUri, healed.Source.Url.AbsoluteUri);
        Assert.Equal(_time.GetUtcNow(), healed.Source.DownloadedUtc);
    }

    [Fact]
    public async Task A_Same_Body_Fetch_From_Another_Address_Rewrites_The_Record()
    {
        await Service().LoadCatalogueAsync(Ct);
        _time.Advance(TimeSpan.FromHours(1));
        var moved = new CoreOptions { GoogleCloudRangesUrl = "https://www.gstatic.com/ipranges/cloud-moved.json" };

        CatalogueResult result = await new GoogleCloudFeedService(_ipinfo, _http, moved, _time, _temp.Paths, _temp.ExportFolder)
            .LoadCatalogueAsync(Ct);

        CachedGoogleRanges saved = Cache().TryRead()!;
        Assert.Equal(result.Catalogue!.Source.Url, saved.Source.Url);
        Assert.Equal(new Uri(moved.GoogleCloudRangesUrl), saved.Source.Url);
        Assert.Equal(_time.GetUtcNow(), saved.Source.DownloadedUtc);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("offline")]
    [InlineData("not-json")]
    [InlineData("no-prefixes")]
    [InlineData("over-cap")]
    [InlineData("over-cap-unsaid")]
    public async Task A_Failed_Fetch_Leaves_The_Saved_Pair_Byte_For_Byte(string failure)
    {
        await Service().LoadCatalogueAsync(Ct);
        byte[] body = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath);
        byte[] record = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath);
        _time.Advance(TimeSpan.FromHours(1));
        Fail(failure);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.NotEqual(CatalogueFailure.None, result.Failure);
        Assert.Null(result.Catalogue);
        Assert.Equal(body, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        Assert.Equal(record, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("offline")]
    [InlineData("not-json")]
    [InlineData("no-prefixes")]
    [InlineData("over-cap")]
    [InlineData("over-cap-unsaid")]
    public async Task A_Failed_Fetch_With_Nothing_Saved_Writes_Nothing(string failure)
    {
        Fail(failure);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.NotEqual(CatalogueFailure.None, result.Failure);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Fact]
    public async Task Without_A_Data_Folder_The_Fetch_Works_And_Nothing_Is_Written()
    {
        var service = new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), _time, null, _temp.ExportFolder);

        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
        Assert.Null(new GoogleRangeCache(null, GoogleCloudRangeClient.MaxBodyBytes).TryRead());
    }

    [Fact]
    public async Task A_Save_That_Fails_Does_Not_Stop_The_Fetch()
    {
        Directory.CreateDirectory(_temp.Paths.GoogleCloudRangesPath);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.False(File.Exists(_temp.Paths.GoogleCloudRangesSourcePath));
        Assert.Null(Cache().TryRead());
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
    }

    [Theory]
    [InlineData("empty-prefixes", 0, 0, "1770000000000")]
    [InlineData("all-skipped", 0, 2, "1770000000000")]
    public async Task A_Fetch_That_Would_Not_Read_Back_Is_Returned_But_Leaves_The_Saved_Pair_Byte_For_Byte(
        string kind, int prefixes, int skipped, string syncToken)
    {
        await Service().LoadCatalogueAsync(Ct);
        byte[] body = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath);
        byte[] record = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath);
        _time.Advance(TimeSpan.FromHours(1));
        _file = Unsaveable(kind);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Equal(prefixes, result.Catalogue.Prefixes.Count);
        Assert.Equal(skipped, result.Catalogue.SkippedEntries);
        Assert.Equal(syncToken, result.Catalogue.Source.SyncToken);
        Assert.Equal(body, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        Assert.Equal(record, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesSourcePath));
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
    }

    [Theory]
    [InlineData("empty-prefixes")]
    [InlineData("all-skipped")]
    public async Task A_Fetch_That_Would_Not_Read_Back_With_Nothing_Saved_Writes_Nothing(string kind)
    {
        _file = Unsaveable(kind);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Theory]
    [InlineData("no-token", "")]
    [InlineData("empty-token", "")]
    [InlineData("letter-token", "177000000000a")]
    [InlineData("long-token", "123456789012345678901")]
    public async Task A_Fetch_With_Any_Sync_Token_Is_Saved_And_Reads_Back(string kind, string syncToken)
    {
        await Service().LoadCatalogueAsync(Ct);
        _time.Advance(TimeSpan.FromHours(1));
        _file = AnyToken(kind);

        CatalogueResult result = await Service().LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.Single(result.Catalogue!.Prefixes);
        Assert.Equal(syncToken, result.Catalogue.Source.SyncToken);
        CachedGoogleRanges cached = Cache().TryRead()!;
        Assert.NotNull(cached);
        Assert.Equal(_file, cached.Json);
        Assert.Equal(syncToken, cached.Source.SyncToken);
        Assert.Equal(_time.GetUtcNow(), cached.Source.DownloadedUtc);
        CloudRangeCatalogue? saved = GoogleCloudRangeClient.TryParseSaved(cached.Json, cached.Source.Url);
        Assert.NotNull(saved);
        Assert.Equal(result.Catalogue.Source, saved.Source);
        Assert.Equal(syncToken, saved.Source.SyncToken);
    }

    [Fact]
    public void A_Record_The_Reader_Would_Refuse_Is_Not_Written()
    {
        var source = new GoogleRangeFileSource(new Uri("cloud.json", UriKind.Relative), "1770000000000",
                                               "2026-01-15T00:00:00Z", Now,
                                               Convert.ToHexStringLower(SHA256.HashData(_file)), _file.Length);

        Assert.False(Cache().TryWrite(_file, source));
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("truncated")]
    [InlineData("record-length")]
    [InlineData("missing-record")]
    [InlineData("missing-body")]
    [InlineData("bad-record")]
    [InlineData("over-max")]
    [InlineData("relative-url")]
    [InlineData("http-url")]
    [InlineData("null-token")]
    [InlineData("null-creation-time")]
    public async Task A_Damaged_Saved_Pair_Counts_As_Missing_And_Is_Not_Deleted(string damage)
    {
        await Service().LoadCatalogueAsync(Ct);
        string body = _temp.Paths.GoogleCloudRangesPath;
        string record = _temp.Paths.GoogleCloudRangesSourcePath;
        long maxBytes = GoogleCloudRangeClient.MaxBodyBytes;
        switch (damage)
        {
            case "body": File.WriteAllBytes(body, [.. _file.AsSpan(0, _file.Length - 1), (byte)' ']); break;
            case "truncated": File.WriteAllBytes(body, _file.AsSpan(0, _file.Length - 5).ToArray()); break;
            case "record-length": File.WriteAllText(record, File.ReadAllText(record).Replace("\"length\": ", "\"length\": 1")); break;
            case "missing-record": File.Delete(record); break;
            case "missing-body": File.Delete(body); break;
            case "bad-record": File.WriteAllText(record, "{not json"); break;
            case "over-max": maxBytes = _file.Length - 1; break;
            case "relative-url": Rewrite(record, source => source with { Url = new Uri("cloud.json", UriKind.Relative) }); break;
            case "http-url": Rewrite(record, source => source with { Url = new Uri("http://www.gstatic.com/ipranges/cloud.json") }); break;
            case "null-token": Rewrite(record, source => source with { SyncToken = null! }); break;
            case "null-creation-time": Rewrite(record, source => source with { CreationTime = null! }); break;
        }

        Assert.Null(new GoogleRangeCache(_temp.Paths, maxBytes).TryRead());
        Assert.Equal(damage is not "missing-body", File.Exists(body));
        Assert.Equal(damage is not "missing-record", File.Exists(record));
    }

    [Fact]
    public async Task The_Saved_File_Parses_To_The_Catalogue_The_Fetch_Gave()
    {
        _file = Encoding.UTF8.GetBytes("""
            {
              "syncToken": "1770000000000",
              "creationTime": "2026-01-15T00:00:00Z",
              "prefixes": [
                {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
                {"ipv6Prefix": "2600:1900:4000::/44", "service": "Google Cloud", "scope": "us-central1"},
                {"ipv4Prefix": "35.199.0.0/24", "service": "Google Cloud", "scope": "global"},
                {"ipv4Prefix": "bad", "service": "Google Cloud", "scope": "us-central1"},
                {"ipv4Prefix": "1.2.4.0/24", "service": "Google", "scope": "europe-west1"}
              ]
            }
            """);
        CloudRangeCatalogue fetched = (await Service().LoadCatalogueAsync(Ct)).Catalogue!;
        CachedGoogleRanges cached = Cache().TryRead()!;

        CloudRangeCatalogue? saved = GoogleCloudRangeClient.TryParseSaved(cached.Json, cached.Source.Url);

        Assert.NotNull(saved);
        Assert.Equal(fetched.Source, saved.Source);
        Assert.Equal(fetched.Prefixes.Select(prefix => (prefix.Block.Text, prefix.Scope.Id)),
                     saved.Prefixes.Select(prefix => (prefix.Block.Text, prefix.Scope.Id)));
        Assert.Equal(fetched.Regions, saved.Regions);
        Assert.Equal(2, saved.SkippedEntries);
        Assert.Equal(fetched.SkippedEntries, saved.SkippedEntries);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"syncToken\":\"1\"}")]
    [InlineData("{\"syncToken\":\"1\",\"creationTime\":\"2026-01-15T00:00:00Z\",\"prefixes\":[]}")]
    [InlineData("{\"syncToken\":\"1\",\"creationTime\":\"2026-01-15T00:00:00Z\",\"prefixes\":["
                + "{\"ipv4Prefix\":\"34.80.1.0/24\",\"service\":\"Other\",\"scope\":\"us-central1\"},"
                + "{\"ipv4Prefix\":\"34.80.2.0/24\",\"ipv6Prefix\":\"2600:1900::/44\",\"service\":\"Google Cloud\",\"scope\":\"us-central1\"}]}")]
    public void A_Saved_File_That_Is_Not_A_Catalogue_Parses_To_Null(string json)
    {
        Assert.Null(GoogleCloudRangeClient.TryParseSaved(Encoding.UTF8.GetBytes(json), new Uri(new CoreOptions().GoogleCloudRangesUrl)));
    }

    private GoogleCloudFeedService Service() =>
        new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private GoogleRangeCache Cache() => new(_temp.Paths, GoogleCloudRangeClient.MaxBodyBytes);

    private void Fail(string failure)
    {
        switch (failure)
        {
            case "status": _status = HttpStatusCode.InternalServerError; break;
            case "offline": _failure = new HttpRequestException("offline"); break;
            case "not-json": _file = Encoding.UTF8.GetBytes("{not json"); break;
            case "no-prefixes": _file = Encoding.UTF8.GetBytes("{\"syncToken\":\"1\"}"); break;
            case "over-cap": _file = OverCap(); break;
            case "over-cap-unsaid": _file = OverCap(); _hideLength = true; break;
        }
    }

    private static void Rewrite(string record, Func<GoogleRangeFileSource, GoogleRangeFileSource> change)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GoogleRangeFileSource source = JsonSerializer.Deserialize<GoogleRangeFileSource>(File.ReadAllBytes(record), options)!;
        File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(change(source), options));
    }

    private static byte[] Unsaveable(string kind) => kind switch
    {
        "empty-prefixes" => RangesFile("1770000000000", "2026-01-15T00:00:00Z"),
        "all-skipped" => Encoding.UTF8.GetBytes("""
            {
              "syncToken": "1770000000000",
              "creationTime": "2026-01-15T00:00:00Z",
              "prefixes": [
                {"ipv4Prefix": "34.80.1.0/24", "service": "Other", "scope": "us-central1"},
                {"ipv4Prefix": "34.80.2.0/24", "ipv6Prefix": "2600:1900::/44", "service": "Google Cloud", "scope": "us-central1"}
              ]
            }
            """),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] AnyToken(string kind) => kind switch
    {
        "no-token" => Encoding.UTF8.GetBytes("""
            {"creationTime": "2026-01-15T00:00:00Z",
             "prefixes": [{"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"}]}
            """),
        "empty-token" => RangesFile("", "2026-01-15T00:00:00Z", "34.80.1.0/24"),
        "letter-token" => RangesFile("177000000000a", "2026-01-15T00:00:00Z", "34.80.1.0/24"),
        "long-token" => RangesFile("123456789012345678901", "2026-01-15T00:00:00Z", "34.80.1.0/24"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] OverCap()
    {
        byte[] body = new byte[GoogleCloudRangeClient.MaxBodyBytes + 10];
        Array.Fill(body, (byte)' ');
        body[0] = (byte)'{';
        body[^1] = (byte)'}';
        return body;
    }

    private HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        if (_failure is not null)
            throw _failure;
        if (_status != HttpStatusCode.OK)
            return new HttpResponseMessage(_status);

        HttpContent content = _hideLength ? new UnknownLengthContent(_file) : new ByteArrayContent(_file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] RangesFile(string syncToken, string creationTime, params string[] prefixes)
    {
        var entries = prefixes.Select(prefix => new Dictionary<string, string>
        {
            ["ipv4Prefix"] = prefix,
            ["service"] = "Google Cloud",
            ["scope"] = "us-central1",
        });
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["syncToken"] = syncToken,
            ["creationTime"] = creationTime,
            ["prefixes"] = entries.ToArray(),
        });
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
