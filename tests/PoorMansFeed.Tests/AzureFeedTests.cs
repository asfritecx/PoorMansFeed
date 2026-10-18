using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AzureFeedTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private const string TagsJson = """
        {"changeNumber":422,"cloud":"Public","values":[
          {"name":"Storage","properties":{"changeNumber":3,"region":"","addressPrefixes":["20.60.0.0/16","2603:1000::/32"]}},
          {"name":"Storage.SoutheastAsia","properties":{"changeNumber":2,"region":"southeastasia","addressPrefixes":["20.44.1.0/24","20.44.0.0/24","2603:1010::/48"]}},
          {"name":"AzureMonitor.Core","properties":{"changeNumber":1,"region":"","addressPrefixes":["52.1.0.0/24"]}}
        ]}
        """;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };
    private readonly HttpClient _http = new(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Check_Lists_The_Tags_Ranges_In_Order_With_The_Region_As_Scope()
    {
        AzureTagCatalogue catalogue = Catalogue();
        _ipinfo.OverlapFunc = Found(_ => [Group("microsoft.com")]);

        CheckResult result = await Service().CheckAsync(catalogue, new AzureTagSelection("Storage.SoutheastAsia"), Ct);

        Assert.Equal(CheckFailure.None, result.Failure);
        Assert.Equal(
            ["20.44.0.0/24", "20.44.1.0/24", "2603:1010::/48"],
            result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.All(result.Check.Ranges, range => Assert.Equal("southeastasia", range.Prefix.Scope));
    }

    [Fact]
    public async Task A_Tag_With_No_Region_Is_Shown_As_Global()
    {
        _ipinfo.OverlapFunc = Found(_ => [Group("microsoft.com")]);

        CheckResult result = await Service().CheckAsync(Catalogue(), new AzureTagSelection("Storage"), Ct);

        Assert.All(result.Check!.Ranges, range => Assert.Equal("global", range.Prefix.Scope));
    }

    [Fact]
    public async Task A_Range_Is_Confirmed_Only_When_Every_Overlap_Is_Microsoft()
    {
        _ipinfo.OverlapFunc = Found(block => block.Text switch
        {
            "20.44.0.0/24" => [Group("microsoft.com")],
            "20.44.1.0/24" => [Group("microsoft.com"), Group("example.com")],
            _ => [],
        });

        CheckResult result = await Service().CheckAsync(Catalogue(), new AzureTagSelection("Storage.SoutheastAsia"), Ct);

        Assert.Equal(
            [RangeConfirmation.Confirmed, RangeConfirmation.Mixed, RangeConfirmation.NotInDatabase],
            result.Check!.Ranges.Select(range => range.Status).ToArray());
    }

    [Fact]
    public async Task With_IPv6_Off_Only_IPv4_Is_Asked_For()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };

        CheckResult result = await Service().CheckAsync(Catalogue(), new AzureTagSelection("Storage"), Ct);

        Assert.Equal(["20.60.0.0/16"], result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(["20.60.0.0/16"], _ipinfo.LastOverlapBlocks!.Select(block => block.Text).ToArray());
    }

    [Fact]
    public async Task An_Unknown_Tag_Selects_Nothing_And_No_Dataset_Is_Still_Reported_First()
    {
        CheckResult unknown = await Service().CheckAsync(Catalogue(), new AzureTagSelection("Nope"), Ct);
        Assert.Equal(CheckFailure.NothingSelected, unknown.Failure);

        _ipinfo.Dataset = null;
        CheckResult noDataset = await Service().CheckAsync(Catalogue(), new AzureTagSelection("Storage"), Ct);
        Assert.Equal(CheckFailure.NoDataset, noDataset.Failure);
        Assert.Null(noDataset.Check);
    }

    [Fact]
    public async Task The_Feed_File_Bytes_Match_The_Header_And_Only_Ticked_Ranges()
    {
        _ipinfo.OverlapFunc = Found(block => block.Text == "20.44.1.0/24" ? [Group("example.com")] : [Group("microsoft.com")]);
        AzureFeedService service = Service();
        CheckResult result = await service.CheckAsync(Catalogue(), new AzureTagSelection("Storage.SoutheastAsia"), Ct);
        List<CidrBlock> ticked = result.Check!.Ranges
            .Where(range => range.Prefix.Block.Text is "20.44.0.0/24" or "20.44.1.0/24")
            .Select(range => range.Prefix.Block)
            .ToList();
        CloudFeedPlan plan = service.PlanWrite(result.Check, ticked);

        CloudFeedWriteResult written = await service.WriteAsync(plan, [], Ct);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(1, written.UnconfirmedWritten);
        string path = Assert.Single(written.Files);
        Assert.Equal(Path.Combine(_temp.ExportFolder, "fortigate", "Azure_Storage_SoutheastAsia_ipv4.txt"), path);
        string nl = Environment.NewLine;
        string expected =
            "# PoorMansFeed - Fortigate Threat Feed" + nl +
            "# Ranges: Azure service tags, ServiceTags_Public_20261012.json (change 422)" + nl +
            "# Service tag: Storage.SoutheastAsia" + nl +
            "# Address Family: IPv4" + nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + nl +
            "# Entries: 2 (1 confirmed by IPinfo, 1 added without confirmation)" + nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + nl +
            "20.44.0.0/24" + nl +
            "20.44.1.0/24" + nl;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Both_Families_Are_Written_And_A_Dotted_Name_Becomes_An_Underscore_In_The_File_Name()
    {
        AzureFeedService service = Service();
        CheckResult storage = await service.CheckAsync(Catalogue(), new AzureTagSelection("Storage"), Ct);
        CloudFeedWriteResult both = await service.WriteAsync(
            service.PlanWrite(storage.Check!, storage.Check!.Ranges.Select(range => range.Prefix.Block).ToList()), [], Ct);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal([Path.Combine(folder, "Azure_Storage_ipv4.txt"), Path.Combine(folder, "Azure_Storage_ipv6.txt")], both.Files.ToArray());
        Assert.Contains("# Address Family: IPv6" + Environment.NewLine + "# Generated", File.ReadAllText(both.Files[1]));
        Assert.Contains("2603:1000::/32" + Environment.NewLine, File.ReadAllText(both.Files[1]));

        CheckResult core = await service.CheckAsync(Catalogue(), new AzureTagSelection("AzureMonitor.Core"), Ct);
        CloudFeedWriteResult dotted = await service.WriteAsync(
            service.PlanWrite(core.Check!, core.Check!.Ranges.Select(range => range.Prefix.Block).ToList()), [], Ct);
        Assert.Equal(Path.Combine(folder, "Azure_AzureMonitor_Core_ipv4.txt"), Assert.Single(dotted.Files));
        Assert.Contains("# Service tag: AzureMonitor.Core", File.ReadAllText(dotted.Files[0]));
    }

    private AzureFeedService Service() =>
        new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private static AzureTagCatalogue Catalogue()
    {
        byte[] json = Encoding.UTF8.GetBytes(TagsJson);
        ParsedAzureTags parsed = AzureServiceTagsParser.TryParse(json)!;
        var source = new AzureTagFileSource(
            new Uri("https://download.microsoft.com/download/7/1/d/aaaa/ServiceTags_Public_20261012.json"),
            "ServiceTags_Public_20261012.json", new DateOnly(2026, 10, 12), parsed.ChangeNumber, parsed.Cloud, Generated, "abc", json.Length);
        return new AzureTagCatalogue(source, parsed.Services, parsed.SkippedTags, parsed.SkippedPrefixes);
    }

    private static Func<IReadOnlyList<CidrBlock>, OverlapResult> Found(Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(),
            Dataset);

    private static OverlapGroup Group(string domain) => new("US", "United States", "AS8075", "Microsoft Corporation", domain, 1);
}
