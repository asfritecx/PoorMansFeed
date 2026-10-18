using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class CloudFeedEngineTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Classify_Uses_The_Domain_It_Is_Given()
    {
        Assert.Equal(RangeConfirmation.Confirmed, DomainConfirmation.Classify([Group("microsoft.com"), Group(" Microsoft.COM ")], "microsoft.com"));
        Assert.Equal(RangeConfirmation.Mixed, DomainConfirmation.Classify([Group("microsoft.com"), Group("google.com")], "microsoft.com"));
        Assert.Equal(RangeConfirmation.Mixed, DomainConfirmation.Classify([Group("google.com")], "microsoft.com"));
        Assert.Equal(RangeConfirmation.Mixed, DomainConfirmation.Classify([Group(null)], "microsoft.com"));
        Assert.Equal(RangeConfirmation.NotInDatabase, DomainConfirmation.Classify([], "microsoft.com"));
    }

    [Fact]
    public async Task Check_Keeps_The_Callers_Order_And_Asks_For_Every_Block_Once()
    {
        CloudFeedEngine engine = Engine();
        FeedPrefix[] ordered = [Prefix("35.1.0.0/16", "b"), Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")];

        CheckResult result = await engine.CheckAsync(Description(), ordered, CancellationToken.None);

        Assert.Equal(CheckFailure.None, result.Failure);
        Assert.Equal(["35.1.0.0/16", "34.1.0.0/16", "2001:db8::/32"], result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(["b", "a", "a"], result.Check.Ranges.Select(range => range.Prefix.Scope).ToArray());
        Assert.Equal(["35.1.0.0/16", "34.1.0.0/16", "2001:db8::/32"], _ipinfo.LastOverlapBlocks!.Select(block => block.Text).ToArray());
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task Check_Drops_IPv6_Before_The_Query_When_IPv6_Is_Off()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };
        CloudFeedEngine engine = Engine();

        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], CancellationToken.None);

        Assert.Equal(["34.1.0.0/16"], result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(["34.1.0.0/16"], _ipinfo.LastOverlapBlocks!.Select(block => block.Text).ToArray());
        Assert.False(result.Check.IncludesIPv6);
    }

    [Fact]
    public async Task Check_Classifies_Each_Range_By_The_Descriptions_Domain()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/16"
            ? [Group("example.com")]
            : block.Text == "36.1.0.0/16" ? [] : [Group("microsoft.com")]);
        CloudFeedEngine engine = Engine();

        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a")], CancellationToken.None);

        Assert.Equal(
            [RangeConfirmation.Confirmed, RangeConfirmation.Mixed, RangeConfirmation.NotInDatabase],
            result.Check!.Ranges.Select(range => range.Status).ToArray());
        Assert.Equal([true, false, false], result.Check.Ranges.Select(range => range.StartsIncluded).ToArray());
    }

    [Fact]
    public async Task Check_Reports_NoDataset_Before_NothingSelected()
    {
        _ipinfo.Dataset = null;
        CloudFeedEngine engine = Engine();

        CheckResult empty = await engine.CheckAsync(Description(), [], CancellationToken.None);
        CheckResult some = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], CancellationToken.None);

        Assert.Equal(CheckFailure.NoDataset, empty.Failure);
        Assert.Equal(CheckFailure.NoDataset, some.Failure);
        Assert.Null(empty.Check);
    }

    [Fact]
    public async Task Check_Reports_NothingSelected_For_An_Empty_List_And_Failed_For_A_Bad_Answer()
    {
        CloudFeedEngine engine = Engine();
        Assert.Equal(CheckFailure.NothingSelected, (await engine.CheckAsync(Description(), [], CancellationToken.None)).Failure);

        _ipinfo.OverlapFunc = _ => new OverlapResult(OverlapOutcome.Found, [], Dataset);
        Assert.Equal(
            CheckFailure.Failed,
            (await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], CancellationToken.None)).Failure);

        _ipinfo.OverlapFunc = _ => new OverlapResult(OverlapOutcome.Failed, [], null);
        Assert.Equal(
            CheckFailure.Failed,
            (await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Write_Puts_The_Header_Lines_After_The_Title_And_Names_The_File_From_The_Stem()
    {
        CloudFeedEngine engine = Engine();
        FeedDescription description = new("microsoft.com", ["Ranges: Test source", "Service tag: Storage"], "Azure_Storage");
        CheckResult result = await engine.CheckAsync(
            description, [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal([Path.Combine(folder, "Azure_Storage_ipv4.txt"), Path.Combine(folder, "Azure_Storage_ipv6.txt")], written.Files.ToArray());
        string nl = Environment.NewLine;
        string expected =
            "# PoorMansFeed - Fortigate Threat Feed" + nl +
            "# Ranges: Test source" + nl +
            "# Service tag: Storage" + nl +
            "# Address Family: IPv4" + nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + nl +
            "# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)" + nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + nl +
            "34.1.0.0/16" + nl;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(written.Files[0]));
        Assert.StartsWith("# PoorMansFeed - Fortigate Threat Feed", File.ReadAllText(written.Files[1]));
        Assert.Contains("# Address Family: IPv6" + nl, File.ReadAllText(written.Files[1]));
    }

    [Fact]
    public async Task Write_Leaves_Out_A_Large_Range_Unless_It_Is_Allowed()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("10.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a")], CancellationToken.None);
        List<CidrBlock> all = result.Check!.Ranges.Select(range => range.Prefix.Block).ToList();
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, all);
        Assert.Equal(["10.0.0.0/8"], plan.LargeRanges.Select(range => range.Network).ToArray());

        CloudFeedWriteResult without = await engine.WriteAsync(plan, [], CancellationToken.None);
        Assert.Equal(1, without.RangesWritten);
        Assert.Equal(1, without.LargeRangesLeftOut);
        Assert.DoesNotContain("10.0.0.0/8", File.ReadAllText(without.Files[0]));

        CloudFeedWriteResult with = await engine.WriteAsync(plan, [all[0]], CancellationToken.None);
        Assert.Equal(2, with.RangesWritten);
        Assert.Contains("10.0.0.0/8", File.ReadAllText(with.Files[0]));
    }

    [Fact]
    public async Task Write_Refuses_When_The_Settings_Changed_After_The_Check()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 12 };

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.SettingsChanged, written.Failure);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("..\\x")]
    [InlineData("caf\u00E9")]
    public void Description_Rejects_A_Stem_That_Is_Not_Safe_In_A_File_Name(string stem)
    {
        Assert.Throws<ArgumentException>(() => new FeedDescription("microsoft.com", ["Ranges: x"], stem));
    }

    [Theory]
    [InlineData("Line one\nLine two")]
    [InlineData("Line one\rLine two")]
    public void Description_Rejects_A_Header_Line_That_Could_Add_A_Feed_Entry(string line)
    {
        Assert.Throws<ArgumentException>(() => new FeedDescription("microsoft.com", [line], "Azure_Storage"));
    }

    [Fact]
    public void Description_Rejects_A_Blank_Domain_And_Accepts_A_Dotted_Stem()
    {
        Assert.Throws<ArgumentException>(() => new FeedDescription(" ", ["Ranges: x"], "Azure_Storage"));
        Assert.Equal("Azure_Storage.v1-x", new FeedDescription("microsoft.com", ["Ranges: x"], "Azure_Storage.v1-x").FileStem);
    }

    [Fact]
    public void Classify_With_Several_Domains_Confirms_A_Range_Only_When_Every_Group_Is_In_The_List()
    {
        string[] domains = ["amazon.com", "sinnet.com.cn"];

        Assert.Equal(RangeConfirmation.Confirmed, DomainConfirmation.Classify([Group("amazon.com"), Group(" SINNET.com.cn ")], domains));
        Assert.Equal(RangeConfirmation.Confirmed, DomainConfirmation.Classify([Group("sinnet.com.cn")], domains));
        Assert.Equal(RangeConfirmation.Mixed, DomainConfirmation.Classify([Group("amazon.com"), Group("chinatelecom.cn")], domains));
        Assert.Equal(RangeConfirmation.Mixed, DomainConfirmation.Classify([Group(null)], domains));
        Assert.Equal(RangeConfirmation.NotInDatabase, DomainConfirmation.Classify([], domains));
        Assert.Throws<ArgumentException>(() => DomainConfirmation.Classify([Group("amazon.com")], Array.Empty<string>()));
    }

    [Fact]
    public async Task Check_Confirms_A_Range_Against_Any_Of_The_Descriptions_Domains()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text switch
        {
            "34.1.0.0/16" => [Group("amazon.com")],
            "35.1.0.0/16" => [Group("sinnet.com.cn")],
            "36.1.0.0/16" => [Group("amazon.com"), Group("sinnet.com.cn")],
            _ => [Group("example.com")],
        });
        CloudFeedEngine engine = Engine();
        var description = new FeedDescription(["amazon.com", "sinnet.com.cn"], ["Ranges: Test source"], "Aws_Test");

        CheckResult result = await engine.CheckAsync(
            description,
            [Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a"), Prefix("37.1.0.0/16", "a")],
            CancellationToken.None);

        Assert.Equal(
            [RangeConfirmation.Confirmed, RangeConfirmation.Confirmed, RangeConfirmation.Confirmed, RangeConfirmation.Mixed],
            result.Check!.Ranges.Select(range => range.Status).ToArray());
    }

    [Fact]
    public void Description_With_Several_Domains_Keeps_Them_And_Exposes_The_First_As_ConfirmDomain()
    {
        var description = new FeedDescription([" amazon.com ", "aws.eu"], ["Ranges: x"], "Aws_Test");

        Assert.Equal(["amazon.com", "aws.eu"], description.ConfirmDomains.ToArray());
        Assert.Equal("amazon.com", description.ConfirmDomain);
        Assert.Equal(["microsoft.com"], new FeedDescription("microsoft.com", ["Ranges: x"], "Azure_Test").ConfirmDomains.ToArray());
    }

    [Fact]
    public void Description_Rejects_An_Empty_Domain_List_And_A_Blank_Entry()
    {
        Assert.Throws<ArgumentException>(() => new FeedDescription(Array.Empty<string>(), ["Ranges: x"], "Aws_Test"));
        Assert.Throws<ArgumentException>(() => new FeedDescription(["amazon.com", " "], ["Ranges: x"], "Aws_Test"));
        Assert.Throws<ArgumentException>(() => new FeedDescription(["amazon.com", null!], ["Ranges: x"], "Aws_Test"));
    }

    private CloudFeedEngine Engine()
    {
        _ipinfo.OverlapFunc ??= blocks => Found(blocks, _ => [Group("microsoft.com")]);
        return new CloudFeedEngine(_ipinfo, _time, _temp.ExportFolder);
    }

    private static FeedDescription Description() => new("microsoft.com", ["Ranges: Test source"], "Azure_Test");

    private static FeedPrefix Prefix(string cidr, string scope)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return new FeedPrefix(block, scope);
    }

    private static OverlapResult Found(IReadOnlyList<CidrBlock> blocks, Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        new(OverlapOutcome.Found, blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(), Dataset);

    private static OverlapGroup Group(string? domain) => new("US", "United States", "AS8075", "Microsoft", domain, 1);
}
