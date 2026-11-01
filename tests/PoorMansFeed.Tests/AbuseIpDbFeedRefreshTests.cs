using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>Refreshing the saved rolling feed without its page and without a download.</summary>
public sealed class AbuseIpDbFeedRefreshTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);

    private readonly InMemorySecretStore _keys = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ServiceTempFolder _temp = new();
    private readonly SavedFeedStore _store = new();
    private readonly FakeIpInfoService _ipinfo;
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public AbuseIpDbFeedRefreshTests()
    {
        _ipinfo = new FakeIpInfoService { Records = new FeedRecords(_temp.Paths, _time, _store) };
        _handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("The refresh sent a request."));
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    /// <summary>Saved feeds kept as config.db keeps them, with a read that can fail and a refused save.</summary>
    private sealed class SavedFeedStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public int Applies { get; private set; }

        public bool Unavailable { get; set; }

        public bool RefuseApply { get; set; }

        public SavedFeedsRead Read() => Unavailable ? new(false, []) : new(true, _list.Feeds);

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            Applies++;
            if (RefuseApply)
                return SavedFeedsFailure.StorageError;
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

    private string ListPath => _temp.Paths.AbuseIpDbDatabasePath;

    private string ExportFolder => _temp.Paths.DefaultExportFolder;

    private string Fg4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: false);

    private string Fg6 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.Fortigate, ipv6: true);

    private AbuseIpDbService Service() =>
        new(_http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _keys, new FakeAbuseAccounting());

    private SavedFeed Stored() => Assert.Single(_store.Read().Feeds);

    private async Task<IReadOnlyList<HistoryRow>> HistoryAsync()
    {
        HistoryRead read = await _ipinfo.Records.SearchHistoryAsync(new HistoryQuery(null, null, null), 1000, Ct);
        return read.Rows;
    }

    private Dictionary<string, byte[]> Files() =>
        Directory.EnumerateFiles(ExportFolder, "*", SearchOption.AllDirectories)
                 .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertSameFiles(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in before)
            Assert.Equal(bytes, after[path]);
    }

    private async Task<AbuseIpDbService> WrittenAsync(params BlacklistEntry[] entries)
    {
        Seed(Generated, Downloaded, entries);
        AbuseIpDbService service = Service();
        AbuseFeedWriteResult written = await service.WriteFeedAsync(Ct);
        Assert.Equal(AbuseFeedWriteFailure.None, written.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
        return service;
    }

    private static void AssertNotRefreshed(RollingFeedRefresh refresh, RollingRefreshOutcome outcome)
    {
        Assert.Equal(outcome, refresh.Outcome);
        Assert.Null(refresh.Write);
        Assert.Null(refresh.Stamp);
    }

    [Fact]
    public async Task Right_after_a_write_the_refresh_is_unchanged_records_nothing_and_leaves_the_files()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2001:4860::8888"));
        SavedFeed stored = Stored();
        Assert.NotNull(stored.Proof);
        int rows = (await HistoryAsync()).Count;
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Null(refresh.Write);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, Now.AddMinutes(5)), refresh.Stamp);
        AssertSameFiles(before, Files());
        Assert.Equal(rows, (await HistoryAsync()).Count);
        Assert.Equal(1, _store.Applies);
        Assert.Equal([SavedFeedStampOutcome.Saved], _ipinfo.Records.CommitRefresh([refresh.Stamp!]));
        Assert.Equal(stored.Proof!.FileSha256, Stored().Proof!.FileSha256);
    }

    [Fact]
    public async Task A_file_edited_by_hand_is_written_again_as_a_refresh_change_and_the_pick_is_not_saved()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"), Entry("9.9.9.9"));
        SavedFeed stored = Stored();
        File.WriteAllLines(Fg4, File.ReadAllLines(Fg4).Where(line => line != "9.9.9.9"));
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        AbuseFeedWriteResult write = Assert.IsType<AbuseFeedWriteResult>(refresh.Write);
        Assert.Equal(AbuseFeedWriteFailure.None, write.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), write.Record);
        Assert.Contains("9.9.9.9", File.ReadAllLines(Fg4));
        HistoryRow added = Assert.Single(await HistoryAsync(), row => row.AtUtc == Now.AddMinutes(5));
        Assert.Equal("9.9.9.9", added.Entry);
        Assert.True(added.Added);
        Assert.Equal(FeedChangeCause.RefreshAll, added.Cause);
        Assert.Equal(1, _store.Applies);
        Assert.Equal(stored, Stored());
        SavedFeedStamp stamp = Assert.IsType<SavedFeedStamp>(refresh.Stamp);
        Assert.Equal(stored, stamp.Expected);
        Assert.Null(stamp.NewPick);
        Assert.Equal(Now.AddMinutes(5), stamp.RefreshedUtc);
        SavedFeedWriteProofTests.AssertProofOfDisk(ExportFolder, write.FeedChanges!, stamp.Proof,
                                                   FeedOptions.ForRollingFeed(_ipinfo.Settings));
    }

    [Fact]
    public async Task Fewer_keep_days_with_no_download_drop_the_addresses_past_them()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        string older = Unix(Downloaded.AddDays(-3));
        Execute($"INSERT INTO addresses VALUES (X'09090909', {older}, {older}, 50, NULL, NULL);");
        await service.WriteFeedAsync(Ct);
        Assert.Contains("9.9.9.9", File.ReadAllLines(Fg4));
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 1 };
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        Assert.DoesNotContain("9.9.9.9", File.ReadAllLines(Fg4));
        HistoryRow removed = Assert.Single(await HistoryAsync(), row => !row.Added);
        Assert.Equal(FeedChangeCause.RefreshAll, removed.Cause);
        Assert.Equal(FeedRemovalReason.LeftTheList, removed.Reason);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_clock_that_moves_past_the_last_download_drops_the_addresses_past_the_keep_days()
    {
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 1 };
        // Within a day behind the download, so the write is allowed and counts the keep days back from this clock
        DateTimeOffset downloaded = Now.AddHours(12);
        Seed(downloaded.AddSeconds(-2), downloaded, Entry("8.8.8.8"));
        string seen = Unix(downloaded.AddHours(-30));
        Execute($"INSERT INTO addresses VALUES (X'09090909', {seen}, {seen}, 50, NULL, NULL);");
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseFeedWriteFailure.None, (await service.WriteFeedAsync(Ct)).Failure);
        Assert.Contains("9.9.9.9", File.ReadAllLines(Fg4));
        _time.Advance(TimeSpan.FromDays(1));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        Assert.DoesNotContain("9.9.9.9", File.ReadAllLines(Fg4));
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_clock_past_the_last_download_alone_changes_nothing()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromDays(40));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Unchanged, refresh.Outcome);
        AssertSameFiles(before, Files());
    }

    [Fact]
    public async Task A_feed_never_saved_is_missing_and_nothing_is_written()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));

        AssertNotRefreshed(await Service().RefreshFeedAsync(Ct), RollingRefreshOutcome.Missing);

        Assert.False(Directory.Exists(ExportFolder));
        Assert.Empty(await HistoryAsync());
        Assert.Equal(0, _store.Applies);
    }

    [Fact]
    public async Task A_retired_feed_is_left_header_only()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        Assert.Equal(AbuseFeedWriteFailure.None, (await service.EmptyFeedAsync(Ct)).Failure);
        await AssertRefusedAsync(service, RollingRefreshOutcome.Retired);
    }

    [Fact]
    public async Task A_feed_whose_save_was_refused_this_session_is_not_written()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        _store.RefuseApply = true;
        Seed(Generated, Downloaded, Entry("7.7.7.7"));
        Assert.Equal(FeedSaveOutcome.StorageError, (await service.WriteFeedAsync(Ct)).Record?.Save);
        _store.RefuseApply = false;
        await AssertRefusedAsync(service, RollingRefreshOutcome.SaveRefused);
    }

    [Fact]
    public async Task Saved_feeds_that_cant_be_read_write_nothing()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        _store.Unavailable = true;
        await AssertRefusedAsync(service, RollingRefreshOutcome.SavedFeedsUnavailable);
    }

    private async Task AssertRefusedAsync(AbuseIpDbService service, RollingRefreshOutcome outcome)
    {
        Seed(Generated, Downloaded, Entry("1.1.1.1"));
        int rows = (await HistoryAsync()).Count;
        int applies = _store.Applies;
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromMinutes(5));

        AssertNotRefreshed(await service.RefreshFeedAsync(Ct), outcome);

        AssertSameFiles(before, Files());
        Assert.Equal(rows, (await HistoryAsync()).Count);
        Assert.Equal(applies, _store.Applies);
    }

    [Fact]
    public async Task A_list_with_no_download_fails_with_no_stamp()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        File.Delete(ListPath);
        Dictionary<string, byte[]> before = Files();

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(AbuseFeedWriteFailure.NoDownload, refresh.Write?.Failure);
        Assert.Null(refresh.Stamp);
        AssertSameFiles(before, Files());
    }

    [Fact]
    public async Task Nothing_to_write_is_unchanged_with_the_stored_proof()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        SavedFeed stored = Stored();
        foreach (string path in Files().Keys)
            File.Delete(path);
        _ipinfo.Exceptions = Exceptions("8.8.8.8");

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Null(refresh.Write);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, Now), refresh.Stamp);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_write_that_fails_after_a_file_moved_stamps_no_proof()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"), Entry("2001:4860::8888"));
        SavedFeed stored = Stored();
        File.Delete(Fg6);
        Directory.CreateDirectory(Fg6);
        Seed(Generated, Downloaded, Entry("1.1.1.1"));
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, refresh.Write?.Failure);
        Assert.Equal([Fg4], refresh.Write!.Files);
        Assert.Equal(new SavedFeedStamp(stored, null, null, null), refresh.Stamp);
        HistoryRow added = Assert.Single(await HistoryAsync(), row => row.Entry == "1.1.1.1");
        Assert.Equal(FeedChangeCause.RefreshAll, added.Cause);
        Assert.Equal(1, _store.Applies);
    }

    [Fact]
    public async Task The_refresh_sends_no_request_and_never_reads_the_key()
    {
        _keys.Secret = "0123456789abcdef0123"u8.ToArray();
        _keys.Marker = true;
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        File.Delete(Fg4);
        int reads = _keys.Reads;

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(reads, _keys.Reads);
    }

    [Fact]
    public async Task After_the_writes_stopped_the_refresh_fails_with_no_stamp()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        Dictionary<string, byte[]> before = Files();
        await _ipinfo.Records.StopWritesAsync(Ct);

        AssertNotRefreshed(await service.RefreshFeedAsync(Ct), RollingRefreshOutcome.Failed);

        AssertSameFiles(before, Files());
    }

    // Bounded, so a gate the refresh never gave back fails the test rather than hanging it
    private static async Task AssertWriteCompletesAsync(AbuseIpDbService service)
    {
        AbuseFeedWriteResult written = await service.WriteFeedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AbuseFeedWriteFailure.None, written.Failure);
    }

    [Fact]
    public async Task After_a_refresh_a_write_from_the_page_is_not_held_back()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        File.Delete(Fg4);

        Assert.Equal(RollingRefreshOutcome.Written, (await service.RefreshFeedAsync(Ct)).Outcome);

        await AssertWriteCompletesAsync(service);
    }

    [Fact]
    public async Task A_refresh_cancelled_before_it_starts_throws_touches_nothing_and_frees_the_gate()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        File.Delete(Fg4);
        Dictionary<string, byte[]> before = Files();
        int rows = (await HistoryAsync()).Count;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshFeedAsync(cancelled.Token));

        AssertSameFiles(before, Files());
        Assert.Equal(rows, (await HistoryAsync()).Count);
        await AssertWriteCompletesAsync(service);
    }

    [Fact]
    public async Task ModSecurity_ticked_after_a_write_is_written_with_its_copies()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"), Entry("2001:4860::8888"));
        string mod4 = AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: false);
        string mod6 = AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: true);
        Assert.False(File.Exists(mod4));
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        Assert.Contains("8.8.8.8", File.ReadAllLines(mod4));
        Assert.Contains("2001:4860::8888", File.ReadAllLines(mod6));
        Assert.NotNull(refresh.Stamp?.Proof);
    }

    [Fact]
    public async Task IPv6_switched_off_after_a_write_is_written_though_the_files_hold_the_same_entries()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        Assert.False(File.Exists(Fg6));
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Written, refresh.Outcome);
        Assert.Contains("8.8.8.8", File.ReadAllLines(Fg4));
        Assert.False(File.Exists(Fg6));
    }

    [Fact]
    public async Task No_feed_type_ticked_fails_with_no_stamp_and_makes_no_folder()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };
        await AssertFailsBeforeAnyFileAsync(service, ExportFolder, AbuseFeedWriteFailure.NoFeedType);
    }

    [Fact]
    public async Task Exceptions_that_cant_be_read_fail_with_no_stamp_and_make_no_folder()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        _ipinfo.Exceptions = PoorMansFeed.Core.FeedExceptions.ExceptionList.Unreadable;
        await AssertFailsBeforeAnyFileAsync(service, ExportFolder, AbuseFeedWriteFailure.ExceptionsUnreadable);
    }

    [Fact]
    public async Task An_export_folder_that_cant_be_made_fails_with_no_stamp()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"));
        string blocker = Path.Combine(_temp.Folder, "a-file");
        File.WriteAllText(blocker, "not a folder");
        string folder = Path.Combine(blocker, "feeds");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = folder };
        await AssertFailsBeforeAnyFileAsync(service, folder, AbuseFeedWriteFailure.FolderUnavailable);
        Assert.Equal("not a folder", File.ReadAllText(blocker));
    }

    private async Task AssertFailsBeforeAnyFileAsync(AbuseIpDbService service, string folder, AbuseFeedWriteFailure failure)
    {
        Directory.Delete(ExportFolder, recursive: true);
        int rows = (await HistoryAsync()).Count;
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh refresh = await service.RefreshFeedAsync(Ct);

        Assert.Equal(RollingRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(failure, refresh.Write?.Failure);
        Assert.Null(refresh.Stamp);
        Assert.False(Directory.Exists(ExportFolder));
        Assert.False(Directory.Exists(folder));
        Assert.Equal(rows, (await HistoryAsync()).Count);
    }

    [Fact]
    public async Task A_family_the_exceptions_emptied_is_written_header_only_once_then_unchanged()
    {
        AbuseIpDbService service = await WrittenAsync(Entry("8.8.8.8"), Entry("2001:4860::8888"));
        _ipinfo.Exceptions = Exceptions("2001:4860::/32");
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh emptied = await service.RefreshFeedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RollingRefreshOutcome.Written, emptied.Outcome);
        Assert.True(File.Exists(Fg6));
        Assert.All(File.ReadAllLines(Fg6), line => Assert.True(line.Length == 0 || line.StartsWith('#'), line));
        Assert.Equal([SavedFeedStampOutcome.Saved], _ipinfo.Records.CommitRefresh([emptied.Stamp!]));
        Dictionary<string, byte[]> before = Files();
        _time.Advance(TimeSpan.FromMinutes(5));

        RollingFeedRefresh again = await service.RefreshFeedAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RollingRefreshOutcome.Unchanged, again.Outcome);
        AssertSameFiles(before, Files());
    }

    private void Seed(DateTimeOffset generatedAt, DateTimeOffset downloadedAt, params BlacklistEntry[] entries)
    {
        ListRead<ListMerge> merged = new AbuseIpDbList(ListPath)
            .Merge(new Blacklist(generatedAt, entries, 0, 0, 0), downloadedAt, downloadedAt.AddDays(-30), Ct);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    private static string Unix(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private void Execute(string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static PoorMansFeed.Core.FeedExceptions.ExceptionList Exceptions(params string[] networks) =>
        PoorMansFeed.Core.FeedExceptions.ExceptionList.Create(networks.Select(network =>
        {
            Assert.True(PoorMansFeed.Core.FeedExceptions.ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _));
            return new PoorMansFeed.Core.FeedExceptions.ExceptionEntry(block, null);
        }));

    private static BlacklistEntry Entry(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
        return new BlacklistEntry(value, 50, null, null);
    }
}
