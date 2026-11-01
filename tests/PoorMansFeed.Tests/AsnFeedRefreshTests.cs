using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

/// <summary>Refreshing one saved ASN feed without its page, on an installed service.</summary>
public sealed class AsnFeedRefreshTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string[] SampleLines =
    [
        Line("8.8.8.0/24"),
        Line("8.8.4.0/24"),
        Line("8.0.0.0/8"),
        Line("2001:4860::/32"),
        Line("1.1.1.0/24", "AS13335", "Cloudflare, Inc."),
    ];

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly HttpClient _http;
    private readonly List<IpInfoService> _services = [];
    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemorySecretStore _configKeys = new();
    private byte[] _body = GzBody(SampleLines);

    public AsnFeedRefreshTests()
    {
        _http = new HttpClient(new StubHttpHandler(Respond));
    }

    public void Dispose()
    {
        foreach (IpInfoService service in _services)
            service.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private static string Line(string network, string asn = "AS15169", string asName = "Google LLC") =>
        $$"""{"network":"{{network}}","country":"United States","country_code":"US","continent":"North America","continent_code":"NA","asn":"{{asn}}","as_name":"{{asName}}","as_domain":"google.com"}""";

    private static byte[] GzBody(IEnumerable<string> lines)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (string line in lines)
                writer.WriteLine(line);
        }
        return ms.ToArray();
    }

    private HttpResponseMessage Respond(HttpRequestMessage request, int callNumber)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/checksums", StringComparison.Ordinal))
        {
            string sha = Convert.ToHexStringLower(SHA256.HashData(_body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"checksums\":{\"sha256\":\"" + sha + "\"}}", Encoding.UTF8, "application/json"),
            };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
    }

    private async Task<IpInfoService> LaunchedServiceAsync()
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, _secrets, _configKeys,
            () => false, (duration, token) => Task.CompletedTask);
        _services.Add(service);
        Assert.Equal(StartupFailure.None, (await service.InitializeAsync(Ct)).Failure);
        return service;
    }

    private async Task<IpInfoService> InstalledServiceAsync()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.True(await service.SaveTokenAsync("test-token"));
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.NotNull(service.Dataset);
        await UseExportSettingsAsync(service);
        return service;
    }

    private async Task UseExportSettingsAsync(IpInfoService service) =>
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = true,
            IPv4MinPrefix = 12,
            IPv6MinPrefix = 24,
        }));

    private static SearchQuery Query(string text)
    {
        Assert.True(SearchQuery.TryParse(text, out SearchQuery? query));
        return query;
    }

    private async Task<SavedFeed> PageWriteAsync(IpInfoService service, IReadOnlyCollection<string> includedLarge,
                                                 string asn = "AS15169")
    {
        ExportResult result = await service.ExportAsync(Query(asn), includedLarge, Ct);
        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, result.Record?.Save);
        return Assert.Single(service.Records.ReadSavedFeeds().Feeds);
    }

    private static async Task<IReadOnlyList<HistoryRow>> HistoryAsync(IpInfoService service) =>
        (await service.Records.SearchHistoryAsync(new HistoryQuery(null, null, null), 1000, Ct)).Rows;

    private string Fortigate(string name) => Path.Combine(_temp.ExportFolder, "fortigate", name);

    private string Report => Path.Combine(_temp.ExportFolder, "AS15169.txt");

    private string EditByHand()
    {
        File.AppendAllText(Fortigate("AS15169_ipv4.txt"), "203.0.113.0/24\n");
        return File.ReadAllText(Fortigate("AS15169_ipv4.txt"));
    }

    private Dictionary<string, byte[]> Files() =>
        Directory.EnumerateFiles(_temp.ExportFolder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertNotRefreshed(AsnFeedRefresh refresh, AsnRefreshOutcome outcome)
    {
        Assert.Equal(outcome, refresh.Outcome);
        Assert.Null(refresh.Stamp);
        Assert.Empty(refresh.NewLarge);
    }

    private static FeedRecordResult Record(IpInfoService service, string key, FeedChangeCause cause, SavedFeedIntent intent,
                                           DateTimeOffset at)
    {
        using IDisposable hold = service.Records.EnterWrite(key);
        return IpInfoService.RecordFeedWrite(service,
            new FeedWriteRecord(key, FeedKind.Asn, cause, at, [FeedType.Fortigate], null, null, null, null, intent));
    }

    [Fact]
    public async Task Right_after_a_page_write_the_refresh_is_unchanged_and_writes_and_records_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        Dictionary<string, byte[]> before = Files();
        int rows = (await HistoryAsync(service)).Count;
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("as15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Null(refresh.Export);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Empty(refresh.NewLarge);
        Dictionary<string, byte[]> after = Files();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
        Assert.Equal(rows, (await HistoryAsync(service)).Count);
        Assert.Equal(stored, Assert.Single(service.Records.ReadSavedFeeds().Feeds));
    }

    [Fact]
    public async Task A_file_edited_by_hand_is_written_again_as_a_refresh_with_no_save_and_no_report()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        EditByHand();
        File.Delete(Report);
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(ExportFailure.None, refresh.Export?.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), refresh.Export?.Record);
        Assert.DoesNotContain(Report, refresh.Export!.Files);
        Assert.False(File.Exists(Report));
        string ipv4 = File.ReadAllText(Fortigate("AS15169_ipv4.txt"));
        Assert.DoesNotContain("203.0.113.0/24", ipv4, StringComparison.Ordinal);
        Assert.DoesNotContain("8.0.0.0/8", ipv4, StringComparison.Ordinal);
        Assert.Empty(refresh.NewLarge);

        Assert.NotNull(refresh.Stamp);
        Assert.Equal(stored, refresh.Stamp.Expected);
        Assert.Null(refresh.Stamp.NewPick);
        Assert.NotNull(refresh.Stamp.Proof);
        Assert.NotEqual(stored.Proof, refresh.Stamp.Proof);
        SavedFeedWriteProofTests.AssertProofOfDisk(_temp.ExportFolder, refresh.Export.FeedChanges!, refresh.Stamp.Proof,
                                                   FeedOptions.ForAsnExport(service.Settings));
        Assert.Equal(stored, Assert.Single(service.Records.ReadSavedFeeds().Feeds));

        HistoryRow row = Assert.Single(await HistoryAsync(service), r => r.AtUtc == _time.GetUtcNow());
        Assert.Equal(FeedChangeCause.RefreshAll, row.Cause);
        Assert.Equal("203.0.113.0/24", row.Entry);
        Assert.False(row.Added);
    }

    [Fact]
    public async Task A_saved_large_range_is_written_and_a_new_one_is_left_out_and_listed_at_every_refresh()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, ["8.0.0.0/8"]);
        Assert.Equal(["8.0.0.0/8"], stored.Pick.LargeIncluded);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6MinPrefix = 48 }));
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["2001:4860::/32"], refresh.NewLarge);
        Assert.Contains("8.0.0.0/8", File.ReadAllText(Fortigate("AS15169_ipv4.txt")), StringComparison.Ordinal);
        Assert.DoesNotContain("2001:4860::/32", File.ReadAllText(Fortigate("AS15169_ipv6.txt")), StringComparison.Ordinal);
        Assert.Equal(stored.Pick, Assert.Single(service.Records.ReadSavedFeeds().Feeds).Pick);

        AsnFeedRefresh again = await service.RefreshAsnFeedAsync("AS15169", Ct);
        Assert.Equal(["2001:4860::/32"], again.NewLarge);
    }

    [Fact]
    public async Task No_saved_feed_is_missing_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.Missing);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task A_retired_feed_is_not_refreshed()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        Assert.Equal(FeedSaveOutcome.Saved,
                     Record(service, "AS15169", FeedChangeCause.Retire, new SavedFeedIntent.Retired(), _time.GetUtcNow()).Save);
        string edited = EditByHand();

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.Retired);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task A_feed_whose_save_was_refused_this_session_is_not_refreshed()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        service.Config!.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);
        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult refused = await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8"], Ct);
        Assert.Equal(FeedSaveOutcome.StorageError, refused.Record?.Save);
        service.Config!.BeforeCommit = null;
        string edited = EditByHand();
        int rows = (await HistoryAsync(service)).Count;

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.SaveRefused);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
        Assert.Equal(rows, (await HistoryAsync(service)).Count);
    }

    [Fact]
    public async Task Saved_feeds_that_cannot_be_read_refresh_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        service.Dispose();
        _services.Remove(service);
        using (SqliteConnection conn = ConfigStoreTests.OpenRaw(_temp.Paths.ConfigPath))
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE metadata SET value = 'not base64!' WHERE key = 'saved_feeds';";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        IpInfoService relaunched = await LaunchedServiceAsync();
        Assert.False(relaunched.Records.ReadSavedFeeds().Available);
        string edited = EditByHand();

        AssertNotRefreshed(await relaunched.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.SavedFeedsUnavailable);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task An_AS_the_dataset_no_longer_holds_is_source_gone_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.Equal(FeedSaveOutcome.Saved, Record(service, "AS64511", FeedChangeCause.PageWrite,
            new SavedFeedIntent.Written(SavedFeedListTests.AsnPick("64511")), _time.GetUtcNow()).Save);
        Directory.CreateDirectory(Path.Combine(_temp.ExportFolder, "fortigate"));
        File.WriteAllText(Fortigate("AS64511_ipv4.txt"), "203.0.113.0/24\n");

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS64511", Ct), AsnRefreshOutcome.SourceGone);
        Assert.Equal("203.0.113.0/24\n", File.ReadAllText(Fortigate("AS64511_ipv4.txt")));
        Assert.Single(Files());
    }

    [Fact]
    public async Task With_no_dataset_the_refresh_is_no_dataset_and_writes_nothing()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.Null(service.Dataset);
        await UseExportSettingsAsync(service);
        Assert.Equal(FeedSaveOutcome.Saved, Record(service, "AS15169", FeedChangeCause.PageWrite,
            new SavedFeedIntent.Written(SavedFeedListTests.AsnPick()), _time.GetUtcNow()).Save);

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.NoDataset);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task With_no_dataset_and_IPv6_off_the_refresh_is_still_no_dataset()
    {
        IpInfoService service = await LaunchedServiceAsync();
        Assert.Null(service.Dataset);
        await UseExportSettingsAsync(service);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        Assert.Equal(FeedSaveOutcome.Saved, Record(service, "AS15169", FeedChangeCause.PageWrite,
            new SavedFeedIntent.Written(SavedFeedListTests.AsnPick()), _time.GetUtcNow()).Save);

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.NoDataset);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task With_no_feed_type_the_refresh_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        string edited = EditByHand();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateEnabled = false }));
        File.Delete(Report);

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.AsnFeedsOff);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
        Assert.False(File.Exists(Report));
    }

    [Fact]
    public async Task With_the_feeds_on_and_no_feed_type_ticked_the_refresh_is_no_feed_type_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        string edited = EditByHand();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            WriteFortigateFeeds = false,
            WriteModSecurityLists = false,
        }));

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.NoFeedType);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task A_failure_after_a_file_moved_stamps_a_proof_cleared()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        EditByHand();
        Directory.CreateDirectory(Fortigate("AS15169_ipv6.txt") + ".tmp");
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(ExportFailure.WriteFailed, refresh.Export?.Failure);
        Assert.Equal(Fortigate("AS15169_ipv6.txt"), refresh.Export?.FailedFile);
        Assert.NotNull(refresh.Export?.FeedChanges);
        Assert.Equal(new SavedFeedStamp(stored, null, null, null), refresh.Stamp);
        Assert.Equal(FeedChangeCause.RefreshAll,
                     Assert.Single(await HistoryAsync(service), r => r.AtUtc == _time.GetUtcNow()).Cause);
    }

    [Fact]
    public async Task A_failure_before_any_file_moved_has_no_stamp()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        EditByHand();
        Directory.CreateDirectory(Fortigate("AS15169_ipv4.txt") + ".tmp");

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Failed, refresh.Outcome);
        Assert.Null(refresh.Export?.FeedChanges);
        Assert.Null(refresh.Stamp);
    }

    private void SetExceptionsUnreadable(IpInfoService service) =>
        typeof(IpInfoService).GetField("_exceptions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, ExceptionList.Unreadable);

    [Fact]
    public async Task A_dataset_made_unusable_after_install_is_no_dataset_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        string edited = EditByHand();
        File.Delete(_temp.Paths.DatabasePath);
        Assert.NotNull(service.Dataset);

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS15169", Ct), AsnRefreshOutcome.NoDataset);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task A_saved_pick_naming_another_AS_than_its_key_fails_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.Equal(FeedSaveOutcome.Saved, Record(service, "AS15169", FeedChangeCause.PageWrite,
            new SavedFeedIntent.Written(SavedFeedListTests.AsnPick("13335")), _time.GetUtcNow()).Save);

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        AssertNotRefreshed(refresh, AsnRefreshOutcome.Failed);
        Assert.Null(refresh.Export);
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task A_refresh_once_writes_are_no_longer_admitted_fails_and_writes_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        string edited = EditByHand();
        await service.Records.StopWritesAsync(Ct);

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        AssertNotRefreshed(refresh, AsnRefreshOutcome.Failed);
        Assert.Null(refresh.Export);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task Exceptions_that_cannot_be_read_fail_the_refresh_with_no_stamp_and_nothing_written()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        string edited = EditByHand();
        int rows = (await HistoryAsync(service)).Count;
        SetExceptionsUnreadable(service);

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(ExportFailure.ExceptionsUnreadable, refresh.Export?.Failure);
        Assert.Null(refresh.Stamp);
        Assert.Equal(edited, File.ReadAllText(Fortigate("AS15169_ipv4.txt")));
        Assert.Equal(rows, (await HistoryAsync(service)).Count);
    }

    [Fact]
    public async Task Every_network_large_and_unanswered_with_no_feed_file_is_unchanged_and_lists_them()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        Directory.Delete(_temp.ExportFolder, recursive: true);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv4MinPrefix = 32, IPv6MinPrefix = 128 }));
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Equal(["2001:4860::/32", "8.8.4.0/24", "8.8.8.0/24"], refresh.NewLarge.Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(_temp.ExportFolder));
    }

    [Fact]
    public async Task A_family_the_refresh_empties_is_written_header_only_and_named()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6MinPrefix = 48 }));
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal([Fortigate("AS15169_ipv6.txt")], refresh.Export?.EmptiedFiles);
        string ipv6 = File.ReadAllText(Fortigate("AS15169_ipv6.txt"));
        Assert.Contains("# Entries: 0", ipv6, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:4860::/32", ipv6, StringComparison.Ordinal);
        Assert.Equal(["2001:4860::/32"], refresh.NewLarge);
    }

    [Fact]
    public async Task A_stored_feed_with_no_proof_is_written_again_though_its_files_match()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed written = await PageWriteAsync(service, []);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(FeedSaveOutcome.Saved, Record(service, "AS15169", FeedChangeCause.PageWrite,
            new SavedFeedIntent.Written(written.Pick), _time.GetUtcNow()).Save);
        SavedFeed stored = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.Null(stored.Proof);
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.NotNull(refresh.Export?.FeedChanges);
        Assert.NotNull(refresh.Stamp?.Proof);
    }

    [Fact]
    public async Task A_committed_refresh_stamp_lets_the_next_refresh_skip()
    {
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, []);
        EditByHand();
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh first = await service.RefreshAsnFeedAsync("AS15169", Ct);
        Assert.Equal(AsnRefreshOutcome.Written, first.Outcome);
        Assert.Equal([SavedFeedStampOutcome.Saved], service.Records.CommitRefresh([first.Stamp!]));
        SavedFeed committed = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(first.Stamp!.Proof!.FileSha256, committed.Proof!.FileSha256);
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh second = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, second.Outcome);
        Assert.Null(second.Export);
        Assert.Equal(new SavedFeedStamp(committed, null, committed.Proof, _time.GetUtcNow()), second.Stamp);
        Dictionary<string, byte[]> after = Files();
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
    }

    [Fact]
    public async Task The_report_alone_deleted_after_a_page_write_stays_unchanged_and_absent()
    {
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        File.Delete(Report);
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.False(File.Exists(Report));
    }

    [Fact]
    public async Task With_IPv6_off_an_AS_holding_only_IPv6_networks_is_unchanged_and_its_file_untouched()
    {
        _body = GzBody([.. SampleLines, Line("2a00:1450::/32", "AS64500", "Example")]);
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, [], "AS64500");
        byte[] ipv6 = File.ReadAllBytes(Fortigate("AS64500_ipv6.txt"));
        SavedFeed stored = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS64500", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Null(refresh.Export);
        Assert.Empty(refresh.NewLarge);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Equal(ipv6, File.ReadAllBytes(Fortigate("AS64500_ipv6.txt")));
        Assert.False(File.Exists(Fortigate("AS64500_ipv4.txt")));
    }

    [Fact]
    public async Task With_IPv6_off_and_a_dataset_built_without_IPv6_an_IPv6_only_AS_is_unchanged_and_its_file_untouched()
    {
        _body = GzBody([.. SampleLines, Line("2a00:1450::/32", "AS64500", "Example")]);
        IpInfoService service = await InstalledServiceAsync();
        await PageWriteAsync(service, [], "AS64500");
        byte[] ipv6 = File.ReadAllBytes(Fortigate("AS64500_ipv6.txt"));
        SavedFeed stored = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        _body = GzBody([.. SampleLines, Line("2a00:1450::/32", "AS64500", "Example"), Line("9.9.9.0/24", "AS19281", "Quad9")]);
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.False(service.Dataset!.IncludesIPv6);
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS64500", Ct);

        Assert.Equal(AsnRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Null(refresh.Export);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Equal(ipv6, File.ReadAllBytes(Fortigate("AS64500_ipv6.txt")));
        Assert.False(File.Exists(Fortigate("AS64500_ipv4.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_IPv6_off_an_AS_left_with_only_IPv6_networks_and_an_IPv4_file_is_source_gone(bool split)
    {
        _body = GzBody([.. SampleLines, Line("192.0.2.0/24", "AS64500", "Example"), Line("2a00:1450::/32", "AS64500", "Example")]);
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateSplitByFamily = split }));
        await PageWriteAsync(service, [], "AS64500");
        Assert.True(File.Exists(Fortigate(split ? "AS64500_ipv4.txt" : "AS64500.txt")));
        _body = GzBody([.. SampleLines, Line("2a00:1450::/32", "AS64500", "Example")]);
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.True(service.Dataset!.IncludesIPv6);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromMinutes(5));

        AssertNotRefreshed(await service.RefreshAsnFeedAsync("AS64500", Ct), AsnRefreshOutcome.SourceGone);
        Assert.Equal(before, Files());
    }

    [Fact]
    public async Task A_saved_large_answer_matches_a_dataset_network_written_in_another_form()
    {
        _body = GzBody([.. SampleLines, Line("2A00:1400::/23")]);
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, ["2A00:1400::/23"]);
        Assert.Equal(["2a00:1400::/23"], stored.Pick.LargeIncluded);
        LargeRange large = Assert.Single((await service.PrepareExportAsync(Query("AS15169"), Ct)).LargeRanges,
                                         range => range.Network == "2A00:1400::/23");
        Assert.True(large.SavedIncluded);
        EditByHand();
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.Empty(refresh.NewLarge);
        Assert.Contains("2A00:1400::/23", File.ReadAllText(Fortigate("AS15169_ipv6.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_left_out_answer_matches_a_dataset_network_written_in_another_form()
    {
        _body = GzBody([.. SampleLines, Line("2A00:1400::/23")]);
        IpInfoService service = await InstalledServiceAsync();
        SavedFeed stored = await PageWriteAsync(service, []);
        Assert.Contains("2a00:1400::/23", stored.Pick.LargeLeftOut);
        EditByHand();
        _time.Advance(TimeSpan.FromMinutes(5));

        AsnFeedRefresh refresh = await service.RefreshAsnFeedAsync("AS15169", Ct);

        Assert.Equal(AsnRefreshOutcome.Written, refresh.Outcome);
        Assert.Empty(refresh.NewLarge);
        Assert.DoesNotContain("2A00:1400::/23", File.ReadAllText(Fortigate("AS15169_ipv6.txt")), StringComparison.Ordinal);
    }
}
