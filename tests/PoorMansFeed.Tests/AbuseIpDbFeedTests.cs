using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The AbuseIPDB rolling feed: what the service writes, and how the writer chooses, orders and words it.</summary>
public sealed class AbuseIpDbFeedTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly ServiceTempFolder _temp = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public AbuseIpDbFeedTests()
    {
        // The feed sends nothing: a request fails the test
        _handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("The feed sent a request."));
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    private string ListPath => _temp.Paths.AbuseIpDbDatabasePath;

    private string ExportFolder => _temp.Paths.DefaultExportFolder;

    private string Ipv4File => AbuseIpDbFeedWriter.PathFor(ExportFolder, ipv6: false);

    private string Ipv6File => AbuseIpDbFeedWriter.PathFor(ExportFolder, ipv6: true);

    // ---------- 1. The exact bytes ----------

    [Fact]
    public async Task Both_files_have_the_exact_header_and_addresses_in_byte_order_with_no_BOM_and_no_IPinfo_line()
    {
        Seed(Generated, Downloaded, Entry("9.9.9.9", 90, "CH"), Entry("2606:4700:4700::1111", 20), Entry("1.1.1.1", 40),
             Entry("2001:4860::8888", 70, "US"), Entry("8.8.8.8", 60));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Equal(0, result.LeftOut);
        Assert.Equal(0, result.ExceptionRemovals);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(Utf8(Header("IPv4", 3) + Lines("1.1.1.1", "8.8.8.8", "9.9.9.9")), File.ReadAllBytes(Ipv4File));
        Assert.Equal(Utf8(Header("IPv6", 2) + Lines("2001:4860::8888", "2606:4700:4700::1111")), File.ReadAllBytes(Ipv6File));
        foreach (string file in new[] { Ipv4File, Ipv6File })
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("ipinfo", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CH", text, StringComparison.Ordinal);
            Assert.DoesNotContain("US", text, StringComparison.Ordinal);
        }
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task A_link_at_the_temporary_path_is_replaced_and_never_written_through()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 60), Entry("2001:4860::8888", 70));
        Directory.CreateDirectory(Path.GetDirectoryName(Ipv4File)!);
        // A file outside the export folder, and a link to nothing
        string victim = Path.Combine(_temp.Folder, "victim.txt");
        File.WriteAllText(victim, "not the feed's");
        byte[] victimBefore = File.ReadAllBytes(victim);
        string missing = Path.Combine(_temp.Folder, "missing.txt");
        try
        {
            File.CreateSymbolicLink(Ipv4File + ".tmp", victim);
            File.CreateSymbolicLink(Ipv6File + ".tmp", missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link, so a link at the temporary path can't be planted.");
            return;
        }

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
        Assert.Equal(victimBefore, File.ReadAllBytes(victim));
        Assert.False(File.Exists(missing));
        Assert.Null(new FileInfo(Ipv4File).LinkTarget);
        Assert.Null(new FileInfo(Ipv6File).LinkTarget);
        Assert.Equal(Utf8(Header("IPv4", 1) + Lines("8.8.8.8")), File.ReadAllBytes(Ipv4File));
        Assert.Equal(Utf8(Header("IPv6", 1) + Lines("2001:4860::8888")), File.ReadAllBytes(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    [Fact]
    public void The_header_leaves_out_the_parenthesis_without_a_generation_and_the_line_without_a_download()
    {
        AbuseFeedFamily family = new(false, [Address("8.8.8.8")], 0, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Ipv4File)!);

        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, family, new AbuseFeedHeader(30, Now, Downloaded, null));
        string[] noGeneration = File.ReadAllLines(Ipv4File);
        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, family, new AbuseFeedHeader(30, Now, null, null));
        string[] noDownload = File.ReadAllLines(Ipv4File);

        Assert.Contains("# Last download: 2026-10-19 07:30:00 UTC", noGeneration);
        Assert.DoesNotContain(noDownload, line => line.StartsWith("# Last download", StringComparison.Ordinal));
        Assert.Equal(["# Generated: 2026-10-19 08:00:00 UTC", "# Entries: 1", "8.8.8.8"], noDownload[^3..]);
    }

    [Fact]
    public void The_header_puts_Exceptions_right_after_Entries_then_Left_out_with_singular_and_plural_wording()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ipv4File)!);

        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, new AbuseFeedFamily(false, [Address("8.8.8.8")], 1, 1),
                                        new AbuseFeedHeader(30, Now, null, null));
        string[] singular = File.ReadAllLines(Ipv4File);
        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, new AbuseFeedFamily(false, [Address("8.8.8.8")], 3, 5),
                                        new AbuseFeedHeader(30, Now, null, null));
        string[] plural = File.ReadAllLines(Ipv4File);

        Assert.Equal(["# Entries: 1", "# Exceptions: 1 address removed",
                      "# Left out: 1 address over FortiOS's 131072 limit", "8.8.8.8"], singular[^4..]);
        Assert.Equal(["# Entries: 1", "# Exceptions: 3 addresses removed",
                      "# Left out: 5 addresses over FortiOS's 131072 limit", "8.8.8.8"], plural[^4..]);
    }

    [Fact]
    public void A_family_with_no_address_gives_the_exact_header_only_file_with_or_without_a_download_line()
    {
        AbuseFeedFamily empty = new(false, [], 0, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(Ipv4File)!);

        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, empty, new AbuseFeedHeader(30, Now, Downloaded, Generated));
        Assert.Equal(Utf8(Header("IPv4", 0)), File.ReadAllBytes(Ipv4File));

        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, empty, new AbuseFeedHeader(30, Now, null, null));
        Assert.Equal(Utf8(Header("IPv4", 0, downloaded: false)), File.ReadAllBytes(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
    }

    // ---------- 2. Expiry at write time ----------

    [Fact]
    public async Task An_address_older_than_the_keep_days_of_the_last_download_is_not_written_raising_them_brings_it_back_and_a_later_download_empties_the_file()
    {
        Seed(Now.AddDays(-10).AddSeconds(-2), Now.AddDays(-10), Entry("8.8.8.8", 50));
        Seed(Now.AddHours(-1).AddSeconds(-2), Now.AddHours(-1), Entry("9.9.9.9", 50));
        AbuseIpDbService service = Service();

        Assert.Equal(2, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], Addresses(Ipv4File));
        Assert.Contains("# Kept: addresses listed within 30 days of the last download", File.ReadAllLines(Ipv4File));

        KeepDays(1);
        Assert.Equal(1, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);
        Assert.Equal(["9.9.9.9"], Addresses(Ipv4File));
        Assert.Contains("# Kept: addresses listed within 1 day of the last download", File.ReadAllLines(Ipv4File));

        // The clock alone ages nothing: 21 days on with no download, the keep days still count from the last one
        _time.Advance(TimeSpan.FromDays(21));
        Assert.Equal(1, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);
        Assert.Equal(["9.9.9.9"], Addresses(Ipv4File));
        KeepDays(30);
        Assert.Equal(2, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);

        // A download lists 9.9.9.9 again an hour ago: 8.8.8.8 was listed 31 days before it
        Seed(Now.AddDays(21).AddHours(-1).AddSeconds(-2), Now.AddDays(21).AddHours(-1), Entry("9.9.9.9", 50));
        Assert.Equal(1, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);
        Assert.Equal(["9.9.9.9"], Addresses(Ipv4File));

        KeepDays(60);
        Assert.Equal(2, (await service.WriteFeedAsync(CancellationToken.None)).IPv4Written);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], Addresses(Ipv4File));
        Assert.Contains("# Kept: addresses listed within 60 days of the last download", File.ReadAllLines(Ipv4File));

        // Two days on, a download lists only an IPv6 address: no IPv4 address is within one day of it, so the IPv4
        // file becomes header-only
        Assert.False(File.Exists(Ipv6File));
        KeepDays(1);
        _time.Advance(TimeSpan.FromDays(2));
        Seed(Now.AddDays(23).AddHours(-1).AddSeconds(-2), Now.AddDays(23).AddHours(-1), Entry("2001:4860::8888", 50));
        AbuseFeedWriteResult none = await service.WriteFeedAsync(CancellationToken.None);
        Assert.Equal(AbuseFeedWriteFailure.None, none.Failure);
        Assert.Equal([Ipv4File, Ipv6File], none.Files);
        Assert.Equal([Ipv4File], none.EmptiedFiles);
        Assert.Equal(0, none.IPv4Written);
        Assert.Equal(1, none.IPv6Written);
        Assert.Empty(Addresses(Ipv4File));
        Assert.Contains("# Entries: 0", File.ReadAllLines(Ipv4File));
        Assert.Equal(["2001:4860::8888"], Addresses(Ipv6File));
    }

    [Fact]
    public async Task A_clock_set_ahead_past_the_keep_days_and_the_horizon_leaves_the_feed_as_the_last_download_left_it()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        AbuseFeedWriteResult before = await service.WriteFeedAsync(CancellationToken.None);
        Assert.Equal(AbuseFeedWriteFailure.None, before.Failure);

        foreach (int days in new[] { 31, 400 - 31 })
        {
            _time.Advance(TimeSpan.FromDays(days));
            AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

            Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
            Assert.Equal([Ipv4File, Ipv6File], result.Files);
            Assert.Equal(1, result.IPv4Written);
            Assert.Equal(1, result.IPv6Written);
            Assert.Empty(result.EmptiedFiles);
            Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
            Assert.Equal(["2001:4860::8888"], Addresses(Ipv6File));
        }
    }

    [Fact]
    public async Task A_clock_behind_the_last_download_within_a_day_counts_the_keep_days_from_now()
    {
        // The second download was stamped by a clock 10 hours ahead of this one. Counting one day back from it would
        // drop 8.8.8.8, listed 20 hours before now
        KeepDays(1);
        Seed(Now.AddHours(-20).AddSeconds(-2), Now.AddHours(-20), Entry("8.8.8.8", 50));
        Seed(Now.AddHours(10).AddSeconds(-2), Now.AddHours(10), Entry("9.9.9.9", 50));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(2, result.IPv4Written);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], Addresses(Ipv4File));
    }

    [Fact]
    public async Task The_keep_days_are_clamped_for_the_cutoff_and_the_header()
    {
        Seed(Now.AddHours(-1), Now.AddHours(-1), Entry("8.8.8.8", 50));

        KeepDays(0);
        await Service().WriteFeedAsync(CancellationToken.None);
        Assert.Contains("# Kept: addresses listed within 1 day of the last download", File.ReadAllLines(Ipv4File));

        KeepDays(1000);
        await Service().WriteFeedAsync(CancellationToken.None);
        Assert.Contains("# Kept: addresses listed within 365 days of the last download", File.ReadAllLines(Ipv4File));
    }

    // ---------- 3. IPv6 off ----------

    [Fact]
    public async Task With_IPv6_off_the_IPv4_file_is_written_and_an_earlier_IPv6_file_is_emptied_and_named()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        File.Delete(Ipv4File);

        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
        Assert.Equal(1, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal([Ipv6File], result.EmptiedFiles);
        Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
        Assert.Equal(Utf8(Header("IPv6", 0)), File.ReadAllBytes(Ipv6File));
    }

    [Fact]
    public async Task With_IPv6_off_and_no_earlier_IPv6_file_nothing_is_named()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal([Ipv4File], result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.False(File.Exists(Ipv6File));
    }

    // ---------- 4. Exceptions ----------

    [Fact]
    public async Task An_exception_removes_and_counts_the_addresses_it_covers_in_its_own_family_only()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("8.8.4.4", 50), Entry("9.9.9.9", 50),
             Entry("2001:4860::8888", 50), Entry("2606:4700:4700::1111", 50));
        // ::808:808 is 8.8.8.8's number and 32.1.72.0/24 holds 2001:4860::'s first bytes: neither crosses family
        _ipinfo.Exceptions = Exceptions("8.8.0.0/16", "::808:808", "32.1.72.0/24");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(2, result.ExceptionRemovals);
        Assert.Equal(1, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Equal(["9.9.9.9"], Addresses(Ipv4File));
        Assert.Equal(["2001:4860::8888", "2606:4700:4700::1111"], Addresses(Ipv6File));
        string[] ipv4 = File.ReadAllLines(Ipv4File);
        Assert.Equal(["# Entries: 1", "# Exceptions: 2 addresses removed", "9.9.9.9"], ipv4[^3..]);
        Assert.DoesNotContain(File.ReadAllLines(Ipv6File), line => line.StartsWith("# Exceptions", StringComparison.Ordinal));

        _ipinfo.Exceptions = Exceptions("8.8.8.8");
        await Service().WriteFeedAsync(CancellationToken.None);
        Assert.Equal(["# Entries: 2", "# Exceptions: 1 address removed", "8.8.4.4", "9.9.9.9"], File.ReadAllLines(Ipv4File)[^4..]);
    }

    [Fact]
    public async Task A_family_the_exceptions_empty_gets_a_header_only_file_with_its_Exceptions_line_and_is_named()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50), Entry("2606:4700:4700::1111", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);

        _ipinfo.Exceptions = Exceptions("2000::/3");
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(2, result.ExceptionRemovals);
        Assert.Equal([Ipv6File], result.EmptiedFiles);
        Assert.Equal(Utf8(Header("IPv6", 0) + Lines("# Exceptions: 2 addresses removed")), File.ReadAllBytes(Ipv6File));

        // Both families emptied: both files header-only, the removals counted
        _ipinfo.Exceptions = Exceptions("2000::/3", "8.0.0.0/8");
        AbuseFeedWriteResult none = await service.WriteFeedAsync(CancellationToken.None);
        Assert.Equal(AbuseFeedWriteFailure.None, none.Failure);
        Assert.Equal([Ipv4File, Ipv6File], none.Files);
        Assert.Equal(0, none.IPv4Written);
        Assert.Equal(0, none.IPv6Written);
        Assert.Equal(3, none.ExceptionRemovals);
        Assert.Equal([Ipv4File, Ipv6File], none.EmptiedFiles);
        Assert.Equal(Utf8(Header("IPv4", 0) + Lines("# Exceptions: 1 address removed")), File.ReadAllBytes(Ipv4File));
    }

    [Fact]
    public async Task Every_address_excepted_with_no_feed_file_is_NothingToWrite_with_the_removals_and_no_folder_created()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("9.9.9.9", 50), Entry("2001:4860::8888", 50));
        _ipinfo.Exceptions = Exceptions("8.0.0.0/7", "2000::/3");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, result.Failure);
        Assert.Empty(result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal((0, 0, 0, 3), (result.IPv4Written, result.IPv6Written, result.LeftOut, result.ExceptionRemovals));
        Assert.False(Directory.Exists(Path.Combine(ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task With_IPv6_off_a_list_of_only_IPv6_addresses_and_no_feed_file_is_NothingToWrite_with_no_folder_created()
    {
        Seed(Generated, Downloaded, Entry("2001:4860::8888", 50), Entry("2606:4700:4700::1111", 50));
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NothingToWrite, result);
        Assert.False(Directory.Exists(Path.Combine(ExportFolder, "fortigate")));
    }

    // ---------- 5. The cap ----------

    [Fact]
    public async Task Over_131072_addresses_the_5_listed_longest_ago_are_left_out_and_counted()
    {
        Seed(Now.AddHours(-1), Now.AddHours(-1), Entry("9.9.9.9", 50));
        string seen = Unix(Now.AddHours(-1));
        string older = Unix(Now.AddDays(-2));
        string[] oldest = ["08000010", "08000100", "0800C803", "08010000", "0801FFFF"];
        Execute(InsertManySql(131_076, seen)
                + $"UPDATE addresses SET first_seen = {older}, last_seen = {older} WHERE ip IN ("
                + string.Join(", ", oldest.Select(hex => $"X'{hex}'")) + ");");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, result.IPv4Written);
        Assert.Equal(5, result.LeftOut);
        string[] lines = File.ReadAllLines(Ipv4File);
        Assert.Equal(["# Entries: 131072", "# Left out: 5 addresses over FortiOS's 131072 limit"],
                     lines.Where(line => line.StartsWith("# Entries", StringComparison.Ordinal)
                                         || line.StartsWith("# Left out", StringComparison.Ordinal)));
        string[] addresses = lines.Where(line => !line.StartsWith('#')).ToArray();
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, addresses.Length);
        Assert.Equal("8.0.0.1", addresses[0]);
        Assert.Equal("9.9.9.9", addresses[^1]);
        var written = new HashSet<string>(addresses, StringComparer.Ordinal);
        foreach (string hex in oldest)
            Assert.DoesNotContain(new IPAddress(Convert.FromHexString(hex)).ToString(), written);
        Assert.Contains("8.0.0.17", written);
        Assert.Contains("8.2.0.4", written);
        for (int i = 1; i < addresses.Length; i++)
            Assert.True(Bytes(addresses[i - 1]).AsSpan().SequenceCompareTo(Bytes(addresses[i])) < 0, addresses[i]);
    }

    // ---------- 6. The order under the cap ----------

    [Fact]
    public void Select_keeps_the_latest_listed_then_the_higher_score_then_the_later_report_then_the_lower_bytes()
    {
        DateTimeOffset seen = Now.AddHours(-1);
        AbuseListEntry[] entries =
        [
            ListEntry("1.1.1.1", seen.AddHours(-1), 100, Now.AddMinutes(-1)),    // listed longest ago: last, whatever its score
            ListEntry("8.8.8.8", seen, 50, null),
            ListEntry("9.9.9.10", seen, 50, null),                              // as 8.8.8.8, with higher bytes
            ListEntry("8.8.4.4", seen, 50, Now.AddHours(-2)),
            ListEntry("8.8.4.5", seen, 50, Now.AddHours(-1.5)),                 // a later report beats lower bytes
            ListEntry("9.9.9.9", seen, 60, null),                               // the higher score beats any report
            ListEntry("2001:4860::8888", seen, 100, Now),                       // the other family: never counted
        ];
        string[][] expected =
        [
            [],
            ["9.9.9.9"],
            ["8.8.4.5", "9.9.9.9"],
            ["8.8.4.4", "8.8.4.5", "9.9.9.9"],
            ["8.8.4.4", "8.8.4.5", "8.8.8.8", "9.9.9.9"],
            ["8.8.4.4", "8.8.4.5", "8.8.8.8", "9.9.9.9", "9.9.9.10"],
            ["1.1.1.1", "8.8.4.4", "8.8.4.5", "8.8.8.8", "9.9.9.9", "9.9.9.10"],
            ["1.1.1.1", "8.8.4.4", "8.8.4.5", "8.8.8.8", "9.9.9.9", "9.9.9.10"],
        ];

        for (int seed = 0; seed < 20; seed++)
        {
            AbuseListEntry[] shuffled = entries.ToArray();
            new Random(seed).Shuffle(shuffled);
            for (int cap = 0; cap < expected.Length; cap++)
            {
                AbuseFeedFamily family = AbuseIpDbFeedWriter.Select(shuffled, ExceptionList.Empty, ipv6: false, cap);

                Assert.False(family.IsIpv6);
                Assert.Equal(expected[cap], family.Addresses.Select(address => address.Text));
                Assert.Equal(Math.Max(0, 6 - cap), family.LeftOut);
                Assert.Equal(0, family.ExceptionRemovals);
            }
        }
    }

    [Fact]
    public void Select_removes_covered_addresses_before_the_cap_so_they_take_no_place()
    {
        DateTimeOffset seen = Now.AddHours(-1);
        AbuseListEntry[] entries =
        [
            ListEntry("9.9.9.9", seen, 100, null),
            ListEntry("8.8.8.8", seen, 90, null),
            ListEntry("1.1.1.1", seen, 10, null),
            ListEntry("2001:4860::8888", seen, 50, null),
            ListEntry("2606:4700:4700::1111", seen, 40, null),
        ];

        AbuseFeedFamily ipv4 = AbuseIpDbFeedWriter.Select(entries, Exceptions("9.9.9.9"), ipv6: false, cap: 2);
        AbuseFeedFamily ipv6 = AbuseIpDbFeedWriter.Select(entries, Exceptions("9.9.9.9"), ipv6: true, cap: 1);

        Assert.Equal(["1.1.1.1", "8.8.8.8"], ipv4.Addresses.Select(address => address.Text));
        Assert.Equal(1, ipv4.ExceptionRemovals);
        Assert.Equal(0, ipv4.LeftOut);
        Assert.True(ipv6.IsIpv6);
        Assert.Equal(["2001:4860::8888"], ipv6.Addresses.Select(address => address.Text));
        Assert.Equal(1, ipv6.LeftOut);
        Assert.Equal(0, ipv6.ExceptionRemovals);
        Assert.Throws<InvalidOperationException>(() => AbuseIpDbFeedWriter.Select(entries, ExceptionList.Unreadable, ipv6: false));
    }

    [Fact]
    public void SelectForReport_keeps_what_Select_keeps_with_no_exceptions_in_both_families()
    {
        DateTimeOffset seen = Now.AddHours(-1);
        AbuseListEntry[] entries =
        [
            ListEntry("1.1.1.1", seen.AddHours(-1), 100, Now.AddMinutes(-1)),
            ListEntry("8.8.8.8", seen, 50, null),
            ListEntry("9.9.9.10", seen, 50, null),
            ListEntry("8.8.4.4", seen, 50, Now.AddHours(-2)),
            ListEntry("8.8.4.5", seen, 50, Now.AddHours(-1.5)),
            ListEntry("9.9.9.9", seen, 60, null),
            ListEntry("2001:4860::1", seen.AddHours(-1), 100, Now),
            ListEntry("2001:4860::8888", seen, 40, null),
            ListEntry("2001:4860::8844", seen, 40, null),
            ListEntry("2606:4700:4700::1111", seen, 40, Now.AddHours(-3)),
            ListEntry("2606:4700:4700::1001", seen, 90, null),
        ];

        for (int seed = 0; seed < 20; seed++)
        {
            AbuseListEntry[] shuffled = entries.ToArray();
            new Random(seed).Shuffle(shuffled);
            foreach (bool ipv6 in new[] { false, true })
            {
                for (int cap = 0; cap <= 7; cap++)
                {
                    AbuseFeedFamily feed = AbuseIpDbFeedWriter.Select(shuffled, ExceptionList.Empty, ipv6, cap);
                    AbuseReportFamily report = AbuseIpDbFeedWriter.SelectForReport(shuffled, ipv6, cap);

                    Assert.Equal(ipv6, report.IsIpv6);
                    Assert.Equal(feed.Addresses.Select(address => address.Text), report.Addresses.Select(address => address.Text));
                    Assert.Equal(feed.LeftOut, report.LeftOut);
                }
            }
        }
    }

    // ---------- 7. Unreadable exceptions ----------

    [Fact]
    public async Task Unreadable_exceptions_write_nothing_and_create_no_folder_and_no_list()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.ExceptionsUnreadable, result);
        Assert.False(Directory.Exists(ExportFolder));
        Assert.False(File.Exists(ListPath));
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task Unreadable_exceptions_leave_an_existing_feed_file_as_it_is()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string ipv4 = Sha(Ipv4File);
        string ipv6 = Sha(Ipv6File);
        Seed(Now.AddMinutes(-5), Now.AddMinutes(-1), Entry("9.9.9.9", 50));
        string list = Sha(ListPath);

        _ipinfo.Exceptions = ExceptionList.Unreadable;
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.ExceptionsUnreadable, result);
        Assert.Equal(ipv4, Sha(Ipv4File));
        Assert.Equal(ipv6, Sha(Ipv6File));
        Assert.Equal(list, Sha(ListPath));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    // ---------- 8. List states ----------

    [Fact]
    public async Task No_list_file_is_NoDownload_with_no_folder_and_no_list_created()
    {
        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NoDownload, result);
        Assert.False(Directory.Exists(ExportFolder));
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task Existing_files_with_no_list_file_are_NoDownload_keep_their_bytes_and_create_no_list()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ipv4File)!);
        File.WriteAllText(Ipv4File, "# an earlier feed" + Environment.NewLine + "8.8.8.8" + Environment.NewLine);
        File.WriteAllText(Ipv6File, "# an earlier feed" + Environment.NewLine + "2001:4860::8888" + Environment.NewLine);
        string ipv4 = Sha(Ipv4File);
        string ipv6 = Sha(Ipv6File);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NoDownload, result);
        Assert.Equal(ipv4, Sha(Ipv4File));
        Assert.Equal(ipv6, Sha(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_cleared_list_is_NoDownload_and_both_existing_files_keep_their_bytes()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string ipv4 = Sha(Ipv4File);
        string ipv6 = Sha(Ipv6File);
        Assert.Equal(AbuseIpDbFailure.None, await service.ClearListAsync(CancellationToken.None));
        Assert.False(File.Exists(ListPath));

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NoDownload, result);
        Assert.Equal(ipv4, Sha(Ipv4File));
        Assert.Equal(ipv6, Sha(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_list_with_only_a_download_attempt_is_NoDownload_with_no_folder_created()
    {
        Assert.Equal(AbuseListState.Usable, new AbuseIpDbList(ListPath).RecordAttempt(Now.AddMinutes(-5)));
        string list = Sha(ListPath);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NoDownload, result);
        Assert.False(Directory.Exists(ExportFolder));
        Assert.Equal(list, Sha(ListPath));
    }

    [Fact]
    public async Task A_list_with_rows_but_no_readable_download_time_is_NoDownload_with_no_folder_created()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        Execute("UPDATE metadata SET value = 'not a time' WHERE key = 'last_download_at';");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NoDownload, result);
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task A_clock_25_hours_behind_the_last_download_is_ClockWrong_with_no_folder_created()
    {
        // The list was downloaded 25 hours after this computer's now
        Seed(Now.AddHours(25).AddSeconds(-2), Now.AddHours(25), Entry("8.8.8.8", 50));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.ClockWrong, result);
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task A_clock_25_hours_behind_the_last_download_leaves_existing_files_as_they_are()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string ipv4 = Sha(Ipv4File);
        string ipv6 = Sha(Ipv6File);
        Seed(Now.AddHours(25).AddSeconds(-2), Now.AddHours(25), Entry("9.9.9.9", 50));

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.ClockWrong, result);
        Assert.Equal(ipv4, Sha(Ipv4File));
        Assert.Equal(ipv6, Sha(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    [Fact]
    public async Task A_clock_23_hours_behind_the_last_download_still_writes()
    {
        Seed(Now.AddHours(23).AddSeconds(-2), Now.AddHours(23), Entry("8.8.8.8", 50));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
    }

    [Fact]
    public async Task One_write_reads_the_settings_once()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        int before = _ipinfo.SettingsReads;

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(1, _ipinfo.SettingsReads - before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_or_unreadable_list_is_reported_unchanged_with_no_folder_created(bool newer)
    {
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        string before = Sha(ListPath);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(newer ? AbuseFeedWriteFailure.ListNewer : AbuseFeedWriteFailure.ListUnreadable, result);
        Assert.Equal(before, Sha(ListPath));
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task A_list_that_cant_be_opened_is_a_storage_error_with_no_folder_created()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.None);
        AbuseFeedWriteResult result;
        try
        {
            result = await Service().WriteFeedAsync(CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }

        AssertFailure(AbuseFeedWriteFailure.StorageError, result);
        Assert.False(Directory.Exists(ExportFolder));
    }

    // ---------- 9. The Fortigate switches ----------

    [Fact]
    public async Task The_Fortigate_switches_are_not_read_so_both_files_are_written_with_them_all_off()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        _ipinfo.Settings = _ipinfo.Settings with
        {
            FortigateEnabled = false, FortigateIncludeIPv4 = false, FortigateIncludeIPv6 = false, FortigateSplitByFamily = false,
        };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
    }

    [Fact]
    public async Task The_feed_goes_into_the_configured_export_folder()
    {
        string folder = Path.Combine(_temp.Folder, "chosen");
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = folder };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal([Path.Combine(folder, "fortigate", "AbuseIPDB_rolling_ipv4.txt")], result.Files);
        Assert.False(Directory.Exists(ExportFolder));
    }

    // ---------- 10. Replacing and failing ----------

    [Fact]
    public async Task An_existing_feed_file_is_replaced_and_no_tmp_file_is_left()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        Seed(Now.AddMinutes(-2), Now.AddMinutes(-1), Entry("9.9.9.9", 50));

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], Addresses(Ipv4File));
        Assert.Contains("# Last download: 2026-10-19 07:59:00 UTC (list generated 2026-10-19 07:58:00 UTC)",
                        File.ReadAllLines(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
    }

    [Fact]
    public async Task A_feed_path_that_is_a_folder_fails_the_write_and_leaves_no_tmp_file()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        Directory.CreateDirectory(Ipv4File);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // The IPv4 file fails first, so nothing was written before it and the IPv6 file is never tried
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv4File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Equal((0, 0, 0, 0), (result.IPv4Written, result.IPv6Written, result.LeftOut, result.ExceptionRemovals));
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
        Assert.True(Directory.Exists(Ipv4File));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_IPv4_write_names_the_old_IPv6_file_it_never_tried(bool ipv4PathIsAFolder)
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        WriteOldFeed(Ipv6File, "2606:4700:4700::1111");
        string beforeV6 = Sha(Ipv6File);
        if (ipv4PathIsAFolder)
        {
            Directory.CreateDirectory(Ipv4File);
        }
        else
        {
            // A folder where the temporary file should go makes the write fail; the move never runs
            WriteOldFeed(Ipv4File, "1.1.1.1");
            Directory.CreateDirectory(Ipv4File + ".tmp");
        }

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // The IPv4 file fails first, so the IPv6 file is never tried: FortiGate keeps reading its old copy, and the
        // failed file's earlier copy too, when there is one
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv4File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Equal(ipv4PathIsAFolder ? [Ipv6File] : [Ipv4File, Ipv6File], result.OldFilesLeft);
        Assert.Equal(beforeV6, Sha(Ipv6File));
    }

    [Fact]
    public async Task A_failed_IPv4_write_names_the_old_IPv6_file_it_would_have_emptied()
    {
        // The list has no IPv6 address, so the IPv6 file is planned header-only after the IPv4 one
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        WriteOldFeed(Ipv4File, "1.1.1.1");
        WriteOldFeed(Ipv6File, "2606:4700:4700::1111");
        string beforeV4 = Sha(Ipv4File);
        string beforeV6 = Sha(Ipv6File);
        // A folder where the temporary file should go makes the IPv4 write fail; the move never runs
        Directory.CreateDirectory(Ipv4File + ".tmp");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // A header-only family never tried is left as it was too, so FortiGate keeps reading its old list
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv4File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Equal([Ipv4File, Ipv6File], result.OldFilesLeft);
        Assert.Equal(beforeV4, Sha(Ipv4File));
        Assert.Equal(beforeV6, Sha(Ipv6File));
    }

    [Fact]
    public async Task A_failed_write_with_no_old_file_left_names_none()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        Directory.CreateDirectory(Ipv6File + ".tmp");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // The IPv6 file had no earlier copy, and the IPv4 file was written before it, so no old file is left
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv6File, result.FailedFile);
        Assert.Equal([Ipv4File], result.Files);
        Assert.Null(result.OldFilesLeft);
    }

    [Fact]
    public async Task A_failed_IPv6_write_keeps_the_IPv4_file_just_written_and_leaves_no_tmp_file()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50), Entry("2606:4700:4700::1111", 50));
        _ipinfo.Exceptions = Exceptions("2606:4700:4700::/48");
        Directory.CreateDirectory(Ipv6File);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // The result names what was written before the failure, and counts both families' removals: the IPv6
        // family's exception removed an address although its file wasn't written
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv6File, result.FailedFile);
        Assert.Equal([Ipv4File], result.Files);
        Assert.Equal(1, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(0, result.LeftOut);
        Assert.Equal(1, result.ExceptionRemovals);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.True(Directory.Exists(Ipv6File));
    }

    [Fact]
    public async Task A_failed_IPv6_write_with_the_IPv4_family_skipped_names_the_IPv6_file()
    {
        Seed(Generated, Downloaded, Entry("2001:4860::8888", 50));
        Directory.CreateDirectory(Ipv6File);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // IPv4 has no address and no file, so the IPv6 file is the first one tried and the one that failed
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv6File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(["AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.True(Directory.Exists(Ipv6File));
    }

    [Fact]
    public async Task Settings_that_throw_give_WriteFailed_with_no_folder_created_and_nothing_escapes()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        _ipinfo.SettingsFailure = new InvalidOperationException("The settings failed.");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.WriteFailed, result);
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task An_export_folder_that_is_a_file_is_FolderUnavailable_and_keeps_both_families_counts()
    {
        string file = Path.Combine(_temp.Folder, "not-a-folder");
        File.WriteAllText(file, "x");
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("9.9.9.9", 50), Entry("2001:4860::8888", 50));
        _ipinfo.Exceptions = Exceptions("9.9.9.0/24", "2001:4860::/32");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = file };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // Chosen before the folder failed, so the removals of both families are kept; nothing was written
        Assert.Equal(AbuseFeedWriteFailure.FolderUnavailable, result.Failure);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(0, result.LeftOut);
        Assert.Equal(2, result.ExceptionRemovals);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal("x", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_last_download_at_the_earliest_time_is_not_an_unexpected_failure()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        Execute("UPDATE metadata SET value = '-62135596800' WHERE key = 'last_download_at';");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        // The clock guard subtracts the two times, so the earliest time a list can hold doesn't overflow it
        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
        Assert.Contains("# Last download: 0001-01-01 00:00:00 UTC (list generated 2026-10-19 07:29:58 UTC)",
                        File.ReadAllLines(Ipv4File));
    }

    [Theory]
    [InlineData(0, AbuseFeedWriteFailure.None)]
    [InlineData(1, AbuseFeedWriteFailure.ClockWrong)]
    public async Task A_clock_exactly_24_hours_behind_the_last_download_writes_and_a_second_more_is_ClockWrong(
        int extraSeconds, AbuseFeedWriteFailure expected)
    {
        DateTimeOffset downloaded = Now.AddHours(24).AddSeconds(extraSeconds);
        Seed(downloaded.AddSeconds(-2), downloaded, Entry("8.8.8.8", 50));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(expected, result.Failure);
        Assert.Equal(expected == AbuseFeedWriteFailure.None, File.Exists(Ipv4File));
    }

    // ---------- 11. Cancellation and the gate ----------

    [Fact]
    public async Task An_already_cancelled_write_throws_and_writes_nothing()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().WriteFeedAsync(cts.Token));

        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task A_write_pressed_during_a_download_waits_for_it_and_writes_the_list_it_merged()
    {
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new HeldHandler(async request =>
        {
            entered.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ListBody(Now.AddHours(-1), "8.8.8.8", "2001:4860::8888"), Encoding.UTF8,
                                            "application/json"),
                RequestMessage = request,
            };
        });
        using var http = new HttpClient(handler);
        AbuseIpDbService service = Service(http);

        Task<AbuseDownloadResult> download = service.DownloadBlacklistAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<AbuseFeedWriteResult> write = service.WriteFeedAsync(CancellationToken.None);
        await Task.Delay(200);
        // The download holds the gate, so the write can't have read the list or written anything yet
        Assert.False(write.IsCompleted);
        Assert.False(Directory.Exists(ExportFolder));

        release.SetResult();
        Assert.Equal(AbuseIpDbFailure.None, (await download.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        AbuseFeedWriteResult result = await write.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(["8.8.8.8"], Addresses(Ipv4File));
        Assert.Equal(["2001:4860::8888"], Addresses(Ipv6File));
        Assert.Equal(1, handler.Calls);
    }

    // ---------- 12. Empty feed ----------

    [Fact]
    public async Task Empty_replaces_both_existing_files_with_header_only_ones_and_leaves_the_list_and_the_key_alone()
    {
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string list = Sha(ListPath);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.Files);
        Assert.Equal([Ipv4File, Ipv6File], result.EmptiedFiles);
        Assert.Equal((0, 0, 0, 0), (result.IPv4Written, result.IPv6Written, result.LeftOut, result.ExceptionRemovals));
        Assert.Equal(Utf8(EmptiedHeader("IPv4")), File.ReadAllBytes(Ipv4File));
        Assert.Equal(Utf8(EmptiedHeader("IPv6")), File.ReadAllBytes(Ipv6File));
        Assert.DoesNotContain(File.ReadAllLines(Ipv4File), line => line.StartsWith("# Last download:", StringComparison.Ordinal));
        Assert.Equal(list, Sha(ListPath));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task Empty_with_only_the_IPv4_file_empties_it_alone_and_makes_no_IPv6_file()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File], result.Files);
        Assert.Equal([Ipv4File], result.EmptiedFiles);
        Assert.Equal(Utf8(EmptiedHeader("IPv4")), File.ReadAllBytes(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
    }

    [Fact]
    public async Task Empty_with_neither_file_is_NothingToWrite_and_creates_no_folder_and_no_list()
    {
        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.NothingToWrite, result);
        Assert.False(Directory.Exists(ExportFolder));
        Assert.False(File.Exists(ListPath));
    }

    [Theory]
    [InlineData("exceptions")]
    [InlineData("newer")]
    [InlineData("unreadable")]
    public async Task Empty_reads_neither_the_exceptions_nor_the_list_so_it_empties_while_they_cant_be_read(string broken)
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        WriteOldFeed(Ipv6File, "2001:4860::8888");
        if (broken == "exceptions")
            _ipinfo.Exceptions = ExceptionList.Unreadable;
        else if (broken == "newer")
            WriteNewerList();
        else
            WriteUnreadableList();
        string? list = File.Exists(ListPath) ? Sha(ListPath) : null;

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.EmptiedFiles);
        Assert.Empty(Addresses(Ipv4File));
        Assert.Empty(Addresses(Ipv6File));
        Assert.Equal(list, File.Exists(ListPath) ? Sha(ListPath) : null);
    }

    [Fact]
    public async Task Empty_with_IPv6_off_empties_an_existing_IPv6_file_too()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        WriteOldFeed(Ipv6File, "2001:4860::8888");
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File, Ipv6File], result.EmptiedFiles);
        Assert.Equal(Utf8(EmptiedHeader("IPv6")), File.ReadAllBytes(Ipv6File));
    }

    [Fact]
    public async Task Empty_over_a_feed_path_that_is_a_folder_fails_names_the_file_emptied_before_it_and_leaves_no_tmp_file()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        Directory.CreateDirectory(Ipv6File);

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv6File, result.FailedFile);
        Assert.Equal([Ipv4File], result.Files);
        Assert.Equal([Ipv4File], result.EmptiedFiles);
        Assert.Equal((0, 0, 0, 0), (result.IPv4Written, result.IPv6Written, result.LeftOut, result.ExceptionRemovals));
        Assert.Empty(Addresses(Ipv4File));
        Assert.True(Directory.Exists(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    [Fact]
    public async Task Empty_over_an_IPv4_feed_path_that_is_a_folder_fails_first_and_names_it()
    {
        Directory.CreateDirectory(Ipv4File);
        WriteOldFeed(Ipv6File, "2001:4860::8888");
        string before = Sha(Ipv6File);

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        // The IPv4 path fails first, so nothing was emptied and the IPv6 file keeps its bytes
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv4File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal([Ipv6File], result.OldFilesLeft);
        Assert.Equal(before, Sha(Ipv6File));
        Assert.True(Directory.Exists(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt", "AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    [Fact]
    public async Task A_failed_empty_names_the_old_files_it_left()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        WriteOldFeed(Ipv6File, "2001:4860::8888");
        string beforeV4 = Sha(Ipv4File);
        string beforeV6 = Sha(Ipv6File);
        // A folder where the temporary file should go makes the IPv4 write fail; the move never runs
        Directory.CreateDirectory(Ipv4File + ".tmp");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv4File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Equal([Ipv4File, Ipv6File], result.OldFilesLeft);
        Assert.Equal(beforeV4, Sha(Ipv4File));
        Assert.Equal(beforeV6, Sha(Ipv6File));
    }

    [Fact]
    public async Task Empty_with_no_IPv4_file_over_an_IPv6_feed_path_that_is_a_folder_names_the_IPv6_file()
    {
        Directory.CreateDirectory(Ipv6File);

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        // No IPv4 file exists, so the IPv6 path is the first one tried and the one that failed
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ipv6File, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.True(Directory.Exists(Ipv6File));
        Assert.Equal(["AbuseIPDB_rolling_ipv6.txt"], FortigateNames());
    }

    [Fact]
    public async Task Empty_with_settings_that_throw_gives_WriteFailed_and_nothing_escapes()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        string before = Sha(Ipv4File);
        _ipinfo.SettingsFailure = new InvalidOperationException("The settings failed.");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        AssertFailure(AbuseFeedWriteFailure.WriteFailed, result);
        Assert.Equal(before, Sha(Ipv4File));
    }

    [Fact]
    public async Task An_already_cancelled_Empty_throws_and_writes_nothing()
    {
        WriteOldFeed(Ipv4File, "8.8.8.8");
        string before = Sha(Ipv4File);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().EmptyFeedAsync(cts.Token));

        Assert.Equal(before, Sha(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
    }

    [Fact]
    public async Task Empty_pressed_during_a_download_waits_for_it()
    {
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
        WriteOldFeed(Ipv4File, "8.8.8.8");
        string old = Sha(Ipv4File);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new HeldHandler(async request =>
        {
            entered.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ListBody(Now.AddHours(-1), "9.9.9.9"), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        });
        using var http = new HttpClient(handler);
        AbuseIpDbService service = Service(http);

        Task<AbuseDownloadResult> download = service.DownloadBlacklistAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<AbuseFeedWriteResult> empty = service.EmptyFeedAsync(CancellationToken.None);
        await Task.Delay(200);
        // The download holds the gate, so the file hasn't been touched yet
        Assert.False(empty.IsCompleted);
        Assert.Equal(old, Sha(Ipv4File));

        release.SetResult();
        Assert.Equal(AbuseIpDbFailure.None, (await download.WaitAsync(TimeSpan.FromSeconds(10))).Failure);
        AbuseFeedWriteResult result = await empty.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ipv4File], result.EmptiedFiles);
        Assert.Empty(Addresses(Ipv4File));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], FortigateNames());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_Write_feed_after_Empty_writes_the_list_addresses_again()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string ipv4 = Sha(Ipv4File);
        string ipv6 = Sha(Ipv6File);
        Assert.Equal(AbuseFeedWriteFailure.None, (await service.EmptyFeedAsync(CancellationToken.None)).Failure);

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(ipv4, Sha(Ipv4File));
        Assert.Equal(ipv6, Sha(Ipv6File));
    }

    [Fact]
    public async Task An_emptied_file_says_it_was_emptied_by_hand_in_place_of_Kept_and_Write_feed_gives_Kept_back()
    {
        const string emptiedLine = "# Emptied by hand: no address is listed until the feed is written again";
        Seed(Generated, Downloaded, Entry("8.8.8.8", 50));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);

        await service.EmptyFeedAsync(CancellationToken.None);
        string[] emptied = File.ReadAllLines(Ipv4File);

        Assert.Equal(emptiedLine, emptied[3]);
        Assert.Contains("# Entries: 0", emptied);
        Assert.DoesNotContain(emptied, line => line.StartsWith("# Kept:", StringComparison.Ordinal));

        await service.WriteFeedAsync(CancellationToken.None);
        string[] written = File.ReadAllLines(Ipv4File);

        Assert.Equal("# Kept: addresses listed within 30 days of the last download", written[3]);
        Assert.DoesNotContain(emptiedLine, written);
        Assert.Equal(Utf8(Header("IPv4", 1) + Lines("8.8.8.8")), File.ReadAllBytes(Ipv4File));
    }

    // ---------- Helpers ----------

    /// <summary>A feed file an earlier write left: a comment line and the addresses.</summary>
    private static void WriteOldFeed(string path, params string[] addresses)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Lines(["# an earlier feed", .. addresses]));
    }

    private AbuseIpDbService Service() => Service(_http);

    private AbuseIpDbService Service(HttpClient http) => new(http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, new FakeAbuseAccounting());

    private void KeepDays(int days) => _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = days };

    /// <summary>Merges a blacklist generated and downloaded at the given times into the service's list file.</summary>
    private void Seed(DateTimeOffset generatedAt, DateTimeOffset downloadedAt, params BlacklistEntry[] entries)
    {
        ListRead<ListMerge> merged = new AbuseIpDbList(ListPath)
            .Merge(new Blacklist(generatedAt, entries, 0, 0, 0), downloadedAt, downloadedAt.AddDays(-30), CancellationToken.None);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    /// <summary>
    /// The header the tests' list gives at <see cref="Now"/>; without a download, no <c>Last download:</c> line.
    /// <see cref="EmptiedHeader"/> is Empty feed's.
    /// </summary>
    private static string Header(string family, int entries, bool downloaded = true) =>
        HeaderLines(family, entries, downloaded, "# Kept: addresses listed within 30 days of the last download");

    /// <summary>The header of a file Empty feed wrote: no download line, and the Emptied line in place of Kept.</summary>
    private static string EmptiedHeader(string family) =>
        HeaderLines(family, 0, downloaded: false, "# Emptied by hand: no address is listed until the feed is written again");

    private static string HeaderLines(string family, int entries, bool downloaded, string keptLine)
    {
        string entriesText = entries.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "# PoorMansFeed - Fortigate Threat Feed",
            "# Source: AbuseIPDB blacklist (https://www.abuseipdb.com), rolling list",
            "# For your own firewalls only: AbuseIPDB's terms don't allow this data to be republished.",
            keptLine,
            $"# Address Family: {family}",
            "# Generated: 2026-10-19 08:00:00 UTC",
        };
        if (downloaded)
            lines.Add("# Last download: 2026-10-19 07:30:00 UTC (list generated 2026-10-19 07:29:58 UTC)");
        lines.Add($"# Entries: {entriesText}");
        return Lines([.. lines]);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    private static string[] Addresses(string file) => File.ReadAllLines(file).Where(line => !line.StartsWith('#')).ToArray();

    private string[] FortigateNames() =>
        Directory.GetFileSystemEntries(Path.Combine(ExportFolder, "fortigate")).Select(Path.GetFileName)
                 .Order(StringComparer.Ordinal).ToArray()!;

    private static void AssertFailure(AbuseFeedWriteFailure failure, AbuseFeedWriteResult result)
    {
        Assert.Equal(failure, result.Failure);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(0, result.LeftOut);
        Assert.Equal(0, result.ExceptionRemovals);
        Assert.Empty(result.EmptiedFiles);
        // A refusal or an unexpected failure names no file: only a file write that failed does
        Assert.Null(result.FailedFile);
    }

    private static ExceptionList Exceptions(params string[] networks) =>
        ExceptionList.Create(networks.Select(network =>
        {
            Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
            return new ExceptionEntry(block, null);
        }));

    private static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }

    private static PublicAddress Address(string text)
    {
        Assert.True(PublicAddress.TryCreate(Bytes(text), out PublicAddress? value));
        return value;
    }

    private static BlacklistEntry Entry(string address, int score, string? country = null, DateTimeOffset? reported = null) =>
        new(Address(address), score, reported, country);

    private static AbuseListEntry ListEntry(string address, DateTimeOffset lastSeen, int score, DateTimeOffset? reported) =>
        new(Address(address), lastSeen.AddDays(-1), lastSeen, score, null, reported);

    private static string Unix(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Inserts count rows of 8.x.x.x addresses (8.0.0.1 upwards), all last seen at the given Unix seconds.</summary>
    private static string InsertManySql(int count, string seen) => $"""
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count})
        INSERT INTO addresses SELECT unhex(printf('08%06X', i)), {seen}, {seen}, 50, NULL, NULL FROM n WHERE true
        ON CONFLICT(ip) DO NOTHING;
        """;

    private void Execute(string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A list file a newer version wrote: its metadata says schema version 2.</summary>
    private void WriteNewerList()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE addresses (ip BLOB PRIMARY KEY, seen INTEGER NOT NULL);
            INSERT INTO metadata (key, value) VALUES ('schema_version', '2');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A list file that isn't a database.</summary>
    private void WriteUnreadableList()
    {
        byte[] bytes = new byte[4096];
        new Random(4711).NextBytes(bytes);
        File.WriteAllBytes(ListPath, bytes);
    }

    private static string ListBody(DateTimeOffset generatedAt, params string[] addresses) =>
        "{\"meta\":{\"generatedAt\":\"" + generatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        + "\"},\"data\":[" + string.Join(",", addresses.Select(address =>
            "{\"ipAddress\":\"" + address + "\",\"abuseConfidenceScore\":100,\"lastReportedAt\":\"2026-10-19T06:00:00+00:00\"}"))
        + "]}";

    /// <summary>Answers each request with an async function, so a test can hold a download inside its request.</summary>
    private sealed class HeldHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return respond(request);
        }
    }
}
