using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Azure;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AzureFeedServiceUpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 30, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    private byte[] _file = TagsFile(422);
    private string _fileUrl = FileUrl("aaaa", "20261012");
    private byte[]? _page;
    private HttpStatusCode _pageStatus = HttpStatusCode.OK;
    private HttpStatusCode _fileStatus = HttpStatusCode.OK;
    private Exception? _pageFailure;
    private Exception? _fileFailure;
    private bool _hideLength;

    public AzureFeedServiceUpdateTests()
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
        _pageFailure = new HttpRequestException("offline");
        AzureFeedService service = Service();

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AzureSourceFailure.Offline, result.Failure);
        Assert.Null(result.Catalogue);
        Assert.Equal(Now, result.CheckedAt);
        Assert.False(File.Exists(_temp.Paths.AzureServiceTagsPath));
    }

    [Fact]
    public async Task With_No_Cache_It_Downloads_Parses_And_Saves_The_File_Exactly_As_Served()
    {
        AzureFeedService service = Service();

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(AzureSourceFailure.None, result.Failure);
        AzureTagCatalogue catalogue = result.Catalogue!;
        Assert.Equal(new Uri(_fileUrl), catalogue.Source.Url);
        Assert.Equal("ServiceTags_Public_20261012.json", catalogue.Source.FileName);
        Assert.Equal(new DateOnly(2026, 10, 12), catalogue.Source.FileDate);
        Assert.Equal(422, catalogue.Source.ChangeNumber);
        Assert.Equal("Public", catalogue.Source.Cloud);
        Assert.Equal(Now, catalogue.Source.DownloadedUtc);
        Assert.Equal(_file.Length, catalogue.Source.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(_file)), catalogue.Source.Sha256);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
        Assert.True(File.Exists(_temp.Paths.AzureServiceTagsSourcePath));
        Assert.Empty(Directory.GetFiles(_temp.Folder, "*.tmp"));
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_Saved_File_Loads_Back_With_The_Same_Source_And_Tags()
    {
        AzureTagCatalogue saved = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;

        AzureTagCatalogue? loaded = await Service().LoadCachedAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Source, loaded.Source);
        Assert.Equal(
            saved.Services.SelectMany(group => group.Tags).Select(tag => tag.Name).ToArray(),
            loaded.Services.SelectMany(group => group.Tags).Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public async Task Loading_With_No_Cache_Gives_Null_And_Makes_No_Request()
    {
        Assert.Null(await Service().LoadCachedAsync(Ct));
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task When_The_Page_Offers_The_Cached_Url_Nothing_Is_Downloaded()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        int before = _handler.Calls;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath);

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.UpToDate, result.Status);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(before + 1, _handler.Calls);
        Assert.Equal("www.microsoft.com", _handler.RequestedUris[^1].Host);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
    }

    [Fact]
    public async Task When_The_Page_Offers_A_Different_Url_The_New_File_Replaces_The_Cache()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _fileUrl = FileUrl("bbbb", "20261019");
        _file = TagsFile(423);
        _time.Advance(TimeSpan.FromDays(7));

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(423, result.Catalogue!.Source.ChangeNumber);
        Assert.Equal("ServiceTags_Public_20261019.json", result.Catalogue.Source.FileName);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
        Assert.Equal(result.Catalogue.Source, (await Service().LoadCachedAsync(Ct))!.Source);
    }

    [Fact]
    public async Task A_Newer_Url_With_A_Lower_Change_Number_Is_Still_Accepted()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _fileUrl = FileUrl("bbbb", "20261019");
        _file = TagsFile(400);

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(400, result.Catalogue!.Source.ChangeNumber);
    }

    [Fact]
    public async Task With_A_Cache_And_No_Network_The_Cache_Is_Kept_And_The_Check_Fails()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath);
        _pageFailure = new HttpRequestException("offline");

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.CheckFailed, result.Status);
        Assert.Equal(AzureSourceFailure.Offline, result.Failure);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_Page_Error_Status_Is_A_Service_Error(HttpStatusCode status)
    {
        _pageStatus = status;

        AzureLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AzureSourceFailure.ServiceError, result.Failure);
    }

    [Fact]
    public async Task A_Page_Without_The_File_Link_Is_Unreadable_And_Keeps_The_Cache()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _page = Encoding.UTF8.GetBytes("<html>The download center was redesigned.</html>");

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.CheckFailed, result.Status);
        Assert.Equal(AzureSourceFailure.PageUnreadable, result.Failure);
        Assert.Same(cached, result.Catalogue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Page_Larger_Than_The_Cap_Is_Unreadable_Whether_Or_Not_It_Says_Its_Length(bool saysLength)
    {
        _page = new byte[200];
        Array.Fill(_page, (byte)'x');
        _hideLength = !saysLength;
        AzureFeedService service = Service(new CoreOptions { AzureMaxPageBytes = 100 });

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AzureSourceFailure.PageUnreadable, result.Failure);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task A_File_That_Is_Not_Service_Tags_Fails_The_Check_And_Leaves_The_Cache_Byte_For_Byte()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        byte[] cacheBytes = File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath);
        byte[] sidecarBytes = File.ReadAllBytes(_temp.Paths.AzureServiceTagsSourcePath);
        _fileUrl = FileUrl("bbbb", "20261019");
        _file = Encoding.UTF8.GetBytes("<html>Service unavailable</html>");

        AzureLoadResult result = await Service().CheckForUpdateAsync(cached, Ct);

        Assert.Equal(AzureUpdateStatus.CheckFailed, result.Status);
        Assert.Equal(AzureSourceFailure.FileUnreadable, result.Failure);
        Assert.Same(cached, result.Catalogue);
        Assert.Equal(cacheBytes, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
        Assert.Equal(sidecarBytes, File.ReadAllBytes(_temp.Paths.AzureServiceTagsSourcePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_File_Larger_Than_The_Cap_Is_Not_Used(bool saysLength)
    {
        _hideLength = !saysLength;
        AzureFeedService service = Service(new CoreOptions { AzureMaxFileBytes = 50 });

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Unavailable, result.Status);
        Assert.Equal(AzureSourceFailure.FileUnreadable, result.Failure);
        Assert.False(File.Exists(_temp.Paths.AzureServiceTagsPath));
    }

    [Fact]
    public async Task A_Failed_File_Download_Is_Reported_By_Its_Cause_And_Keeps_The_Cache()
    {
        AzureTagCatalogue cached = (await Service().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _fileUrl = FileUrl("bbbb", "20261019");

        _fileStatus = HttpStatusCode.NotFound;
        AzureLoadResult notFound = await Service().CheckForUpdateAsync(cached, Ct);
        Assert.Equal(AzureUpdateStatus.CheckFailed, notFound.Status);
        Assert.Equal(AzureSourceFailure.ServiceError, notFound.Failure);

        _fileStatus = HttpStatusCode.OK;
        _fileFailure = new HttpRequestException("reset");
        AzureLoadResult reset = await Service().CheckForUpdateAsync(cached, Ct);
        Assert.Equal(AzureSourceFailure.Offline, reset.Failure);
        Assert.Same(cached, reset.Catalogue);
    }

    [Fact]
    public async Task A_Timeout_Is_Reported_As_Offline_But_A_Cancel_By_The_Caller_Is_Not()
    {
        _pageFailure = new TaskCanceledException("timed out");
        AzureLoadResult timedOut = await Service().CheckForUpdateAsync(null, Ct);
        Assert.Equal(AzureSourceFailure.Offline, timedOut.Failure);

        _pageFailure = null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CheckForUpdateAsync(null, cancelled.Token));
    }

    [Fact]
    public async Task A_Cache_That_Cannot_Be_Written_Does_Not_Stop_The_Update()
    {
        string blocker = Path.Combine(_temp.Folder, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "x");
        var service = new AzureFeedService(_ipinfo, _http, new CoreOptions(), _time, new AppPaths(blocker, _temp.ExportFolder), _temp.ExportFolder);

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.NotNull(result.Catalogue);
    }

    [Fact]
    public async Task Without_A_Data_Folder_The_Update_Works_And_Nothing_Is_Cached()
    {
        var service = new AzureFeedService(_ipinfo, _http, new CoreOptions(), _time, null, _temp.ExportFolder);

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Null(await service.LoadCachedAsync(Ct));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("sidecar")]
    [InlineData("missing-sidecar")]
    [InlineData("missing-body")]
    [InlineData("bad-sidecar")]
    [InlineData("truncated")]
    public async Task A_Damaged_Cache_Counts_As_Missing_And_Is_Not_Deleted(string damage)
    {
        await Service().CheckForUpdateAsync(null, Ct);
        string body = _temp.Paths.AzureServiceTagsPath;
        string sidecar = _temp.Paths.AzureServiceTagsSourcePath;
        switch (damage)
        {
            case "body": File.WriteAllBytes(body, [.. _file.AsSpan(0, _file.Length - 1), (byte)' ']); break;
            case "truncated": File.WriteAllBytes(body, _file.AsSpan(0, _file.Length - 5).ToArray()); break;
            case "sidecar": File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"length\": ", "\"length\": 1")); break;
            case "missing-sidecar": File.Delete(sidecar); break;
            case "missing-body": File.Delete(body); break;
            case "bad-sidecar": File.WriteAllText(sidecar, "{not json"); break;
        }

        Assert.Null(await Service().LoadCachedAsync(Ct));
        Assert.Equal(damage is not "missing-body", File.Exists(body));
    }

    [Fact]
    public async Task A_Damaged_Cache_Is_Replaced_By_The_Next_Update()
    {
        await Service().CheckForUpdateAsync(null, Ct);
        File.WriteAllText(_temp.Paths.AzureServiceTagsPath, "damaged");

        AzureLoadResult result = await Service().CheckForUpdateAsync(await Service().LoadCachedAsync(Ct), Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(_file, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
        Assert.NotNull(await Service().LoadCachedAsync(Ct));
    }

    [Fact]
    public async Task Requests_Go_Only_To_The_Page_And_The_File_Host_With_The_Configured_User_Agent()
    {
        var options = new CoreOptions { UserAgent = "test-agent/1.0" };

        await Service(options).CheckForUpdateAsync(null, Ct);

        Assert.Equal(
            ["https://www.microsoft.com/en-us/download/details.aspx?id=56519", _fileUrl],
            _handler.RequestedUris.Select(uri => uri.AbsoluteUri).ToArray());
        Assert.All(_handler.UserAgents, agent => Assert.Equal("test-agent/1.0", agent));
    }

    [Fact]
    public async Task A_Link_To_Another_Host_On_The_Page_Is_Never_Requested()
    {
        _page = Encoding.UTF8.GetBytes("<a href=\"https://evil.example/ServiceTags_Public_20261012.json\">Download</a>");

        AzureLoadResult result = await Service().CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureSourceFailure.PageUnreadable, result.Failure);
        Assert.Equal(1, _handler.Calls);
    }

    private AzureFeedService Service(CoreOptions? options = null) =>
        new(_ipinfo, _http, options ?? new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        if (request.RequestUri!.Host == "www.microsoft.com")
        {
            if (_pageFailure is not null)
                throw _pageFailure;
            return Reply(_pageStatus, _page ?? PageFor(_fileUrl), "text/html");
        }

        Assert.Equal("download.microsoft.com", request.RequestUri.Host);
        if (_fileFailure is not null)
            throw _fileFailure;
        return Reply(_fileStatus, _file, "application/octet-stream");
    }

    private HttpResponseMessage Reply(HttpStatusCode status, byte[] body, string contentType)
    {
        HttpContent content = _hideLength ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
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

    private static string FileUrl(string folder, string date) =>
        $"https://download.microsoft.com/download/7/1/d/{folder}/ServiceTags_Public_{date}.json";

    private static byte[] PageFor(string url) => Encoding.UTF8.GetBytes(
        "<html><script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"\",\"downloadFile\":[{\"name\":\"n\",\"url\":\"" + url + "\"}]}};" +
        "var x = {\"later\":true};</script></html>");

    private static byte[] TagsFile(int changeNumber) => Encoding.UTF8.GetBytes(
        "{\"changeNumber\":" + changeNumber + ",\"cloud\":\"Public\",\"values\":[" +
        "{\"name\":\"Storage\",\"id\":\"Storage\",\"properties\":{\"changeNumber\":3,\"region\":\"\",\"addressPrefixes\":[\"20.60.0.0/16\",\"2603:1000::/32\"]}}," +
        "{\"name\":\"Storage.SoutheastAsia\",\"id\":\"Storage.SoutheastAsia\",\"properties\":{\"changeNumber\":2,\"region\":\"southeastasia\",\"addressPrefixes\":[\"20.44.0.0/24\"]}}]}");
}
