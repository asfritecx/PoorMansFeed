using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.History;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class CloudFeedRefreshTests : IDisposable
{
    private static readonly DateTimeOffset Generated = new(2026, 3, 19, 4, 5, 6, TimeSpan.Zero);
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", Generated, true);
    private static readonly CloudFeedPick TestPick = new(FeedKind.Azure, null, false, "Test", null, null);

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Generated);
    private readonly FakeIpInfoService _ipinfo = new() { Dataset = Dataset };
    private readonly Store _store = new();
    private readonly Recorded _records;
    private readonly HashSet<string> _mixed = new(StringComparer.Ordinal);
    private readonly Dictionary<CloudFeedCheck, DateTimeOffset> _checkStarted = [];
    private readonly CloudFeedEngine _engine;

    public CloudFeedRefreshTests()
    {
        _records = new Recorded(new FeedRecords(_temp.Paths, _time, _store));
        _ipinfo.Records = _records;
        _ipinfo.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true, IPv6Enabled = true };
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [Group(_mixed.Contains(block.Text) ? "example.com" : "microsoft.com")]))
                  .ToList(),
            Dataset);
        _engine = new CloudFeedEngine(_ipinfo, _time, _temp.ExportFolder);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_Refresh_Right_After_A_Page_Write_Of_The_Same_Check_Writes_And_Records_Nothing()
    {
        string[] prefixes = ["34.1.0.0/16", "2001:db8::/32"];
        await PageWriteAsync(prefixes, prefixes, []);
        SavedFeed stored = Stored();
        Assert.NotNull(stored.Proof);
        byte[][] before = AllFiles().Select(File.ReadAllBytes).ToArray();
        int requests = _records.Requests.Count;
        _time.Advance(TimeSpan.FromMinutes(1));

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(prefixes));

        Assert.Equal(CloudRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Equal(before, AllFiles().Select(File.ReadAllBytes).ToArray());
        Assert.Equal(requests, _records.Requests.Count);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Empty(refresh.NewUnconfirmed);
        Assert.Empty(refresh.NewLarge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_File_Edited_Or_Deleted_After_The_Page_Write_Is_Written_And_Recorded_With_No_Save(bool deleted)
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        SavedFeed stored = Stored();
        string file = FilePath("modsecurity", "ipv4");
        if (deleted)
            File.Delete(file);
        else
            File.AppendAllText(file, "99.0.0.0/8\n");
        _time.Advance(TimeSpan.FromMinutes(1));

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["34.1.0.0/16"], Entries(file));
        Assert.Equal(2, _records.Requests.Count);
        FeedRecordRequest request = _records.Requests[1];
        Assert.Null(request.Intent);
        Assert.Equal(FeedChangeCause.RefreshAll, request.Change!.Cause);
        Assert.Same(stored, Stored());
        SavedFeedStamp stamp = refresh.Stamp!;
        Assert.Same(stored, stamp.Expected);
        Assert.Null(stamp.NewPick);
        Assert.Equal(_time.GetUtcNow(), stamp.RefreshedUtc);
        Assert.NotNull(stamp.Proof);
        Assert.NotEqual(stored.Proof, stamp.Proof);
        Assert.Equal(SavedFeedWriteProof.HashOf(File.ReadAllText(file)), stamp.Proof.FileSha256["modsecurity/Azure_Test_ipv4.txt"]);
    }

    [Fact]
    public async Task A_Refresh_Writes_The_Saved_Pick_Against_The_New_Check_And_Lists_New_Unconfirmed_Ranges()
    {
        _mixed.Add("35.1.0.0/16");
        string[] first = ["34.1.0.0/16", "35.1.0.0/16", "36.1.0.0/16", "37.1.0.0/16"];
        await PageWriteAsync(first, first, []);
        _mixed.Add("36.1.0.0/16");
        _mixed.Add("38.1.0.0/16");

        CloudFeedRefresh refresh = await RefreshAsync(
            await CheckAsync(["34.1.0.0/16", "35.1.0.0/16", "36.1.0.0/16", "38.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["34.1.0.0/16", "35.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
        Assert.Equal(["36.1.0.0/16", "38.1.0.0/16"], refresh.NewUnconfirmed);
        Assert.Empty(refresh.NewLarge);
    }

    [Fact]
    public async Task A_Refresh_Keeps_The_Large_Answers_And_Lists_A_New_Large_Range_It_Left_Out()
    {
        string[] first = ["10.0.0.0/8", "11.0.0.0/8", "34.1.0.0/16"];
        await PageWriteAsync(first, first, ["10.0.0.0/8"]);
        Assert.Equal(["11.0.0.0/8"], Stored().Pick.LargeLeftOut);

        CloudFeedRefresh refresh = await RefreshAsync(
            await CheckAsync(["10.0.0.0/8", "11.0.0.0/8", "12.0.0.0/8", "34.1.0.0/16", "35.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["10.0.0.0/8", "34.1.0.0/16", "35.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
        Assert.Equal(["12.0.0.0/8"], refresh.NewLarge);
    }

    [Fact]
    public async Task A_Skipped_Refresh_Still_Lists_A_New_Large_Range()
    {
        string[] first = ["10.0.0.0/8", "34.1.0.0/16"];
        await PageWriteAsync(first, first, ["10.0.0.0/8"]);

        CloudFeedRefresh refresh = await RefreshAsync(
            await CheckAsync(["10.0.0.0/8", "12.0.0.0/8", "34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Equal(["12.0.0.0/8"], refresh.NewLarge);
        Assert.Equal(["10.0.0.0/8", "34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
    }

    [Fact]
    public async Task With_The_Large_Range_Check_Turned_Off_After_The_Check_A_New_Large_Range_Is_Written_And_Not_Listed()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        CloudFeedCheck check = await CheckAsync(["12.0.0.0/8", "34.1.0.0/16"]);
        Assert.True(check.Ranges[0].IsLarge);
        _ipinfo.Settings = _ipinfo.Settings with { LargeRangeCheckEnabled = false };

        CloudFeedRefresh refresh = await RefreshAsync(check);

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["12.0.0.0/8", "34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
        Assert.Empty(refresh.NewLarge);
    }

    [Fact]
    public async Task A_Feed_Not_Saved_Or_Saved_As_Another_Kind_Is_Missing()
    {
        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"])),
                           CloudRefreshOutcome.Missing);
        Assert.False(Directory.Exists(_temp.ExportFolder));

        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        string edited = EditByHand();
        var google = new CloudFeedPick(FeedKind.GoogleCloud, "global", false, null, null, null);
        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"], google)),
                           CloudRefreshOutcome.Missing);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Retired_Feed_Is_Not_Refreshed()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        Assert.Equal(SavedFeedsFailure.None, _store.Apply("Azure_Test", FeedKind.Azure, new SavedFeedIntent.Retired(), Generated));
        string edited = EditByHand();

        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"])),
                           CloudRefreshOutcome.Retired);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Feed_Whose_Save_Was_Refused_This_Session_Is_Not_Refreshed()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        _store.Refuse = true;
        CloudFeedWriteResult refused = await PageWriteAsync(["34.1.0.0/16", "35.1.0.0/16"], ["34.1.0.0/16", "35.1.0.0/16"], []);
        Assert.Equal(FeedSaveOutcome.TooMany, refused.Record?.Save);
        _store.Refuse = false;
        string edited = EditByHand();

        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"])),
                           CloudRefreshOutcome.SaveRefused);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Equal(2, _records.Requests.Count);
    }

    [Fact]
    public async Task A_Saved_Feed_Whose_Selection_Differs_From_The_Checks_Is_Superseded()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        string edited = EditByHand();
        CloudFeedPick other = TestPick with { AzureTag = "Other" };

        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"], other)),
                           CloudRefreshOutcome.Superseded);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task Saved_Feeds_That_Cannot_Be_Read_Refresh_Nothing()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        string edited = EditByHand();
        _store.Available = false;

        AssertNotRefreshed(await RefreshAsync(await CheckAsync(["34.1.0.0/16"])),
                           CloudRefreshOutcome.SavedFeedsUnavailable);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Refresh_That_Fails_After_A_File_Moved_Stamps_No_Proof()
    {
        string[] prefixes = ["34.1.0.0/16", "2001:db8::/32"];
        await PageWriteAsync(prefixes, prefixes, []);
        SavedFeed stored = Stored();
        string blocked = FilePath("fortigate", "ipv6");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        _time.Advance(TimeSpan.FromMinutes(1));

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(prefixes));

        Assert.Equal(CloudRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(CloudFeedWriteFailure.WriteFailed, refresh.Write?.Failure);
        Assert.NotNull(refresh.Write!.FeedChanges);
        Assert.Equal(new SavedFeedStamp(stored, null, null, null), refresh.Stamp);
    }

    [Fact]
    public async Task A_Refresh_That_Fails_Before_Any_File_Moved_Has_No_Stamp()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        string edited = EditByHand();
        UserSettings settings = _ipinfo.Settings;

        CloudFeedCheck check = await CheckAsync(["34.1.0.0/16"]);
        _ipinfo.Settings = settings with { IPv4MinPrefix = 12 };
        CloudFeedRefresh changed = await RefreshAsync(check);
        _ipinfo.Settings = settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };
        CloudFeedRefresh noType = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudFeedWriteFailure.SettingsChanged, changed.Write?.Failure);
        Assert.Equal(CloudFeedWriteFailure.NoFeedType, noType.Write?.Failure);
        foreach (CloudFeedRefresh refresh in new[] { changed, noType })
        {
            Assert.Equal(CloudRefreshOutcome.Failed, refresh.Outcome);
            Assert.Null(refresh.Stamp);
        }

        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Refresh_Whose_Check_Now_Ticks_Nothing_And_Whose_Files_Are_Gone_Keeps_The_Stored_Proof()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        SavedFeed stored = Stored();
        Assert.NotNull(stored.Proof);
        foreach (string file in AllFiles())
            File.Delete(file);
        _mixed.Add("34.1.0.0/16");
        int requests = _records.Requests.Count;

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Unchanged, refresh.Outcome);
        Assert.Equal(CloudFeedWriteFailure.NothingToWrite, refresh.Write?.Failure);
        Assert.Equal(new SavedFeedStamp(stored, null, stored.Proof, _time.GetUtcNow()), refresh.Stamp);
        Assert.Equal(["34.1.0.0/16"], refresh.NewUnconfirmed);
        Assert.Equal(requests, _records.Requests.Count);
        Assert.All(AllFiles(), file => Assert.False(File.Exists(file)));
    }

    [Fact]
    public async Task A_Written_Refresh_Whose_Stamp_Was_Committed_Is_Skipped_The_Next_Time()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        EditByHand();
        CloudFeedCheck check = await CheckAsync(["34.1.0.0/16"]);
        CloudFeedRefresh written = await RefreshAsync(check);
        Assert.Equal(CloudRefreshOutcome.Written, written.Outcome);
        Assert.Equal([SavedFeedStampOutcome.Saved], ((IFeedRecords)_records).CommitRefresh([written.Stamp!]));
        SavedFeed stamped = Stored();
        Assert.Equal(written.Stamp!.Proof!.FileSha256, stamped.Proof!.FileSha256);
        byte[][] before = AllFiles().Where(File.Exists).Select(File.ReadAllBytes).ToArray();
        int requests = _records.Requests.Count;
        _time.Advance(TimeSpan.FromMinutes(1));

        CloudFeedRefresh again = await RefreshAsync(check);

        Assert.Equal(CloudRefreshOutcome.Unchanged, again.Outcome);
        Assert.Null(again.Write);
        Assert.Equal(new SavedFeedStamp(stamped, null, stamped.Proof, _time.GetUtcNow()), again.Stamp);
        Assert.Equal(requests, _records.Requests.Count);
        Assert.Equal(before, AllFiles().Where(File.Exists).Select(File.ReadAllBytes).ToArray());
    }

    [Fact]
    public async Task A_Refresh_After_Writes_Were_Stopped_Fails_With_No_Stamp_And_Writes_Nothing()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        string edited = EditByHand();
        CloudFeedCheck check = await CheckAsync(["34.1.0.0/16"]);
        await _records.StopWritesAsync(CancellationToken.None);

        CloudFeedRefresh refresh = await RefreshAsync(check);

        Assert.Equal(CloudRefreshOutcome.Failed, refresh.Outcome);
        Assert.Equal(CloudFeedWriteFailure.WriteFailed, refresh.Write?.Failure);
        Assert.Null(refresh.Stamp);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Refresh_That_Fails_After_A_File_Moved_Records_A_Refresh_With_No_Save()
    {
        string[] prefixes = ["34.1.0.0/16", "2001:db8::/32"];
        await PageWriteAsync(prefixes, prefixes, []);
        string blocked = FilePath("fortigate", "ipv6");
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        File.AppendAllText(FilePath("fortigate", "ipv4"), "99.0.0.0/8\n");

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(prefixes));

        Assert.Equal(CloudFeedWriteFailure.WriteFailed, refresh.Write?.Failure);
        Assert.Equal(2, _records.Requests.Count);
        FeedRecordRequest request = _records.Requests[1];
        Assert.Null(request.Intent);
        Assert.Equal(FeedChangeCause.RefreshAll, request.Change!.Cause);
    }

    [Fact]
    public async Task A_Stored_Feed_With_No_Proof_Is_Written_Even_When_Its_Files_Are_Unchanged()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        Assert.Equal(SavedFeedsFailure.None,
                     _store.Apply("Azure_Test", FeedKind.Azure, new SavedFeedIntent.Written(Stored().Pick, null), _time.GetUtcNow()));
        SavedFeed stored = Stored();
        Assert.Null(stored.Proof);

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Same(stored, refresh.Stamp!.Expected);
        Assert.NotNull(refresh.Stamp.Proof);
        Assert.Equal(["34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
    }

    [Fact]
    public async Task A_Refresh_Whose_Source_Dropped_A_Family_Empties_That_Familys_Files()
    {
        await PageWriteAsync(["34.1.0.0/16", "2001:db8::/32"], ["34.1.0.0/16", "2001:db8::/32"], []);

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
        foreach (string type in new[] { "fortigate", "modsecurity" })
        {
            string file = FilePath(type, "ipv6");
            Assert.Empty(Entries(file));
            Assert.Contains(File.ReadAllLines(file), line => line.StartsWith("# Entries: 0 ", StringComparison.Ordinal));
            Assert.Contains(file, refresh.Write!.EmptiedFiles!);
        }
    }

    [Fact]
    public async Task A_Range_The_Pick_Includes_As_Large_Is_Written_When_The_Large_Range_Check_Was_Off_At_The_Check()
    {
        string[] first = ["10.0.0.0/8", "34.1.0.0/16"];
        await PageWriteAsync(first, first, ["10.0.0.0/8"]);
        Assert.Equal(["10.0.0.0/8"], Stored().Pick.LargeIncluded);
        EditByHand();
        UserSettings settings = _ipinfo.Settings;
        _ipinfo.Settings = settings with { LargeRangeCheckEnabled = false };
        CloudFeedCheck check = await CheckAsync(first);
        Assert.False(check.Ranges[0].IsLarge);
        _ipinfo.Settings = settings;

        CloudFeedRefresh refresh = await RefreshAsync(check);

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["10.0.0.0/8", "34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
        Assert.Empty(refresh.NewLarge);
    }

    [Fact]
    public async Task A_Page_Write_Saved_After_The_Check_Started_Supersedes_The_Refresh()
    {
        CloudFeedCheck before = await CheckAsync(["34.1.0.0/16"]);
        await PageWriteAsync(["34.1.0.0/16", "35.1.0.0/16"], ["34.1.0.0/16", "35.1.0.0/16"], []);
        string edited = EditByHand();

        AssertNotRefreshed(await RefreshAsync(before), CloudRefreshOutcome.Superseded);

        // A check started within the second the page write was stored in counts as older than it
        CloudFeedCheck sameSecond = await CheckAsync(["34.1.0.0/16"]);
        AssertNotRefreshed(await _engine.RefreshAsync(sameSecond, Stored().LastWriteUtc.AddMilliseconds(300), CancellationToken.None),
                           CloudRefreshOutcome.Superseded);
        Assert.Equal(edited, File.ReadAllText(FilePath("fortigate", "ipv4")));
        Assert.Single(_records.Requests);
    }

    [Fact]
    public async Task A_Page_Write_Stored_While_The_Clock_Ran_Ahead_Does_Not_Stop_The_Refresh()
    {
        await PageWriteAsync(["34.1.0.0/16"], ["34.1.0.0/16"], []);
        SavedFeed written = Stored();
        Assert.Equal(SavedFeedsFailure.None, _store.Apply("Azure_Test", FeedKind.Azure,
            new SavedFeedIntent.Written(written.Pick, written.Proof), _time.GetUtcNow().AddHours(3)));
        EditByHand();

        CloudFeedRefresh refresh = await RefreshAsync(await CheckAsync(["34.1.0.0/16"]));

        Assert.Equal(CloudRefreshOutcome.Written, refresh.Outcome);
        Assert.Equal(["34.1.0.0/16"], Entries(FilePath("fortigate", "ipv4")));
    }

    private static void AssertNotRefreshed(CloudFeedRefresh refresh, CloudRefreshOutcome outcome)
    {
        Assert.Equal(outcome, refresh.Outcome);
        Assert.Null(refresh.Write);
        Assert.Null(refresh.Stamp);
        Assert.Empty(refresh.NewUnconfirmed);
        Assert.Empty(refresh.NewLarge);
    }

    // Each check starts a second after the one before, so a page write before it is stored as earlier
    private async Task<CloudFeedCheck> CheckAsync(string[] prefixes, CloudFeedPick? pick = null)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset started = _time.GetUtcNow();
        CheckResult result = await _engine.CheckAsync(
            new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"),
            prefixes.Select(prefix => new FeedPrefix(Block(prefix), "a")).ToList(), pick ?? TestPick, CancellationToken.None);
        Assert.Equal(CheckFailure.None, result.Failure);
        _checkStarted[result.Check!] = started;
        return result.Check!;
    }

    private Task<CloudFeedRefresh> RefreshAsync(CloudFeedCheck check) =>
        _engine.RefreshAsync(check, _checkStarted[check], CancellationToken.None);

    private async Task<CloudFeedWriteResult> PageWriteAsync(string[] prefixes, string[] ticked, string[] largeIncluded)
    {
        CloudFeedPlan plan = _engine.PlanWrite(await CheckAsync(prefixes), ticked.Select(Block).ToList());
        CloudFeedWriteResult written = await _engine.WriteAsync(plan, largeIncluded.Select(Block).ToList(), CancellationToken.None);
        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        return written;
    }

    private string EditByHand()
    {
        string file = FilePath("fortigate", "ipv4");
        File.AppendAllText(file, "99.0.0.0/8\n");
        return File.ReadAllText(file);
    }

    private SavedFeed Stored() => Assert.Single(_store.Read().Feeds);

    private string FilePath(string type, string family) => Path.Combine(_temp.ExportFolder, type, $"Azure_Test_{family}.txt");

    private IEnumerable<string> AllFiles() =>
        from type in new[] { "fortigate", "modsecurity" }
        from family in new[] { "ipv4", "ipv6" }
        select FilePath(type, family);

    private static string[] Entries(string path) =>
        File.ReadAllLines(path).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

    private static CidrBlock Block(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return block;
    }

    private static OverlapGroup Group(string domain) => new("US", "United States", "AS8075", "Microsoft", domain, 1);

    private sealed class Store : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public bool Available { get; set; } = true;

        public bool Refuse { get; set; }

        public SavedFeedsRead Read() => Available ? new(true, _list.Feeds) : new(false, []);

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
            if (Refuse)
                return SavedFeedsFailure.TooMany;
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

    // Every call reaches the real records, so the lock, the history and the save behave as in the app
    private sealed class Recorded(IFeedRecords inner) : IFeedRecords
    {
        public List<FeedRecordRequest> Requests { get; } = [];

        FeedRecordResult IFeedRecords.Record(FeedRecordRequest request)
        {
            Requests.Add(request);
            return inner.Record(request);
        }

        public IDisposable EnterWrite(string feedKey) => inner.EnterWrite(feedKey);
        public int WritesInFlight => inner.WritesInFlight;
        public Task StopWritesAsync(CancellationToken ct) => inner.StopWritesAsync(ct);
        public Task<HistoryRead> SearchHistoryAsync(HistoryQuery query, int limit, CancellationToken ct) =>
            inner.SearchHistoryAsync(query, limit, ct);
        public Task<HistoryState> ClearHistoryAsync(CancellationToken ct) => inner.ClearHistoryAsync(ct);
        public SavedFeedsRead ReadSavedFeeds() => inner.ReadSavedFeeds();
        public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct) => inner.RemoveSavedFeedAsync(key, ct);
        IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps) =>
            inner.CommitRefresh(stamps);
        bool IFeedRecords.SaveRefusedThisSession(string feedKey) => inner.SaveRefusedThisSession(feedKey);
    }
}
