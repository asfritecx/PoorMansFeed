using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class CloudSourceUpdatedTests : IDisposable
{
    private const string AwsETag = "\"aaaa1111\"";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    private byte[] _azureFile = AzureFile(422);
    private string _azureUrl = AzureUrl("aaaa", "20261026");
    private byte[] _awsFile = AwsFile("1792136826", "10.1.0.0/16");
    private string _awsETag = AwsETag;
    private byte[] _googleFile = GoogleFile("1770000000000", "34.80.1.0/24");
    private bool _offline;

    public CloudSourceUpdatedTests()
    {
        _handler = new StubHttpHandler(Respond);
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    // ----- Azure -----

    [Fact]
    public async Task Azure_An_Update_That_Replaced_The_File_Raises_Once_After_The_File_Is_In_Place()
    {
        AzureFeedService service = Azure();
        var heard = new List<CloudSourceUpdated<AzureTagCatalogue>>();
        byte[]? onDisk = null;
        service.SourceUpdated += (sender, e) =>
        {
            Assert.Same(service, sender);
            onDisk = File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath);
            heard.Add(e);
        };

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        CloudSourceUpdated<AzureTagCatalogue> told = Assert.Single(heard);
        Assert.Same(result.Catalogue, told.Catalogue);
        Assert.Equal(Now, told.UpdatedUtc);
        Assert.Equal(_azureFile, onDisk);
    }

    [Fact]
    public async Task Azure_A_Newer_File_Raises_Once_With_The_New_Catalogue()
    {
        AzureTagCatalogue first = (await Azure().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _azureUrl = AzureUrl("bbbb", "20261102");
        _azureFile = AzureFile(423);
        AzureFeedService service = Azure();
        var heard = new List<CloudSourceUpdated<AzureTagCatalogue>>();
        service.SourceUpdated += (_, e) => heard.Add(e);

        AzureLoadResult result = await service.CheckForUpdateAsync(first, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Same(result.Catalogue, Assert.Single(heard).Catalogue);
        Assert.Equal(423, heard[0].Catalogue.Source.ChangeNumber);
    }

    [Fact]
    public async Task Azure_The_Same_File_At_A_New_Address_Is_Saved_But_Raises_Nothing()
    {
        AzureTagCatalogue first = (await Azure().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _azureUrl = AzureUrl("bbbb", "20261102");
        AzureFeedService service = Azure();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        AzureLoadResult result = await service.CheckForUpdateAsync(first, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Azure_Up_To_Date_Raises_Nothing()
    {
        AzureTagCatalogue first = (await Azure().CheckForUpdateAsync(null, Ct)).Catalogue!;
        AzureFeedService service = Azure();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        AzureLoadResult result = await service.CheckForUpdateAsync(first, Ct);

        Assert.Equal(AzureUpdateStatus.UpToDate, result.Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Azure_A_Failed_Check_Raises_Nothing()
    {
        AzureTagCatalogue first = (await Azure().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _offline = true;
        AzureFeedService service = Azure();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        Assert.Equal(AzureUpdateStatus.Unavailable, (await service.CheckForUpdateAsync(null, Ct)).Status);
        Assert.Equal(AzureUpdateStatus.CheckFailed, (await service.CheckForUpdateAsync(first, Ct)).Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Azure_A_Cache_Write_That_Failed_Raises_Nothing()
    {
        Directory.CreateDirectory(_temp.Paths.AzureServiceTagsPath);
        AzureFeedService service = Azure();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Azure_A_Throwing_Handler_Leaves_The_Result_And_The_Cache_And_The_Next_Handler_Still_Hears()
    {
        AzureFeedService service = Azure();
        int heard = 0;
        service.SourceUpdated += (_, _) => throw new InvalidOperationException("listener");
        service.SourceUpdated += (_, _) => heard++;

        AzureLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AzureUpdateStatus.Updated, result.Status);
        Assert.Equal(AzureSourceFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Equal(_azureFile, File.ReadAllBytes(_temp.Paths.AzureServiceTagsPath));
        Assert.NotNull(await Azure().LoadCachedAsync(Ct));
        Assert.Equal(1, heard);
    }

    // ----- AWS -----

    [Fact]
    public async Task Aws_An_Update_That_Replaced_The_File_Raises_Once_After_The_File_Is_In_Place()
    {
        AwsFeedService service = Aws();
        var heard = new List<CloudSourceUpdated<AwsRangeCatalogue>>();
        byte[]? onDisk = null;
        service.SourceUpdated += (sender, e) =>
        {
            Assert.Same(service, sender);
            onDisk = File.ReadAllBytes(_temp.Paths.AwsIpRangesPath);
            heard.Add(e);
        };

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        CloudSourceUpdated<AwsRangeCatalogue> told = Assert.Single(heard);
        Assert.Same(result.Catalogue, told.Catalogue);
        Assert.Equal(Now, told.UpdatedUtc);
        Assert.Equal(_awsFile, onDisk);
    }

    [Fact]
    public async Task Aws_Up_To_Date_Raises_Nothing_On_A_304_Or_The_Same_Bytes_Under_A_New_ETag()
    {
        AwsRangeCatalogue first = (await Aws().CheckForUpdateAsync(null, Ct)).Catalogue!;
        AwsFeedService service = Aws();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        AwsLoadResult notModified = await service.CheckForUpdateAsync(first, Ct);
        _awsETag = "\"bbbb2222\"";
        AwsLoadResult sameBytes = await service.CheckForUpdateAsync(first, Ct);

        Assert.Equal(AwsUpdateStatus.UpToDate, notModified.Status);
        Assert.Equal(AwsUpdateStatus.UpToDate, sameBytes.Status);
        Assert.Equal("\"bbbb2222\"", sameBytes.Catalogue!.Source.ETag);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Aws_A_Failed_Check_Raises_Nothing()
    {
        AwsRangeCatalogue first = (await Aws().CheckForUpdateAsync(null, Ct)).Catalogue!;
        _offline = true;
        AwsFeedService service = Aws();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        Assert.Equal(AwsUpdateStatus.Unavailable, (await service.CheckForUpdateAsync(null, Ct)).Status);
        Assert.Equal(AwsUpdateStatus.CheckFailed, (await service.CheckForUpdateAsync(first, Ct)).Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Aws_A_Cache_Write_That_Failed_Raises_Nothing()
    {
        Directory.CreateDirectory(_temp.Paths.AwsIpRangesPath);
        AwsFeedService service = Aws();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Aws_A_Throwing_Handler_Leaves_The_Result_And_The_Cache_And_The_Next_Handler_Still_Hears()
    {
        AwsFeedService service = Aws();
        int heard = 0;
        service.SourceUpdated += (_, _) => throw new InvalidOperationException("listener");
        service.SourceUpdated += (_, _) => heard++;

        AwsLoadResult result = await service.CheckForUpdateAsync(null, Ct);

        Assert.Equal(AwsUpdateStatus.Updated, result.Status);
        Assert.Equal(AwsSourceFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Equal(_awsFile, File.ReadAllBytes(_temp.Paths.AwsIpRangesPath));
        Assert.NotNull(await Aws().LoadCachedAsync(Ct));
        Assert.Equal(1, heard);
    }

    // ----- Google Cloud -----

    [Fact]
    public async Task Google_A_First_Fetch_Raises_Once_After_The_File_Is_In_Place()
    {
        GoogleCloudFeedService service = Google();
        var heard = new List<CloudSourceUpdated<CloudRangeCatalogue>>();
        byte[]? onDisk = null;
        service.SourceUpdated += (sender, e) =>
        {
            Assert.Same(service, sender);
            onDisk = File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath);
            heard.Add(e);
        };

        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        CloudSourceUpdated<CloudRangeCatalogue> told = Assert.Single(heard);
        Assert.Same(result.Catalogue, told.Catalogue);
        Assert.Equal(Now, told.UpdatedUtc);
        Assert.Equal(_googleFile, onDisk);
    }

    [Fact]
    public async Task Google_The_Same_Bytes_Raise_Nothing_And_New_Bytes_Raise_Once()
    {
        await Google().LoadCatalogueAsync(Ct);
        GoogleCloudFeedService service = Google();
        var heard = new List<CloudSourceUpdated<CloudRangeCatalogue>>();
        service.SourceUpdated += (_, e) => heard.Add(e);

        await service.LoadCatalogueAsync(Ct);
        Assert.Empty(heard);

        _googleFile = GoogleFile("1770000000001", "34.80.1.0/24", "34.80.2.0/24");
        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.Same(result.Catalogue, Assert.Single(heard).Catalogue);
        Assert.Equal(_googleFile, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
    }

    [Fact]
    public async Task Google_The_Same_Bytes_Saved_From_Another_Address_Are_Saved_Again_And_Raise_Nothing()
    {
        await Google().LoadCatalogueAsync(Ct);
        string record = _temp.Paths.GoogleCloudRangesSourcePath;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GoogleRangeFileSource saved = JsonSerializer.Deserialize<GoogleRangeFileSource>(File.ReadAllBytes(record), options)!;
        File.WriteAllBytes(record, JsonSerializer.SerializeToUtf8Bytes(
            saved with { Url = new Uri("https://www.gstatic.com/ipranges/cloud.json#x") }, options));
        GoogleCloudFeedService service = Google();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        await service.LoadCatalogueAsync(Ct);

        Assert.Equal(new Uri(new CoreOptions().GoogleCloudRangesUrl).AbsoluteUri,
                     (await Google().LoadSavedAsync(Ct)).Catalogue!.Source.Url.AbsoluteUri);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Google_A_Failed_Fetch_Raises_Nothing()
    {
        _offline = true;
        GoogleCloudFeedService service = Google();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.NotEqual(CatalogueFailure.None, result.Failure);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Google_A_Cache_Write_That_Failed_Raises_Nothing()
    {
        Directory.CreateDirectory(_temp.Paths.GoogleCloudRangesPath);
        GoogleCloudFeedService service = Google();
        int heard = 0;
        service.SourceUpdated += (_, _) => heard++;

        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task Google_A_Throwing_Handler_Leaves_The_Result_And_The_Cache_And_The_Next_Handler_Still_Hears()
    {
        GoogleCloudFeedService service = Google();
        int heard = 0;
        service.SourceUpdated += (_, _) => throw new InvalidOperationException("listener");
        service.SourceUpdated += (_, _) => heard++;

        CatalogueResult result = await service.LoadCatalogueAsync(Ct);

        Assert.Equal(CatalogueFailure.None, result.Failure);
        Assert.NotNull(result.Catalogue);
        Assert.Equal(_googleFile, File.ReadAllBytes(_temp.Paths.GoogleCloudRangesPath));
        Assert.Equal(SavedRangesState.Read, (await Google().LoadSavedAsync(Ct)).State);
        Assert.Equal(1, heard);
    }

    private AzureFeedService Azure() => new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private AwsFeedService Aws() => new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private GoogleCloudFeedService Google() =>
        new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        if (_offline)
            throw new HttpRequestException("offline");

        string host = request.RequestUri!.Host;
        if (host == "www.microsoft.com")
            return Reply(PageFor(_azureUrl), "text/html");
        if (host == "download.microsoft.com")
            return Reply(_azureFile, "application/octet-stream");
        if (host == "ip-ranges.amazonaws.com")
        {
            if (request.Headers.IfNoneMatch.Any(tag => string.Equals(tag.ToString(), _awsETag, StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            HttpResponseMessage aws = Reply(_awsFile, "application/json");
            aws.Headers.TryAddWithoutValidation("ETag", _awsETag);
            return aws;
        }

        return Reply(_googleFile, "application/json");
    }

    private static HttpResponseMessage Reply(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string AzureUrl(string folder, string date) =>
        $"https://download.microsoft.com/download/7/1/d/{folder}/ServiceTags_Public_{date}.json";

    private static byte[] PageFor(string url) => Encoding.UTF8.GetBytes(
        $"<html><body><a href=\"{url}\">Download</a></body></html>");

    private static byte[] AzureFile(int changeNumber) => Encoding.UTF8.GetBytes(
        "{\"changeNumber\":" + changeNumber + ",\"cloud\":\"Public\",\"values\":[" +
        "{\"name\":\"Storage\",\"id\":\"Storage\",\"properties\":{\"changeNumber\":3,\"region\":\"\",\"addressPrefixes\":[\"20.60.0.0/16\"]}}]}");

    private static byte[] AwsFile(string syncToken, params string[] ipv4) => Encoding.UTF8.GetBytes(
        "{\"syncToken\":\"" + syncToken + "\",\"createDate\":\"2026-10-31-07-47-06\",\"prefixes\":["
        + string.Join(",", ipv4.Select(cidr => "{\"ip_prefix\":\"" + cidr + "\",\"region\":\"eu-west-1\",\"service\":\"EC2\",\"network_border_group\":\"eu-west-1\"}"))
        + "],\"ipv6_prefixes\":[]}");

    private static byte[] GoogleFile(string syncToken, params string[] prefixes) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["syncToken"] = syncToken,
            ["creationTime"] = "2026-10-31T00:00:00Z",
            ["prefixes"] = prefixes.Select(prefix => new Dictionary<string, string>
            {
                ["ipv4Prefix"] = prefix,
                ["service"] = "Google Cloud",
                ["scope"] = "us-central1",
            }).ToArray(),
        });
}
