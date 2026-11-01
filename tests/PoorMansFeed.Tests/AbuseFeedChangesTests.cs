using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The rolling feed's changed entries: each file moved into place against the one it replaced.</summary>
public sealed class AbuseFeedChangesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly ServiceTempFolder _temp = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public AbuseFeedChangesTests()
    {
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

    private string Fg4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: false);

    private string Fg6 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: true);

    private string Ms4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: false);

    private string Ms6 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: true);

    [Fact]
    public async Task A_first_write_adds_every_address_in_its_own_family_one_change_per_file_in_write_order()
    {
        Seed(Generated, Downloaded, Entry("9.9.9.9"), Entry("2001:4860::8888"), Entry("8.8.8.8"));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal(result.Files, result.FeedChanges.Select(change => change.Path));
        Assert.Equal(2, result.FeedChanges.Count);
        AssertChange(result.FeedChanges[0], Fg4, added4: ["8.8.8.8", "9.9.9.9"]);
        AssertChange(result.FeedChanges[1], Fg6, added6: ["2001:4860::8888"]);
    }

    [Fact]
    public async Task The_same_write_again_changes_no_entry_and_no_byte()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string ipv4 = Sha(Fg4);
        string ipv6 = Sha(Fg6);

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal([Fg4, Fg6], result.FeedChanges.Select(change => change.Path));
        Assert.All(result.FeedChanges, change => Assert.True(change.IsEmpty));
        Assert.Equal(ipv4, Sha(Fg4));
        Assert.Equal(ipv6, Sha(Fg6));
    }

    [Fact]
    public async Task A_download_that_drops_one_address_and_adds_another_gives_exactly_those()
    {
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 1 };
        Seed(Generated.AddDays(-2), Downloaded.AddDays(-2), Entry("8.8.8.8"), Entry("9.9.9.9"));
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseFeedWriteFailure.None, (await service.WriteFeedAsync(CancellationToken.None)).Failure);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], Addresses(Fg4));

        // 9.9.9.9 was last listed two days before this download, past the one keep day
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("1.1.1.1"));
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        AssertChange(Assert.Single(result.FeedChanges), Fg4, added4: ["1.1.1.1"], removed4: ["9.9.9.9"]);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], Addresses(Fg4));
    }

    [Fact]
    public async Task An_old_file_edited_by_hand_for_addresses_still_written_counts_no_change()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2a00:1450:4001::200e"));
        Directory.CreateDirectory(Path.GetDirectoryName(Fg4)!);
        // A byte order mark, CRLF, a /32 and upper-case IPv6 all name the same entries
        File.WriteAllBytes(Fg4, [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("# old\r\n8.8.8.8/32\r\n9.9.9.9\r\n")]);
        File.WriteAllText(Fg6, "# old\r\n2A00:1450:4001::200E\r\n");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal([Fg4, Fg6], result.FeedChanges.Select(change => change.Path));
        Assert.All(result.FeedChanges, change => Assert.True(change.IsEmpty));
        Assert.Equal(["2a00:1450:4001::200e"], Addresses(Fg6));
    }

    [Fact]
    public async Task With_both_types_FortiGate_comes_first_and_each_copy_is_compared_with_its_own_old_file()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2001:4860::8888"));
        Directory.CreateDirectory(Path.GetDirectoryName(Fg4)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Ms4)!);
        File.WriteAllText(Fg4, "9.9.9.9\n");
        File.WriteAllText(Ms4, "8.8.8.8\n1.0.0.1\n");
        File.WriteAllText(Ms6, "2001:4860::8888\n");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.FeedChanges.Select(change => change.Path));
        Assert.Equal(result.Files, result.FeedChanges.Select(change => change.Path));
        AssertChange(result.FeedChanges[0], Fg4, added4: ["8.8.8.8"]);
        AssertChange(result.FeedChanges[1], Fg6, added6: ["2001:4860::8888"]);
        AssertChange(result.FeedChanges[2], Ms4, added4: ["9.9.9.9"], removed4: ["1.0.0.1"]);
        AssertChange(result.FeedChanges[3], Ms6);
    }

    [Fact]
    public async Task A_family_the_exceptions_empty_while_its_file_exists_is_header_only_with_every_old_address_removed()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"), Entry("2606:4700:4700::1111"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);

        _ipinfo.Exceptions = Exceptions("2001:4860::/32", "2606:4700::/32");
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg6], result.EmptiedFiles);
        Assert.Empty(Addresses(Fg6));
        Assert.NotNull(result.FeedChanges);
        Assert.Equal(2, result.FeedChanges.Count);
        AssertChange(result.FeedChanges[0], Fg4);
        AssertChange(result.FeedChanges[1], Fg6, removed6: ["2001:4860::8888", "2606:4700:4700::1111"]);
    }

    [Fact]
    public async Task Empty_feed_removes_every_old_address_from_each_file_it_writes_whatever_is_ticked()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        File.WriteAllText(Ms4, "1.0.0.1\n");

        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };
        AbuseFeedWriteResult result = await service.EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.FeedChanges.Select(change => change.Path));
        AssertChange(result.FeedChanges[0], Fg4, removed4: ["8.8.8.8", "9.9.9.9"]);
        AssertChange(result.FeedChanges[1], Fg6, removed6: ["2001:4860::8888"]);
        AssertChange(result.FeedChanges[2], Ms4, removed4: ["1.0.0.1"]);
        AssertChange(result.FeedChanges[3], Ms6, removed6: ["2001:4860::8888"]);
    }

    [Fact]
    public async Task Empty_feed_that_fails_on_the_second_file_names_only_the_first_files_change()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        File.Delete(Fg6);
        Directory.CreateDirectory(Fg6);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg6, result.FailedFile);
        Assert.NotNull(result.FeedChanges);
        AssertChange(Assert.Single(result.FeedChanges), Fg4, removed4: ["8.8.8.8"]);
    }

    [Fact]
    public async Task A_failure_on_the_second_planned_file_keeps_exactly_the_first_files_change()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        File.Delete(Fg6);
        Directory.CreateDirectory(Fg6);
        Seed(Generated, Downloaded, Entry("1.1.1.1"));

        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg6, result.FailedFile);
        Assert.Equal([Fg4], result.Files);
        Assert.NotNull(result.FeedChanges);
        AssertChange(Assert.Single(result.FeedChanges), Fg4, added4: ["1.1.1.1"]);
    }

    [Fact]
    public async Task A_failure_on_the_first_planned_file_gives_no_changes()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        Directory.CreateDirectory(Fg4);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg4, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Null(result.FeedChanges);
    }

    [Fact]
    public async Task No_feed_type_gives_no_changes()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NoFeedType, result.Failure);
        Assert.Null(result.FeedChanges);
    }

    [Fact]
    public async Task Unreadable_exceptions_give_no_changes_and_leave_the_files()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(CancellationToken.None);
        string before = Sha(Fg4);
        Seed(Generated, Downloaded, Entry("9.9.9.9"));

        _ipinfo.Exceptions = ExceptionList.Unreadable;
        AbuseFeedWriteResult result = await service.WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.ExceptionsUnreadable, result.Failure);
        Assert.Null(result.FeedChanges);
        Assert.Equal(before, Sha(Fg4));
    }

    [Fact]
    public async Task No_download_gives_no_changes()
    {
        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NoDownload, result.Failure);
        Assert.Null(result.FeedChanges);
    }

    [Fact]
    public async Task A_clock_behind_the_download_gives_no_changes()
    {
        Seed(Now.AddHours(25).AddSeconds(-2), Now.AddHours(25), Entry("8.8.8.8"));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.ClockWrong, result.Failure);
        Assert.Null(result.FeedChanges);
    }

    [Fact]
    public async Task Nothing_to_write_gives_no_changes_from_Write_feed_and_Empty_feed()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.Exceptions = Exceptions("8.8.8.8");
        AbuseIpDbService service = Service();

        AbuseFeedWriteResult written = await service.WriteFeedAsync(CancellationToken.None);
        AbuseFeedWriteResult emptied = await service.EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, written.Failure);
        Assert.Null(written.FeedChanges);
        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, emptied.Failure);
        Assert.Null(emptied.FeedChanges);
    }

    [Fact]
    public async Task Each_file_carries_its_type_and_family_and_whether_an_old_file_was_read()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();

        AbuseFeedWriteResult first = await service.WriteFeedAsync(CancellationToken.None);
        AbuseFeedWriteResult second = await service.WriteFeedAsync(CancellationToken.None);
        AbuseFeedWriteResult retired = await service.EmptyFeedAsync(CancellationToken.None);

        foreach (AbuseFeedWriteResult result in new[] { first, second, retired })
        {
            Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
            Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.FeedChanges!.Select(change => change.Path));
            Assert.Equal([FeedType.Fortigate, FeedType.Fortigate, FeedType.ModSecurity, FeedType.ModSecurity],
                         result.FeedChanges!.Select(change => change.Type));
            Assert.Equal([FeedFamilies.IPv4, FeedFamilies.IPv6, FeedFamilies.IPv4, FeedFamilies.IPv6],
                         result.FeedChanges!.Select(change => change.Families));
        }
        Assert.All(first.FeedChanges!, change => Assert.Equal(FeedFileState.Missing, change.OldFile));
        Assert.All(second.FeedChanges!, change => Assert.Equal(FeedFileState.Read, change.OldFile));
        Assert.All(retired.FeedChanges!, change => Assert.Equal(FeedFileState.Read, change.OldFile));
    }

    [Fact]
    public async Task With_IPv6_off_only_the_IPv4_file_is_written_and_compared()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        AssertChange(Assert.Single(result.FeedChanges), Fg4, added4: ["8.8.8.8"]);
        Assert.False(File.Exists(Fg6));
    }

    private AbuseIpDbService Service() =>
        new(_http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, new FakeAbuseAccounting());

    private void Seed(DateTimeOffset generatedAt, DateTimeOffset downloadedAt, params BlacklistEntry[] entries)
    {
        ListRead<ListMerge> merged = new AbuseIpDbList(ListPath)
            .Merge(new Blacklist(generatedAt, entries, 0, 0, 0), downloadedAt, downloadedAt.AddDays(-30), CancellationToken.None);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    private static void AssertChange(FeedFileChange change, string path, string[]? added4 = null, string[]? removed4 = null,
                                     string[]? added6 = null, string[]? removed6 = null)
    {
        Assert.Equal(path, change.Path);
        Assert.Equal(added4 ?? [], change.IPv4.Added);
        Assert.Equal(removed4 ?? [], change.IPv4.Removed);
        Assert.Equal(added6 ?? [], change.IPv6.Added);
        Assert.Equal(removed6 ?? [], change.IPv6.Removed);
    }

    private static string[] Addresses(string file) => File.ReadAllLines(file).Where(line => !line.StartsWith('#')).ToArray();

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static ExceptionList Exceptions(params string[] networks) =>
        ExceptionList.Create(networks.Select(network =>
        {
            Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
            return new ExceptionEntry(block, null);
        }));

    private static BlacklistEntry Entry(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
        return new BlacklistEntry(value, 50, null, null);
    }
}
