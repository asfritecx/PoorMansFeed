using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The AbuseIPDB rolling feed per ticked feed type: the FortiGate and ModSecurity copies, Write feed and Empty feed.</summary>
public sealed class ModSecurityFeedRollingTests : IDisposable
{
    private const string ModSecurityFirstLine = "# PoorMansFeed - ModSecurity IP list";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);
    private static readonly DateTime OldWriteTime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly ServiceTempFolder _temp = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public ModSecurityFeedRollingTests()
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

    private string ExportFolder => _temp.Paths.DefaultExportFolder;

    private string FortigateFolder => Path.Combine(ExportFolder, "fortigate");

    private string ModSecurityFolder => Path.Combine(ExportFolder, "modsecurity");

    private string Fg4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: false);

    private string Fg6 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: true);

    private string Ms4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: false);

    private string Ms6 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: true);

    // ---------- Write feed, per ticked type ----------

    [Fact]
    public async Task FortiGate_alone_writes_the_fortigate_files_as_before_and_no_modsecurity_folder()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: false);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg4, Fg6], result.Files);
        Assert.Null(result.ModSecurityFiles);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Equal(Utf8(Header("IPv4", 3) + Lines("1.1.1.1", "8.8.8.8", "9.9.9.9")), File.ReadAllBytes(Fg4));
        Assert.Equal(Utf8(Header("IPv6", 2) + Lines("2001:4860::8888", "2606:4700:4700::1111")), File.ReadAllBytes(Fg6));
        Assert.False(Directory.Exists(ModSecurityFolder));
    }

    [Fact]
    public async Task ModSecurity_alone_writes_the_fortigate_bytes_with_line_one_replaced_and_leaves_fortigate_alone()
    {
        SeedFive();
        Ticks(fortigate: false, modSecurity: true);

        AbuseFeedWriteResult first = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, first.Failure);
        Assert.Equal([Ms4, Ms6], first.Files);
        Assert.Equal([Ms4, Ms6], first.ModSecurityFiles);
        Assert.Equal(3, first.IPv4Written);
        Assert.Equal(2, first.IPv6Written);
        Assert.False(Directory.Exists(FortigateFolder));

        // An earlier FortiGate file is neither read, written nor named while FortiGate isn't ticked
        WriteOldFeed(Fg4, "4.4.4.4");
        File.SetLastWriteTimeUtc(Fg4, OldWriteTime);
        byte[] oldBytes = File.ReadAllBytes(Fg4);

        AbuseFeedWriteResult second = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, second.Failure);
        Assert.Equal([Ms4, Ms6], second.Files);
        Assert.Empty(second.EmptiedFiles);
        Assert.Null(second.OldFilesLeft);
        Assert.Equal(oldBytes, File.ReadAllBytes(Fg4));
        Assert.Equal(OldWriteTime, File.GetLastWriteTimeUtc(Fg4));
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], Directory.GetFileSystemEntries(FortigateFolder).Select(Path.GetFileName));

        // A FortiGate-only write of the same list, at the same time, into another export folder
        string reference = Path.Combine(_temp.Folder, "reference");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = reference, WriteFortigateFeeds = true, WriteModSecurityLists = false };
        Assert.Equal(AbuseFeedWriteFailure.None, (await Service().WriteFeedAsync(CancellationToken.None)).Failure);
        foreach (bool ipv6 in new[] { false, true })
        {
            byte[] fortigate = File.ReadAllBytes(AbuseIpDbFeedWriter.PathFor(reference, FeedType.Fortigate, ipv6));
            Assert.Equal(AsModSecurity(fortigate), File.ReadAllBytes(ipv6 ? Ms6 : Ms4));
        }
    }

    [Fact]
    public async Task Both_types_write_fortigate_then_modsecurity_and_count_each_family_once()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.Files);
        Assert.Equal([Ms4, Ms6], result.ModSecurityFiles);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Empty(result.EmptiedFiles);
        Assert.Null(result.FailedFile);
        foreach (string file in result.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
        foreach (string file in result.ModSecurityFiles!)
            Assert.True(ModSecurityRules.IsFileName(Path.GetFileName(file)), file);
        Assert.Equal(AsModSecurity(File.ReadAllBytes(Fg4)), File.ReadAllBytes(Ms4));
        Assert.Equal(AsModSecurity(File.ReadAllBytes(Fg6)), File.ReadAllBytes(Ms6));
        Assert.Equal(ModSecurityFirstLine, File.ReadLines(Ms4).First());
    }

    [Fact]
    public void The_left_out_line_over_the_cap_is_in_both_copies()
    {
        var family = new AbuseFeedFamily(false, [Address("8.8.8.8")], 0, 5);
        var header = new AbuseFeedHeader(30, Now, Downloaded, Generated);

        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, FeedType.Fortigate, family, header);
        AbuseIpDbFeedWriter.WriteFamily(ExportFolder, FeedType.ModSecurity, family, header);

        Assert.Contains("# Left out: 5 addresses over FortiOS's 131072 limit", File.ReadAllLines(Fg4));
        Assert.Contains("# Left out: 5 addresses over FortiOS's 131072 limit", File.ReadAllLines(Ms4));
        Assert.Equal(AsModSecurity(File.ReadAllBytes(Fg4)), File.ReadAllBytes(Ms4));
    }

    [Fact]
    public async Task No_type_ticked_is_NoFeedType_before_the_exceptions_or_the_list_are_read()
    {
        byte[] listBytes = UnreadableListBytes();
        File.WriteAllBytes(_temp.Paths.AbuseIpDbDatabasePath, listBytes);
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        Ticks(fortigate: false, modSecurity: false);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NoFeedType, result.Failure);
        Assert.Equal(listBytes, File.ReadAllBytes(_temp.Paths.AbuseIpDbDatabasePath));
        Assert.False(Path.Exists(_temp.Paths.AbuseIpDbDatabasePath + "-journal"));
        Assert.Empty(result.Files);
        Assert.Empty(result.EmptiedFiles);
        Assert.Equal(0, result.IPv4Written);
        Assert.Equal(0, result.ExceptionRemovals);
        Assert.Null(result.FailedFile);
        Assert.Null(result.ModSecurityFiles);
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task No_type_ticked_leaves_the_list_file_untouched_and_opens_no_journal()
    {
        SeedFive();
        string list = _temp.Paths.AbuseIpDbDatabasePath;
        byte[] before = File.ReadAllBytes(list);
        Ticks(fortigate: false, modSecurity: false);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NoFeedType, result.Failure);
        Assert.Equal(before, File.ReadAllBytes(list));
        Assert.False(Path.Exists(list + "-journal"));
        Assert.False(Directory.Exists(ExportFolder));
    }

    [Fact]
    public async Task FortiGate_alone_leaves_the_modsecurity_files_of_an_earlier_write_unread_and_unnamed()
    {
        SeedFive();
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        Ticks(fortigate: true, modSecurity: false);
        WriteOldFeed(Ms4, "4.4.4.4");
        WriteOldFeed(Ms6, "2001:4860::1");
        byte[] old4 = File.ReadAllBytes(Ms4);
        byte[] old6 = File.ReadAllBytes(Ms6);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg4], result.Files);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(old4, File.ReadAllBytes(Ms4));
        Assert.Equal(old6, File.ReadAllBytes(Ms6));
        string[] named = [.. result.Files, .. result.EmptiedFiles, .. result.OldFilesLeft ?? [], .. result.ModSecurityFiles ?? []];
        Assert.DoesNotContain(Ms4, named);
        Assert.DoesNotContain(Ms6, named);
        Assert.Equal(["AbuseIPDB_rolling_ipv4.txt"], Directory.GetFileSystemEntries(FortigateFolder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ModSecurity_alone_with_IPv6_off_empties_its_own_ipv6_file_and_leaves_the_fortigate_one()
    {
        SeedFive();
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        Ticks(fortigate: false, modSecurity: true);
        WriteOldFeed(Ms6, "2001:4860::1");
        WriteOldFeed(Fg6, "2001:4860::1");
        byte[] oldFortigate = File.ReadAllBytes(Fg6);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ms4, Ms6], result.Files);
        Assert.Equal([Ms6], result.EmptiedFiles);
        Assert.Equal([Ms4, Ms6], result.ModSecurityFiles);
        Assert.Null(result.OldFilesLeft);
        Assert.Equal(ModSecurityFirstLine, File.ReadLines(Ms6).First());
        Assert.Contains("# Entries: 0", File.ReadAllLines(Ms6));
        Assert.Empty(Addresses(Ms6));
        Assert.Equal(oldFortigate, File.ReadAllBytes(Fg6));
        Assert.DoesNotContain(Fg6, result.Files.Concat(result.EmptiedFiles));
    }

    [Fact]
    public async Task A_family_with_no_address_is_header_only_only_in_the_types_whose_file_exists()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);
        _ipinfo.Exceptions = Exceptions("2001:4860::/32", "2606:4700::/32");
        WriteOldFeed(Fg6, "2001:4860::8888");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg4, Fg6, Ms4], result.Files);
        Assert.Equal([Fg6], result.EmptiedFiles);
        Assert.Equal([Ms4], result.ModSecurityFiles);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal(2, result.ExceptionRemovals);
        Assert.Empty(Addresses(Fg6));
        Assert.Contains("# Entries: 0", File.ReadAllLines(Fg6));
        Assert.False(File.Exists(Ms6));
    }

    // ---------- Write feed failures ----------

    [Fact]
    public async Task A_file_named_modsecurity_fails_the_first_modsecurity_file_after_every_fortigate_file()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);
        Directory.CreateDirectory(ExportFolder);
        File.WriteAllText(ModSecurityFolder, "x");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ms4, result.FailedFile);
        Assert.Equal([Fg4, Fg6], result.Files);
        Assert.Null(result.ModSecurityFiles);
        Assert.Null(result.OldFilesLeft);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Equal(Utf8(Header("IPv4", 3) + Lines("1.1.1.1", "8.8.8.8", "9.9.9.9")), File.ReadAllBytes(Fg4));
        Assert.Equal("x", File.ReadAllText(ModSecurityFolder));
    }

    [Fact]
    public async Task A_folder_at_a_modsecurity_temporary_path_fails_that_file_and_counts_each_family_once()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);
        WriteOldFeed(Ms6, "2001:4860::1");
        Directory.CreateDirectory(Ms6 + ".tmp");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ms6, result.FailedFile);
        Assert.Equal([Fg4, Fg6, Ms4], result.Files);
        Assert.Equal([Ms4], result.ModSecurityFiles);
        Assert.Equal([Ms6], result.OldFilesLeft);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(2, result.IPv6Written);
        Assert.Equal(["2001:4860::1"], Addresses(Ms6));
    }

    [Fact]
    public async Task A_file_named_fortigate_fails_the_first_fortigate_file_and_names_the_modsecurity_files_left()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);
        WriteOldFeed(Ms4, "4.4.4.4");
        WriteOldFeed(Ms6, "2001:4860::1");
        File.WriteAllText(FortigateFolder, "x");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg4, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Null(result.ModSecurityFiles);
        Assert.Equal([Ms4, Ms6], result.OldFilesLeft);
        Assert.Equal(0, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Equal("x", File.ReadAllText(FortigateFolder));
        Assert.Equal(["4.4.4.4"], Addresses(Ms4));
    }

    [Fact]
    public async Task A_failure_at_the_fortigate_ipv6_file_stops_the_write_and_names_every_old_file_left()
    {
        SeedFive();
        Ticks(fortigate: true, modSecurity: true);
        WriteOldFeed(Fg6, "2001:4860::1");
        WriteOldFeed(Ms4, "4.4.4.4");
        WriteOldFeed(Ms6, "2001:4860::2");
        byte[] oldFg6 = File.ReadAllBytes(Fg6);
        byte[] oldMs4 = File.ReadAllBytes(Ms4);
        byte[] oldMs6 = File.ReadAllBytes(Ms6);
        Directory.CreateDirectory(Fg6 + ".tmp");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg6, result.FailedFile);
        Assert.Equal([Fg4], result.Files);
        Assert.Equal([Fg6, Ms4, Ms6], result.OldFilesLeft);
        Assert.Equal(3, result.IPv4Written);
        Assert.Equal(0, result.IPv6Written);
        Assert.Null(result.ModSecurityFiles);
        Assert.Equal(oldFg6, File.ReadAllBytes(Fg6));
        Assert.Equal(oldMs4, File.ReadAllBytes(Ms4));
        Assert.Equal(oldMs6, File.ReadAllBytes(Ms6));
    }

    // ---------- Empty feed ----------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Empty_feed_empties_every_existing_file_in_both_folders_whatever_is_ticked(bool fortigate, bool modSecurity)
    {
        Ticks(fortigate, modSecurity);
        foreach (string file in new[] { Fg4, Fg6, Ms4, Ms6 })
            WriteOldFeed(file, "8.8.8.8");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.Files);
        Assert.Equal([Fg4, Fg6, Ms4, Ms6], result.EmptiedFiles);
        Assert.Equal([Ms4, Ms6], result.ModSecurityFiles);
        Assert.Equal(Utf8(EmptiedHeader("IPv4")), File.ReadAllBytes(Fg4));
        Assert.Equal(Utf8(EmptiedHeader("IPv6")), File.ReadAllBytes(Fg6));
        Assert.Equal(AsModSecurity(Utf8(EmptiedHeader("IPv4"))), File.ReadAllBytes(Ms4));
        Assert.Equal(AsModSecurity(Utf8(EmptiedHeader("IPv6"))), File.ReadAllBytes(Ms6));
    }

    [Fact]
    public async Task Empty_feed_with_only_the_modsecurity_files_empties_them_and_makes_no_fortigate_folder()
    {
        Ticks(fortigate: true, modSecurity: false);
        WriteOldFeed(Ms4, "8.8.8.8");
        WriteOldFeed(Ms6, "2001:4860::8888");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Ms4, Ms6], result.Files);
        Assert.Equal([Ms4, Ms6], result.ModSecurityFiles);
        Assert.Equal(AsModSecurity(Utf8(EmptiedHeader("IPv6"))), File.ReadAllBytes(Ms6));
        Assert.False(Directory.Exists(FortigateFolder));
    }

    [Fact]
    public async Task Empty_feed_failing_in_the_fortigate_folder_names_the_modsecurity_files_left_as_they_were()
    {
        Ticks(fortigate: true, modSecurity: true);
        WriteOldFeed(Fg4, "8.8.8.8");
        WriteOldFeed(Ms4, "8.8.8.8");
        WriteOldFeed(Ms6, "2001:4860::8888");
        byte[] oldMs4 = File.ReadAllBytes(Ms4);
        byte[] oldMs6 = File.ReadAllBytes(Ms6);
        // A folder at the feed's path counts as a file to empty, and the move over it fails
        Directory.CreateDirectory(Fg6);

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Fg6, result.FailedFile);
        Assert.Equal([Fg4], result.Files);
        Assert.Equal([Fg4], result.EmptiedFiles);
        Assert.Equal([Ms4, Ms6], result.OldFilesLeft);
        Assert.Null(result.ModSecurityFiles);
        Assert.Equal(oldMs4, File.ReadAllBytes(Ms4));
        Assert.Equal(oldMs6, File.ReadAllBytes(Ms6));
    }

    [Fact]
    public async Task Empty_feed_failing_in_the_modsecurity_folder_keeps_the_copies_written_before_it()
    {
        Ticks(fortigate: true, modSecurity: true);
        foreach (string file in new[] { Fg4, Fg6, Ms4 })
            WriteOldFeed(file, "8.8.8.8");
        WriteOldFeed(Ms6, "2001:4860::8888");
        byte[] oldMs6 = File.ReadAllBytes(Ms6);
        Directory.CreateDirectory(Ms6 + ".tmp");

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal(Ms6, result.FailedFile);
        Assert.Equal([Fg4, Fg6, Ms4], result.Files);
        Assert.Equal([Ms4], result.ModSecurityFiles);
        Assert.Equal([Ms6], result.OldFilesLeft);
        Assert.Equal(AsModSecurity(Utf8(EmptiedHeader("IPv4"))), File.ReadAllBytes(Ms4));
        Assert.Equal(oldMs6, File.ReadAllBytes(Ms6));
    }

    [Fact]
    public async Task Empty_feed_with_no_file_in_either_folder_is_NothingToWrite_and_makes_no_folder()
    {
        Ticks(fortigate: true, modSecurity: true);

        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(CancellationToken.None);

        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, result.Failure);
        Assert.Empty(result.Files);
        Assert.Null(result.ModSecurityFiles);
        Assert.False(Directory.Exists(ExportFolder));
    }

    // ---------- The write's type barrier ----------

    [Fact]
    public void Only_a_feed_family_can_become_a_body_and_a_file_write_takes_only_a_body()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                      | BindingFlags.Static | BindingFlags.DeclaredOnly;

        ConstructorInfo[] constructors =
            typeof(AbuseFeedBody).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate, constructor.ToString()));

        MethodInfo writeFile = Assert.Single(typeof(AbuseIpDbFeedWriter).GetMethods(Declared),
                                             method => method.Name == nameof(AbuseIpDbFeedWriter.WriteFile));
        Assert.Equal([typeof(string), typeof(FeedType), typeof(AbuseFeedBody)],
                     writeFile.GetParameters().Select(parameter => parameter.ParameterType));

        MethodInfo[] makers = typeof(AbuseFeedBody).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(Declared))
            .Where(method => !method.IsPrivate && !method.IsSpecialName
                             && !method.IsDefined(typeof(CompilerGeneratedAttribute))
                             && !method.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute))
                             && method.ReturnType == typeof(AbuseFeedBody))
            .ToArray();
        MethodInfo of = Assert.Single(makers);
        Assert.Equal(typeof(AbuseFeedBody), of.DeclaringType);
        Assert.Equal(nameof(AbuseFeedBody.Of), of.Name);
        Assert.Equal([typeof(AbuseFeedFamily), typeof(AbuseFeedHeader)],
                     of.GetParameters().Select(parameter => parameter.ParameterType));

        Assert.DoesNotContain(typeof(AbuseIpDbFeedWriter).GetMethods(Declared).Where(method => !method.IsPrivate),
                              method => method.GetParameters()
                                              .Any(parameter => parameter.ParameterType == typeof(AbuseReportFamily)));
    }

    // ---------- Helpers ----------

    private void Ticks(bool fortigate, bool modSecurity) =>
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = fortigate, WriteModSecurityLists = modSecurity };

    private AbuseIpDbService Service() =>
        new(_http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, new FakeAbuseAccounting());

    private void SeedFive()
    {
        BlacklistEntry[] entries =
        [
            Entry("9.9.9.9"), Entry("2606:4700:4700::1111"), Entry("1.1.1.1"), Entry("2001:4860::8888"), Entry("8.8.8.8"),
        ];
        ListRead<ListMerge> merged = new AbuseIpDbList(_temp.Paths.AbuseIpDbDatabasePath)
            .Merge(new Blacklist(Generated, entries, 0, 0, 0), Downloaded, Downloaded.AddDays(-30), CancellationToken.None);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    /// <summary>The FortiGate copy's bytes with only the first line replaced by the ModSecurity one.</summary>
    private static byte[] AsModSecurity(byte[] fortigate)
    {
        string text = Encoding.UTF8.GetString(fortigate);
        Assert.StartsWith("# PoorMansFeed - Fortigate Threat Feed\n", text, StringComparison.Ordinal);
        return Utf8(ModSecurityFirstLine + text[text.IndexOf('\n', StringComparison.Ordinal)..]);
    }

    private static void WriteOldFeed(string path, params string[] addresses)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Lines(["# an earlier feed", .. addresses]));
    }

    private static string Header(string family, int entries) =>
        HeaderLines(family, entries, downloaded: true, "# Kept: addresses listed within 30 days of the last download");

    private static string EmptiedHeader(string family) =>
        HeaderLines(family, 0, downloaded: false, "# Emptied by hand: no address is listed until the feed is written again");

    private static string HeaderLines(string family, int entries, bool downloaded, string keptLine)
    {
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
        lines.Add("# Entries: " + entries.ToString(CultureInfo.InvariantCulture));
        return Lines([.. lines]);
    }

    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + "\n"));

    private static byte[] Utf8(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    private static string[] Addresses(string file) => File.ReadAllLines(file).Where(line => !line.StartsWith('#')).ToArray();

    private static byte[] UnreadableListBytes()
    {
        byte[] bytes = new byte[4096];
        new Random(4711).NextBytes(bytes);
        return bytes;
    }

    private static ExceptionList Exceptions(params string[] networks) =>
        ExceptionList.Create(networks.Select(network =>
        {
            Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
            return new ExceptionEntry(block, null);
        }));

    private static PublicAddress Address(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
        return value;
    }

    private static BlacklistEntry Entry(string address) => new(Address(address), 50, null, null);
}
