using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>The saved feeds page's door: pass-through to the records, Retire by kind, typed failures only.</summary>
public sealed class SavedFeedsServiceTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly PassingRecords _records = new();
    private readonly FakeIpInfoService _ipinfo;
    private readonly FakeAbuseIpDbService _abuse = new();

    public SavedFeedsServiceTests()
    {
        _ipinfo = new FakeIpInfoService
        {
            Records = _records,
            Settings = new UserSettings { ExportFolder = _temp.ExportFolder, WriteFortigateFeeds = true },
        };
    }

    public void Dispose() => _temp.Dispose();

    private SavedFeedsService Service() => new(_ipinfo, _abuse, _time, _temp.Paths.DefaultExportFolder);

    /// <summary>Answers each call with what is set and records its arguments; every call throws Failure.</summary>
    private sealed class PassingRecords : IFeedRecords
    {
        private readonly IFeedRecords _locks = FeedRecords.None;

        public Exception? Failure { get; set; }
        public SavedFeedsRead Saved { get; set; } = new(true, []);
        public HistoryRead History { get; set; } = HistoryRead.Empty(HistoryState.Usable);
        public SavedFeedsFailure RemoveAnswer { get; set; } = SavedFeedsFailure.None;
        public HistoryState ClearAnswer { get; set; } = HistoryState.Missing;
        public List<string> Calls { get; } = [];
        public List<object?> Arguments { get; } = [];

        public FeedRecordResult Record(FeedRecordRequest request) => throw new NotSupportedException();

        public IDisposable EnterWrite(string feedKey) => _locks.EnterWrite(feedKey);

        public int WritesInFlight => _locks.WritesInFlight;

        public Task StopWritesAsync(CancellationToken ct) => _locks.StopWritesAsync(ct);

        public Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct)
        {
            Calls.Add(nameof(SearchHistoryAsync));
            Arguments.AddRange([query, limit, ct]);
            return Failure is null ? Task.FromResult(History) : Task.FromException<HistoryRead>(Failure);
        }

        public Task<HistoryState> ClearHistoryAsync(CancellationToken ct)
        {
            Calls.Add(nameof(ClearHistoryAsync));
            Arguments.Add(ct);
            return Failure is null ? Task.FromResult(ClearAnswer) : Task.FromException<HistoryState>(Failure);
        }

        public SavedFeedsRead ReadSavedFeeds()
        {
            Calls.Add(nameof(ReadSavedFeeds));
            return Failure is null ? Saved : throw Failure;
        }

        IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps) =>
            throw new NotSupportedException();
        bool IFeedRecords.SaveRefusedThisSession(string feedKey) => false;

        public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct)
        {
            Calls.Add(nameof(RemoveSavedFeedAsync));
            Arguments.AddRange([key, ct]);
            return Failure is null ? Task.FromResult(RemoveAnswer) : Task.FromException<SavedFeedsFailure>(Failure);
        }
    }

    private static AbuseFeedWriteResult Rolling(AbuseFeedWriteFailure failure, IReadOnlyList<string>? oldFilesLeft = null,
                                                FeedRecordResult? record = null) =>
        new(failure, ["a.txt"], 0, 0, 0, 0, [], "b.txt", oldFilesLeft, null, null, record);

    private static readonly SavedFeedPick NoPick = new(null, null, false, null, null, null, [], [], [], [], null, null, null);

    private void Save(params (FeedKind Kind, string Key)[] feeds) =>
        _records.Saved = new SavedFeedsRead(true, feeds.Select(feed => new SavedFeed(feed.Key, feed.Kind, NoPick,
                                                                                       _time.GetUtcNow(), null, false)).ToList());

    [Fact]
    public void Read_saved_feeds_passes_through_and_a_throwing_store_reads_as_unavailable()
    {
        var saved = new SavedFeedsRead(true, []);
        _records.Saved = saved;
        Assert.Same(saved, Service().ReadSavedFeeds());

        _records.Failure = new IOException("disk");
        SavedFeedsRead failed = Service().ReadSavedFeeds();
        Assert.False(failed.Available);
        Assert.Empty(failed.Feeds);
    }

    [Fact]
    public async Task Search_history_passes_its_arguments_through()
    {
        var read = new HistoryRead(HistoryState.Usable, [], 3, ["AS1"]);
        _records.History = read;
        var query = new HistoryQuery("AS1", null, null);
        using var cts = new CancellationTokenSource();

        Assert.Same(read, await Service().SearchHistoryAsync(query, 25, cts.Token));
        Assert.Equal([query, 25, cts.Token], _records.Arguments);
    }

    [Fact]
    public async Task Remove_and_clear_pass_through()
    {
        _records.RemoveAnswer = SavedFeedsFailure.NotFound;
        _records.ClearAnswer = HistoryState.Usable;

        Assert.Equal(SavedFeedsFailure.NotFound, await Service().RemoveAsync("AS1", Ct));
        Assert.Equal(HistoryState.Usable, await Service().ClearHistoryAsync(Ct));
        Assert.Equal([nameof(IFeedRecords.RemoveSavedFeedAsync), nameof(IFeedRecords.ClearHistoryAsync)], _records.Calls);
        Assert.Equal(["AS1", Ct, Ct], _records.Arguments);
    }

    [Fact]
    public async Task A_throwing_store_gives_each_methods_typed_failure()
    {
        // Another flow's cancel is not the caller's: it is a failure like any other
        _records.Failure = new OperationCanceledException();

        HistoryRead read = await Service().SearchHistoryAsync(new HistoryQuery(null, null, null), 10, Ct);
        Assert.Equal(HistoryState.StorageError, read.State);
        Assert.Empty(read.Rows);
        Assert.Equal(SavedFeedsFailure.StorageError, await Service().RemoveAsync("AS1", Ct));
        Assert.Equal(HistoryState.StorageError, await Service().ClearHistoryAsync(Ct));
    }

    [Fact]
    public async Task The_callers_own_cancel_escapes_every_async_method()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Save((FeedKind.Asn, "AS1"), (FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling"));
        _records.Failure = new OperationCanceledException(cts.Token);
        SavedFeedsService service = Service();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchHistoryAsync(new HistoryQuery(null, null, null), 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RemoveAsync("AS1", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ClearHistoryAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RetireAsync(FeedKind.Asn, "AS1", cts.Token));
        _abuse.EmptyGate = new TaskCompletionSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RetireAsync(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", cts.Token));
    }

    [Theory]
    [InlineData(AbuseFeedWriteFailure.None, RetireFailure.None)]
    [InlineData(AbuseFeedWriteFailure.NothingToWrite, RetireFailure.NothingToRetire)]
    [InlineData(AbuseFeedWriteFailure.WriteFailed, RetireFailure.WriteFailed)]
    [InlineData(AbuseFeedWriteFailure.ListUnreadable, RetireFailure.WriteFailed)]
    public async Task The_rolling_kind_retires_through_Empty_feed_and_maps_its_result(AbuseFeedWriteFailure failure,
                                                                                    RetireFailure expected)
    {
        var record = new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded);
        Save((FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling"));
        _abuse.EmptyResults.Enqueue(Rolling(failure, ["c.txt"], record));
        using var cts = new CancellationTokenSource();

        RetireResult result = await Service().RetireAsync(FeedKind.AbuseIpDbRolling, "abuseipdb_ROLLING", cts.Token);

        Assert.Equal(expected, result.Failure);
        Assert.Equal(_temp.ExportFolder, result.Folder);
        Assert.Equal(["a.txt"], result.Files);
        Assert.Equal("b.txt", result.FailedFile);
        Assert.Equal(["c.txt"], result.OldFilesLeft);
        Assert.Same(record, result.Record);
        Assert.Equal([cts.Token], _abuse.EmptyTokens);
    }

    [Fact]
    public async Task No_old_files_named_by_Empty_feed_reads_as_none()
    {
        Save((FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling"));
        _abuse.EmptyResults.Enqueue(Rolling(AbuseFeedWriteFailure.None));

        RetireResult result = await Service().RetireAsync(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", Ct);

        Assert.Empty(result.OldFilesLeft);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task The_rolling_kind_with_another_key_is_not_retirable()
    {
        Save((FeedKind.AbuseIpDbRolling, "AS15169"));
        RetireResult result = await Service().RetireAsync(FeedKind.AbuseIpDbRolling, "AS15169", Ct);

        Assert.Equal(RetireFailure.NotRetirable, result.Failure);
        Assert.Equal(0, _abuse.EmptyCalls);
    }

    [Fact]
    public async Task An_ASN_or_cloud_kind_runs_the_retire_writer_and_never_Empty_feed()
    {
        Save((FeedKind.Asn, "AS15169"), (FeedKind.Aws, "AWS_EC2"), (FeedKind.GoogleCloud, "../x"));
        RetireResult asn = await Service().RetireAsync(FeedKind.Asn, "AS15169", Ct);
        RetireResult cloud = await Service().RetireAsync(FeedKind.Aws, "AWS_EC2", Ct);
        RetireResult unsafeKey = await Service().RetireAsync(FeedKind.GoogleCloud, "../x", Ct);

        Assert.Equal(RetireFailure.NothingToRetire, asn.Failure);
        Assert.Equal(_temp.ExportFolder, asn.Folder);
        Assert.Equal(RetireFailure.NothingToRetire, cloud.Failure);
        Assert.Equal(RetireFailure.NotRetirable, unsafeKey.Failure);
        Assert.Equal(0, _abuse.EmptyCalls);
    }

    [Fact]
    public async Task A_throwing_dependency_in_a_retire_gives_write_failed()
    {
        Save((FeedKind.Asn, "AS15169"), (FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling"));
        // Nothing queued: the fake's Empty feed throws
        RetireResult rolling = await Service().RetireAsync(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", Ct);
        _ipinfo.SettingsFailure = new InvalidOperationException("settings");
        RetireResult asn = await Service().RetireAsync(FeedKind.Asn, "AS15169", Ct);
        RetireResult rollingSettings = await Service().RetireAsync(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", Ct);

        Assert.Equal(RetireFailure.WriteFailed, rolling.Failure);
        Assert.Null(rolling.Record);
        Assert.Equal(RetireFailure.WriteFailed, asn.Failure);
        Assert.Empty(asn.Files);
        Assert.Equal(RetireFailure.WriteFailed, rollingSettings.Failure);
    }
    [Theory]
    [InlineData(FeedKind.Asn, "AS15169", FeedKind.Azure, "AS15169", true)]
    [InlineData(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", FeedKind.Aws, "AbuseIPDB_rolling", true)]
    [InlineData(FeedKind.Asn, "AS15169", FeedKind.Asn, "AS64500", true)]
    [InlineData(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling", false)]
    public async Task A_retire_of_a_feed_not_saved_as_that_kind_touches_nothing(FeedKind savedKind, string savedKey,
                                                                                 FeedKind kind, string key, bool readable)
    {
        Save((savedKind, savedKey));
        if (!readable)
            _records.Saved = _records.Saved with { Available = false };
        // The rolling feed's names fit the cloud pattern too, so a wrong kind would rewrite them without its header
        string rolling = Path.Combine(_temp.ExportFolder, "fortigate", "AbuseIPDB_rolling_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(rolling)!);
        File.WriteAllText(rolling, "# PoorMansFeed - Fortigate Threat Feed\n8.8.8.8\n");
        string asn = Path.Combine(_temp.ExportFolder, "fortigate", key + "_ipv4.txt");
        File.WriteAllText(asn, "# PoorMansFeed - Fortigate Threat Feed\n8.8.8.0/24\n");
        byte[] rollingBefore = File.ReadAllBytes(rolling);
        byte[] asnBefore = File.ReadAllBytes(asn);

        RetireResult result = await Service().RetireAsync(kind, key, Ct);

        Assert.Equal(RetireFailure.NotSaved, result.Failure);
        Assert.Empty(result.Files);
        Assert.Null(result.Record);
        Assert.Equal(0, _abuse.EmptyCalls);
        Assert.Equal(rollingBefore, File.ReadAllBytes(rolling));
        Assert.Equal(asnBefore, File.ReadAllBytes(asn));
    }
    [Fact]
    public async Task A_retire_goes_by_the_saved_feeds_own_key_whatever_case_the_caller_used()
    {
        Save((FeedKind.Azure, "Azure_Test"));
        string path = Path.Combine(_temp.ExportFolder, "fortigate", "Azure_Test_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# PoorMansFeed - Fortigate Threat Feed\n34.1.0.0/16\n");

        RetireResult result = await Service().RetireAsync(FeedKind.Azure, "azure_test", Ct);

        // The history and the save go by that key, and on a case-sensitive disk only it names the files
        Assert.Equal(RetireFailure.None, result.Failure);
        Assert.Equal([path], result.Files);
    }
}
