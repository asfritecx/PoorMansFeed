using System.Net;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

public sealed class FeedSkipTests : IDisposable
{
    private static readonly DateTimeOffset Written = new(2026, 10, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Written.AddHours(5);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Written, true);

    private static readonly NetworkRecord GoogleV4 = new("8.8.8.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");
    private static readonly NetworkRecord GoogleV6 = new("2001:4860::/32", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");
    private static readonly NetworkRecord OtherV4 = new("8.34.208.0/20", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly ReadableExceptions NoExceptions = ReadableExceptions.WrapForFeed(ExceptionList.Empty)!;

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Writers => ["asn-split", "asn-combined", "cloud", "rolling"];

    /// <summary>A feed one writer just wrote, its proof, and that writer's plan made again.</summary>
    private sealed record Feed(FeedOptions Options, SavedFeedWriteProof Proof,
                               Func<FeedOptions, bool, IReadOnlyList<PlannedFeedFile>> Replan)
    {
        public IReadOnlyList<PlannedFeedFile> Plan() => Replan(Options, false);
    }

    private static readonly FeedOptions Both = new([FeedType.Fortigate, FeedType.ModSecurity], true, true, true, true);

    private async Task<Feed> WriteAsync(string writer)
    {
        string folder = _temp.Path;
        switch (writer)
        {
            case "asn-split":
            case "asn-combined":
            {
                FeedOptions options = Both with { SplitByFamily = writer == "asn-split" };
                FeedWrite write = ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", [GoogleV4, GoogleV6], options, Written,
                                                          NoExceptions);
                return new Feed(options, Proof(folder, options, write.Changes), (planned, extra) => ExportWriter.PlanFeeds(
                    folder, "AS15169", "Google LLC", extra ? [GoogleV4, GoogleV6, OtherV4] : [GoogleV4, GoogleV6], planned, Later,
                    NoExceptions, datasetHasIPv6: true).Files);
            }
            case "cloud":
            {
                FeedOptions options = Both;
                CloudFeedCheck check = await CheckAsync(folder, ["34.1.0.0/16", "2001:db8::/32"]);
                CloudFeedCheck extra = await CheckAsync(folder, ["34.1.0.0/16", "35.1.0.0/16", "2001:db8::/32"]);
                var progress = new CloudFeedWriter.Progress();
                CloudFeedWriter.Write(CloudPlan(folder, options, check, Written), progress);
                return new Feed(options, Proof(folder, options, progress.Changes),
                                (planned, more) => CloudPlan(folder, planned, more ? extra : check, Later));
            }
            case "rolling":
            {
                FeedOptions options = Both;
                var changes = new List<FeedFileChange>();
                foreach (AbuseIpDbService.RollingFeedFile file in RollingPlan(folder, options, false, Written))
                {
                    FeedFileRead before = FeedFileEntries.Read(file.Path);
                    string hash = AbuseIpDbFeedWriter.WriteFile(file.Path, file.Type, file.Body);
                    changes.Add(FeedFileEntries.Compare(file.Path, file.Type, file.Families, before, file.Body.Lines)
                                with { WrittenSha256 = hash });
                }

                return new Feed(options, Proof(folder, options, changes),
                                (planned, more) => RollingPlan(folder, planned, more, Later).Select(file => file.Planned).ToList());
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(writer));
        }
    }

    private static SavedFeedWriteProof Proof(string folder, FeedOptions options, IReadOnlyList<FeedFileChange> changes)
    {
        SavedFeedWriteProof? proof = SavedFeedWriteProof.Of(folder, options, changes);
        Assert.NotNull(proof);
        Assert.Equal(options.Types.Count * 2 / (options.SplitByFamily ? 1 : 2), proof.FileSha256.Count);
        return proof;
    }

    private static async Task<CloudFeedCheck> CheckAsync(string folder, string[] prefixes)
    {
        var ipinfo = new FakeIpInfoService
        {
            Dataset = Dataset,
            Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true },
            OverlapFunc = blocks => new OverlapResult(OverlapOutcome.Found, blocks.Select(block => new NetworkOverlap(
                block, [new OverlapGroup("US", "United States", "AS8075", "Microsoft", "microsoft.com", 1)])).ToList(), Dataset),
        };
        var engine = new CloudFeedEngine(ipinfo, new FakeTimeProvider(Written), folder);
        List<FeedPrefix> asked = prefixes.Select(text =>
        {
            Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
            return new FeedPrefix(block, "a");
        }).ToList();
        CheckResult result = await engine.CheckAsync(new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"),
                                                     asked, CloudFeedEngineTests.TestPick, CancellationToken.None);
        Assert.NotNull(result.Check);
        return result.Check;
    }

    private static IReadOnlyList<PlannedFeedFile> CloudPlan(string folder, FeedOptions options, CloudFeedCheck check,
                                                            DateTimeOffset utcNow) =>
        CloudFeedWriter.Plan(folder, options, check,
                             check.Ranges.Select(range => new FeedLine(range.Prefix.Block.Text, range.Prefix.Block.IsIpv6,
                                                                       range.Status == RangeConfirmation.Confirmed)).ToList(),
                             new ExceptionTally(), 0, 0, utcNow);

    private static IReadOnlyList<AbuseIpDbService.RollingFeedFile> RollingPlan(string folder, FeedOptions options, bool extra,
                                                                               DateTimeOffset utcNow)
    {
        string[] addresses = extra ? ["8.8.8.8", "9.9.9.9", "1.1.1.1", "2001:4860::8888"] : ["8.8.8.8", "9.9.9.9", "2001:4860::8888"];
        List<AbuseListEntry> entries = addresses.Select(text =>
        {
            Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
            Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
            return new AbuseListEntry(value, Written.AddDays(-1), Written, 90, null, null);
        }).ToList();
        var header = new AbuseFeedHeader(30, utcNow, Written, Written);
        AbuseFeedBody[] bodies =
        [
            AbuseFeedBody.Of(AbuseIpDbFeedWriter.Select(entries, NoExceptions, ipv6: false), header),
            AbuseFeedBody.Of(AbuseIpDbFeedWriter.Select(entries, NoExceptions, ipv6: true), header),
        ];
        return AbuseIpDbService.PlanRollingFeed(folder, options.Types, bodies);
    }

    [Fact]
    public void A_check_without_IPv6_plans_no_IPv6_file_even_when_the_options_write_IPv6()
    {
        string folder = _temp.Path;
        Assert.True(CidrBlock.TryParse("34.1.0.0/16", out CidrBlock? block));
        var check = new CloudFeedCheck(new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"), Dataset,
                                       includesIPv6: false, 16, 32,
                                       [new CheckedRange(new FeedPrefix(block, "a"), RangeConfirmation.Confirmed, [], false)],
                                       CloudFeedEngineTests.TestPick);
        string ipv6Path = Path.Combine(folder, ExportWriter.FortigateFolderName, "Azure_Test_ipv6.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(ipv6Path)!);
        File.WriteAllText(ipv6Path, "# Old\n2001:db8::/32\n");
        FeedOptions options = Both with { Types = [FeedType.Fortigate] };
        Assert.True(options.WritesIPv6);

        IReadOnlyList<PlannedFeedFile> plan = CloudPlan(folder, options, check, Written);

        Assert.Equal([Path.Combine(folder, ExportWriter.FortigateFolderName, "Azure_Test_ipv4.txt")],
                     plan.Select(file => file.Path));
    }

    private bool IsUnchanged(Feed feed, IReadOnlyList<PlannedFeedFile> plan, SavedFeedWriteProof? proof) =>
        FeedSkip.IsUnchanged(_temp.Path, plan, feed.Proof.SettingsFingerprint, proof);

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_feed_just_written_with_its_proof_is_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);

        Assert.Equal(SavedFeedWriteProof.FingerprintOf(feed.Options), feed.Proof.SettingsFingerprint);
        Assert.True(FeedSkip.IsUnchanged(_temp.Path, feed.Plan(), SavedFeedWriteProof.FingerprintOf(feed.Options), feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task No_proof_or_another_fingerprint_is_never_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);

        Assert.False(IsUnchanged(feed, feed.Plan(), null));
        Assert.False(FeedSkip.IsUnchanged(_temp.Path, feed.Plan(),
                                          SavedFeedWriteProof.FingerprintOf(feed.Options with { IPv6Enabled = false }), feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_deleted_file_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);
        IReadOnlyList<PlannedFeedFile> plan = feed.Plan();

        File.Delete(plan[^1].Path);

        Assert.False(IsUnchanged(feed, plan, feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_hand_edit_that_keeps_the_entries_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);
        IReadOnlyList<PlannedFeedFile> plan = feed.Plan();
        File.AppendAllText(plan[0].Path, "# A note added by hand\n");

        Assert.Empty(FeedFileEntries.Compare(plan[0].Path, plan[0].Type, plan[0].Families, FeedFileEntries.Read(plan[0].Path),
                                             plan[0].Lines).IPv4.Added);
        Assert.False(IsUnchanged(feed, plan, feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_planned_file_the_proof_lacks_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);
        string dropped = feed.Proof.FileSha256.Keys.Order(StringComparer.Ordinal).Last();
        var proof = new SavedFeedWriteProof(feed.Proof.SettingsFingerprint,
                                            feed.Proof.FileSha256.Where(pair => pair.Key != dropped)
                                                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        Assert.False(IsUnchanged(feed, feed.Plan(), proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_proof_file_no_longer_planned_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);

        // ModSecurity unticked since the write: its files are still in the proof but no longer planned
        IReadOnlyList<PlannedFeedFile> plan = feed.Replan(feed.Options with { Types = [FeedType.Fortigate] }, false);

        Assert.NotEmpty(plan);
        Assert.False(IsUnchanged(feed, plan, feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task An_entry_added_to_the_source_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);

        Assert.False(IsUnchanged(feed, feed.Replan(feed.Options, true), feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_folder_at_a_planned_path_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);
        IReadOnlyList<PlannedFeedFile> plan = feed.Plan();
        File.Delete(plan[0].Path);
        Directory.CreateDirectory(plan[0].Path);

        Assert.False(IsUnchanged(feed, plan, feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task A_link_to_the_same_bytes_at_a_planned_path_is_not_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);
        IReadOnlyList<PlannedFeedFile> plan = feed.Plan();
        string target = _temp.File("target.txt");
        File.Move(plan[0].Path, target);
        try
        {
            File.CreateSymbolicLink(plan[0].Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link.");
            return;
        }

        Assert.False(IsUnchanged(feed, plan, feed.Proof));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task An_empty_plan_is_never_unchanged(string writer)
    {
        Feed feed = await WriteAsync(writer);

        Assert.False(IsUnchanged(feed, [], feed.Proof));
    }

    [Fact]
    public void A_header_only_file_just_written_is_unchanged_on_a_second_plan()
    {
        string folder = _temp.Path;
        ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", [GoogleV4, GoogleV6], Both, Written, NoExceptions);
        ReadableExceptions noIpv6 = ReadableExceptions.WrapForFeed(ExceptionList.Create(
            [ExceptionEntryTests.Entry("2001:4860::/32", "Every IPv6 range")]))!;

        FeedWrite emptied = ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", [GoogleV4, GoogleV6], Both, Written, noIpv6);
        IReadOnlyList<PlannedFeedFile> plan =
            ExportWriter.PlanFeeds(folder, "AS15169", "Google LLC", [GoogleV4, GoogleV6], Both, Later, noIpv6, datasetHasIPv6: true).Files;

        Assert.Equal(2, emptied.EmptiedFiles.Count);
        Assert.Equal(emptied.EmptiedFiles, plan.Where(file => file.Emptied).Select(file => file.Path));
        Assert.True(FeedSkip.IsUnchanged(folder, plan, SavedFeedWriteProof.FingerprintOf(Both),
                                         SavedFeedWriteProof.Of(folder, Both, emptied.Changes)));
    }

    [Fact]
    public void A_family_the_source_dropped_is_emptied_and_then_unchanged_on_a_second_plan()
    {
        string folder = _temp.Path;
        ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", [GoogleV4, GoogleV6], Both, Written, NoExceptions);

        FeedWrite emptied = ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", [GoogleV4], Both, Written, NoExceptions);
        IReadOnlyList<PlannedFeedFile> plan =
            ExportWriter.PlanFeeds(folder, "AS15169", "Google LLC", [GoogleV4], Both, Later, NoExceptions, datasetHasIPv6: true).Files;

        Assert.Equal([Path.Combine(folder, "fortigate", "AS15169_ipv6.txt"), Path.Combine(folder, "modsecurity", "AS15169_ipv6.txt")],
                     emptied.EmptiedFiles);
        Assert.Equal(4, plan.Count);
        Assert.True(FeedSkip.IsUnchanged(folder, plan, SavedFeedWriteProof.FingerprintOf(Both),
                                         SavedFeedWriteProof.Of(folder, Both, emptied.Changes)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0folder")]
    public async Task An_export_folder_the_path_work_refuses_is_not_unchanged(string exportFolder)
    {
        Feed feed = await WriteAsync("asn-split");

        Assert.False(FeedSkip.IsUnchanged(exportFolder, feed.Plan(), feed.Proof.SettingsFingerprint, feed.Proof));
    }

    [Fact]
    public void A_path_outside_the_export_folder_has_no_key()
    {
        string folder = _temp.Path;

        Assert.Equal("fortigate/AS1_ipv4.txt", SavedFeedWriteProof.KeyOf(folder, Path.Combine(folder, "fortigate", "AS1_ipv4.txt")));
        Assert.Null(SavedFeedWriteProof.KeyOf(folder, Path.Combine(folder, "..", "fortigate", "AS1_ipv4.txt")));
        Assert.Null(SavedFeedWriteProof.KeyOf(folder, Path.Combine(folder, "AS1_ipv4.txt")));
        Assert.Null(SavedFeedWriteProof.KeyOf(folder, Path.Combine(folder, "other", "AS1_ipv4.txt")));
    }
}
