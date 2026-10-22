using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

/// <summary>
/// Pins the bytes and the order of the Google Cloud feed for the cases the shared feed engine must not change:
/// the IPv6 file, a region without global, global listed first in the source, and IPv6 turned off.
/// </summary>
public sealed class GoogleCloudFeedOutputTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    private const string SourceJson = """
        {
          "syncToken": "1770000000000",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "35.199.0.0/24", "service": "Google Cloud", "scope": "global"},
            {"ipv6Prefix": "2600:1900:4000::/44", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.191.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv6Prefix": "2600:1900::/44", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "8.8.4.0/24", "service": "Google Cloud", "scope": "europe-west1"},
            {"ipv6Prefix": "2600:1901::/48", "service": "Google Cloud", "scope": "global"}
          ]
        }
        """;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Region_Only_Writes_Both_Family_Files_With_Exact_Bytes()
    {
        (CheckResult check, GoogleCloudFeedService service, FakeIpInfoService ipinfo) = await CheckAsync("us-central1", includeGlobal: false);
        Assert.Equal(
            ["34.80.1.0/24", "35.191.1.0/24", "2600:1900::/44", "2600:1900:4000::/44"],
            check.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal(
            [Path.Combine(folder, "GoogleCloud_us-central1_ipv4.txt"), Path.Combine(folder, "GoogleCloud_us-central1_ipv6.txt")],
            written.Files.ToArray());
        Assert.Equal(
            Bytes(Header("us-central1", "IPv4", entries: 2, confirmed: 2) + "34.80.1.0/24\n35.191.1.0/24\n"),
            File.ReadAllBytes(written.Files[0]));
        Assert.Equal(
            Bytes(Header("us-central1", "IPv6", entries: 2, confirmed: 2) + "2600:1900::/44\n2600:1900:4000::/44\n"),
            File.ReadAllBytes(written.Files[1]));
        Assert.Equal(1, ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task Exceptions_That_Overlap_No_Range_Leave_The_Bytes_As_They_Were()
    {
        (CheckResult check, GoogleCloudFeedService service, FakeIpInfoService ipinfo) = await CheckAsync("us-central1", includeGlobal: false);
        // Next to each range but inside none, and an IPv6 block whose numbers equal 34.80.1.0/24
        ipinfo.Exceptions = RangeSubtractionTests.List(
            "34.80.0.255", "34.80.2.0/24", "35.191.0.0/24", "35.191.2.0", "2600:1900:10::/44", "2600:18ff:ffff:ffff:ffff:ffff:ffff:ffff",
            "::2250:100/120");

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        Assert.Equal(0, written.ExceptionCuts);
        Assert.Equal(0, written.ExceptionRemovals);
        Assert.Equal(
            Bytes(Header("us-central1", "IPv4", entries: 2, confirmed: 2) + "34.80.1.0/24\n35.191.1.0/24\n"),
            File.ReadAllBytes(written.Files[0]));
        Assert.Equal(
            Bytes(Header("us-central1", "IPv6", entries: 2, confirmed: 2) + "2600:1900::/44\n2600:1900:4000::/44\n"),
            File.ReadAllBytes(written.Files[1]));
    }

    [Fact]
    public async Task Region_And_Global_Lists_The_Region_First_Then_Global_And_Names_Both_Files_With_Global()
    {
        (CheckResult check, GoogleCloudFeedService service, _) = await CheckAsync("us-central1", includeGlobal: true);
        Assert.Equal(
            ["34.80.1.0/24", "35.191.1.0/24", "2600:1900::/44", "2600:1900:4000::/44", "35.199.0.0/24", "2600:1901::/48"],
            check.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal(
            [Path.Combine(folder, "GoogleCloud_us-central1_global_ipv4.txt"), Path.Combine(folder, "GoogleCloud_us-central1_global_ipv6.txt")],
            written.Files.ToArray());
        Assert.Equal(
            Bytes(Header("us-central1 + global", "IPv4", entries: 3, confirmed: 3) + "34.80.1.0/24\n35.191.1.0/24\n35.199.0.0/24\n"),
            File.ReadAllBytes(written.Files[0]));
        Assert.Equal(
            Bytes(Header("us-central1 + global", "IPv6", entries: 3, confirmed: 3) + "2600:1900::/44\n2600:1900:4000::/44\n2600:1901::/48\n"),
            File.ReadAllBytes(written.Files[1]));
    }

    [Fact]
    public async Task Selecting_Global_Alone_Names_The_File_Global_Without_A_Suffix()
    {
        (CheckResult check, GoogleCloudFeedService service, _) = await CheckAsync("global", includeGlobal: true);

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal(
            [Path.Combine(folder, "GoogleCloud_global_ipv4.txt"), Path.Combine(folder, "GoogleCloud_global_ipv6.txt")],
            written.Files.ToArray());
        Assert.Equal(
            Bytes(Header("global", "IPv4", entries: 1, confirmed: 1) + "35.199.0.0/24\n"),
            File.ReadAllBytes(written.Files[0]));
    }

    [Fact]
    public async Task With_IPv6_Off_The_Check_And_The_Files_Are_IPv4_Only()
    {
        (CheckResult check, GoogleCloudFeedService service, FakeIpInfoService ipinfo) =
            await CheckAsync("us-central1", includeGlobal: true, settings: new UserSettings { IPv6Enabled = false });

        Assert.Equal(
            ["34.80.1.0/24", "35.191.1.0/24", "35.199.0.0/24"],
            check.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.All(ipinfo.LastOverlapBlocks!, block => Assert.False(block.IsIpv6));

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        string path = Assert.Single(written.Files);
        Assert.Equal(Path.Combine(_temp.ExportFolder, "fortigate", "GoogleCloud_us-central1_global_ipv4.txt"), path);
        Assert.False(File.Exists(Path.Combine(_temp.ExportFolder, "fortigate", "GoogleCloud_us-central1_global_ipv6.txt")));
    }

    [Fact]
    public async Task An_Unconfirmed_Range_That_Is_Ticked_Is_Counted_In_The_Header()
    {
        (CheckResult check, GoogleCloudFeedService service, _) = await CheckAsync(
            "us-central1", includeGlobal: false, groups: block => block.Text == "35.191.1.0/24" ? [Group("example.com")] : [Group("google.com")]);

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        Assert.Equal(1, written.UnconfirmedWritten);
        Assert.Equal(
            Bytes(Header("us-central1", "IPv4", entries: 2, confirmed: 1) + "34.80.1.0/24\n35.191.1.0/24\n"),
            File.ReadAllBytes(written.Files[0]));
    }

    [Fact]
    public async Task A_Line_Break_In_The_Source_Header_Cannot_Add_A_Feed_Entry()
    {
        string json = SourceJson.Replace("\"2026-01-15T00:00:00Z\"", "\"2026-01-15T00:00:00Z\\n9.9.9.9/32\"");
        (CheckResult check, GoogleCloudFeedService service, _) = await CheckAsync("us-central1", includeGlobal: false, json: json);

        CloudFeedWriteResult written = await WriteAllAsync(service, check.Check!);

        string[] lines = File.ReadAllLines(written.Files[0]);
        Assert.Equal("# Ranges: Google Cloud, https://www.gstatic.com/ipranges/cloud.json (created 2026-01-15T00:00:00Z 9.9.9.9/32)", lines[1]);
        Assert.Equal(["34.80.1.0/24", "35.191.1.0/24"], lines.Where(line => !line.StartsWith('#')).ToArray());
    }

    private async Task<(CheckResult Check, GoogleCloudFeedService Service, FakeIpInfoService IpInfo)> CheckAsync(
        string region, bool includeGlobal, UserSettings? settings = null, Func<CidrBlock, IReadOnlyList<OverlapGroup>>? groups = null,
        string? json = null)
    {
        groups ??= _ => [Group("google.com")];
        var ipinfo = new FakeIpInfoService
        {
            Dataset = Dataset,
            Settings = settings ?? new UserSettings(),
            OverlapFunc = blocks => new OverlapResult(
                OverlapOutcome.Found,
                blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(),
                Dataset),
        };
        var http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json ?? SourceJson, Encoding.UTF8, "application/json"),
        }));
        var service = new GoogleCloudFeedService(ipinfo, http, new CoreOptions(), _time, _temp.ExportFolder);
        CatalogueResult catalogue = await service.LoadCatalogueAsync(CancellationToken.None);
        Assert.Equal(CatalogueFailure.None, catalogue.Failure);
        Assert.True(GoogleCloudScope.TryCreate(region, out GoogleCloudScope scope));
        CheckResult check = await service.CheckAsync(
            catalogue.Catalogue!, new RegionSelection(scope, includeGlobal), CancellationToken.None);
        Assert.Equal(CheckFailure.None, check.Failure);
        return (check, service, ipinfo);
    }

    private static Task<CloudFeedWriteResult> WriteAllAsync(GoogleCloudFeedService service, CloudFeedCheck check)
    {
        CloudFeedPlan plan = service.PlanWrite(check, check.Ranges.Select(range => range.Prefix.Block).ToList());
        return service.WriteAsync(plan, [], CancellationToken.None);
    }

    private static string Header(string region, string family, int entries, int confirmed) =>
        "# PoorMansFeed - Fortigate Threat Feed\n" +
        "# Ranges: Google Cloud, https://www.gstatic.com/ipranges/cloud.json (created 2026-01-15T00:00:00Z)\n" +
        $"# Region: {region}\n" +
        $"# Address Family: {family}\n" +
        "# Generated: 2026-03-19 04:05:06 UTC\n" +
        $"# Entries: {entries} ({confirmed} confirmed by IPinfo, {entries - confirmed} added without confirmation)\n" +
        "# Confirmed against: IPinfo Lite dataset of 2026-10-08\n" +
        "# IP address data powered by IPinfo (https://ipinfo.io)\n";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text.Replace("\n", Environment.NewLine));

    private static OverlapGroup Group(string domain) => new("US", "United States", "AS15169", "Google LLC", domain, 1);
}
