using System.Net;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class GoogleCloudFeedTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Classify_Confirms_Only_Google_Domains()
    {
        Assert.Equal(RangeConfirmation.Confirmed, GoogleConfirmation.Classify(
            [Group("google.com"), Group("google.com")]));
        Assert.Equal(RangeConfirmation.Mixed, GoogleConfirmation.Classify(
            [Group("google.com"), Group("example.com")]));
        Assert.Equal(RangeConfirmation.NotInDatabase, GoogleConfirmation.Classify([]));
        Assert.Equal(RangeConfirmation.Mixed, GoogleConfirmation.Classify([Group("  ")]));
        Assert.Equal(RangeConfirmation.Mixed, GoogleConfirmation.Classify([Group(null)]));
        Assert.Equal(RangeConfirmation.Confirmed, GoogleConfirmation.Classify([Group(" Google.COM ")]));
    }

    [Fact]
    public void Scope_Rejects_A_Slash_Or_A_Dot()
    {
        Assert.False(GoogleCloudScope.TryCreate("us/central1", out _));
        Assert.False(GoogleCloudScope.TryCreate("us.central1", out _));
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));
        Assert.Equal("us-central1", scope.Id);
    }

    [Fact]
    public async Task Selecting_Global_With_The_Checkbox_On_Does_Not_Duplicate_Prefixes()
    {
        const string json = """
            {
              "syncToken": "1",
              "creationTime": "2026-01-15T00:00:00Z",
              "prefixes": [
                {"ipv4Prefix": "1.2.3.0/24", "service": "Google Cloud", "scope": "global"},
                {"ipv4Prefix": "9.9.9.0/24", "service": "Google Cloud", "scope": "us-central1"}
              ]
            }
            """;
        using var feeds = Feed(json, Overlaps(EmptyGroups));
        CatalogueResult catalogue = await feeds.Service.LoadCatalogueAsync(CancellationToken.None);
        Assert.Equal(CatalogueFailure.None, catalogue.Failure);

        Assert.True(GoogleCloudScope.TryCreate("global", out GoogleCloudScope global));
        CheckResult check = await feeds.Service.CheckAsync(
            catalogue.Catalogue!,
            new RegionSelection(global, IncludeGlobal: true),
            CancellationToken.None);

        Assert.Equal(CheckFailure.None, check.Failure);
        CheckedRange range = Assert.Single(check.Check!.Ranges);
        Assert.Equal("1.2.3.0/24", range.Prefix.Block.Text);
        Assert.Equal("global", range.Prefix.Scope);
    }

    [Fact]
    public async Task Prefixes_With_One_Start_Are_Checked_Broadest_First()
    {
        const string json = """
            {
              "syncToken": "1",
              "creationTime": "2026-01-15T00:00:00Z",
              "prefixes": [
                {"ipv4Prefix": "9.0.0.0/8", "service": "Google Cloud", "scope": "us-central1"},
                {"ipv4Prefix": "10.0.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
                {"ipv4Prefix": "10.0.0.0/8", "service": "Google Cloud", "scope": "us-central1"}
              ]
            }
            """;
        using var feeds = Feed(json, Overlaps(EmptyGroups));
        CatalogueResult catalogue = await feeds.Service.LoadCatalogueAsync(CancellationToken.None);
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));

        CheckResult check = await feeds.Service.CheckAsync(
            catalogue.Catalogue!,
            new RegionSelection(scope, IncludeGlobal: false),
            CancellationToken.None);

        Assert.Equal(CheckFailure.None, check.Failure);
        Assert.Equal(["9.0.0.0/8", "10.0.0.0/8", "10.0.0.0/16"], check.Check!.Ranges.Select(range => range.Prefix.Block.Text));
    }

    [Fact]
    public async Task Unusable_Database_Returns_NoDataset_And_Writes_No_File()
    {
        using var http = new HttpClient(new StubHttpHandler((_, _) => JsonResponse("{}")));
        using var ipinfo = new IpInfoService(_temp.Paths, http, new CoreOptions(), _time, () => false);
        Assert.True(CidrBlock.TryParse("10.1.2.0/24", out CidrBlock? block));
        OverlapResult overlaps = await ipinfo.LookUpOverlapsAsync([block], CancellationToken.None);
        Assert.Equal(OverlapOutcome.NoDataset, overlaps.Outcome);
        Assert.Empty(overlaps.Overlaps);

        var service = new FakeIpInfoService { Dataset = null };
        service.OverlapFunc = _ => throw new InvalidOperationException("queried");
        using var cloudHttp = new HttpClient(new StubHttpHandler((_, _) => JsonResponse(OnePrefixJson)));
        var feeds = new GoogleCloudFeedService(service, cloudHttp, new CoreOptions(), _time, _temp.ExportFolder);
        CatalogueResult catalogue = await feeds.LoadCatalogueAsync(CancellationToken.None);
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));
        CheckResult check = await feeds.CheckAsync(
            catalogue.Catalogue!,
            new RegionSelection(scope, IncludeGlobal: false),
            CancellationToken.None);

        Assert.Equal(CheckFailure.NoDataset, check.Failure);
        Assert.Null(check.Check);
        Assert.Equal(1, service.OverlapCalls);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task Feed_File_Bytes_Match_The_Header_And_Only_Ticked_Prefixes()
    {
        using FeedHarness feeds = Feed(FeedJson, Overlaps(GroupsFor));
        CatalogueResult catalogue = await feeds.Service.LoadCatalogueAsync(CancellationToken.None);
        Assert.Equal(CatalogueFailure.None, catalogue.Failure);
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));
        CheckResult check = await feeds.Service.CheckAsync(
            catalogue.Catalogue!,
            new RegionSelection(scope, IncludeGlobal: true),
            CancellationToken.None);
        Assert.Equal(CheckFailure.None, check.Failure);

        var ticked = check.Check!.Ranges
            .Where(range => range.Prefix.Block.Text is "34.80.1.0/24" or "35.190.1.0/24")
            .Select(range => range.Prefix.Block)
            .ToList();
        CloudFeedPlan plan = feeds.Service.PlanWrite(check.Check, ticked);
        feeds.IpInfo.Settings = feeds.IpInfo.Settings with { FortigateEnabled = false, FortigateSplitByFamily = false };
        CloudFeedWriteResult written = await feeds.Service.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string path = Assert.Single(written.Files);
        Assert.Equal(
            Path.Combine(_temp.ExportFolder, "fortigate", "GoogleCloud_us-central1_global_ipv4.txt"),
            path);
        Assert.False(File.Exists(Path.Combine(_temp.ExportFolder, "fortigate", "GoogleCloud_us-central1_global_ipv6.txt")));

        string nl = Environment.NewLine;
        string expected =
            "# PoorMansFeed - Fortigate Threat Feed" + nl +
            "# Ranges: Google Cloud, https://www.gstatic.com/ipranges/cloud.json (created 2026-01-15T00:00:00Z)" + nl +
            "# Region: us-central1 + global" + nl +
            "# Address Family: IPv4" + nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + nl +
            "# Entries: 2 (1 confirmed by IPinfo, 1 added without confirmation)" + nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + nl +
            "34.80.1.0/24" + nl +
            "35.190.1.0/24" + nl;
        byte[] actual = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actual);
        Assert.Equal((byte)'#', actual[0]);
    }

    [Fact]
    public async Task Settings_Change_Writes_Nothing()
    {
        using FeedHarness feeds = Feed(OnePrefixJson, Overlaps(EmptyGroups));
        CatalogueResult catalogue = await feeds.Service.LoadCatalogueAsync(CancellationToken.None);
        Assert.True(GoogleCloudScope.TryCreate("us-central1", out GoogleCloudScope scope));
        CheckResult check = await feeds.Service.CheckAsync(
            catalogue.Catalogue!,
            new RegionSelection(scope, IncludeGlobal: false),
            CancellationToken.None);
        CloudFeedPlan plan = feeds.Service.PlanWrite(check.Check!, check.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        feeds.IpInfo.Settings = feeds.IpInfo.Settings with { IPv6Enabled = false };
        CloudFeedWriteResult ipv6Changed = await feeds.Service.WriteAsync(plan, [], CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.SettingsChanged, ipv6Changed.Failure);
        Assert.Empty(ipv6Changed.Files);

        feeds.IpInfo.Settings = feeds.IpInfo.Settings with { IPv6Enabled = true, IPv4MinPrefix = 20 };
        CloudFeedWriteResult prefixChanged = await feeds.Service.WriteAsync(plan, [], CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.SettingsChanged, prefixChanged.Failure);
        Assert.Empty(prefixChanged.Files);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    private const string OnePrefixJson = """
        {
          "syncToken": "1",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"}
          ]
        }
        """;

    private const string FeedJson = """
        {
          "syncToken": "1770000000000",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "34.80.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.190.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.191.1.0/24", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.199.0.0/24", "service": "Google Cloud", "scope": "global"},
            {"ipv4Prefix": "bad", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "1.2.3.0/24", "ipv6Prefix": "2001:db8::/32", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "1.2.4.0/24", "service": "Google", "scope": "us-central1"}
          ]
        }
        """;

    private FeedHarness Feed(string json, Func<IReadOnlyList<CidrBlock>, OverlapResult> overlaps)
    {
        var ipinfo = new FakeIpInfoService
        {
            Dataset = Dataset,
            OverlapFunc = overlaps,
        };
        var http = new HttpClient(new StubHttpHandler((_, _) => JsonResponse(json)));
        var service = new GoogleCloudFeedService(ipinfo, http, new CoreOptions(), _time, _temp.ExportFolder);
        return new FeedHarness(ipinfo, http, service);
    }

    private static Func<IReadOnlyList<CidrBlock>, OverlapResult> Overlaps(Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(),
            Dataset);

    private static IReadOnlyList<OverlapGroup> GroupsFor(CidrBlock block) => block.Text switch
    {
        "35.190.1.0/24" => [Group("example.com")],
        _ => [Group("google.com")],
    };

    private static IReadOnlyList<OverlapGroup> EmptyGroups(CidrBlock block) => [];

    private static OverlapGroup Group(string? domain) => new("US", "United States", "AS15169", "Google LLC", domain, 1);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class FeedHarness(FakeIpInfoService ipinfo, HttpClient http, GoogleCloudFeedService service) : IDisposable
    {
        public FakeIpInfoService IpInfo { get; } = ipinfo;
        public GoogleCloudFeedService Service { get; } = service;

        public void Dispose() => http.Dispose();
    }
}
