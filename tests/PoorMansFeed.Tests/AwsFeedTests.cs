using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.CloudFeeds;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class AwsFeedTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);
    private static readonly CancellationToken Ct = CancellationToken.None;

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
    public async Task Check_Lists_One_Regions_Ranges_In_Order_With_The_Border_Group_As_Scope()
    {
        _ipinfo.OverlapFunc = Found(_ => [Group("amazon.com")]);

        CheckResult result = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "us-east-1"), Ct);

        Assert.Equal(CheckFailure.None, result.Failure);
        Assert.Equal(
            ["3.5.0.0/22", "3.5.4.0/22", "15.181.0.0/17", "2600:1f00::/40"],
            result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(
            ["us-east-1", "us-east-1", "us-east-1-iah-1", "us-east-1"],
            result.Check.Ranges.Select(range => range.Prefix.Scope).ToArray());
    }

    [Fact]
    public async Task All_Regions_Lists_The_Union_Of_The_Services_Ranges()
    {
        CheckResult result = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", null), Ct);

        Assert.Equal(
            ["3.5.0.0/22", "3.5.4.0/22", "15.181.0.0/17", "43.198.0.0/24", "52.80.0.0/24", "52.80.1.0/24", "54.222.0.0/24", "2406:da00::/32", "2600:1f00::/40"],
            result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
    }

    [Theory]
    [InlineData("amazon.com")]
    [InlineData("sinnet.com.cn")]
    [InlineData("westcloudvalley.com")]
    [InlineData("aws.eu")]
    public async Task A_Range_Is_Confirmed_When_Every_Overlap_Belongs_To_Any_One_Of_The_Four_Operators(string domain)
    {
        _ipinfo.OverlapFunc = Found(_ => [Group(domain)]);

        CheckResult result = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "cn-north-1"), Ct);

        Assert.All(result.Check!.Ranges, range => Assert.Equal(RangeConfirmation.Confirmed, range.Status));
        Assert.All(result.Check.Ranges, range => Assert.True(range.StartsIncluded));
    }

    [Fact]
    public async Task A_Range_That_Overlaps_Anyone_Else_Is_Mixed_And_One_With_No_Overlap_Is_Not_In_The_Database()
    {
        _ipinfo.OverlapFunc = Found(block => block.Text switch
        {
            "52.80.0.0/24" => [Group("amazon.com"), Group("sinnet.com.cn")],
            "52.80.1.0/24" => [Group("sinnet.com.cn"), Group("chinatelecom.cn")],
            _ => [],
        });

        CheckResult result = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "cn-north-1"), Ct);

        Assert.Equal(
            [RangeConfirmation.Confirmed, RangeConfirmation.Mixed, RangeConfirmation.NotInDatabase],
            result.Check!.Ranges.Select(range => range.Status).ToArray());
        Assert.Equal([true, false, false], result.Check.Ranges.Select(range => range.StartsIncluded).ToArray());
    }

    [Fact]
    public async Task With_IPv6_Off_Only_IPv4_Is_Asked_For()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };

        CheckResult result = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "us-east-1"), Ct);

        Assert.Equal(["3.5.0.0/22", "3.5.4.0/22", "15.181.0.0/17"], result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(["3.5.0.0/22", "3.5.4.0/22", "15.181.0.0/17"], _ipinfo.LastOverlapBlocks!.Select(block => block.Text).ToArray());
    }

    [Theory]
    [InlineData("Nope", "us-east-1")]
    [InlineData("EC2", "mars-1")]
    [InlineData("EC2", "US-EAST-1")]
    [InlineData("ec2", null)]
    [InlineData("a b/c", "x\ny")]
    public async Task An_Unknown_Pick_Selects_Nothing_And_No_Dataset_Is_Still_Reported_First(string service, string? region)
    {
        CheckResult unknown = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection(service, region), Ct);
        Assert.Equal(CheckFailure.NothingSelected, unknown.Failure);

        _ipinfo.Dataset = null;
        CheckResult noDataset = await Service().CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "us-east-1"), Ct);
        Assert.Equal(CheckFailure.NoDataset, noDataset.Failure);
        Assert.Null(noDataset.Check);
    }

    [Fact]
    public async Task The_Feed_File_Bytes_Match_The_Header_And_Only_Ticked_Ranges()
    {
        _ipinfo.OverlapFunc = Found(block => block.Text == "52.80.1.0/24" ? [Group("chinatelecom.cn")] : [Group("sinnet.com.cn")]);
        AwsFeedService service = Service();
        CheckResult result = await service.CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", "cn-north-1"), Ct);
        CloudFeedPlan plan = service.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await service.WriteAsync(plan, [], Ct);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(1, written.UnconfirmedWritten);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal([Path.Combine(folder, "AWS_EC2_cn-north-1_ipv4.txt"), Path.Combine(folder, "AWS_EC2_cn-north-1_ipv6.txt")], written.Files.ToArray());
        string nl = Environment.NewLine;
        string expected =
            "# PoorMansFeed - Fortigate Threat Feed" + nl +
            "# Ranges: AWS ip-ranges.json (sync token 1792136826, created 2026-10-16 07:47:06 UTC)" + nl +
            "# Service: EC2" + nl +
            "# Region: cn-north-1" + nl +
            "# Address Family: IPv4" + nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + nl +
            "# Entries: 2 (1 confirmed by IPinfo, 1 added without confirmation)" + nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + nl +
            "52.80.0.0/24" + nl +
            "52.80.1.0/24" + nl;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(written.Files[0]));
        Assert.Contains("# Address Family: IPv6" + nl, File.ReadAllText(written.Files[1]));
        Assert.Contains("2406:da00::/32" + nl, File.ReadAllText(written.Files[1]));
    }

    [Fact]
    public async Task All_Regions_Is_Written_Under_The_All_Stem_And_Says_So_In_The_Header()
    {
        AwsFeedService service = Service();
        CheckResult result = await service.CheckAsync(AwsTestData.Catalogue(), new AwsSelection("EC2", null), Ct);
        CloudFeedPlan plan = service.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await service.WriteAsync(plan, [], Ct);

        Assert.Equal(
            Path.Combine(_temp.ExportFolder, "fortigate", "AWS_EC2_all_ipv4.txt"),
            written.Files[0]);
        Assert.Contains("# Region: all regions" + Environment.NewLine, File.ReadAllText(written.Files[0]));
    }

    [Fact]
    public async Task The_Global_Region_Gets_A_Plain_File_Name()
    {
        AwsFeedService service = Service();
        CheckResult result = await service.CheckAsync(AwsTestData.Catalogue(), new AwsSelection("CLOUDFRONT", "GLOBAL"), Ct);
        CloudFeedPlan plan = service.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await service.WriteAsync(plan, [], Ct);

        Assert.Equal(Path.Combine(_temp.ExportFolder, "fortigate", "AWS_CLOUDFRONT_GLOBAL_ipv4.txt"), Assert.Single(written.Files));
    }

    private AwsFeedService Service() =>
        new(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder);

    private static Func<IReadOnlyList<CidrBlock>, OverlapResult> Found(Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(),
            Dataset);

    private static OverlapGroup Group(string domain) => new("US", "United States", "AS16509", "Amazon.com, Inc.", domain, 1);
}
