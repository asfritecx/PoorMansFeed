using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class SavedRangesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 15, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly byte[] GoogleFile = Encoding.UTF8.GetBytes("""
        {"syncToken": "1770000000000", "creationTime": "2026-01-15T00:00:00Z", "prefixes": [
          {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv6Prefix": "2600:1900:4000::/44", "service": "Google Cloud", "scope": "us-central1"},
          {"ipv4Prefix": "35.199.0.0/24", "service": "Google Cloud", "scope": "global"}]}
        """);

    private static readonly byte[] AzureFile = Encoding.UTF8.GetBytes(
        "{\"changeNumber\":422,\"cloud\":\"Public\",\"values\":[" +
        "{\"name\":\"Storage\",\"id\":\"Storage\",\"properties\":{\"changeNumber\":3,\"region\":\"\",\"addressPrefixes\":[\"20.60.0.0/16\",\"2603:1000::/32\"]}}," +
        "{\"name\":\"Storage.SoutheastAsia\",\"id\":\"Storage.SoutheastAsia\",\"properties\":{\"changeNumber\":2,\"region\":\"southeastasia\",\"addressPrefixes\":[\"20.44.0.0/24\"]}}]}");

    private static readonly byte[] AwsFile = Encoding.UTF8.GetBytes(
        "{\"syncToken\":\"1792136826\",\"createDate\":\"2026-10-16-07-47-06\",\"prefixes\":[" +
        "{\"ip_prefix\":\"10.1.0.0/16\",\"region\":\"eu-west-1\",\"service\":\"EC2\",\"network_border_group\":\"eu-west-1\"}]," +
        "\"ipv6_prefixes\":[]}");

    private const string AzureFileUrl = "https://download.microsoft.com/download/7/1/d/aaaa/ServiceTags_Public_20261012.json";

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public SavedRangesTests()
    {
        _handler = new StubHttpHandler(Respond);
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    public static TheoryData<string> Providers => ["google", "azure", "aws"];

    public static TheoryData<string, string> ProviderDamage()
    {
        string[] damages = ["missing-record", "missing-data", "data-changed", "record-not-json", "data-unparseable", "data-is-folder"];
        var data = new TheoryData<string, string>();
        foreach (string provider in Providers)
        {
            foreach (string damage in damages)
                data.Add(provider, damage);
        }

        data.Add("google", "data-no-usable-prefix");
        data.Add("google", "record-relative-url");
        data.Add("google", "record-http-url");
        data.Add("google", "record-null-token");
        return data;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_Good_Save_Reads_Back_As_The_Same_Catalogue_With_The_Record_Download_Time(string provider)
    {
        string expected = await SaveGoodAsync(provider);
        _time.Advance(TimeSpan.FromHours(3));

        (SavedRangesState state, object? catalogue, DateTimeOffset? savedUtc) = await LoadSavedAsync(provider, _temp.Paths);

        Assert.Equal(SavedRangesState.Read, state);
        Assert.NotNull(catalogue);
        Assert.Equal(expected, Describe(catalogue));
        Assert.Equal(Now, savedUtc);
        Assert.Equal(0, _handler.Calls - CallsAfterSave);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task An_Empty_Data_Folder_Is_Not_Saved_And_Makes_No_Request(string provider)
    {
        (SavedRangesState state, object? catalogue, DateTimeOffset? savedUtc) = await LoadSavedAsync(provider, _temp.Paths);

        Assert.Equal(SavedRangesState.NotSaved, state);
        Assert.Null(catalogue);
        Assert.Null(savedUtc);
        Assert.Equal(0, _handler.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Without_Paths_Nothing_Is_Saved_Even_Beside_A_Good_Pair(string provider)
    {
        await SaveGoodAsync(provider);

        (SavedRangesState state, object? catalogue, DateTimeOffset? savedUtc) = await LoadSavedAsync(provider, null);

        Assert.Equal(SavedRangesState.NotSaved, state);
        Assert.Null(catalogue);
        Assert.Null(savedUtc);
    }

    [Theory]
    [MemberData(nameof(ProviderDamage))]
    public async Task A_Damaged_Pair_Is_Unusable_And_Left_As_It_Was(string provider, string damage)
    {
        await SaveGoodAsync(provider);
        (string data, string record) = PathsFor(provider);
        Damage(provider, damage, data, record);
        byte[]? dataBefore = File.Exists(data) ? File.ReadAllBytes(data) : null;
        byte[]? recordBefore = File.Exists(record) ? File.ReadAllBytes(record) : null;
        string[] entriesBefore = Directory.GetFileSystemEntries(_temp.Folder, "*", SearchOption.AllDirectories);

        (SavedRangesState state, object? catalogue, DateTimeOffset? savedUtc) = await LoadSavedAsync(provider, _temp.Paths);

        Assert.Equal(SavedRangesState.Unusable, state);
        Assert.Null(catalogue);
        Assert.Null(savedUtc);
        Assert.Equal(dataBefore, File.Exists(data) ? File.ReadAllBytes(data) : null);
        Assert.Equal(recordBefore, File.Exists(record) ? File.ReadAllBytes(record) : null);
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(_temp.Folder, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_Cancelled_Caller_Gets_Its_Cancellation(string provider)
    {
        await SaveGoodAsync(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LoadSavedAsync(provider, _temp.Paths, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task The_Fakes_Pass_The_Read_Through_And_Count_It()
    {
        await SaveGoodAsync("azure");
        await SaveGoodAsync("aws");
        var azure = new FakeAzureFeedService(AzureService(_temp.Paths));
        var aws = new FakeAwsFeedService(AwsService(_temp.Paths));

        SavedRanges<AzureTagCatalogue> azureSaved = await azure.LoadSavedAsync(Ct);
        SavedRanges<AwsRangeCatalogue> awsSaved = await aws.LoadSavedAsync(Ct);
        await aws.LoadSavedAsync(Ct);

        Assert.Equal(SavedRangesState.Read, azureSaved.State);
        Assert.Equal(SavedRangesState.Read, awsSaved.State);
        Assert.Equal(1, azure.LoadSavedCalls);
        Assert.Equal(2, aws.LoadSavedCalls);
        Assert.Equal(0, azure.LoadCachedCalls);
        Assert.Equal(0, aws.LoadCachedCalls);
    }

    private int CallsAfterSave { get; set; }

    // Saves a good pair through the provider's own fetch and gives the catalogue a page would show for it
    private async Task<string> SaveGoodAsync(string provider)
    {
        string expected;
        switch (provider)
        {
            case "google":
                CatalogueResult fetched = await GoogleService(_temp.Paths).LoadCatalogueAsync(Ct);
                expected = Describe(fetched.Catalogue!);
                break;
            case "azure":
                await AzureService(_temp.Paths).CheckForUpdateAsync(null, Ct);
                expected = Describe((await AzureService(_temp.Paths).LoadCachedAsync(Ct))!);
                break;
            case "aws":
                await AwsService(_temp.Paths).CheckForUpdateAsync(null, Ct);
                expected = Describe((await AwsService(_temp.Paths).LoadCachedAsync(Ct))!);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }

        (string data, string record) = PathsFor(provider);
        Assert.True(File.Exists(data) && File.Exists(record));
        CallsAfterSave = _handler.Calls;
        return expected;
    }

    private async Task<(SavedRangesState, object?, DateTimeOffset?)> LoadSavedAsync(string provider, AppPaths? paths,
                                                                                     CancellationToken ct = default)
    {
        switch (provider)
        {
            case "google":
                SavedRanges<CloudRangeCatalogue> google = await GoogleService(paths).LoadSavedAsync(ct);
                return (google.State, google.Catalogue, google.SavedUtc);
            case "azure":
                SavedRanges<AzureTagCatalogue> azure = await AzureService(paths).LoadSavedAsync(ct);
                return (azure.State, azure.Catalogue, azure.SavedUtc);
            case "aws":
                SavedRanges<AwsRangeCatalogue> aws = await AwsService(paths).LoadSavedAsync(ct);
                return (aws.State, aws.Catalogue, aws.SavedUtc);
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private (string Data, string Record) PathsFor(string provider) => provider switch
    {
        "google" => (_temp.Paths.GoogleCloudRangesPath, _temp.Paths.GoogleCloudRangesSourcePath),
        "azure" => (_temp.Paths.AzureServiceTagsPath, _temp.Paths.AzureServiceTagsSourcePath),
        "aws" => (_temp.Paths.AwsIpRangesPath, _temp.Paths.AwsIpRangesSourcePath),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static void Damage(string provider, string damage, string data, string record)
    {
        switch (damage)
        {
            case "missing-record": File.Delete(record); break;
            case "missing-data": File.Delete(data); break;
            case "data-changed":
                byte[] bytes = File.ReadAllBytes(data);
                bytes[^1] = (byte)' ';
                File.WriteAllBytes(data, bytes);
                break;
            case "record-not-json": File.WriteAllText(record, "{not json"); break;
            case "data-unparseable": ReplaceDataKeepingRecordTrue(data, record, Encoding.UTF8.GetBytes("{\"not\":\"ranges\"}")); break;
            case "data-no-usable-prefix":
                Assert.Equal("google", provider);
                ReplaceDataKeepingRecordTrue(data, record, Encoding.UTF8.GetBytes(
                    "{\"syncToken\":\"1770000000000\",\"creationTime\":\"2026-01-15T00:00:00Z\",\"prefixes\":["
                    + "{\"ipv4Prefix\":\"34.80.1.0/24\",\"service\":\"Other\",\"scope\":\"us-central1\"}]}"));
                break;
            case "record-relative-url":
                Assert.Equal("google", provider);
                ChangeRecordField(record, "url", "cloud.json");
                break;
            case "record-http-url":
                Assert.Equal("google", provider);
                ChangeRecordField(record, "url", "http://www.gstatic.com/ipranges/cloud.json");
                break;
            case "record-null-token":
                Assert.Equal("google", provider);
                ChangeRecordField(record, "syncToken", null);
                break;
            case "data-is-folder":
                File.Delete(data);
                Directory.CreateDirectory(data);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }
    }

    // The record is rewritten to the new bytes' length and SHA-256, so only the parse can refuse them
    private static void ReplaceDataKeepingRecordTrue(string data, string record, byte[] bytes)
    {
        File.WriteAllBytes(data, bytes);
        JsonObject source = JsonNode.Parse(File.ReadAllBytes(record))!.AsObject();
        source["length"] = bytes.Length;
        source["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllText(record, source.ToJsonString());
    }

    private static void ChangeRecordField(string record, string field, string? value)
    {
        JsonObject source = JsonNode.Parse(File.ReadAllBytes(record))!.AsObject();
        Assert.True(source.ContainsKey(field));
        source[field] = value;
        File.WriteAllText(record, source.ToJsonString());
    }

    private static string Describe(object catalogue) => catalogue switch
    {
        CloudRangeCatalogue google => string.Join("|",
            google.Source.ToString(), google.SkippedEntries.ToString(),
            string.Join(",", google.Prefixes.Select(prefix => prefix.Block.Text + "@" + prefix.Scope.Id)),
            string.Join(",", google.Regions.Select(region => region.ToString()))),
        AzureTagCatalogue azure => string.Join("|",
            azure.Source.ToString(), azure.SkippedTags.ToString(), azure.SkippedPrefixes.ToString(),
            string.Join(",", azure.Services.SelectMany(group => group.Tags).Select(
                tag => tag.Name + "@" + tag.Region + ":" + string.Join(" ", tag.Prefixes.Select(block => block.Text))))),
        AwsRangeCatalogue aws => string.Join("|",
            aws.Source.ToString(), aws.SkippedPrefixes.ToString(),
            string.Join(",", aws.Services.SelectMany(service => service.Regions.Select(
                region => service.Name + "@" + region.Region + ":" + string.Join(" ", region.Prefixes.Select(prefix => prefix.Block.Text)))))),
        _ => throw new ArgumentOutOfRangeException(nameof(catalogue)),
    };

    private GoogleCloudFeedService GoogleService(AppPaths? paths) =>
        new(_ipinfo, _http, new CoreOptions(), _time, paths, _temp.ExportFolder);

    private AzureFeedService AzureService(AppPaths? paths) =>
        new(_ipinfo, _http, new CoreOptions(), _time, paths, _temp.ExportFolder);

    private AwsFeedService AwsService(AppPaths? paths) =>
        new(_ipinfo, _http, new CoreOptions(), _time, paths, _temp.ExportFolder);

    private static HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        (byte[] body, string type) = request.RequestUri!.Host switch
        {
            "www.gstatic.com" => (GoogleFile, "application/json"),
            "www.microsoft.com" => (Encoding.UTF8.GetBytes(
                "<html><script>window.__DLCDetails__={\"dlcDetailsView\":{\"error\":\"\",\"downloadFile\":[{\"name\":\"n\",\"url\":\""
                + AzureFileUrl + "\"}]}};</script></html>"), "text/html"),
            "download.microsoft.com" => (AzureFile, "application/octet-stream"),
            "ip-ranges.amazonaws.com" => (AwsFile, "application/json"),
            _ => throw new InvalidOperationException("Unexpected host."),
        };
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
