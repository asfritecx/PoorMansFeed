using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class ModSecurityFeedCloudTests : IDisposable
{
    private const string Nl = "\n";
    private const string FortigateTitle = "# PoorMansFeed - Fortigate Threat Feed";
    private const string ModSecurityTitle = "# PoorMansFeed - ModSecurity IP list";
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };

    public void Dispose() => _temp.Dispose();

    private string Fortigate(string name) => Path.Combine(_temp.ExportFolder, "fortigate", name);

    private string ModSecurity(string name) => Path.Combine(_temp.ExportFolder, "modsecurity", name);

    [Fact]
    public async Task FortiGate_Only_Writes_Todays_Files_And_No_ModSecurity_Folder()
    {
        CloudFeedEngine engine = Engine();
        CloudFeedPlan plan = await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32");

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([Fortigate("Azure_Test_ipv4.txt"), Fortigate("Azure_Test_ipv6.txt")], written.Files.ToArray());
        Assert.Null(written.ModSecurityFiles);
        Assert.Equal(0, written.WholeRangesLeftOut);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "modsecurity")));
        Assert.Equal(
            FortigateTitle + Nl +
            "# Ranges: Test source" + Nl +
            "# Address Family: IPv4" + Nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + Nl +
            "# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)" + Nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + Nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + Nl +
            "34.1.0.0/16" + Nl,
            File.ReadAllText(written.Files[0]));
    }

    [Fact]
    public async Task ModSecurity_Only_Writes_The_FortiGate_Bytes_With_Its_Own_First_Line_And_Makes_No_FortiGate_Folder()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/24" ? [Group("example.com")] : [Group("microsoft.com")]);
        string[] prefixes = ["34.1.0.0/16", "35.1.0.0/24", "203.0.113.7/32", "2001:db8::/32"];
        string otherExport = Path.Combine(_temp.Folder, "other");
        CloudFeedEngine fortigateEngine = new(_ipinfo, _time, otherExport);
        CloudFeedWriteResult reference = await fortigateEngine.WriteAsync(await PlanAll(fortigateEngine, prefixes), [], CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.None, reference.Failure);

        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = true };
        CloudFeedEngine engine = Engine();
        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, prefixes), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string[] expected = [ModSecurity("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv6.txt")];
        Assert.Equal(expected, written.Files.ToArray());
        Assert.Equal(expected, written.ModSecurityFiles!.ToArray());
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(Path.GetFileName(reference.Files[i]), Path.GetFileName(expected[i]));
            string fortigateText = File.ReadAllText(reference.Files[i]);
            Assert.StartsWith(FortigateTitle + Nl, fortigateText);
            Assert.Equal(ModSecurityTitle + Nl + fortigateText[(FortigateTitle.Length + Nl.Length)..], File.ReadAllText(expected[i]));
            FeedFormat.AssertWellFormed(File.ReadAllBytes(expected[i]));
        }

        Assert.Equal(reference.RangesWritten, written.RangesWritten);
        Assert.Equal(reference.UnconfirmedWritten, written.UnconfirmedWritten);
    }

    [Fact]
    public async Task ModSecurity_Only_Never_Touches_Or_Names_An_Existing_FortiGate_File()
    {
        // The exceptions empty the IPv4 family, so its old file in each folder is a candidate for OldFilesLeft
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/16");
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = true };
        string oldFortigate = Fortigate("Azure_Test_ipv4.txt");
        string oldModSecurity = ModSecurity("Azure_Test_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFortigate)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldModSecurity)!);
        File.WriteAllText(oldFortigate, "old fortigate\n");
        File.WriteAllText(oldModSecurity, "old modsecurity\n");
        DateTime stamp = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(oldFortigate, stamp);
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([ModSecurity("Azure_Test_ipv6.txt")], written.Files.ToArray());
        Assert.Equal([oldModSecurity], written.OldFilesLeft!);
        Assert.Equal("old fortigate\n", File.ReadAllText(oldFortigate));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(oldFortigate));
        Assert.Equal([oldFortigate], Directory.GetFiles(Path.GetDirectoryName(oldFortigate)!));
    }

    [Fact]
    public async Task Both_Types_Write_FortiGate_First_And_Count_One_Copy()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/24" ? [Group("example.com")] : [Group("microsoft.com")]);
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(
            await PlanAll(engine, "34.1.0.0/16", "35.1.0.0/24", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(
            [Fortigate("Azure_Test_ipv4.txt"), Fortigate("Azure_Test_ipv6.txt"), ModSecurity("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv6.txt")],
            written.Files.ToArray());
        Assert.Equal([ModSecurity("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv6.txt")], written.ModSecurityFiles!.ToArray());
        Assert.Equal(3, written.RangesWritten);
        Assert.Equal(1, written.UnconfirmedWritten);
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
        foreach (string file in written.ModSecurityFiles!)
            Assert.True(ModSecurityRules.IsFileName(Path.GetFileName(file)), file);
        for (int i = 0; i < 2; i++)
        {
            string fortigateText = File.ReadAllText(written.Files[i]);
            Assert.Equal(ModSecurityTitle + fortigateText[FortigateTitle.Length..], File.ReadAllText(written.Files[i + 2]));
        }
    }

    [Fact]
    public async Task Exceptions_Cut_Both_Copies_Alike_And_An_Emptied_Family_Names_Each_Old_File_FortiGate_First()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/17", "2001:db8::/32");
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        string oldFortigate = Fortigate("Azure_Test_ipv6.txt");
        string oldModSecurity = ModSecurity("Azure_Test_ipv6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFortigate)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldModSecurity)!);
        File.WriteAllText(oldFortigate, "old\n");
        File.WriteAllText(oldModSecurity, "old\n");
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([Fortigate("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv4.txt")], written.Files.ToArray());
        Assert.Equal([oldFortigate, oldModSecurity], written.OldFilesLeft!);
        Assert.Equal(1, written.ExceptionCuts);
        Assert.Equal(1, written.ExceptionRemovals);
        Assert.Equal(1, written.RangesWritten);
        foreach (string file in written.Files)
        {
            string text = File.ReadAllText(file);
            Assert.Contains("# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)" + Nl +
                            "# Exceptions: 1 range cut, 0 removed" + Nl + "# Confirmed against:", text);
            Assert.EndsWith(Nl + "34.1.128.0/17" + Nl, text);
        }

        Assert.Equal("old\n", File.ReadAllText(oldFortigate));
        Assert.Equal("old\n", File.ReadAllText(oldModSecurity));
    }

    [Fact]
    public async Task No_Feed_Type_Ticked_Is_Refused_First_And_Makes_No_Folder()
    {
        CloudFeedEngine engine = Engine();
        CloudFeedPlan plan = await PlanAll(engine, "34.1.0.0/16");
        // The settings also changed since the check and the exceptions are unreadable: no type ticked wins
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false, IPv4MinPrefix = 12 };
        _ipinfo.Exceptions = ExceptionList.Unreadable;

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.NoFeedType, written.Failure);
        Assert.Empty(written.Files);
        Assert.Equal(0, written.RangesWritten);
        Assert.Null(written.ModSecurityFiles);
        Assert.Null(written.OldFilesLeft);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task A_Range_Covering_Every_Address_Is_Left_Out_Of_Both_Copies_And_Counted_Once()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/17");
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        string oldFortigate = Fortigate("Azure_Test_ipv6.txt");
        string oldModSecurity = ModSecurity("Azure_Test_ipv6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFortigate)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldModSecurity)!);
        File.WriteAllText(oldFortigate, "old\n");
        File.WriteAllText(oldModSecurity, "old\n");
        CloudFeedEngine engine = Engine();
        CloudFeedPlan plan = await PlanAll(engine, "0.0.0.0/0", "34.1.0.0/16", "::/0");
        CidrBlock[] whole = plan.Included.Select(range => range.Prefix.Block).Where(block => block.PrefixLength == 0).ToArray();
        Assert.Equal(2, whole.Length);

        CloudFeedWriteResult written = await engine.WriteAsync(plan, whole, CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(2, written.WholeRangesLeftOut);
        Assert.Equal(0, written.LargeRangesLeftOut);
        Assert.Equal(1, written.ExceptionCuts);
        Assert.Equal(1, written.RangesWritten);
        Assert.Equal([Fortigate("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv4.txt")], written.Files.ToArray());
        Assert.Equal([oldFortigate, oldModSecurity], written.OldFilesLeft!);
        foreach (string file in written.Files)
        {
            string text = File.ReadAllText(file);
            Assert.Contains(
                "# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)" + Nl +
                "# Exceptions: 1 range cut, 0 removed" + Nl +
                "# Left out: 1 range covering every address: 0.0.0.0/0" + Nl +
                "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + Nl,
                text);
            Assert.Equal(["34.1.128.0/17"], text.Split(Nl, StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).ToArray());
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
        }
    }

    [Fact]
    public async Task A_Plan_Of_Only_Whole_Ranges_Writes_Nothing_And_Counts_Them()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        CloudFeedEngine engine = Engine();
        CloudFeedPlan plan = await PlanAll(engine, "0.0.0.0/0");

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [plan.Included[0].Prefix.Block], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.NothingToWrite, written.Failure);
        Assert.Equal(1, written.WholeRangesLeftOut);
        Assert.Empty(written.Files);
        Assert.Empty(written.OldFilesLeft!);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_ModSecurity_File_That_Fails_Is_Named_After_Every_FortiGate_File(bool ipv6Fails)
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        string ms4 = ModSecurity("Azure_Test_ipv4.txt");
        string ms6 = ModSecurity("Azure_Test_ipv6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(ms4)!);
        File.WriteAllText(ms4, "old v4\n");
        File.WriteAllText(ms6, "old v6\n");
        string blocked = ipv6Fails ? ms6 : ms4;
        Directory.CreateDirectory(blocked + ".tmp");
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(blocked, written.FailedFile);
        string[] fortigate = [Fortigate("Azure_Test_ipv4.txt"), Fortigate("Azure_Test_ipv6.txt")];
        Assert.Equal(ipv6Fails ? [.. fortigate, ms4] : fortigate, written.Files.ToArray());
        if (ipv6Fails)
            Assert.Equal([ms4], written.ModSecurityFiles!.ToArray());
        else
            Assert.Null(written.ModSecurityFiles);
        Assert.Equal(ipv6Fails ? [ms6] : [ms4, ms6], written.OldFilesLeft!);
        Assert.Equal(0, written.WholeRangesLeftOut);
        Assert.Equal("old v6\n", File.ReadAllText(ms6));
    }

    [Fact]
    public async Task A_File_Named_Modsecurity_Fails_The_First_ModSecurity_File_After_FortiGate_Is_Written()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        Directory.CreateDirectory(_temp.ExportFolder);
        string blocker = Path.Combine(_temp.ExportFolder, "modsecurity");
        File.WriteAllText(blocker, "");
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(ModSecurity("Azure_Test_ipv4.txt"), written.FailedFile);
        Assert.Equal([Fortigate("Azure_Test_ipv4.txt"), Fortigate("Azure_Test_ipv6.txt")], written.Files.ToArray());
        Assert.Null(written.ModSecurityFiles);
        Assert.Empty(written.OldFilesLeft!);
        Assert.True(File.Exists(blocker));
    }

    [Fact]
    public async Task A_File_Named_Fortigate_Fails_The_First_FortiGate_File_And_No_ModSecurity_File_Is_Tried()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        string ms4 = ModSecurity("Azure_Test_ipv4.txt");
        string ms6 = ModSecurity("Azure_Test_ipv6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(ms4)!);
        File.WriteAllText(ms4, "old v4\n");
        File.WriteAllText(ms6, "old v6\n");
        string blocker = Path.Combine(_temp.ExportFolder, "fortigate");
        File.WriteAllText(blocker, "");
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(Fortigate("Azure_Test_ipv4.txt"), written.FailedFile);
        Assert.Empty(written.Files);
        Assert.Null(written.ModSecurityFiles);
        Assert.Equal([ms4, ms6], written.OldFilesLeft!);
        Assert.Equal("old v4\n", File.ReadAllText(ms4));
        Assert.Equal("old v6\n", File.ReadAllText(ms6));
        Assert.True(File.Exists(blocker));
    }

    [Fact]
    public async Task A_Failed_Write_Names_The_Emptied_Old_Files_Then_The_Failed_File_Then_The_Untried_Ones()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("2001:db8::/32");
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        string[] names = ["Azure_Test_ipv4.txt", "Azure_Test_ipv6.txt"];
        foreach (string file in names.Select(Fortigate).Concat(names.Select(ModSecurity)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "old\n");
        }
        Directory.CreateDirectory(Fortigate("Azure_Test_ipv4.txt") + ".tmp");
        CloudFeedEngine engine = Engine();

        CloudFeedWriteResult written = await engine.WriteAsync(await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32"), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(Fortigate("Azure_Test_ipv4.txt"), written.FailedFile);
        Assert.Empty(written.Files);
        Assert.Equal(
            [Fortigate("Azure_Test_ipv6.txt"), ModSecurity("Azure_Test_ipv6.txt"), Fortigate("Azure_Test_ipv4.txt"), ModSecurity("Azure_Test_ipv4.txt")],
            written.OldFilesLeft!);
    }

    [Fact]
    public async Task A_Whole_IPv6_Range_Is_Named_Only_In_The_IPv6_File()
    {
        CloudFeedEngine engine = Engine();
        CloudFeedPlan plan = await PlanAll(engine, "34.1.0.0/16", "2001:db8::/32", "::/0");
        CidrBlock[] whole = plan.Included.Select(range => range.Prefix.Block).Where(block => block.PrefixLength == 0).ToArray();

        CloudFeedWriteResult written = await engine.WriteAsync(plan, whole, CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(1, written.WholeRangesLeftOut);
        Assert.Equal([Fortigate("Azure_Test_ipv4.txt"), Fortigate("Azure_Test_ipv6.txt")], written.Files.ToArray());
        string ipv6 = File.ReadAllText(Fortigate("Azure_Test_ipv6.txt"));
        Assert.Contains(
            "# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)" + Nl +
            "# Left out: 1 range covering every address: ::/0" + Nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + Nl,
            ipv6);
        Assert.Equal(["2001:db8::/32"], ipv6.Split(Nl, StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).ToArray());
        Assert.DoesNotContain("# Left out:", File.ReadAllText(Fortigate("Azure_Test_ipv4.txt")));
    }

    private CloudFeedEngine Engine()
    {
        _ipinfo.OverlapFunc ??= blocks => Found(blocks, _ => [Group("microsoft.com")]);
        return new CloudFeedEngine(_ipinfo, _time, _temp.ExportFolder);
    }

    private async Task<CloudFeedPlan> PlanAll(CloudFeedEngine engine, params string[] cidrs)
    {
        _ipinfo.OverlapFunc ??= blocks => Found(blocks, _ => [Group("microsoft.com")]);
        FeedPrefix[] prefixes = cidrs.Select(Prefix).ToArray();
        CheckResult result = await engine.CheckAsync(new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"), prefixes,
                                                     CancellationToken.None);
        Assert.Equal(CheckFailure.None, result.Failure);
        return engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
    }

    private static FeedPrefix Prefix(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block), cidr);
        return new FeedPrefix(block, "a");
    }

    private static OverlapResult Found(IReadOnlyList<CidrBlock> blocks, Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        new(OverlapOutcome.Found, blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(), Dataset);

    private static OverlapGroup Group(string? domain) => new("US", "United States", "AS8075", "Microsoft", domain, 1);
}
