using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AwsFeedServiceUpdateTests : IDisposable
{
    private const string FirstETag = "\"aaaa1111\"";
    private const string SecondETag = "\"bbbb2222\"";

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 30, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly List<string> _ifNoneMatch = [];

    private byte[] _file = RangesFile("1792136826", "10.1.0.0/16");
    private string? _eTag = FirstETag;
    private HttpStatusCode _status = HttpStatusCode.OK;
    private Exception? _failure;
    private bool _hideLength;
    private bool _ignoreConditional;

    public AwsFeedServiceUpdateTests()
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
    public async Task With_No_Cache_And_No_Network_Nothing_Is_Available()
    {
        _failure = new HttpRequestException("offline");

        AwsLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AwsSourceFailure.Offline, result.Failure);
        Assert.Null(result.Catalogue);
        Assert.Equal(Now, result.CheckedAt);
        Assert.False(File.Exists(_temp.Paths.AwsIpRangesPath));
    }

    [Fact]
    public async Task With_No_Cache_It_Downloads_Parses_And_Saves_The_File_Exactly_As_Served()
    {
        AwsLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal(AwsSourceFailure.None, result.Failure);
        AwsFileSource source = result.Catalogue!.Source;
        Assert.Equal("1792136826", source.SyncToken);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 7, 47, 6, TimeSpan.Zero), source.CreatedUtc);
        Assert.Equal(FirstETag, source.ETag);
        Assert.Equal(Now, source.DownloadedUtc);
        Assert.Equal(_file.Length, source.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_file)), source.Sha256);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.True(File.Exists(_temp.Paths.AwsIpRangesSourcePath));
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
        Assert.Equal(1, _handler.Calls);
        Assert.Equal([""], _ifNoneMatch);
    }

    [Fact]
    public async Task A_Saved_File_Loads_Back_With_The_Same_Source_And_Services()
    {
        AwsRangeCatalogue saved = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;

        AwsRangeCatalogue? loaded = await Service().LoadCachedAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Source, loaded.Source);
        Assert.Equal(saved.Services.Select(service => service.Name).ToArray(), loaded.Services.Select(service => service.Name).ToArray());
    }

    [Fact]
    public async Task Loading_With_No_Cache_Gives_Null_And_Makes_No_Request()
    {
        Assert.Null(await Service().LoadCachedAsync(Ct));
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_Saved_ETag_Is_Sent_Back_And_A_304_Means_Up_To_Date_With_Nothing_Rewritten()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesPath);
        byte[] sidecarBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesSourcePath);

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, result.Status);
        Assert.Equal(AwsSourceFailure.None, result.Failure);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(2, _handler.Calls);
        Assert.Equal(FirstETag, _ifNoneMatch[^1]);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.Equal(sidecarBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesSourcePath));
    }

    [Fact]
    public async Task A_Weak_ETag_Is_Stored_As_Served_And_Sent_Back_Unchanged()
    {
        _eTag = "W/\"weak-1\"";
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        Assert.Equal("W/\"weak-1\"", cached.Source.ETag);

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, result.Status);
        Assert.Equal("W/\"weak-1\"", _ifNoneMatch[^1]);
    }

    [Fact]
    public async Task A_New_File_With_A_New_ETag_Replaces_The_Cache()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _file = RangesFile("1792200000", "10.1.0.0/16", "10.2.0.0/16");
        _eTag = SecondETag;
        _time.Advance(TimeSpan.FromDays(2));

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal("1792200000", result.Catalogue!.Source.SyncToken);
        Assert.Equal(SecondETag, result.Catalogue.Source.ETag);
        Assert.Equal(_time.GetUtcNow(), result.Catalogue.Source.DownloadedUtc);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.Equal(result.Catalogue.Source, (await Service().LoadCachedAsync(Ct))!.Source);
    }

    [Fact]
    public async Task The_Same_File_Under_A_New_ETag_Is_Up_To_Date_And_The_New_ETag_Is_Remembered()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesPath);
        _eTag = SecondETag;

        AwsLoadResult first = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, first.Status);
        Assert.Equal(SecondETag, first.Catalogue!.Source.ETag);
        Assert.Equal(cached.Source.Sha256, first.Catalogue.Source.Sha256);
        Assert.Equal(cached.Source.DownloadedUtc, first.Catalogue.Source.DownloadedUtc);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.Equal(SecondETag, (await Service().LoadCachedAsync(Ct))!.Source.ETag);

        int before = _handler.Calls;
        AwsLoadResult second = await Service().CheckForUpdateAsync(first.Catalogue, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, second.Status);
        Assert.Same(first.Catalogue, second.Catalogue);
        Assert.Equal(SecondETag, _ifNoneMatch[^1]);
        Assert.Equal(before + 1, _handler.Calls);
    }

    [Fact]
    public async Task A_Server_That_Ignores_The_Condition_Still_Gets_Up_To_Date_For_An_Identical_File()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _ignoreConditional = true;

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, result.Status);
        Assert.Same(cached, result.Catalogue);
    }

    [Fact]
    public async Task A_Cache_Without_An_ETag_Sends_No_Condition_And_Compares_The_Bytes()
    {
        _eTag = null;
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        Assert.Null(cached.Source.ETag);

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, result.Status);
        Assert.Equal("", _ifNoneMatch[^1]);
        Assert.Same(cached, result.Catalogue);
    }

    [Fact]
    public async Task A_Lower_Sync_Token_Is_Still_Accepted()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _file = RangesFile("1700000000", "10.3.0.0/16");
        _eTag = SecondETag;

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal("1700000000", result.Catalogue!.Source.SyncToken);
    }

    [Fact]
    public async Task A_304_When_Nothing_Is_Saved_Is_A_Service_Error_Not_A_Success()
    {
        _status = HttpStatusCode.NotModified;

        AwsLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AwsSourceFailure.ServiceError, result.Failure);
        Assert.Null(result.Catalogue);
    }

    [Fact]
    public async Task With_A_Cache_And_No_Network_The_Cache_Is_Kept_And_The_Check_Fails()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesPath);
        _failure = new HttpRequestException("offline");

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.CheckFailed, result.Status);
        Assert.Equal(AwsSourceFailure.Offline, result.Failure);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_Error_Status_Is_A_Service_Error_And_Keeps_The_Cache(HttpStatusCode status)
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _status = status;

        AwsLoadResult withCache = await Service().CheckForUpdateAsync(cached, Ct);
        AwsLoadResult withoutCache = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.CheckFailed, withCache.Status);
        Assert.Equal(AwsSourceFailure.ServiceError, withCache.Failure);
        Assert.Same(cached, withCache.Catalogue);
        Assert.Equal(AwsUpdateStatus.Unavailable, withoutCache.Status);
        Assert.Equal(AwsSourceFailure.ServiceError, withoutCache.Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_File_Larger_Than_The_Cap_Is_Not_Used_Whether_Or_Not_It_Says_Its_Length(bool saysLength)
    {
        _hideLength = !saysLength;
        AwsFeedService service = Service(new CoreOptions { AwsMaxFileBytes = 50 });

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AwsSourceFailure.FileUnreadable, result.Failure);
        Assert.False(File.Exists(_temp.Paths.AwsIpRangesPath));
    }

    [Fact]
    public async Task A_File_That_Is_Not_AWS_Ranges_Fails_The_Check_And_Leaves_The_Cache_Byte_For_Byte()
    {
        AwsRangeCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesPath);
        byte[] sidecarBytes = File.ReadAllBytes(_temp.Paths.AwsIpRangesSourcePath);
        _file = Encoding.UTF8.GetBytes("<html>Service unavailable</html>");
        _eTag = SecondETag;

        AwsLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AwsUpdateStatus.CheckFailed, result.Status);
        Assert.Equal(AwsSourceFailure.FileUnreadable, result.Failure);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.Equal(sidecarBytes, File.ReadAllBytes(_temp.Paths.AwsIpRangesSourcePath));
    }

    [Fact]
    public async Task A_Garbage_File_With_No_Cache_Is_Unavailable_And_Saves_Nothing()
    {
        _file = Encoding.UTF8.GetBytes("{\"syncToken\":\"1\"}");

        AwsLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AwsSourceFailure.FileUnreadable, result.Failure);
        Assert.False(File.Exists(_temp.Paths.AwsIpRangesPath));
        Assert.False(File.Exists(_temp.Paths.AwsIpRangesSourcePath));
    }

    [Fact]
    public async Task An_ETag_That_Cannot_Be_Read_Is_Treated_As_No_ETag()
    {
        _eTag = "no-quotes";

        AwsLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Null(result.Catalogue!.Source.ETag);
    }

    [Fact]
    public async Task A_Timeout_Is_Reported_As_Offline_But_A_Cancel_By_The_Caller_Is_Not()
    {
        _failure = new TaskCanceledException("timed out");
        AwsLoadResult timedOut = await Service().CheckForUpdateAsync(null, Ct);
        Assert.Equal(AwsSourceFailure.Offline, timedOut.Failure);

        _failure = null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CheckForUpdateAsync(null, cancelled.Token));
    }

    [Fact]
    public async Task A_Cache_That_Cannot_Be_Written_Does_Not_Stop_The_Update()
    {
        string blocker = Path.Combine(_temp.Folder, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "x");
        var service = new AwsFeedService(_ipinfo, _http, new CoreOptions(), _time, new AppPaths(blocker, _temp.ExportFolder), _temp.ExportFolder);

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.NotNull(result.Catalogue);
    }

    [Fact]
    public async Task Without_A_Data_Folder_The_Update_Works_And_Nothing_Is_Cached()
    {
        var service = new AwsFeedService(_ipinfo, _http, new CoreOptions(), _time, null, _temp.ExportFolder);

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Null(await service.LoadCachedAsync(Ct));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("sidecar")]
    [InlineData("missing-sidecar")]
    [InlineData("missing-body")]
    [InlineData("bad-sidecar")]
    [InlineData("truncated")]
    [InlineData("unsafe-token")]
    public async Task A_Damaged_Cache_Counts_As_Missing_And_Is_Not_Deleted(string damage)
    {
        await Service().CheckForUpdateAsync(null, Ct);
        string body = _temp.Paths.AwsIpRangesPath;
        string sidecar = _temp.Paths.AwsIpRangesSourcePath;
        switch (damage)
        {
            case "body": File.WriteAllBytes(body, [.. _file.AsSpan(0, _file.Length - 1), (byte)' ']); break;
            case "truncated": File.WriteAllBytes(body, _file.AsSpan(0, _file.Length - 5).ToArray()); break;
            case "sidecar": File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"length\": ", "\"length\": 1")); break;
            case "missing-sidecar": File.Delete(sidecar); break;
            case "missing-body": File.Delete(body); break;
            case "bad-sidecar": File.WriteAllText(sidecar, "{not json"); break;
            case "unsafe-token": File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("1792136826", "1\\nInjected")); break;
        }

        Assert.Null(await Service().LoadCachedAsync(Ct));
        Assert.Equal(damage is not "missing-body", File.Exists(body));
    }

    [Fact]
    public async Task A_Damaged_Cache_Is_Replaced_By_The_Next_Update()
    {
        await Service().CheckForUpdateAsync(null, Ct);
        File.WriteAllText(_temp.Paths.AwsIpRangesPath, "damaged");

        AwsLoadResult result = await Service().CheckForUpdateAsync(await Service().LoadCachedAsync(Ct), Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.NotNull(await Service().LoadCachedAsync(Ct));
    }

    [Fact]
    public async Task Requests_Go_Only_To_The_Configured_Address_With_The_Configured_User_Agent()
    {
        var options = new CoreOptions { UserAgent = "test-agent/1.0" };

        await Service(options).CheckForUpdateAsync(null, Ct);

        Assert.Equal(["https://ip-ranges.amazonaws.com/ip-ranges.json"], _handler.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
        Assert.All(_handler.UserAgents, agent => Assert.Equal("test-agent/1.0", agent));
    }

    private AwsFeedService Service(CoreOptions? options = null) =>
        new(_ipinfo, _http, options ?? new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        Assert.Equal("ip-ranges.amazonaws.com", request.RequestUri!.Host);
        _ifNoneMatch.Add(request.Headers.IfNoneMatch.ToString());
        if (_failure is not null)
            throw _failure;

        if (_status != HttpStatusCode.OK)
            return new HttpResponseMessage(_status);

        if (!_ignoreConditional && _eTag is not null
            && request.Headers.IfNoneMatch.Any(tag => string.Equals(tag.ToString(), _eTag, StringComparison.Ordinal)))
        {
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        }

        HttpContent content = _hideLength ? new UnknownLengthContent(_file) : new ByteArrayContent(_file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (_eTag is not null)
            response.Headers.TryAddWithoutValidation("ETag", _eTag);
        return response;
    }

    /// <summary>A body whose length is not announced, so the client has to count the bytes as it reads them.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static byte[] RangesFile(string syncToken, params string[] ipv4) => Encoding.UTF8.GetBytes(
        "{\"syncToken\":\"" + syncToken + "\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":["
        + string.Join(",", ipv4.Select(cidr => "{\"ip_prefix\":\"" + cidr + "\",\"region\":\"eu-west-1\",\"service\":\"EC2\",\"network_border_group\":\"eu-west-1\"}"))
        + "],\"ipv6_prefixes\":[]}");
}
