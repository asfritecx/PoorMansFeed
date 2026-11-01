using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class CloudFeedChangesTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };

    public void Dispose() => _temp.Dispose();

    private string Fortigate(string family) => Path.Combine(_temp.ExportFolder, "fortigate", $"Azure_Test_{family}.txt");

    private string ModSecurity(string family) => Path.Combine(_temp.ExportFolder, "modsecurity", $"Azure_Test_{family}.txt");

    [Fact]
    public async Task A_first_write_adds_every_entry_as_written_in_its_own_family()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) =
            await CheckAsync("34.1.2.3/32", "34.1.0.0/16", "2001:db8::1/128", "2001:db8:1::/48");

        CloudFeedWriteResult written = await WriteAsync(engine, check, "34.1.2.3/32", "34.1.0.0/16", "2001:db8::1/128", "2001:db8:1::/48");

        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal([Fortigate("ipv4"), Fortigate("ipv6")], written.Files);
        Assert.NotNull(written.FeedChanges);
        Assert.Equal(written.Files, written.FeedChanges.Select(change => change.Path));
        FeedFileChange v4 = written.FeedChanges[0];
        FeedFileChange v6 = written.FeedChanges[1];
        Assert.Equal(["34.1.2.3", "34.1.0.0/16"], v4.IPv4.Added);
        Assert.Equal(Entries(v4.Path), v4.IPv4.Added);
        Assert.Empty(v4.IPv4.Removed);
        Assert.True(v4.IPv6.IsEmpty);
        Assert.Equal(["2001:db8::1", "2001:db8:1::/48"], v6.IPv6.Added);
        Assert.Equal(Entries(v6.Path), v6.IPv6.Added);
        Assert.Empty(v6.IPv6.Removed);
        Assert.True(v6.IPv4.IsEmpty);
    }

    [Fact]
    public async Task The_same_write_again_changes_no_entry_and_no_byte()
    {
        string[] ranges = ["34.1.2.3/32", "34.1.0.0/16", "2001:db8::1/128"];
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync(ranges);
        CloudFeedWriteResult first = await WriteAsync(engine, check, ranges);
        byte[][] before = first.Files.Select(File.ReadAllBytes).ToArray();

        CloudFeedWriteResult second = await WriteAsync(engine, check, ranges);

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.Equal(2, second.FeedChanges!.Count);
        Assert.All(second.FeedChanges, change => Assert.True(change.IsEmpty));
        Assert.Equal(before, second.Files.Select(File.ReadAllBytes).ToArray());
    }

    [Fact]
    public async Task A_range_dropped_and_one_added_give_exactly_that_Removed_and_Added()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) =
            await CheckAsync("34.1.0.0/16", "35.1.0.0/16", "36.1.0.0/16", "2001:db8::/32", "2001:db8:1::/48");
        await WriteAsync(engine, check, "34.1.0.0/16", "35.1.0.0/16", "2001:db8::/32");

        CloudFeedWriteResult second = await WriteAsync(engine, check, "34.1.0.0/16", "36.1.0.0/16", "2001:db8:1::/48");

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        FeedFileChange v4 = second.FeedChanges![0];
        FeedFileChange v6 = second.FeedChanges[1];
        Assert.Equal(["36.1.0.0/16"], v4.IPv4.Added);
        Assert.Equal(["35.1.0.0/16"], v4.IPv4.Removed);
        Assert.True(v4.IPv6.IsEmpty);
        Assert.Equal(["2001:db8:1::/48"], v6.IPv6.Added);
        Assert.Equal(["2001:db8::/32"], v6.IPv6.Removed);
        Assert.True(v6.IPv4.IsEmpty);
    }

    [Fact]
    public async Task A_hand_edited_old_file_holding_the_same_networks_counts_no_change()
    {
        string[] ranges = ["34.1.2.3/32", "34.1.0.0/16", "2001:db8::1/128", "2001:db8:1::/48"];
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync(ranges);
        await WriteAsync(engine, check, ranges);
        File.WriteAllText(Fortigate("ipv4"), "\uFEFF# edited\r\n34.1.2.3/32\r\n34.1.0.0/16\r\n", new UTF8Encoding(false));
        File.WriteAllText(Fortigate("ipv6"), "2001:DB8::1/128\n2001:DB8:1::/48\n", new UTF8Encoding(false));

        CloudFeedWriteResult second = await WriteAsync(engine, check, ranges);

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.Equal(2, second.FeedChanges!.Count);
        Assert.All(second.FeedChanges, change => Assert.True(change.IsEmpty));
    }

    [Fact]
    public async Task With_both_types_FortiGates_changes_come_first_and_each_copy_is_compared_with_its_own_old_file()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        string[] ranges = ["34.1.2.3/32", "34.1.0.0/16", "2001:db8::/32"];
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync(ranges);
        await WriteAsync(engine, check, ranges);
        File.WriteAllText(Fortigate("ipv4"), "34.1.2.3\n9.9.9.0/24\n");
        File.WriteAllText(ModSecurity("ipv4"), "34.1.0.0/16\n198.51.100.0/24\n");

        CloudFeedWriteResult second = await WriteAsync(engine, check, ranges);

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.Equal([Fortigate("ipv4"), Fortigate("ipv6"), ModSecurity("ipv4"), ModSecurity("ipv6")], second.Files);
        Assert.NotNull(second.FeedChanges);
        Assert.Equal(second.Files, second.FeedChanges.Select(change => change.Path));
        Assert.Equal(["34.1.0.0/16"], second.FeedChanges[0].IPv4.Added);
        Assert.Equal(["9.9.9.0/24"], second.FeedChanges[0].IPv4.Removed);
        Assert.True(second.FeedChanges[1].IsEmpty);
        Assert.Equal(["34.1.2.3"], second.FeedChanges[2].IPv4.Added);
        Assert.Equal(["198.51.100.0/24"], second.FeedChanges[2].IPv4.Removed);
        Assert.True(second.FeedChanges[3].IsEmpty);
    }

    [Fact]
    public async Task Each_file_carries_its_type_and_family_and_whether_an_old_file_was_read()
    {
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        string[] ranges = ["34.1.2.3/32", "2001:db8::/32"];
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync(ranges);

        CloudFeedWriteResult first = await WriteAsync(engine, check, ranges);
        CloudFeedWriteResult second = await WriteAsync(engine, check, ranges);

        foreach (CloudFeedWriteResult written in new[] { first, second })
        {
            Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
            Assert.Equal([FeedType.Fortigate, FeedType.Fortigate, FeedType.ModSecurity, FeedType.ModSecurity],
                         written.FeedChanges!.Select(change => change.Type));
            Assert.Equal([FeedFamilies.IPv4, FeedFamilies.IPv6, FeedFamilies.IPv4, FeedFamilies.IPv6],
                         written.FeedChanges!.Select(change => change.Families));
        }
        Assert.All(first.FeedChanges!, change => Assert.Equal(FeedFileState.Missing, change.OldFile));
        Assert.All(second.FeedChanges!, change => Assert.Equal(FeedFileState.Read, change.OldFile));
    }

    [Fact]
    public async Task A_family_the_exceptions_empty_rewrites_its_old_file_and_gives_its_removals()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("34.1.0.0/16", "2001:db8::/32");
        await WriteAsync(engine, check, "34.1.0.0/16", "2001:db8::/32");
        _ipinfo.Exceptions = RangeSubtractionTests.List("2001:db8::/32");
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;

        CloudFeedWriteResult second = await WriteAsync(engine, check, "34.1.0.0/16", "2001:db8::/32");

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.NotNull(second.FeedChanges);
        Assert.Equal([Fortigate("ipv4"), Fortigate("ipv6")], second.FeedChanges.Select(change => change.Path));
        Assert.True(second.FeedChanges[0].IsEmpty);
        Assert.Equal(["2001:db8::/32"], second.FeedChanges[1].IPv6.Removed);
        Assert.Empty(second.FeedChanges[1].IPv6.Added);
        Assert.Empty(Entries(Fortigate("ipv6")));
        Assert.Equal([Fortigate("ipv6")], second.EmptiedFiles!);
        Assert.Null(second.OldFilesLeft);
        Assert.Equal([new FeedRemoval("2001:db8::/32", FeedRemovalReason.Exception)],
                     Assert.Single(records.Requests).Change!.Families.SelectMany(family => family.Removed).ToArray());
    }

    [Fact]
    public async Task A_family_the_whole_range_rule_empties_rewrites_its_old_file_with_its_left_out_line()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("34.1.0.0/16", "2001:db8::/32", "::/0");
        await WriteAsync(engine, check, "34.1.0.0/16", "2001:db8::/32");
        var records = new RecordingRecords { Locks = FeedRecords.None };
        _ipinfo.Records = records;

        CloudFeedWriteResult second = await WriteAsync(engine, check, [Block("::/0")], "34.1.0.0/16", "::/0");

        Assert.Equal(CloudFeedWriteFailure.None, second.Failure);
        Assert.Equal(1, second.WholeRangesLeftOut);
        Assert.NotNull(second.FeedChanges);
        Assert.Equal([Fortigate("ipv4"), Fortigate("ipv6")], second.FeedChanges.Select(change => change.Path));
        Assert.True(second.FeedChanges[0].IsEmpty);
        Assert.Equal(["2001:db8::/32"], second.FeedChanges[1].IPv6.Removed);
        Assert.Equal([Fortigate("ipv6")], second.EmptiedFiles!);
        Assert.Null(second.OldFilesLeft);
        string text = File.ReadAllText(Fortigate("ipv6"));
        Assert.Contains("# Entries: 0 (0 confirmed by IPinfo, 0 added without confirmation)\n"
                        + "# Left out: 1 range covering every address: ::/0\n", text);
        Assert.DoesNotContain("# Exceptions:", text);
        Assert.Empty(Entries(Fortigate("ipv6")));
        Assert.Equal([new FeedRemoval("2001:db8::/32", FeedRemovalReason.LeftOut)],
                     Assert.Single(records.Requests).Change!.Families.SelectMany(family => family.Removed).ToArray());
    }

    [Fact]
    public async Task An_exception_cut_gives_the_cut_pieces_as_Added()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("34.1.0.0/16");
        await WriteAsync(engine, check, "34.1.0.0/16");
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/17");

        CloudFeedWriteResult second = await WriteAsync(engine, check, "34.1.0.0/16");

        FeedFileChange change = Assert.Single(second.FeedChanges!);
        Assert.Equal(["34.1.128.0/17"], change.IPv4.Added);
        Assert.Equal(["34.1.0.0/16"], change.IPv4.Removed);
        Assert.True(change.IPv6.IsEmpty);
    }

    [Fact]
    public async Task A_failure_on_the_second_file_keeps_exactly_the_first_files_change()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("34.1.0.0/16", "35.1.0.0/16", "2001:db8::/32");
        await WriteAsync(engine, check, "34.1.0.0/16", "2001:db8::/32");
        // A folder where the temporary file should go makes that one write fail
        Directory.CreateDirectory(Fortigate("ipv6") + ".tmp");

        CloudFeedWriteResult written = await WriteAsync(engine, check, "34.1.0.0/16", "35.1.0.0/16", "2001:db8::/32");

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Equal(Fortigate("ipv6"), written.FailedFile);
        FeedFileChange change = Assert.Single(written.FeedChanges!);
        Assert.Equal(Fortigate("ipv4"), change.Path);
        Assert.Equal(["35.1.0.0/16"], change.IPv4.Added);
        Assert.Empty(change.IPv4.Removed);
        Assert.True(change.IPv6.IsEmpty);
    }

    [Fact]
    public async Task A_failure_on_the_first_file_gives_no_changes()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("34.1.0.0/16", "2001:db8::/32");
        Directory.CreateDirectory(Fortigate("ipv4"));

        CloudFeedWriteResult written = await WriteAsync(engine, check, "34.1.0.0/16", "2001:db8::/32");

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, written.Failure);
        Assert.Null(written.FeedChanges);
    }

    [Fact]
    public async Task The_left_out_large_ranges_are_named_in_plan_order_and_match_the_count()
    {
        string[] ranges = ["10.0.0.0/8", "34.1.0.0/16", "11.0.0.0/8", "2001:db8::/32", "2001::/16"];
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync(ranges);

        CloudFeedWriteResult none = await WriteAsync(engine, check, ranges);
        Assert.Equal(CloudFeedWriteFailure.None, none.Failure);
        Assert.Equal(["10.0.0.0/8", "11.0.0.0/8", "2001::/16"], none.LeftOutLargeRanges!);
        Assert.Equal(none.LargeRangesLeftOut, none.LeftOutLargeRanges!.Count);

        CloudFeedWriteResult allowed = await WriteAsync(engine, check, [Block("11.0.0.0/8")], ranges);
        Assert.Equal(["10.0.0.0/8", "2001::/16"], allowed.LeftOutLargeRanges!);
        Assert.Equal(2, allowed.LargeRangesLeftOut);

        CloudFeedWriteResult all = await WriteAsync(engine, check, [Block("10.0.0.0/8"), Block("11.0.0.0/8"), Block("2001::/16")], ranges);
        Assert.Null(all.LeftOutLargeRanges);
        Assert.Equal(0, all.LargeRangesLeftOut);
    }

    [Fact]
    public async Task The_left_out_large_ranges_are_named_on_NothingToWrite_and_WriteFailed()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("10.0.0.0/8", "34.1.0.0/16", "2001:db8::/32");
        _ipinfo.Exceptions = RangeSubtractionTests.List("34.1.0.0/16", "2001:db8::/32");

        CloudFeedWriteResult nothing = await WriteAsync(engine, check, "10.0.0.0/8", "34.1.0.0/16", "2001:db8::/32");

        Assert.Equal(CloudFeedWriteFailure.NothingToWrite, nothing.Failure);
        Assert.Equal(["10.0.0.0/8"], nothing.LeftOutLargeRanges!);
        Assert.Equal(1, nothing.LargeRangesLeftOut);
        Assert.Null(nothing.FeedChanges);

        _ipinfo.Exceptions = ExceptionList.Empty;
        Directory.CreateDirectory(Fortigate("ipv4"));
        CloudFeedWriteResult failed = await WriteAsync(engine, check, "10.0.0.0/8", "34.1.0.0/16", "2001:db8::/32");

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, failed.Failure);
        Assert.Equal(["10.0.0.0/8"], failed.LeftOutLargeRanges!);
        Assert.Equal(1, failed.LargeRangesLeftOut);
    }

    [Fact]
    public async Task The_left_out_large_ranges_are_named_when_the_folder_cant_be_made()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("10.0.0.0/8", "34.1.0.0/16");
        File.WriteAllText(_temp.ExportFolder, "");

        CloudFeedWriteResult result = await WriteAsync(engine, check, "10.0.0.0/8", "34.1.0.0/16");

        Assert.Equal(CloudFeedWriteFailure.FolderUnavailable, result.Failure);
        Assert.Equal(["10.0.0.0/8"], result.LeftOutLargeRanges!);
        Assert.Equal(1, result.LargeRangesLeftOut);
        Assert.Null(result.FeedChanges);
    }

    [Fact]
    public async Task Refusals_before_the_large_filter_name_no_range()
    {
        (CloudFeedEngine engine, CloudFeedCheck check) = await CheckAsync("10.0.0.0/8", "34.1.0.0/16");
        _ipinfo.Exceptions = ExceptionList.Unreadable;

        CloudFeedWriteResult refused = await WriteAsync(engine, check, "10.0.0.0/8", "34.1.0.0/16");

        Assert.Equal(CloudFeedWriteFailure.ExceptionsUnreadable, refused.Failure);
        Assert.Equal(0, refused.LargeRangesLeftOut);
        Assert.Null(refused.LeftOutLargeRanges);
        Assert.Null(refused.FeedChanges);
    }

    private async Task<(CloudFeedEngine Engine, CloudFeedCheck Check)> CheckAsync(params string[] cidrs)
    {
        _ipinfo.OverlapFunc ??= blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS8075", "Microsoft", "microsoft.com", 1)])).ToList(),
            Dataset);
        var engine = new CloudFeedEngine(_ipinfo, _time, _temp.ExportFolder);
        FeedDescription description = new("microsoft.com", ["Ranges: Test source"], "Azure_Test");
        CheckResult result = await engine.CheckAsync(description, cidrs.Select(cidr => new FeedPrefix(Block(cidr), "a")).ToList(),
                                                     CloudFeedEngineTests.TestPick, CancellationToken.None);
        Assert.Equal(CheckFailure.None, result.Failure);
        return (engine, result.Check!);
    }

    private static Task<CloudFeedWriteResult> WriteAsync(CloudFeedEngine engine, CloudFeedCheck check, params string[] ticked) =>
        WriteAsync(engine, check, [], ticked);

    private static Task<CloudFeedWriteResult> WriteAsync(CloudFeedEngine engine, CloudFeedCheck check,
                                                         IReadOnlyCollection<CidrBlock> allowedLarge, params string[] ticked)
    {
        CloudFeedPlan plan = engine.PlanWrite(check, ticked.Select(Block).ToList());
        return engine.WriteAsync(plan, allowedLarge, CancellationToken.None);
    }

    private static CidrBlock Block(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return block;
    }

    private static string[] Entries(string path) =>
        File.ReadAllLines(path).Where(line => line.Length > 0 && line[0] != '#').ToArray();
}
