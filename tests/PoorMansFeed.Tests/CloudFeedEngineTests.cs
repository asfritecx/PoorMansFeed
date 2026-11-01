using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class CloudFeedEngineTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    /// <summary>The pick every test check carries; the other cloud feed tests share it.</summary>
    internal static readonly CloudFeedPick TestPick = new(FeedKind.Azure, null, false, "Test", null, null);

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

        CheckResult result = await engine.CheckAsync(Description(), ordered, TestPick, CancellationToken.None);

        Assert.Equal(CheckFailure.None, result.Failure);
        Assert.Equal(["35.1.0.0/16", "34.1.0.0/16", "2001:db8::/32"], result.Check!.Ranges.Select(range => range.Prefix.Block.Text).ToArray());
        Assert.Equal(["b", "a", "a"], result.Check.Ranges.Select(range => range.Prefix.Scope).ToArray());
        Assert.Equal(["35.1.0.0/16", "34.1.0.0/16", "2001:db8::/32"], _ipinfo.LastOverlapBlocks!.Select(block => block.Text).ToArray());
        Assert.Equal(1, _ipinfo.OverlapCalls);
    }

    [Fact]
    public async Task Check_Drops_IPv6_Before_The_Query_When_IPv6_Is_Off()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false, WriteFortigateFeeds = true };
        CloudFeedEngine engine = Engine();

        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);

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
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a")], TestPick, CancellationToken.None);

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

        CheckResult empty = await engine.CheckAsync(Description(), [], TestPick, CancellationToken.None);
        CheckResult some = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);

        Assert.Equal(CheckFailure.NoDataset, empty.Failure);
        Assert.Equal(CheckFailure.NoDataset, some.Failure);
        Assert.Null(empty.Check);
    }

    [Fact]
    public async Task Check_Reports_NothingSelected_For_An_Empty_List_And_Failed_For_A_Bad_Answer()
    {
        CloudFeedEngine engine = Engine();
        Assert.Equal(CheckFailure.NothingSelected, (await engine.CheckAsync(Description(), [], TestPick, CancellationToken.None)).Failure);

        _ipinfo.OverlapFunc = _ => new OverlapResult(OverlapOutcome.Found, [], Dataset);
        Assert.Equal(
            CheckFailure.Failed,
            (await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None)).Failure);

        _ipinfo.OverlapFunc = _ => new OverlapResult(OverlapOutcome.Failed, [], null);
        Assert.Equal(
            CheckFailure.Failed,
            (await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Write_Puts_The_Header_Lines_After_The_Title_And_Names_The_File_From_The_Stem()
    {
        CloudFeedEngine engine = Engine();
        FeedDescription description = new("microsoft.com", ["Ranges: Test source", "Service tag: Storage"], "Azure_Storage");
        CheckResult result = await engine.CheckAsync(
            description, [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Assert.Equal([Path.Combine(folder, "Azure_Storage_ipv4.txt"), Path.Combine(folder, "Azure_Storage_ipv6.txt")], written.Files.ToArray());
        string nl = "\n";
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
    public async Task Write_Replaces_A_Link_At_The_Temporary_Path_And_Never_Writes_Through_It()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string ipv4 = Path.Combine(folder, "Azure_Test_ipv4.txt");
        string ipv6 = Path.Combine(folder, "Azure_Test_ipv6.txt");
        Directory.CreateDirectory(folder);
        // A file outside the export folder, and a link to nothing
        string victim = Path.Combine(_temp.Folder, "victim.txt");
        File.WriteAllText(victim, "not the feed's");
        byte[] victimBefore = File.ReadAllBytes(victim);
        string missing = Path.Combine(_temp.Folder, "missing.txt");
        try
        {
            File.CreateSymbolicLink(ipv4 + ".tmp", victim);
            File.CreateSymbolicLink(ipv6 + ".tmp", missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link, so a link at the temporary path can't be planted.");
            return;
        }

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([ipv4, ipv6], written.Files.ToArray());
        Assert.Equal(victimBefore, File.ReadAllBytes(victim));
        Assert.False(File.Exists(missing));
        string nl = "\n";
        Assert.Null(new FileInfo(ipv4).LinkTarget);
        Assert.Null(new FileInfo(ipv6).LinkTarget);
        Assert.StartsWith("# PoorMansFeed - Fortigate Threat Feed" + nl, File.ReadAllText(ipv4));
        Assert.EndsWith("# IP address data powered by IPinfo (https://ipinfo.io)" + nl + "34.1.0.0/16" + nl, File.ReadAllText(ipv4));
        Assert.EndsWith("# IP address data powered by IPinfo (https://ipinfo.io)" + nl + "2001:db8::/32" + nl, File.ReadAllText(ipv6));
        Assert.Empty(Directory.GetFileSystemEntries(folder, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_Names_The_File_That_Failed_And_The_Files_Written_Before_It(bool ipv6Fails)
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string ipv4 = Path.Combine(folder, "Azure_Test_ipv4.txt");
        string ipv6 = Path.Combine(folder, "Azure_Test_ipv6.txt");
        // A folder where the file should go makes that one write fail
        string blocked = ipv6Fails ? ipv6 : ipv4;
        Directory.CreateDirectory(blocked);

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(blocked, written.FailedFile);
        Assert.Equal(ipv6Fails ? [ipv4] : Array.Empty<string>(), written.Files.ToArray());
        // No earlier write left a file, so no old file is named
        Assert.Empty(written.OldFilesLeft!);
        Assert.All(written.Files, file => Assert.True(File.Exists(file)));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_Names_The_Old_Files_A_Failed_Write_Left(bool ipv6Fails)
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult first = await engine.WriteAsync(plan, [], CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.None, first.Failure);
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string ipv4 = Path.Combine(folder, "Azure_Test_ipv4.txt");
        string ipv6 = Path.Combine(folder, "Azure_Test_ipv6.txt");
        Assert.Equal([ipv4, ipv6], first.Files);
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // A folder where the temporary file should go makes that one write fail; the move never runs, so the earlier
        // copy is whole, and a family after it is never tried
        string blocked = ipv6Fails ? ipv6 : ipv4;
        Directory.CreateDirectory(blocked + ".tmp");

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(blocked, written.FailedFile);
        Assert.Equal(ipv6Fails ? [ipv4] : Array.Empty<string>(), written.Files.ToArray());
        Assert.Equal(ipv6Fails ? [ipv6] : [ipv4, ipv6], written.OldFilesLeft!);
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));
    }

    [Fact]
    public async Task Write_Fails_The_First_File_When_The_Feed_Folder_Cannot_Be_Made()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        // The folder is made inside the first file's write, so a file in its place fails that file
        Directory.CreateDirectory(_temp.ExportFolder);
        string fortigate = Path.Combine(_temp.ExportFolder, "fortigate");
        File.WriteAllText(fortigate, "");

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Empty(written.Files);
        Assert.Equal(Path.Combine(fortigate, "Azure_Test_ipv4.txt"), written.FailedFile);
        Assert.Empty(written.OldFilesLeft!);
        Assert.True(File.Exists(fortigate));
    }

    [Fact]
    public async Task Write_Leaves_Out_A_Large_Range_Unless_It_Is_Allowed()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("10.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
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
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        _ipinfo.Settings = _ipinfo.Settings with { IPv4MinPrefix = 12 };

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.SettingsChanged, written.Failure);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task Write_Cuts_Exceptions_Out_In_Place_And_Each_Piece_Keeps_Its_Ranges_Status()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/24" ? [Group("example.com")] : [Group("microsoft.com")]);
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/17", "35.1.0.0/25", "2001:db8::/32", "203.0.113.0/24");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("35.1.0.0/24", "a"), Prefix("34.1.0.0/16", "a"), Prefix("36.1.0.0/24", "a"), Prefix("2001:db8::/32", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(3, written.RangesWritten);
        Assert.Equal(1, written.UnconfirmedWritten);
        Assert.Equal(2, written.ExceptionCuts);
        Assert.Equal(1, written.ExceptionRemovals);
        // Every IPv6 range was removed, so no IPv6 file is written
        string file = Assert.Single(written.Files);
        string nl = "\n";
        string expected =
            "# PoorMansFeed - Fortigate Threat Feed" + nl +
            "# Ranges: Test source" + nl +
            "# Address Family: IPv4" + nl +
            "# Generated: 2026-03-19 04:05:06 UTC" + nl +
            "# Entries: 3 (2 confirmed by IPinfo, 1 added without confirmation)" + nl +
            "# Exceptions: 2 ranges cut, 0 removed" + nl +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08" + nl +
            "# IP address data powered by IPinfo (https://ipinfo.io)" + nl +
            "35.1.0.128/25" + nl +
            "34.1.128.0/17" + nl +
            "36.1.0.0/24" + nl;
        Assert.Equal(expected, File.ReadAllText(file));
        Assert.Null(written.OldFilesLeft);
        Assert.Null(written.EmptiedFiles);
    }

    [Fact]
    public async Task Write_Without_Any_Cut_Writes_No_Exceptions_Line()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("203.0.113.0/24", "2001:db8:1::/48");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/48", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(0, written.ExceptionCuts);
        Assert.Equal(0, written.ExceptionRemovals);
        Assert.All(written.Files, path => Assert.DoesNotContain("# Exceptions:", File.ReadAllText(path)));
    }

    [Fact]
    public async Task Write_Counts_Exceptions_For_Each_Familys_Own_File()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/24", "35.1.0.0/16", "2001:db8::/33");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        string nl = "\n";
        Assert.Contains("# Exceptions: 1 range cut, 1 removed" + nl, File.ReadAllText(written.Files[0]));
        Assert.Contains("# Exceptions: 1 range cut, 0 removed" + nl + "# Confirmed against:", File.ReadAllText(written.Files[1]));
        Assert.EndsWith("2001:db8:8000::/33" + nl, File.ReadAllText(written.Files[1]));
    }

    [Fact]
    public async Task Write_Writes_A_Single_Host_Bare_And_Keeps_A_Networks_Prefix()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("34.1.2.3/32", "a"), Prefix("2001:db8::1/128", "a"), Prefix("2001:db8::/32", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(3, written.RangesWritten);
        Assert.Equal(0, written.UnconfirmedWritten);
        Assert.Equal(2, written.Files.Count);
        string[] v4 = File.ReadAllLines(written.Files[0]);
        Assert.Contains("# Entries: 1 (1 confirmed by IPinfo, 0 added without confirmation)", v4);
        Assert.Equal(["34.1.2.3"], v4.Where(line => !line.StartsWith('#')));
        string[] v6 = File.ReadAllLines(written.Files[1]);
        Assert.Contains("# Entries: 2 (2 confirmed by IPinfo, 0 added without confirmation)", v6);
        Assert.Equal(["2001:db8::1", "2001:db8::/32"], v6.Where(line => !line.StartsWith('#')));
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Write_Writes_A_Single_Host_An_Exception_Cut_Leaves_Bare()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.2.0/31", "34.1.2.3/32", "2001:db8::1/128");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.2.0/30", "a"), Prefix("2001:db8::/127", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(2, written.ExceptionCuts);
        Assert.Equal(2, written.Files.Count);
        string[] v4 = File.ReadAllLines(written.Files[0]);
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", v4);
        Assert.Equal(["34.1.2.2"], v4.Where(line => !line.StartsWith('#')));
        string[] v6 = File.ReadAllLines(written.Files[1]);
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", v6);
        Assert.Equal(["2001:db8::"], v6.Where(line => !line.StartsWith('#')));
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Write_Reads_The_Settings_Once()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        int before = _ipinfo.SettingsReads;

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(before + 1, _ipinfo.SettingsReads);
    }

    [Fact]
    public async Task Write_Takes_Its_Types_From_The_Options_Not_The_Settings()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        var settings = new UserSettings { WriteFortigateFeeds = true };
        var modSecurityOnly = new FeedOptions([FeedType.ModSecurity], true, true, true, settings.IPv6Enabled);
        var noType = new FeedOptions([], true, true, true, settings.IPv6Enabled);

        CloudFeedWriteResult none = engine.Write(plan, [], settings, noType, CancellationToken.None);
        CloudFeedWriteResult written = engine.Write(plan, [], settings, modSecurityOnly, CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.NoFeedType, none.Failure);
        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([Path.Combine(_temp.ExportFolder, "modsecurity", "Azure_Test_ipv4.txt")], written.Files);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task Write_Refuses_When_The_Exceptions_Are_Unreadable()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.ExceptionsUnreadable, written.Failure);
        Assert.Empty(written.Files);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task Write_Writes_Nothing_When_Exceptions_Cover_Every_Ticked_Range()
    {
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("34.2.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.NothingToWrite, written.Failure);
        Assert.Equal(0, written.LargeRangesLeftOut);
        Assert.Equal(2, written.ExceptionRemovals);
        Assert.Null(written.OldFilesLeft);
        Assert.Null(written.EmptiedFiles);
        Assert.Empty(written.Files);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task Write_Empties_The_Old_File_Of_A_Family_Exceptions_Emptied()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult first = await engine.WriteAsync(plan, [], CancellationToken.None);
        string ipv4File = first.Files[0];
        string ipv6File = first.Files[1];
        byte[] ipv4Before = File.ReadAllBytes(ipv4File);

        _ipinfo.Exceptions = RangeSubtractionTests.List("2001:db8::/32");
        CloudFeedWriteResult second = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.Equal([ipv4File, ipv6File], second.Files);
        Assert.Equal([ipv6File], second.EmptiedFiles!);
        Assert.Null(second.OldFilesLeft);
        Assert.Equal(1, second.RangesWritten);
        Assert.Equal(ipv4Before, File.ReadAllBytes(ipv4File));
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# Ranges: Test source\n" +
            "# Address Family: IPv6\n" +
            "# Generated: 2026-03-19 04:05:06 UTC\n" +
            "# Entries: 0 (0 confirmed by IPinfo, 0 added without confirmation)\n" +
            "# Exceptions: 0 ranges cut, 1 removed\n" +
            "# Confirmed against: IPinfo Lite dataset of 2026-10-08\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n",
            File.ReadAllText(ipv6File));
    }

    [Fact]
    public async Task Write_Empties_The_Old_File_Of_A_Family_Whose_Only_Ticked_Range_Was_Left_Out_As_Large()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("10.0.0.0/8", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult first = await engine.WriteAsync(plan, [Block("10.0.0.0/8")], CancellationToken.None);
        string ipv4File = first.Files[0];
        string ipv6File = first.Files[1];
        byte[] ipv6Before = File.ReadAllBytes(ipv6File);

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([ipv4File, ipv6File], written.Files);
        Assert.Equal([ipv4File], written.EmptiedFiles!);
        Assert.Equal(["10.0.0.0/8"], written.LeftOutLargeRanges!);
        Assert.Equal(1, written.RangesWritten);
        Assert.Equal(ipv6Before, File.ReadAllBytes(ipv6File));
        Assert.Contains("\n# Entries: 0 (0 confirmed by IPinfo, 0 added without confirmation)\n", File.ReadAllText(ipv4File));
        Assert.DoesNotContain("10.0.0.0/8", File.ReadAllText(ipv4File));

        // Every ticked range left out as large, and the IPv6 range unticked, empties both old files
        CloudFeedPlan onlyLarge = engine.PlanWrite(result.Check!, [Block("10.0.0.0/8")]);
        CloudFeedWriteResult emptied = await engine.WriteAsync(onlyLarge, [], CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.None, emptied.Failure);
        Assert.Equal([ipv4File, ipv6File], emptied.EmptiedFiles!);
    }

    [Fact]
    public async Task Write_Of_Only_Emptied_Families_Rewrites_Each_Old_File_And_Records_The_Change()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult first = await engine.WriteAsync(plan, [], CancellationToken.None);
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;

        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/16", "2001:db8::/32");
        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(0, written.RangesWritten);
        Assert.Equal(2, written.ExceptionRemovals);
        Assert.Equal(first.Files, written.Files);
        Assert.Equal(first.Files, written.EmptiedFiles!);
        Assert.Null(written.OldFilesLeft);
        Assert.All(written.Files, file => Assert.Contains("# Entries: 0 (0 confirmed by IPinfo", File.ReadAllText(file)));
        Assert.All(written.Files, file => Assert.All(File.ReadAllLines(file), line => Assert.StartsWith("#", line)));
        Assert.NotNull(written.Record);
        FeedRecordRequest request = Assert.Single(records.Requests);
        Assert.Equal(
            [new FeedRemoval("34.1.0.0/16", FeedRemovalReason.Exception), new FeedRemoval("2001:db8::/32", FeedRemovalReason.Exception)],
            request.Change!.Families.SelectMany(family => family.Removed).ToArray());
    }

    [Fact]
    public async Task Write_Empties_A_Family_Before_A_Failed_File_And_Names_The_Failed_Files_Old_Copy()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        CloudFeedWriteResult first = await engine.WriteAsync(plan, [], CancellationToken.None);
        string ipv4 = first.Files[0];
        string ipv6 = first.Files[1];
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // The exceptions empty IPv4, and a folder where the IPv6 temporary file should go makes its write fail
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/16");
        Directory.CreateDirectory(ipv6 + ".tmp");

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(ipv6, written.FailedFile);
        Assert.Equal([ipv4], written.Files);
        // Only a write that succeeded names emptied files; a failure's written files are in Files
        Assert.Null(written.EmptiedFiles);
        Assert.Equal([ipv6], written.OldFilesLeft!);
        Assert.Contains("# Entries: 0 (0 confirmed by IPinfo", File.ReadAllText(ipv4));
        Assert.DoesNotContain("34.1.0.0/16", File.ReadAllText(ipv4));
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));
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
            TestPick, CancellationToken.None);

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

    [Fact]
    public async Task Check_Carries_The_Pick_It_Was_Given_And_Refuses_None()
    {
        CloudFeedEngine engine = Engine();

        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);

        Assert.Same(TestPick, result.Check!.Pick);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], null!, CancellationToken.None));
    }

    [Fact]
    public async Task Plan_Keeps_Each_Tick_That_Differs_From_The_Default_Once_In_Check_Order()
    {
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text switch
        {
            "35.1.0.0/16" => [Group("example.com")],
            "37.1.0.0/16" => [],
            _ => [Group("microsoft.com")],
        });
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("37.1.0.0/16", "a"), Prefix("34.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"),
             Prefix("37.1.0.0/16", "b"), Prefix("36.1.0.0/16", "b")],
            TestPick, CancellationToken.None);
        CloudFeedCheck check = result.Check!;

        CloudFeedPlan plan = engine.PlanWrite(check, [Block("35.1.0.0/16"), Block("34.1.0.0/16"), Block("37.1.0.0/16")]);
        CloudFeedPlan byDefault = engine.PlanWrite(check, [Block("36.1.0.0/16"), Block("34.1.0.0/16")]);

        Assert.Equal(["37.1.0.0/16", "35.1.0.0/16"], plan.HandTicked);
        Assert.Equal(["36.1.0.0/16"], plan.HandUnticked);
        Assert.Empty(byDefault.HandTicked);
        Assert.Empty(byDefault.HandUnticked);
    }

    [Fact]
    public async Task Write_Records_Its_Change_And_Saves_The_Pick_With_Its_Hand_Ticks_And_Large_Answers()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/16" ? [Group("example.com")] : [Group("microsoft.com")]);
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("10.0.0.0/8", "a"), Prefix("11.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"),
             Prefix("36.1.0.0/16", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(
            result.Check!, [Block("10.0.0.0/8"), Block("11.0.0.0/8"), Block("34.1.0.0/16"), Block("35.1.0.0/16")]);

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [Block("10.0.0.0/8")], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(2, written.Files.Count);
        FeedRecordRequest request = Assert.Single(records.Requests);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), written.Record);
        Assert.Equal("Azure_Test", request.FeedKey);
        Assert.Equal(FeedKind.Azure, request.Kind);
        Assert.Equal(Generated, request.AtUtc);
        Assert.Contains("# Generated: 2026-03-19 04:05:06 UTC\n", File.ReadAllText(written.Files[0]));
        FeedChangeRecord change = request.Change!;
        Assert.Equal(FeedChangeCause.PageWrite, change.Cause);
        Assert.Equal(Dataset.DownloadedOn, change.DatasetDate);
        Assert.Null(change.ListDownloadedUtc);
        FeedFamilyChange family = Assert.Single(change.Families);
        // Both planned copies were written alike, so the change names no one copy
        Assert.Null(family.Copy);
        Assert.Equal(["10.0.0.0/8", "34.1.0.0/16", "35.1.0.0/16"], family.Added.Order(StringComparer.Ordinal));
        SavedFeedIntent.Written intent = Assert.IsType<SavedFeedIntent.Written>(request.Intent);
        Assert.Equal(
            new SavedFeedPick(null, null, false, "Test", null, null, [], [], [], [], null, null, null) with
            {
                HandTicked = intent.Pick.HandTicked,
                HandUnticked = intent.Pick.HandUnticked,
                LargeIncluded = intent.Pick.LargeIncluded,
                LargeLeftOut = intent.Pick.LargeLeftOut,
                SeenUnconfirmed = intent.Pick.SeenUnconfirmed,
            },
            intent.Pick);
        Assert.Equal(["35.1.0.0/16"], intent.Pick.SeenUnconfirmed!);
        SavedFeedWriteProofTests.AssertProofOfDisk(_temp.ExportFolder, written.FeedChanges!, intent.Proof,
                                                   FeedOptions.ForCloudFeed(_ipinfo.Settings));
        Assert.Equal(["35.1.0.0/16"], intent.Pick.HandTicked);
        Assert.Equal(["36.1.0.0/16"], intent.Pick.HandUnticked);
        Assert.Equal(["10.0.0.0/8"], intent.Pick.LargeIncluded);
        Assert.Equal(["11.0.0.0/8"], intent.Pick.LargeLeftOut);
    }

    [Fact]
    public async Task A_Second_Write_Tells_Why_Each_Entry_Went()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        CheckResult first = await engine.CheckAsync(
            Description(),
            [Prefix("10.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("36.1.0.0/16", "a"),
             Prefix("37.1.0.0/16", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan firstPlan = engine.PlanWrite(first.Check!, first.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        Assert.Equal(CloudFeedWriteFailure.None, (await engine.WriteAsync(firstPlan, [Block("10.0.0.0/8")], CancellationToken.None)).Failure);

        _ipinfo.Exceptions = RangeSubtractionTests.List("37.1.0.0/16");
        CheckResult second = await engine.CheckAsync(
            Description(),
            [Prefix("10.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("37.1.0.0/16", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan secondPlan = engine.PlanWrite(
            second.Check!, [Block("10.0.0.0/8"), Block("34.1.0.0/16"), Block("37.1.0.0/16")]);
        CloudFeedWriteResult written = await engine.WriteAsync(secondPlan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(2, records.Requests.Count);
        FeedFamilyChange family = Assert.Single(records.Requests[1].Change!.Families);
        Assert.Equal(
            [
                new FeedRemoval("10.0.0.0/8", FeedRemovalReason.LeftOut),
                new FeedRemoval("35.1.0.0/16", FeedRemovalReason.LeftOut),
                new FeedRemoval("36.1.0.0/16", FeedRemovalReason.SourceDropped),
                new FeedRemoval("37.1.0.0/16", FeedRemovalReason.Exception),
            ],
            family.Removed.OrderBy(removal => removal.Text, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_Range_Covering_Every_Address_That_An_Old_File_Held_Went_As_Left_Out()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        Directory.CreateDirectory(folder);
        // No write carries one, so the old files are written by hand
        File.WriteAllText(Path.Combine(folder, "Azure_Test_ipv4.txt"), "0.0.0.0/0\n34.1.0.0/16\n");
        File.WriteAllText(Path.Combine(folder, "Azure_Test_ipv6.txt"), "::/0\n2001:db8::/32\n");
        CheckResult result = await engine.CheckAsync(
            Description(),
            [Prefix("0.0.0.0/0", "a"), Prefix("34.1.0.0/16", "a"), Prefix("::/0", "a"), Prefix("2001:db8::/32", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [Block("0.0.0.0/0"), Block("::/0")], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(2, written.WholeRangesLeftOut);
        IReadOnlyList<FeedFamilyChange> families = Assert.Single(records.Requests).Change!.Families;
        Assert.Equal(
            [new FeedRemoval("0.0.0.0/0", FeedRemovalReason.LeftOut), new FeedRemoval("::/0", FeedRemovalReason.LeftOut)],
            families.SelectMany(family => family.Removed).ToArray());
    }

    [Fact]
    public async Task A_Write_That_Fails_After_A_File_Moved_Records_And_Saves_And_One_Before_Does_Neither()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        string folder = Path.Combine(_temp.ExportFolder, "fortigate");
        string blocked = Directory.CreateDirectory(Path.Combine(folder, "Azure_Test_ipv4.txt")).FullName;

        CloudFeedWriteResult before = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, before.Failure);
        Assert.Null(before.Record);
        Assert.Empty(records.Requests);

        Directory.Delete(blocked);
        Directory.CreateDirectory(Path.Combine(folder, "Azure_Test_ipv6.txt"));
        CloudFeedWriteResult after = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, after.Failure);
        Assert.NotNull(after.Record);
        FeedRecordRequest request = Assert.Single(records.Requests);
        Assert.Null(Assert.IsType<SavedFeedIntent.Written>(request.Intent).Proof);
        Assert.Equal(["34.1.0.0/16"], Assert.Single(request.Change!.Families).Added);
    }

    [Fact]
    public async Task A_Refused_Or_Empty_Write_Gives_No_Record()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        var outcomes = new List<CloudFeedWriteResult>();

        var noType = new FeedOptions([], true, true, true, true);
        outcomes.Add(engine.Write(plan, [], _ipinfo.Settings, noType, CancellationToken.None));
        outcomes.Add(engine.Write(plan, [], _ipinfo.Settings with { IPv4MinPrefix = 12 },
                                  FeedOptions.ForCloudFeed(_ipinfo.Settings), CancellationToken.None));
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        outcomes.Add(await engine.WriteAsync(plan, [], CancellationToken.None));
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.0.0.0/8");
        outcomes.Add(await engine.WriteAsync(plan, [], CancellationToken.None));
        _ipinfo.Exceptions = ExceptionList.Empty;
        string file = Path.Combine(_temp.Folder, "not-a-folder");
        File.WriteAllText(file, "");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = Path.Combine(file, "feeds") };
        outcomes.Add(await engine.WriteAsync(plan, [], CancellationToken.None));

        Assert.Equal(
            [CloudFeedWriteFailure.NoFeedType, CloudFeedWriteFailure.SettingsChanged, CloudFeedWriteFailure.ExceptionsUnreadable,
             CloudFeedWriteFailure.NothingToWrite, CloudFeedWriteFailure.FolderUnavailable],
            outcomes.Select(outcome => outcome.Failure).ToArray());
        Assert.All(outcomes, outcome => Assert.Null(outcome.Record));
        Assert.Empty(records.Requests);
    }

    [Fact]
    public async Task WriteAsync_Waits_For_Another_Write_Of_The_Feed_And_Runs_On_The_Settings_Current_When_It_Gets_In()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        Task<IDisposable> entered;
        // The other write's own flow: one started inside the test's flow would carry its hold into the write
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => records.EnterWrite("azure_test"));
        IDisposable other = await entered;

        Task<CloudFeedWriteResult> waiting = engine.WriteAsync(plan, [], CancellationToken.None);
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(2, records.WritesInFlight);
        Assert.False(waiting.IsCompleted);
        Assert.False(Directory.Exists(_temp.ExportFolder));
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = true };
        other.Dispose();
        CloudFeedWriteResult written = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([Path.Combine(_temp.ExportFolder, "modsecurity", "Azure_Test_ipv4.txt")], written.Files);
        Assert.Single(records.Requests);
    }

    [Fact]
    public async Task WriteAsync_After_The_Writes_Stopped_Writes_Nothing_And_Records_Nothing()
    {
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(Description(), [Prefix("34.1.0.0/16", "a")], TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        await records.StopWritesAsync(CancellationToken.None);

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Empty(written.Files);
        Assert.Null(written.Record);
        Assert.Empty(records.Requests);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    private sealed class SavedFeedListStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public SavedFeedsRead Read() => new(true, _list.Feeds);

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            (SavedFeedsFailure failure, _list) = _list.Apply(key, kind, intent, atUtc);
            return failure;
        }

        public IReadOnlyList<SavedFeedStampOutcome> ApplyStamps(IReadOnlyList<SavedFeedStamp> stamps)
        {
            (IReadOnlyList<SavedFeedStampOutcome> outcomes, _list) = _list.ApplyStamps(stamps, SavedFeedList.MaxJsonBytes);
            return outcomes;
        }

        public SavedFeedsFailure Remove(string key)
        {
            (SavedFeedsFailure failure, _list) = _list.Remove(key);
            return failure;
        }
    }

    private async Task<CloudFeedWriteResult> CheckAndWriteAsync(CloudFeedEngine engine, string[] prefixes, string[] ticked)
    {
        CheckResult result = await engine.CheckAsync(Description(), prefixes.Select(prefix => Prefix(prefix, "a")).ToList(),
                                                     TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, ticked.Select(Block).ToList());
        return await engine.WriteAsync(plan, [], CancellationToken.None);
    }

    [Fact]
    public async Task A_Write_With_IPv6_Off_Keeps_The_Saved_IPv6_Untick()
    {
        _ipinfo.Records = new FeedRecords(_temp.Paths, _time, new SavedFeedListStore());
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, IPv6Enabled = true };
        CloudFeedEngine engine = Engine();
        CloudFeedWriteResult first = await CheckAndWriteAsync(engine, ["34.1.0.0/16", "2001:db8::/32"], ["34.1.0.0/16"]);
        Assert.Equal(FeedSaveOutcome.Saved, first.Record?.Save);

        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        _time.Advance(TimeSpan.FromMinutes(1));
        CloudFeedWriteResult second = await CheckAndWriteAsync(engine, ["34.1.0.0/16", "2001:db8::/32"], ["34.1.0.0/16"]);

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.NothingToRecord), second.Record);
        SavedFeed saved = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(_time.GetUtcNow(), saved.LastWriteUtc);
        Assert.Equal(["2001:db8::/32"], saved.Pick.HandUnticked);
        Assert.Empty(saved.Pick.HandTicked);
    }

    [Fact]
    public async Task A_Saved_Untick_Is_Kept_When_The_Range_Is_Now_Mixed_And_Still_Unticked()
    {
        _ipinfo.Records = new FeedRecords(_temp.Paths, _time, new SavedFeedListStore());
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true };
        CloudFeedEngine engine = Engine();
        await CheckAndWriteAsync(engine, ["34.1.0.0/16", "35.1.0.0/16"], ["34.1.0.0/16"]);
        Assert.Equal(["35.1.0.0/16"], Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds).Pick.HandUnticked);

        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/16" ? [Group("example.com")] : [Group("microsoft.com")]);
        _time.Advance(TimeSpan.FromMinutes(1));
        CloudFeedWriteResult second = await CheckAndWriteAsync(engine, ["34.1.0.0/16", "35.1.0.0/16"], ["34.1.0.0/16"]);

        Assert.Equal(FeedSaveOutcome.Saved, second.Record?.Save);
        SavedFeedPick pick = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds).Pick;
        Assert.Equal(["35.1.0.0/16"], pick.HandUnticked);
        Assert.Empty(pick.HandTicked);
    }

    [Fact]
    public async Task A_Write_Saves_Every_Unconfirmed_Range_It_Saw_And_Carries_Those_Of_A_Family_Switched_Off()
    {
        _ipinfo.Records = new FeedRecords(_temp.Paths, _time, new SavedFeedListStore());
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true, IPv6Enabled = true };
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text switch
        {
            "35.1.0.0/16" => [Group("example.com")],
            "36.1.0.0/16" or "2001:db8::/32" => [],
            _ => [Group("microsoft.com")],
        });
        CloudFeedEngine engine = Engine();
        string[] prefixes = ["36.1.0.0/16", "34.1.0.0/16", "35.1.0.0/16", "2001:db8::/32"];

        CloudFeedWriteResult first = await CheckAndWriteAsync(engine, prefixes, ["34.1.0.0/16", "35.1.0.0/16"]);

        Assert.Equal(FeedSaveOutcome.Saved, first.Record?.Save);
        SavedFeed saved = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(["36.1.0.0/16", "35.1.0.0/16", "2001:db8::/32"], saved.Pick.SeenUnconfirmed!);
        SavedFeedWriteProofTests.AssertProofOfDisk(_temp.ExportFolder, first.FeedChanges!, saved.Proof,
                                                   FeedOptions.ForCloudFeed(_ipinfo.Settings));
        Assert.Equal(["fortigate/Azure_Test_ipv4.txt", "modsecurity/Azure_Test_ipv4.txt"],
                     saved.Proof!.FileSha256.Keys.Order(StringComparer.Ordinal));

        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        _time.Advance(TimeSpan.FromMinutes(1));
        CloudFeedWriteResult second = await CheckAndWriteAsync(engine, prefixes, ["34.1.0.0/16"]);

        Assert.Equal(FeedSaveOutcome.Saved, second.Record?.Save);
        SavedFeed again = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(["36.1.0.0/16", "35.1.0.0/16", "2001:db8::/32"], again.Pick.SeenUnconfirmed!);
        Assert.NotEqual(saved.Proof.SettingsFingerprint, again.Proof!.SettingsFingerprint);
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

    private static CidrBlock Block(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return block;
    }

    private static OverlapResult Found(IReadOnlyList<CidrBlock> blocks, Func<CidrBlock, IReadOnlyList<OverlapGroup>> groups) =>
        new(OverlapOutcome.Found, blocks.Select(block => new NetworkOverlap(block, groups(block))).ToList(), Dataset);

    private static OverlapGroup Group(string? domain) => new("US", "United States", "AS8075", "Microsoft", domain, 1);

    [Fact]
    public async Task The_Cloud_Feed_Bytes_Are_Pinned_With_The_Render_Version()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.Text == "35.1.0.0/16" ? [Group("example.com")] : [Group("microsoft.com")]);
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("35.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick,
            CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        FeedGoldenHashes.AssertPinned("cloud", _temp.ExportFolder, new Dictionary<string, string>
        {
            ["fortigate/Azure_Test_ipv4.txt"] = "3aa754e8ac5b37269a4e86075415dfb57a4afa8d148ceb52bbc7edf13c3e97c4",
            ["fortigate/Azure_Test_ipv6.txt"] = "1d42095b832de1b2421ee0cff3028aa1406e87bb2ac70f3bc0360a423527cc88",
            ["modsecurity/Azure_Test_ipv4.txt"] = "02468a813ae00802e620836ac5741ac21f446c2464bad346b927aa38d436b31c",
            ["modsecurity/Azure_Test_ipv6.txt"] = "a9bf527db26bf7549b3b9e2185d71b498ce5e58b07280380ff9551deaaa4111c",
        });
    }

    [Fact]
    public async Task The_Cloud_Header_Only_Exceptions_And_Left_Out_Bytes_Are_Pinned_With_The_Render_Version()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/24", "2001:db8::/32");
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("0.0.0.0/0", "a"), Prefix("2001:db8::/32", "a")], TestPick,
            CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(result.Check!, result.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        foreach (FeedType type in FeedTypes.All)
        {
            string old = CloudFeedWriter.PathFor(_temp.ExportFolder, type, result.Check!, ipv6: true);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, "# old\n2001:db8::/32\n");
        }

        CloudFeedWriteResult written = await engine.WriteAsync(plan, [Block("0.0.0.0/0")], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        string[] ipv4 = File.ReadAllLines(CloudFeedWriter.PathFor(_temp.ExportFolder, FeedType.Fortigate, result.Check!, ipv6: false));
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", ipv4);
        Assert.Contains("# Left out: 1 range covering every address: 0.0.0.0/0", ipv4);
        string[] ipv6 = File.ReadAllLines(CloudFeedWriter.PathFor(_temp.ExportFolder, FeedType.Fortigate, result.Check!, ipv6: true));
        Assert.Contains("# Exceptions: 0 ranges cut, 1 removed", ipv6);
        Assert.DoesNotContain("2001:db8::/32", ipv6);
        FeedGoldenHashes.AssertPinned("cloud, header-only", _temp.ExportFolder, new Dictionary<string, string>
        {
            ["fortigate/Azure_Test_ipv4.txt"] = "45b28a8db43fe7be23bf2a2b63cb8c59be33ffaafc4cecf80995d796d493d97b",
            ["fortigate/Azure_Test_ipv6.txt"] = "2086621ee9df67071e62179a09a2aeefb3e93548bd428c912147ce35c5cd55c5",
            ["modsecurity/Azure_Test_ipv4.txt"] = "44c4e145f4bb96f72126f370689565061fa188589301d55b32715fd2281264af",
            ["modsecurity/Azure_Test_ipv6.txt"] = "a994931723e09c28ad3602596a1e3a985bf0dcf9a784bd602aa91cff12c7cc8a",
        });
    }

    private async Task<(CloudFeedEngine Engine, CloudFeedCheck Check)> CheckBothFamiliesAsync()
    {
        CloudFeedEngine engine = Engine();
        CheckResult result = await engine.CheckAsync(
            Description(), [Prefix("34.1.0.0/16", "a"), Prefix("2001:db8::/32", "a")], TestPick, CancellationToken.None);
        return (engine, result.Check!);
    }

    private static void WriteOldFile(string path, DateTime stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# old\n2001:db8::/32\n");
        File.SetLastWriteTimeUtc(path, stamp);
    }

    [Fact]
    public async Task Write_Empties_The_Old_File_Of_A_Family_Whose_Ranges_Are_All_Unticked()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckBothFamiliesAsync();
        string[] old = FeedTypes.All.Select(type => CloudFeedWriter.PathFor(_temp.ExportFolder, type, check, ipv6: true)).ToArray();
        foreach (string path in old)
            WriteOldFile(path, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        CloudFeedWriteResult written = await engine.WriteAsync(engine.PlanWrite(check, [Block("34.1.0.0/16")]), [],
                                                               CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(old, written.EmptiedFiles);
        foreach (string path in old)
        {
            string[] lines = File.ReadAllLines(path);
            Assert.Contains("# Entries: 0 (0 confirmed by IPinfo, 0 added without confirmation)", lines);
            Assert.All(lines, line => Assert.StartsWith("#", line));
        }
    }

    [Fact]
    public async Task Write_Leaves_The_IPv6_File_Untouched_While_The_IPv6_Switch_Is_Off()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, IPv6Enabled = false };
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckBothFamiliesAsync();
        string path = CloudFeedWriter.PathFor(_temp.ExportFolder, FeedType.Fortigate, check, ipv6: true);
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        WriteOldFile(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        CloudFeedWriteResult written = await engine.WriteAsync(
            engine.PlanWrite(check, check.Ranges.Select(range => range.Prefix.Block).ToList()), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.DoesNotContain(path, written.Files);
        Assert.Null(written.EmptiedFiles);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Write_Leaves_The_IPv6_File_Untouched_While_The_Dataset_Has_No_IPv6_And_Writes_A_Ticked_IPv6_Range()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        DatasetInfo ipv4Only = Dataset with { IncludesIPv6 = false };
        _ipinfo.Dataset = ipv4Only;
        _ipinfo.OverlapFunc = blocks => Found(blocks, block => block.IsIpv6 ? [] : [Group("microsoft.com")]) with { Dataset = ipv4Only };
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckBothFamiliesAsync();
        string[] old = FeedTypes.All.Select(type => CloudFeedWriter.PathFor(_temp.ExportFolder, type, check, ipv6: true)).ToArray();
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        foreach (string path in old)
            WriteOldFile(path, stamp);

        CloudFeedWriteResult unticked = await engine.WriteAsync(engine.PlanWrite(check, [Block("34.1.0.0/16")]), [],
                                                                CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, unticked.Failure);
        Assert.Null(unticked.EmptiedFiles);
        Assert.All(old, path => Assert.DoesNotContain(path, unticked.Files));
        Assert.All(old, path => Assert.Equal("# old\n2001:db8::/32\n", File.ReadAllText(path)));
        Assert.All(old, path => Assert.Equal(stamp, File.GetLastWriteTimeUtc(path)));

        CloudFeedWriteResult ticked = await engine.WriteAsync(
            engine.PlanWrite(check, check.Ranges.Select(range => range.Prefix.Block).ToList()), [], CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, ticked.Failure);
        Assert.All(old, path => Assert.Contains(path, ticked.Files));
        Assert.All(old, path => Assert.Contains("\n2001:db8::/32\n", File.ReadAllText(path)));
        Assert.All(old, path => Assert.Contains("\n# Entries: 1 (", File.ReadAllText(path)));
    }

    [Fact]
    public async Task Write_Makes_No_File_For_A_Family_With_No_Ranges_And_No_Old_File()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true };
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckBothFamiliesAsync();

        CloudFeedWriteResult written = await engine.WriteAsync(engine.PlanWrite(check, [Block("34.1.0.0/16")]), [],
                                                               CancellationToken.None);

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([CloudFeedWriter.PathFor(_temp.ExportFolder, FeedType.Fortigate, check, ipv6: false)], written.Files);
        Assert.False(File.Exists(CloudFeedWriter.PathFor(_temp.ExportFolder, FeedType.Fortigate, check, ipv6: true)));
        Assert.Null(written.EmptiedFiles);
    }

    [Fact]
    public async Task The_Plan_Marks_Only_The_Large_Ranges_The_Saved_Feed_Includes()
    {
        _ipinfo.Records = new FeedRecords(_temp.Paths, _time, new SavedFeedListStore());
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true };
        CloudFeedEngine engine = Engine();
        CheckResult first = await engine.CheckAsync(
            Description(), [Prefix("10.0.0.0/8", "a"), Prefix("11.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a")], TestPick,
            CancellationToken.None);
        CloudFeedPlan unsaved = engine.PlanWrite(first.Check!, first.Check!.Ranges.Select(range => range.Prefix.Block).ToList());
        Assert.Equal(["10.0.0.0/8", "11.0.0.0/8"], unsaved.LargeRanges.Select(range => range.Network));
        Assert.All(unsaved.LargeRanges, range => Assert.False(range.SavedIncluded));
        CloudFeedWriteResult written = await engine.WriteAsync(unsaved, [Block("10.0.0.0/8")], CancellationToken.None);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
        SavedFeedPick pick = Assert.Single(_ipinfo.Records.ReadSavedFeeds().Feeds).Pick;
        Assert.Equal(["10.0.0.0/8"], pick.LargeIncluded);
        Assert.Equal(["11.0.0.0/8"], pick.LargeLeftOut);

        CheckResult second = await engine.CheckAsync(
            Description(),
            [Prefix("10.0.0.0/8", "a"), Prefix("11.0.0.0/8", "a"), Prefix("12.0.0.0/8", "a"), Prefix("34.1.0.0/16", "a")],
            TestPick, CancellationToken.None);
        CloudFeedPlan plan = engine.PlanWrite(second.Check!, second.Check!.Ranges.Select(range => range.Prefix.Block).ToList());

        Assert.Equal(["10.0.0.0/8", "11.0.0.0/8", "12.0.0.0/8"], plan.LargeRanges.Select(range => range.Network));
        Assert.Equal([true, false, false], plan.LargeRanges.Select(range => range.SavedIncluded));
    }
}
