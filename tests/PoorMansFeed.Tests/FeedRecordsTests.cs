using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class FeedRecordsTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Dataset = new(2026, 10, 1);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(T);
    private readonly FakeSavedFeedStore _store = new();

    public void Dispose() => _temp.Dispose();

    private sealed class FakeSavedFeedStore : ISavedFeedStore
    {
        public List<(string Key, FeedKind Kind, SavedFeedIntent Intent, DateTimeOffset AtUtc)> Applies { get; } = [];
        public List<string> Removes { get; } = [];
        public SavedFeedsFailure Failure { get; set; }
        public Exception? Throws { get; set; }
        public SavedFeedsRead ReadResult { get; set; } = new(true, []);

        public SavedFeedsRead Read() => ReadResult;

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            Applies.Add((key, kind, intent, atUtc));
            return Throws is null ? Failure : throw Throws;
        }

        public List<IReadOnlyList<SavedFeedStamp>> StampBatches { get; } = [];
        public SavedFeedStampOutcome StampOutcome { get; set; } = SavedFeedStampOutcome.Saved;

        public IReadOnlyList<SavedFeedStampOutcome> ApplyStamps(IReadOnlyList<SavedFeedStamp> stamps)
        {
            StampBatches.Add(stamps);
            return Throws is null ? stamps.Select(_ => StampOutcome).ToList() : throw Throws;
        }

        public SavedFeedsFailure Remove(string key)
        {
            Removes.Add(key);
            return Throws is null ? Failure : throw Throws;
        }
    }

    private static FeedEntryChanges Entries(string[] added, string[]? removed = null) => new(added, removed ?? []);

    private static FeedFileChange Copy(FeedType type, FeedFamilies families, FeedEntryChanges? ipv4 = null,
                                       FeedEntryChanges? ipv6 = null, FeedFileState old = FeedFileState.Read) =>
        new($"/feeds/{type}/{families}.txt", type, families, ipv4 ?? FeedEntryChanges.None, ipv6 ?? FeedEntryChanges.None, old);

    private static readonly FeedType[] Both = [FeedType.Fortigate, FeedType.ModSecurity];
    private static readonly FeedType[] FortiGateOnly = [FeedType.Fortigate];

    private static FeedWriteRecord Write(FeedType[] types, IReadOnlyList<FeedFileChange>? changes,
                                         RemovalReasons? reasons = null,
                                         SavedFeedIntent? intent = null) =>
        new("asn:15169", FeedKind.Asn, FeedChangeCause.RefreshAll, T, types, changes, reasons, Dataset, T.AddHours(-3),
            intent);

    private static (FeedRecordResult Result, FeedChangeRecord? Change, RecordingRecords Records) Recorded(FeedWriteRecord write)
    {
        var records = new RecordingRecords();
        var service = new FakeIpInfoService { Records = records };
        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, write);
        FeedRecordRequest request = Assert.Single(records.Requests);
        Assert.Equal("asn:15169", request.FeedKey);
        Assert.Equal(FeedKind.Asn, request.Kind);
        Assert.Equal(T, request.AtUtc);
        Assert.Equal(write.Intent, request.Intent);
        return (result, request.Change, records);
    }

    private static FeedChangeRecord ChangeOf(FeedWriteRecord write)
    {
        (FeedRecordResult result, FeedChangeRecord? change, _) = Recorded(write);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), result);
        return Assert.IsType<FeedChangeRecord>(change);
    }

    private static string[] Texts(FeedFamilyChange family) => family.Removed.Select(removal => removal.Text).ToArray();

    [Fact]
    public void Equal_copies_give_one_change_per_family_naming_no_copy()
    {
        FeedEntryChanges ipv4 = Entries(["8.8.8.0/24", "8.8.4.0/24"], ["9.9.9.0/24"]);
        FeedEntryChanges ipv6 = Entries(["2001:4860::/32"]);

        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: ipv4), Copy(FeedType.Fortigate, FeedFamilies.IPv6, ipv6: ipv6),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24", "8.8.4.0/24"], ["9.9.9.0/24"])),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv6, ipv6: ipv6),
        ]));

        Assert.Equal(T, change.AtUtc);
        Assert.Equal("asn:15169", change.FeedKey);
        Assert.Equal(FeedKind.Asn, change.Kind);
        Assert.Equal(FeedChangeCause.RefreshAll, change.Cause);
        Assert.Equal(Dataset, change.DatasetDate);
        Assert.Equal(T.AddHours(-3), change.ListDownloadedUtc);
        Assert.Collection(change.Families,
            family =>
            {
                Assert.False(family.IsIPv6);
                Assert.Null(family.Copy);
                Assert.Equal(FeedFileState.Read, family.OldFile);
                Assert.Equal(["8.8.8.0/24", "8.8.4.0/24"], family.Added);
                Assert.Equal(["9.9.9.0/24"], Texts(family));
            },
            family =>
            {
                Assert.True(family.IsIPv6);
                Assert.Null(family.Copy);
                Assert.Equal(["2001:4860::/32"], family.Added);
                Assert.Empty(family.Removed);
            });
    }

    [Fact]
    public void A_missing_FortiGate_copy_beside_a_read_ModSecurity_copy_gives_one_change_per_copy()
    {
        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24", "8.8.4.0/24"]), old: FeedFileState.Missing),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.4.0/24"], ["9.9.9.0/24"])),
        ]));

        Assert.Collection(change.Families,
            family =>
            {
                Assert.Equal(FeedType.Fortigate, family.Copy);
                Assert.Equal(FeedFileState.Missing, family.OldFile);
                Assert.Equal(["8.8.8.0/24", "8.8.4.0/24"], family.Added);
                Assert.Empty(family.Removed);
            },
            family =>
            {
                Assert.Equal(FeedType.ModSecurity, family.Copy);
                Assert.Equal(FeedFileState.Read, family.OldFile);
                Assert.Equal(["8.8.4.0/24"], family.Added);
                Assert.Equal(["9.9.9.0/24"], Texts(family));
            });
    }

    [Fact]
    public void Copies_that_differ_give_one_change_per_copy_and_skip_a_copy_with_no_change()
    {
        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"])),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4),
        ]));

        FeedFamilyChange family = Assert.Single(change.Families);
        Assert.Equal(FeedType.Fortigate, family.Copy);
        Assert.Equal(["8.8.8.0/24"], family.Added);
    }

    [Fact]
    public void Copies_that_differ_only_in_their_old_file_are_told_apart()
    {
        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]), old: FeedFileState.Unreadable),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"])),
        ]));

        Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], change.Families.Select(family => family.Copy));
    }

    [Fact]
    public void Copies_that_differ_only_in_entry_order_are_told_apart()
    {
        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24", "8.8.4.0/24"])),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.4.0/24", "8.8.8.0/24"])),
        ]));

        Assert.Equal(2, change.Families.Count);
    }

    [Fact]
    public void Copies_that_differ_only_in_what_they_removed_are_told_apart()
    {
        FeedChangeRecord change = ChangeOf(Write(Both, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries([], ["9.9.9.0/24"])),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries([], ["1.1.1.0/24"])),
        ]));

        Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], change.Families.Select(family => family.Copy));
        Assert.Equal(["9.9.9.0/24"], Texts(change.Families[0]));
        Assert.Equal(["1.1.1.0/24"], Texts(change.Families[1]));
    }

    [Fact]
    public void The_unsplit_file_gives_both_families_from_one_file()
    {
        FeedChangeRecord change = ChangeOf(Write(FortiGateOnly, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.Both, ipv4: Entries(["8.8.8.0/24"]), ipv6: Entries([], ["2001:4860::/32"])),
        ]));

        Assert.Collection(change.Families,
            family =>
            {
                Assert.False(family.IsIPv6);
                Assert.Null(family.Copy);
                Assert.Equal(["8.8.8.0/24"], family.Added);
            },
            family =>
            {
                Assert.True(family.IsIPv6);
                Assert.Null(family.Copy);
                Assert.Empty(family.Added);
                Assert.Equal(["2001:4860::/32"], Texts(family));
            });
    }

    [Fact]
    public void A_family_no_file_holds_gives_nothing_even_when_its_slot_holds_entries()
    {
        FeedChangeRecord change = ChangeOf(Write(FortiGateOnly, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]), ipv6: Entries(["2001:4860::/32"])),
        ]));

        FeedFamilyChange family = Assert.Single(change.Families);
        Assert.False(family.IsIPv6);
    }

    [Fact]
    public void Split_files_give_their_own_families()
    {
        FeedChangeRecord change = ChangeOf(Write(FortiGateOnly, 
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]), ipv6: Entries(["2001:db8::/32"])),
            Copy(FeedType.Fortigate, FeedFamilies.IPv6, ipv4: Entries(["9.9.9.0/24"]), ipv6: Entries(["2001:4860::/32"])),
        ]));

        Assert.Collection(change.Families,
            family => Assert.Equal((false, "8.8.8.0/24"), (family.IsIPv6, Assert.Single(family.Added))),
            family => Assert.Equal((true, "2001:4860::/32"), (family.IsIPv6, Assert.Single(family.Added))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_changes_or_only_empty_ones_still_ask_with_no_change(bool changesNull)
    {
        FeedWriteRecord write = Write(Both, changesNull ? null :
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4), Copy(FeedType.ModSecurity, FeedFamilies.Both),
        ]);

        (FeedRecordResult result, FeedChangeRecord? change, _) = Recorded(write);

        Assert.Null(change);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.NothingToRecord), result);
    }

    [Fact]
    public void Each_removed_entry_takes_the_reason_the_write_names()
    {
        var reasons = new RemovalReasons(ReadableExceptions.WrapForFeed(RangeSubtractionTests.List("9.9.9.0/24"))!,
                                         [Cidr.Parse("8.8.8.0/24")], [], null, FeedRemovalReason.SourceDropped);

        FeedChangeRecord change = ChangeOf(Write(FortiGateOnly, 
            [Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries([], ["9.9.9.0/24", "1.1.1.0/24", "8.8.8.0/24"]))],
            reasons));

        Assert.Equal(
            [
                new FeedRemoval("9.9.9.0/24", FeedRemovalReason.Exception),
                new FeedRemoval("1.1.1.0/24", FeedRemovalReason.SourceDropped),
                new FeedRemoval("8.8.8.0/24", FeedRemovalReason.NotTold),
            ],
            Assert.Single(change.Families).Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_reasons_or_ones_whose_sets_cannot_be_built_give_NotTold_for_every_entry(bool unbuildable)
    {
        RemovalReasons? reasons = unbuildable
            ? new RemovalReasons(ReadableExceptions.WrapForFeed(ExceptionList.Empty)!, [(new byte[5], new byte[5])], [], null,
                                 FeedRemovalReason.LeftTheList)
            : null;

        FeedChangeRecord change = ChangeOf(Write(FortiGateOnly, 
            [Copy(FeedType.Fortigate, FeedFamilies.Both, ipv4: Entries([], ["9.9.9.0/24", "1.1.1.0/24"]),
                  ipv6: Entries([], ["2001:4860::/32"]))],
            reasons));

        Assert.All(change.Families.SelectMany(family => family.Removed),
                   removal => Assert.Equal(FeedRemovalReason.NotTold, removal.Reason));
        Assert.Equal(3, change.Families.Sum(family => family.Removed.Count));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_record_that_throws_never_escapes(bool withChange)
    {
        var records = new RecordingRecords { Failure = new InvalidOperationException() };
        var service = new FakeIpInfoService { Records = records };
        FeedWriteRecord write = Write(FortiGateOnly, withChange ? [Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))] : null);

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, write);

        Assert.Single(records.Requests);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked,
                                          withChange ? HistoryOutcome.Failed : HistoryOutcome.NothingToRecord), result);
    }

    [Fact]
    public void A_change_list_that_cant_be_grouped_gives_Failed_and_never_escapes()
    {
        var records = new RecordingRecords();
        var service = new FakeIpInfoService { Records = records };

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, Write(FortiGateOnly, [null!]));

        Assert.Null(Assert.Single(records.Requests).Change);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Failed), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_change_list_that_cant_be_grouped_still_saves(bool retired)
    {
        var service = new FakeIpInfoService { Records = new FeedRecords(_temp.Paths, _time, _store) };
        SavedFeedIntent intent = retired ? new SavedFeedIntent.Retired() : Written;
        FeedWriteRecord write = Write(FortiGateOnly,
                                      [Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))],
                                      intent: intent);
        using IDisposable hold = service.Records.EnterWrite(write.FeedKey);

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, write);

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Failed), result);
        Assert.Same(intent, Assert.Single(_store.Applies).Intent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_record_that_throws_with_an_intent_gives_StorageError(bool withChange)
    {
        var records = new RecordingRecords { Failure = new InvalidOperationException() };
        var service = new FakeIpInfoService { Records = records };
        FeedWriteRecord write = Write(FortiGateOnly,
                                      withChange ? [Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))] : null,
                                      intent: Written);

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, write);

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.StorageError,
                                          withChange ? HistoryOutcome.Failed : HistoryOutcome.NothingToRecord), result);
    }

    [Fact]
    public void A_null_service_or_record_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => IpInfoService.RecordFeedWrite(null!, Write(FortiGateOnly, null)));
        Assert.Throws<ArgumentNullException>(() => IpInfoService.RecordFeedWrite(new FakeIpInfoService(), null!));
    }

    private static FeedChangeRecord OneChange(string entry = "8.8.8.0/24") =>
        new(T, "asn:15169", FeedKind.Asn, FeedChangeCause.PageWrite, Dataset, null,
            [new FeedFamilyChange(false, null, FeedFileState.Read, [entry], [])]);

    [Fact]
    public async Task A_record_lands_in_history_db_is_found_and_clear_removes_it()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, OneChange(), T));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), result);
        Assert.True(File.Exists(_temp.Paths.HistoryDatabasePath));
        HistoryRead read = await records.SearchHistoryAsync(new HistoryQuery(null, null, null), 10, CancellationToken.None);
        Assert.Equal(HistoryState.Usable, read.State);
        HistoryRow row = Assert.Single(read.Rows);
        Assert.Equal(("asn:15169", "8.8.8.0/24", true), (row.FeedKey, row.Entry, row.Added));

        Assert.Equal(HistoryState.Missing, await records.ClearHistoryAsync(CancellationToken.None));
        Assert.False(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void No_change_records_nothing_and_makes_no_file()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, null, T));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.NothingToRecord), result);
        Assert.False(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void An_unreadable_history_gives_Failed_and_is_left_as_it_was()
    {
        byte[] junk = "this is not a SQLite database, only text that fills a page or two."u8.ToArray();
        File.WriteAllBytes(_temp.Paths.HistoryDatabasePath, junk);
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, OneChange(), T));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Failed), result);
        Assert.Equal(junk, File.ReadAllBytes(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public async Task A_cancelled_search_or_clear_throws_and_the_clear_deletes_nothing()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");
        Assert.Equal(HistoryOutcome.Recorded,
                     records.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, OneChange(), T)).History);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => records.SearchHistoryAsync(new HistoryQuery(null, null, null), 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => records.ClearHistoryAsync(cts.Token));
        Assert.True(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void The_service_records_into_its_own_data_folder()
    {
        using var http = new HttpClient();
        using var service = new IpInfoService(_temp.Paths, http, new CoreOptions(), _time, new InMemorySecretStore(),
                                              new InMemorySecretStore(), () => false);
        using IDisposable hold = service.Records.EnterWrite("asn:15169");

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service,
            Write(FortiGateOnly, [Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))]));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), result);
        Assert.True(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public async Task None_records_nothing_finds_nothing_clears_nothing_and_still_locks()
    {
        IFeedRecords none = FeedRecords.None;

        using (none.EnterWrite("asn:15169"))
        {
            Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.NothingToRecord),
                         none.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, null, T)));
            Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Failed),
                         none.Record(new FeedRecordRequest("asn:15169", FeedKind.Asn, OneChange(), T)));
        }
        HistoryRead read = await none.SearchHistoryAsync(new HistoryQuery(null, null, null), 10, CancellationToken.None);
        Assert.Equal(HistoryState.Missing, read.State);
        Assert.Empty(read.Rows);
        Assert.Equal(HistoryState.Missing, await none.ClearHistoryAsync(CancellationToken.None));

        IDisposable hold = none.EnterWrite("AS15169");
        Assert.Equal(1, none.WritesInFlight);
        Task stopped = none.StopWritesAsync(CancellationToken.None);
        Assert.False(stopped.IsCompleted);
        hold.Dispose();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, none.WritesInFlight);
        Assert.Throws<InvalidOperationException>(() => none.EnterWrite("AS15170"));
        Assert.Equal(0, none.WritesInFlight);
    }

    [Fact]
    public async Task Stopping_the_writes_admits_no_new_one_and_waits_for_the_holder()
    {
        var records = new FeedRecords(_temp.Paths, _time, _store);
        IDisposable hold = records.EnterWrite("AS15169");

        Task stopped = records.StopWritesAsync(CancellationToken.None);

        Assert.False(stopped.IsCompleted);
        hold.Dispose();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => records.EnterWrite("AS15170"));
        Assert.Equal(0, records.WritesInFlight);
    }

    [Fact]
    public void Only_the_FortiGate_copy_moved_of_two_planned_names_that_copy()
    {
        FeedChangeRecord change = ChangeOf(Write(Both,
            [Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))]));

        FeedFamilyChange family = Assert.Single(change.Families);
        Assert.Equal(FeedType.Fortigate, family.Copy);
        Assert.Equal(["8.8.8.0/24"], family.Added);
    }

    [Fact]
    public void One_planned_type_with_its_file_names_no_copy()
    {
        FeedChangeRecord change = ChangeOf(Write([FeedType.ModSecurity],
            [Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"]))]));

        Assert.Null(Assert.Single(change.Families).Copy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_of_an_unplanned_type_or_no_planned_type_gives_Failed(bool noTypes)
    {
        var records = new RecordingRecords();
        var service = new FakeIpInfoService { Records = records };
        FeedWriteRecord write = Write(noTypes ? [] : FortiGateOnly,
        [
            Copy(FeedType.Fortigate, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"])),
            Copy(FeedType.ModSecurity, FeedFamilies.IPv4, ipv4: Entries(["8.8.8.0/24"])),
        ]);

        FeedRecordResult result = IpInfoService.RecordFeedWrite(service, write);

        Assert.Null(Assert.Single(records.Requests).Change);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Failed), result);
    }

    // ----- Saved feeds -----

    private static readonly SavedFeedIntent Written = new SavedFeedIntent.Written(SavedFeedListTests.AsnPick());

    private static FeedRecordRequest Request(FeedChangeRecord? change, SavedFeedIntent? intent) =>
        new("asn:15169", FeedKind.Asn, change, T.AddMinutes(5), intent);

    [Theory]
    [InlineData(SavedFeedsFailure.None, FeedSaveOutcome.Saved)]
    [InlineData(SavedFeedsFailure.NotFound, FeedSaveOutcome.NotAsked)]
    [InlineData(SavedFeedsFailure.TooMany, FeedSaveOutcome.TooMany)]
    [InlineData(SavedFeedsFailure.Unavailable, FeedSaveOutcome.Unavailable)]
    [InlineData(SavedFeedsFailure.StorageError, FeedSaveOutcome.StorageError)]
    public void Each_store_answer_maps_to_its_save_outcome(SavedFeedsFailure failure, FeedSaveOutcome expected)
    {
        _store.Failure = failure;
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(Request(OneChange(), Written));

        Assert.Equal(new FeedRecordResult(expected, HistoryOutcome.Recorded), result);
    }

    [Fact]
    public void The_store_gets_the_requests_key_kind_intent_and_time()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("AWS_eu-west-1");

        records.Record(Request(null, Written) with { FeedKey = "AWS_eu-west-1", Kind = FeedKind.Aws });

        var apply = Assert.Single(_store.Applies);
        Assert.Equal("AWS_eu-west-1", apply.Key);
        Assert.Equal(FeedKind.Aws, apply.Kind);
        Assert.Same(Written, apply.Intent);
        Assert.Equal(T.AddMinutes(5), apply.AtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_store_that_throws_gives_StorageError_and_the_history_is_still_recorded(bool invalidPick)
    {
        _store.Throws = invalidPick ? new ArgumentException("bad pick") : new InvalidOperationException();
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(Request(OneChange(), Written));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Recorded), result);
        Assert.Single(_store.Applies);
        Assert.True(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void A_history_that_fails_still_saves()
    {
        File.WriteAllBytes(_temp.Paths.HistoryDatabasePath, "this is not a SQLite database, only some text."u8.ToArray());
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(Request(OneChange(), Written));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Failed), result);
        Assert.Single(_store.Applies);
    }

    [Fact]
    public void A_retire_with_no_change_saves_and_records_nothing()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");
        var retired = new SavedFeedIntent.Retired();

        FeedRecordResult result = records.Record(Request(null, retired));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.NothingToRecord), result);
        Assert.Same(retired, Assert.Single(_store.Applies).Intent);
        Assert.False(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_intent_never_calls_the_store(bool withChange)
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("asn:15169");

        FeedRecordResult result = records.Record(Request(withChange ? OneChange() : null, null));

        Assert.Equal(FeedSaveOutcome.NotAsked, result.Save);
        Assert.Empty(_store.Applies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task None_saves_nothing_reads_nothing_and_removes_nothing(bool withChange)
    {
        IFeedRecords none = FeedRecords.None;
        HistoryOutcome history = withChange ? HistoryOutcome.Failed : HistoryOutcome.NothingToRecord;
        FeedChangeRecord? change = withChange ? OneChange() : null;

        using (none.EnterWrite("asn:15169"))
        {
            Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Unavailable, history), none.Record(Request(change, Written)));
            Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, history), none.Record(Request(change, null)));
        }
        SavedFeedsRead read = none.ReadSavedFeeds();
        Assert.False(read.Available);
        Assert.Empty(read.Feeds);
        Assert.Equal(SavedFeedsFailure.Unavailable, await none.RemoveSavedFeedAsync("AS15169", CancellationToken.None));
    }

    [Fact]
    public void The_read_is_the_stores()
    {
        _store.ReadResult = new SavedFeedsRead(false, []);
        var records = new FeedRecords(_temp.Paths, _time, _store);

        Assert.Same(_store.ReadResult, records.ReadSavedFeeds());
    }

    [Theory]
    [InlineData(SavedFeedsFailure.None)]
    [InlineData(SavedFeedsFailure.NotFound)]
    [InlineData(SavedFeedsFailure.Unavailable)]
    [InlineData(SavedFeedsFailure.StorageError)]
    public async Task Remove_passes_the_stores_answer_through(SavedFeedsFailure failure)
    {
        _store.Failure = failure;
        var records = new FeedRecords(_temp.Paths, _time, _store);

        Assert.Equal(failure, await records.RemoveSavedFeedAsync("AS15169", CancellationToken.None));
        Assert.Equal(["AS15169"], _store.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_remove_that_throws_gives_StorageError(bool cancelledInside)
    {
        _store.Throws = cancelledInside ? new OperationCanceledException() : new InvalidOperationException();
        var records = new FeedRecords(_temp.Paths, _time, _store);

        Assert.Equal(SavedFeedsFailure.StorageError, await records.RemoveSavedFeedAsync("AS15169", CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_remove_throws_and_never_reaches_the_store()
    {
        var records = new FeedRecords(_temp.Paths, _time, _store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => records.RemoveSavedFeedAsync("AS15169", cts.Token));
        Assert.Empty(_store.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_remove_waits_for_a_write_of_the_same_feed_already_in_flight(bool otherCase)
    {
        var records = new FeedRecords(_temp.Paths, _time, _store);
        Task<IDisposable> entered;
        // The write's own flow: one started inside the test's flow would carry its hold into the remove
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => records.EnterWrite("AS15169"));
        IDisposable write = await entered;

        Task<SavedFeedsFailure> remove = records.RemoveSavedFeedAsync(otherCase ? "as15169" : "AS15169", CancellationToken.None);
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(2, records.WritesInFlight);
        Assert.Empty(_store.Removes);
        write.Dispose();
        Assert.Equal(SavedFeedsFailure.None, await remove.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(_store.Removes);
        Assert.Equal(0, records.WritesInFlight);
    }

    [Fact]
    public async Task A_remove_after_the_writes_stopped_gives_StorageError_and_never_reaches_the_store()
    {
        var records = new FeedRecords(_temp.Paths, _time, _store);
        await records.StopWritesAsync(CancellationToken.None);

        Assert.Equal(SavedFeedsFailure.StorageError, await records.RemoveSavedFeedAsync("AS15169", CancellationToken.None));
        Assert.Empty(_store.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_recording_point_passes_the_time_and_the_intent(bool retired)
    {
        SavedFeedIntent intent = retired ? new SavedFeedIntent.Retired() : Written;

        (_, _, RecordingRecords records) = Recorded(Write(FortiGateOnly, null, intent: intent));

        Assert.Same(intent, Assert.Single(records.Requests).Intent);
    }

    // ----- The feed lock -----

    public enum LockState { None, OtherKey, Released }

    [Theory]
    [InlineData(LockState.None, false, false)]
    [InlineData(LockState.None, true, true)]
    [InlineData(LockState.OtherKey, false, true)]
    [InlineData(LockState.OtherKey, true, false)]
    [InlineData(LockState.Released, true, true)]
    [InlineData(LockState.Released, false, false)]
    public void A_record_without_its_feeds_lock_writes_nothing(LockState state, bool withChange, bool withIntent)
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        IDisposable? hold = state switch
        {
            LockState.OtherKey => records.EnterWrite("AS13335"),
            LockState.Released => records.EnterWrite("asn:15169"),
            _ => null,
        };
        if (state == LockState.Released)
            hold!.Dispose();

        FeedRecordResult result = records.Record(Request(withChange ? OneChange() : null, withIntent ? Written : null));
        hold?.Dispose();

        Assert.Equal(new FeedRecordResult(withIntent ? FeedSaveOutcome.StorageError : FeedSaveOutcome.NotAsked,
                                          withChange ? HistoryOutcome.Failed : HistoryOutcome.NothingToRecord), result);
        Assert.Empty(_store.Applies);
        Assert.False(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void A_record_under_its_feeds_lock_in_another_case_records_and_saves()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using IDisposable hold = records.EnterWrite("ASN:15169");

        FeedRecordResult result = records.Record(Request(OneChange(), Written));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result);
        Assert.Single(_store.Applies);
        Assert.True(File.Exists(_temp.Paths.HistoryDatabasePath));
    }

    [Fact]
    public void None_refuses_a_record_without_the_lock()
    {
        IFeedRecords none = FeedRecords.None;

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Failed),
                     none.Record(Request(OneChange(), Written)));
    }

    // ----- The refresh commit and the session's refused saves -----

    private static SavedFeedStamp RefreshStamp(string key) =>
        new(new SavedFeed(key, FeedKind.Asn, SavedFeedListTests.AsnPick(), T, null, false), null, null, T.AddHours(1));

    [Fact]
    public void A_refresh_commit_goes_to_the_store_in_one_call_without_a_lock()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        _store.StampOutcome = SavedFeedStampOutcome.Superseded;
        SavedFeedStamp[] stamps = [RefreshStamp("AS1"), RefreshStamp("AS2")];

        IReadOnlyList<SavedFeedStampOutcome> outcomes = records.CommitRefresh(stamps);

        Assert.Equal([SavedFeedStampOutcome.Superseded, SavedFeedStampOutcome.Superseded], outcomes);
        Assert.Same(stamps, Assert.Single(_store.StampBatches));
        Assert.Equal(0, records.WritesInFlight);
    }

    [Fact]
    public void A_refresh_commit_that_throws_is_a_storage_error_for_every_stamp()
    {
        _store.Throws = new InvalidOperationException("boom");
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);

        Assert.Equal([SavedFeedStampOutcome.StorageError, SavedFeedStampOutcome.StorageError],
                     records.CommitRefresh([RefreshStamp("AS1"), RefreshStamp("AS2")]));
    }

    [Fact]
    public void A_refresh_commit_with_no_store_is_unavailable_for_every_stamp()
    {
        IFeedRecords none = FeedRecords.None;

        Assert.Equal([SavedFeedStampOutcome.Unavailable], none.CommitRefresh([RefreshStamp("AS1")]));
        Assert.False(none.SaveRefusedThisSession("AS1"));
    }

    [Theory]
    [InlineData(SavedFeedsFailure.TooMany)]
    [InlineData(SavedFeedsFailure.StorageError)]
    public void A_refused_page_save_is_kept_for_the_session_until_a_save_of_the_key(SavedFeedsFailure refusal)
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        void Save(string key)
        {
            using IDisposable hold = records.EnterWrite(key);
            records.Record(Request(null, Written) with { FeedKey = key });
        }

        _store.Failure = refusal;
        Save("AS15169");
        Assert.True(records.SaveRefusedThisSession("as15169"));
        Assert.False(records.SaveRefusedThisSession("AS1"));

        _store.StampOutcome = SavedFeedStampOutcome.Saved;
        records.CommitRefresh([RefreshStamp("AS15169")]);
        Assert.True(records.SaveRefusedThisSession("AS15169"));

        _store.Failure = SavedFeedsFailure.Unavailable;
        Save("AS15169");
        Assert.True(records.SaveRefusedThisSession("AS15169"));

        _store.Failure = SavedFeedsFailure.None;
        Save("AS15169");
        Assert.False(records.SaveRefusedThisSession("AS15169"));

        _store.StampOutcome = SavedFeedStampOutcome.StorageError;
        records.CommitRefresh([RefreshStamp("AS15169")]);
        Assert.False(records.SaveRefusedThisSession("AS15169"));
    }

    [Fact]
    public void A_refused_retire_marks_the_feed_until_a_retire_or_write_of_it_saves()
    {
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        void Retire()
        {
            using IDisposable hold = records.EnterWrite("AS15169");
            records.Record(Request(null, new SavedFeedIntent.Retired()) with { FeedKey = "AS15169" });
        }

        _store.Failure = SavedFeedsFailure.StorageError;
        Retire();
        Assert.True(records.SaveRefusedThisSession("AS15169"));

        _store.Failure = SavedFeedsFailure.None;
        Retire();
        Assert.False(records.SaveRefusedThisSession("AS15169"));
    }

    [Fact]
    public void A_save_that_throws_marks_the_feed_refused()
    {
        _store.Throws = new InvalidOperationException("boom");
        IFeedRecords records = new FeedRecords(_temp.Paths, _time, _store);
        using (records.EnterWrite("AS15169"))
            Assert.Equal(FeedSaveOutcome.StorageError, records.Record(Request(null, Written) with { FeedKey = "AS15169" }).Save);

        Assert.True(records.SaveRefusedThisSession("AS15169"));
    }
}
