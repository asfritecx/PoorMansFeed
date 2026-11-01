using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>The rolling feed's Write feed and Retire: the feed lock, the change record with reasons, the saved feed.</summary>
public sealed class AbuseFeedRecordTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 19, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Generated = new(2026, 10, 19, 7, 29, 58, TimeSpan.Zero);

    private readonly InMemorySecretStore _keys = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ServiceTempFolder _temp = new();
    private readonly SavedFeedListStore _store = new();
    private readonly FakeIpInfoService _ipinfo;
    private readonly HttpClient _http;

    public AbuseFeedRecordTests()
    {
        _ipinfo = new FakeIpInfoService { Records = new FeedRecords(_temp.Paths, _time, _store) };
        _http = new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("The feed sent a request.")));
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    /// <summary>The saved feeds kept as config.db keeps them, so a pick the list refuses fails the save.</summary>
    private sealed class SavedFeedListStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public int Applies { get; private set; }

        public SavedFeedsRead Read() => new(true, _list.Feeds);

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            Applies++;
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

    private string Ms4 => AbuseIpDbFeedWriter.PathFor(ExportFolder, FeedType.ModSecurity, ipv6: false);

    private AbuseIpDbService Service() =>
        new(_http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _keys, new FakeAbuseAccounting());

    private async Task<IReadOnlyList<HistoryRow>> HistoryAsync(string? match = null)
    {
        HistoryMatch? filter = null;
        if (match is not null)
            Assert.True(HistoryMatch.TryParse(match, out filter));
        HistoryRead read = await _ipinfo.Records.SearchHistoryAsync(new HistoryQuery(null, null, filter), 1000, Ct);
        return read.Rows;
    }

    private static FeedRemovalReason RemovedReason(IReadOnlyList<HistoryRow> rows, string entry) =>
        Assert.Single(rows, row => !row.Added && row.Entry == entry).Reason;

    [Fact]
    public async Task A_first_write_saves_the_rolling_feed_and_records_every_address_added()
    {
        Seed(Generated, Downloaded, Entry("9.9.9.9"), Entry("8.8.8.8"), Entry("2001:4860::8888"));

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        SavedFeed feed = Assert.Single(_store.Read().Feeds);
        Assert.Equal("AbuseIPDB_rolling", feed.Key);
        Assert.Equal(FeedKind.AbuseIpDbRolling, feed.Kind);
        Assert.Equal(Now, feed.LastWriteUtc);
        Assert.False(feed.Retired);
        Assert.Null(feed.Pick.Asn);
        Assert.Null(feed.Pick.GoogleScope);
        Assert.Empty(feed.Pick.HandTicked);
        Assert.Empty(feed.Pick.LargeLeftOut);
        Assert.Null(feed.Pick.LargeRangeCheckEnabled);
        Assert.Empty(feed.Pick.SeenUnconfirmed ?? []);
        SavedFeedWriteProofTests.AssertProofOfDisk(ExportFolder, result.FeedChanges!, feed.Proof,
                                                   FeedOptions.ForRollingFeed(_ipinfo.Settings));
        Assert.Equal(result.Files.Count, feed.Proof!.FileSha256.Count);

        IReadOnlyList<HistoryRow> rows = await HistoryAsync();
        Assert.Equal(["2001:4860::8888", "8.8.8.8", "9.9.9.9"], rows.Select(row => row.Entry).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.True(row.Added);
            Assert.Equal("AbuseIPDB_rolling", row.FeedKey);
            Assert.Equal(FeedKind.AbuseIpDbRolling, row.Kind);
            Assert.Equal(FeedChangeCause.PageWrite, row.Cause);
            Assert.Equal(Now, row.AtUtc);
            Assert.Equal(Downloaded, row.ListDownloadedUtc);
            Assert.Null(row.DatasetDate);
        });
    }

    [Fact]
    public async Task An_address_an_exception_now_covers_is_removed_for_the_exception()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("9.9.9.9"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);

        _ipinfo.Exceptions = Exceptions("9.9.9.0/24");
        AbuseFeedWriteResult result = await service.WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(FeedRemovalReason.Exception, RemovedReason(await HistoryAsync(), "9.9.9.9"));
    }

    [Fact]
    public async Task An_IPv6_address_after_IPv6_is_switched_off_is_removed_for_the_family()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);

        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        AbuseFeedWriteResult result = await service.WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal([Fg6], result.EmptiedFiles);
        Assert.Equal(FeedRemovalReason.FamilyOff, RemovedReason(await HistoryAsync(), "2001:4860::8888"));
    }

    [Fact]
    public async Task An_address_no_longer_within_the_keep_days_is_removed_for_the_keep_days()
    {
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 1 };
        Seed(Generated.AddDays(-2), Downloaded.AddDays(-2), Entry("8.8.8.8"), Entry("9.9.9.9"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);

        // 9.9.9.9 stays in the list, last listed two days before this download, past the one keep day
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseFeedWriteResult result = await service.WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(FeedRemovalReason.LeftTheList, RemovedReason(await HistoryAsync(), "9.9.9.9"));
    }

    [Fact]
    public async Task An_address_pushed_past_the_cap_is_removed_as_left_out()
    {
        Seed(Generated, Downloaded, Entry("9.9.9.9"));
        string seen = Unix(Downloaded.AddHours(-1));
        Execute(InsertManySql(AbuseIpDbLimits.MaxFeedEntries - 1, seen));
        AbuseIpDbService service = Service();
        AbuseFeedWriteResult first = await service.WriteFeedAsync(Ct);
        Assert.Equal(AbuseIpDbLimits.MaxFeedEntries, first.IPv4Written);
        Assert.Equal(0, first.LeftOut);

        // One more address, so the one listed longest ago no longer fits under the cap
        string older = Unix(Downloaded.AddDays(-2));
        Execute($"UPDATE addresses SET first_seen = {older}, last_seen = {older} WHERE ip = X'08000005';"
                + $"INSERT INTO addresses VALUES (X'01010101', {seen}, {seen}, 50, NULL, NULL);");
        AbuseFeedWriteResult result = await service.WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(1, result.LeftOut);
        Assert.Equal(FeedRemovalReason.LeftOut, RemovedReason(await HistoryAsync("8.0.0.5"), "8.0.0.5"));
    }

    [Fact]
    public async Task A_write_that_fails_after_a_file_moved_records_and_saves()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        File.Delete(Fg6);
        Directory.CreateDirectory(Fg6);
        Seed(Generated, Downloaded, Entry("1.1.1.1"));
        _time.Advance(TimeSpan.FromMinutes(5));

        AbuseFeedWriteResult result = await service.WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal([Fg4], result.Files);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        Assert.Equal(Now.AddMinutes(5), Assert.Single(_store.Read().Feeds).LastWriteUtc);
        Assert.Null(Assert.Single(_store.Read().Feeds).Proof);
        Assert.Single(await HistoryAsync("1.1.1.1"), row => row.Added && row.AtUtc == Now.AddMinutes(5));
    }

    [Fact]
    public async Task A_write_that_fails_before_any_file_moved_records_and_saves_nothing()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        Directory.CreateDirectory(Fg4);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        await AssertNothingRecordedAsync(result);
    }

    [Fact]
    public async Task Nothing_to_write_records_and_saves_nothing()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        _ipinfo.Exceptions = Exceptions("8.8.8.8");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, result.Failure);
        await AssertNothingRecordedAsync(result);
    }

    [Fact]
    public async Task An_export_folder_that_cant_be_made_records_and_saves_nothing()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        File.WriteAllText(ExportFolder, "a file where the folder goes");

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.FolderUnavailable, result.Failure);
        await AssertNothingRecordedAsync(result);
    }

    [Theory]
    [InlineData(AbuseFeedWriteFailure.NoFeedType)]
    [InlineData(AbuseFeedWriteFailure.ExceptionsUnreadable)]
    [InlineData(AbuseFeedWriteFailure.ListUnreadable)]
    [InlineData(AbuseFeedWriteFailure.NoDownload)]
    [InlineData(AbuseFeedWriteFailure.ClockWrong)]
    public async Task A_refused_write_records_and_saves_nothing(AbuseFeedWriteFailure refusal)
    {
        switch (refusal)
        {
            case AbuseFeedWriteFailure.NoFeedType:
                Seed(Generated, Downloaded, Entry("8.8.8.8"));
                _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };
                break;
            case AbuseFeedWriteFailure.ExceptionsUnreadable:
                Seed(Generated, Downloaded, Entry("8.8.8.8"));
                _ipinfo.Exceptions = ExceptionList.Unreadable;
                break;
            case AbuseFeedWriteFailure.ListUnreadable:
                File.WriteAllBytes(ListPath, [.. "not a database file, only some bytes in its place"u8]);
                break;
            case AbuseFeedWriteFailure.ClockWrong:
                Seed(Now.AddHours(25).AddSeconds(-2), Now.AddHours(25), Entry("8.8.8.8"));
                break;
        }

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(refusal, result.Failure);
        await AssertNothingRecordedAsync(result);
    }

    [Fact]
    public async Task Write_feed_waits_for_the_rolling_feeds_lock_and_reads_the_settings_once_it_has_it()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        IFeedRecords records = _ipinfo.Records;
        Task<IDisposable> entered;
        // The other write's own flow: one started inside the test's flow would carry its hold into Write feed
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => records.EnterWrite("AbuseIPDB_rolling"));
        IDisposable other = await entered;

        Task<AbuseFeedWriteResult> write = Service().WriteFeedAsync(Ct);
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(2, records.WritesInFlight);
        Assert.False(write.IsCompleted);
        Assert.False(File.Exists(Fg4));
        // Changed while Write feed waits: it reads the settings only once it holds the lock
        _ipinfo.Settings = _ipinfo.Settings with { WriteModSecurityLists = true };
        other.Dispose();
        AbuseFeedWriteResult result = await write.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.True(File.Exists(Fg4));
        Assert.True(File.Exists(Ms4));
        Assert.Equal(0, records.WritesInFlight);
    }

    [Fact]
    public async Task After_the_writes_stopped_Write_feed_fails_and_writes_nothing()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        await _ipinfo.Records.StopWritesAsync(Ct);

        AbuseFeedWriteResult result = await Service().WriteFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.False(File.Exists(Fg4));
        Assert.False(Directory.Exists(ExportFolder));
        await AssertNothingRecordedAsync(result);
    }

    [Fact]
    public async Task Retire_records_each_old_address_removed_and_marks_the_saved_feed_retired()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        Assert.NotNull(Assert.Single(_store.Read().Feeds).Proof);
        _time.Advance(TimeSpan.FromMinutes(5));

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        SavedFeed feed = Assert.Single(_store.Read().Feeds);
        Assert.True(feed.Retired);
        Assert.Null(feed.Proof);
        Assert.All(result.FeedChanges!, change => Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(change.Path))), change.WrittenSha256));
        Assert.Equal(Now, feed.LastWriteUtc);
        IReadOnlyList<HistoryRow> removed = (await HistoryAsync()).Where(row => !row.Added).ToList();
        Assert.Equal(["2001:4860::8888", "8.8.8.8"], removed.Select(row => row.Entry).Order(StringComparer.Ordinal));
        Assert.All(removed, row =>
        {
            Assert.Equal(FeedChangeCause.Retire, row.Cause);
            Assert.Equal(FeedRemovalReason.NotTold, row.Reason);
            Assert.Equal("AbuseIPDB_rolling", row.FeedKey);
            Assert.Equal(Now.AddMinutes(5), row.AtUtc);
            Assert.Null(row.DatasetDate);
            Assert.Null(row.ListDownloadedUtc);
        });
    }

    [Fact]
    public async Task Retiring_header_only_files_marks_the_feed_retired_and_records_no_history_row()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        // An exception now covers the one address, so Write feed leaves the file header-only
        _ipinfo.Exceptions = Exceptions("8.8.8.8");
        Assert.Equal([Fg4], (await service.WriteFeedAsync(Ct)).EmptiedFiles);
        int rows = (await HistoryAsync()).Count;
        Assert.False(Assert.Single(_store.Read().Feeds).Retired);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.NotNull(result.FeedChanges);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.NothingToRecord), result.Record);
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
        Assert.Equal(rows, (await HistoryAsync()).Count);
        Assert.DoesNotContain(await HistoryAsync(), row => row.Cause == FeedChangeCause.Retire);
    }

    [Fact]
    public async Task Retire_with_no_saved_feed_writes_records_and_asks_no_save()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        Assert.Equal(SavedFeedsFailure.None, await _ipinfo.Records.RemoveSavedFeedAsync("AbuseIPDB_rolling", Ct));

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), result.Record);
        Assert.Empty(_store.Read().Feeds);
        Assert.Single(await HistoryAsync(), row => !row.Added && row.Entry == "8.8.8.8" && row.Cause == FeedChangeCause.Retire);
        Assert.Contains("# Entries: 0", File.ReadAllLines(Fg4));
    }

    [Fact]
    public async Task A_retire_that_fails_after_a_file_moved_records_it_and_marks_the_feed_retired()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"), Entry("2001:4860::8888"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        File.Delete(Fg6);
        Directory.CreateDirectory(Fg6);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Equal([Fg4], result.Files);
        Assert.Equal(Fg6, result.FailedFile);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
        Assert.Single(await HistoryAsync(), row => !row.Added && row.Cause == FeedChangeCause.Retire);
        Assert.Single(await HistoryAsync("8.8.8.8"), row => !row.Added && row.Cause == FeedChangeCause.Retire);
    }

    [Fact]
    public async Task A_retire_with_nothing_to_write_records_and_saves_nothing()
    {
        AbuseFeedWriteResult result = await Service().EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.NothingToWrite, result.Failure);
        await AssertNothingRecordedAsync(result);
    }

    [Fact]
    public async Task A_retire_that_fails_before_any_file_moved_leaves_the_saved_feed_as_it_was()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        int rows = (await HistoryAsync()).Count;
        File.Delete(Fg4);
        Directory.CreateDirectory(Fg4);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Null(result.FeedChanges);
        await AssertUnchangedAsync(result, rows);
    }

    [Fact]
    public async Task After_the_writes_stopped_a_retire_fails_writes_nothing_and_leaves_the_saved_feed()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        int rows = (await HistoryAsync()).Count;
        string before = File.ReadAllText(Fg4);
        await _ipinfo.Records.StopWritesAsync(Ct);

        AbuseFeedWriteResult result = await service.EmptyFeedAsync(Ct);

        Assert.Equal(AbuseFeedWriteFailure.WriteFailed, result.Failure);
        Assert.Empty(result.Files);
        Assert.Equal(before, File.ReadAllText(Fg4));
        await AssertUnchangedAsync(result, rows);
    }

    [Fact]
    public async Task A_retire_waits_for_the_rolling_feeds_lock_and_completes_once_it_is_released()
    {
        Seed(Generated, Downloaded, Entry("8.8.8.8"));
        AbuseIpDbService service = Service();
        await service.WriteFeedAsync(Ct);
        string before = File.ReadAllText(Fg4);
        IFeedRecords records = _ipinfo.Records;
        Task<IDisposable> entered;
        // The other write's own flow: one started inside the test's flow would carry its hold into the retire
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => records.EnterWrite("AbuseIPDB_rolling"));
        IDisposable other = await entered;

        Task<AbuseFeedWriteResult> retire = service.EmptyFeedAsync(Ct);
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(2, records.WritesInFlight);
        Assert.False(retire.IsCompleted);
        Assert.Equal(before, File.ReadAllText(Fg4));
        other.Dispose();
        AbuseFeedWriteResult result = await retire.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AbuseFeedWriteFailure.None, result.Failure);
        Assert.Contains("# Entries: 0", File.ReadAllLines(Fg4));
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
        Assert.Equal(0, records.WritesInFlight);
    }

    private async Task AssertUnchangedAsync(AbuseFeedWriteResult result, int rows)
    {
        Assert.Null(result.Record);
        Assert.Equal(1, _store.Applies);
        SavedFeed feed = Assert.Single(_store.Read().Feeds);
        Assert.False(feed.Retired);
        Assert.Equal(Now, feed.LastWriteUtc);
        Assert.Equal(rows, (await HistoryAsync()).Count);
    }

    private async Task AssertNothingRecordedAsync(AbuseFeedWriteResult result)
    {
        Assert.Null(result.Record);
        Assert.Equal(0, _store.Applies);
        Assert.Empty(_store.Read().Feeds);
        Assert.Empty(await HistoryAsync());
    }

    private void Seed(DateTimeOffset generatedAt, DateTimeOffset downloadedAt, params BlacklistEntry[] entries)
    {
        ListRead<ListMerge> merged = new AbuseIpDbList(ListPath)
            .Merge(new Blacklist(generatedAt, entries, 0, 0, 0), downloadedAt, downloadedAt.AddDays(-30), Ct);
        Assert.Equal(AbuseListState.Usable, merged.State);
    }

    private static string Unix(DateTimeOffset time) =>
        time.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Inserts count rows of 8.x.x.x addresses (8.0.0.1 upwards), all last seen at the Unix seconds.</summary>
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
