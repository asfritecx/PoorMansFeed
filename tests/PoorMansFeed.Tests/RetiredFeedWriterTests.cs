using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>Retire for an ASN or cloud saved feed: header-only files, a Retire change, the feed marked.</summary>
public sealed class RetiredFeedWriterTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 16, 5, 0, TimeSpan.Zero);

    private static readonly string[] SampleLines =
    [
        Line("8.8.8.0/24"),
        Line("8.8.4.0/24"),
        Line("2001:4860::/32"),
    ];

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly HttpClient _http;
    private readonly List<IpInfoService> _services = [];
    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemorySecretStore _configKeys = new();
    private readonly byte[] _body = GzBody(SampleLines);
    private readonly SavedFeedListStore _store = new();
    private readonly FakeIpInfoService _ipinfo;

    public RetiredFeedWriterTests()
    {
        _http = new HttpClient(new StubHttpHandler(Respond));
        _ipinfo = new FakeIpInfoService
        {
            Records = new FeedRecords(_temp.Paths, _time, _store),
            Dataset = new DatasetInfo(new DateOnly(2026, 10, 7), 3, "abc", Start, true),
            Settings = new UserSettings { ExportFolder = _temp.ExportFolder, WriteFortigateFeeds = true, WriteModSecurityLists = true },
        };
    }

    public void Dispose()
    {
        foreach (IpInfoService service in _services)
            service.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private sealed class SavedFeedListStore : ISavedFeedStore
    {
        private SavedFeedList _list = SavedFeedList.Empty;

        public SavedFeedsRead Read() => new(true, _list.Feeds);

        public SavedFeedsFailure Apply(string key, FeedKind kind, SavedFeedIntent intent, DateTimeOffset atUtc)
        {
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

    private static string Line(string network) =>
        $$"""{"network":"{{network}}","country":"United States","country_code":"US","continent":"North America","continent_code":"NA","asn":"AS15169","as_name":"Google LLC","as_domain":"google.com"}""";

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

    private async Task<IpInfoService> ExportedServiceAsync(bool split)
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, _secrets, _configKeys,
            () => false, (duration, token) => Task.CompletedTask);
        _services.Add(service);
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("test-token"));
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
            FortigateSplitByFamily = split,
        }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        ExportResult export = await service.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.None, export.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, export.Record?.Save);
        return service;
    }

    private async Task WriteCloudFeedAsync()
    {
        _ipinfo.OverlapFunc = blocks => new OverlapResult(OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS8075", "Microsoft",
                                                                                 "microsoft.com", 1)])).ToList(),
            _ipinfo.Dataset);
        var engine = new CloudFeedEngine(_ipinfo, _time, _temp.Paths.DefaultExportFolder);
        FeedPrefix[] prefixes = [Prefix("34.1.0.0/16"), Prefix("2001:db8::/32")];
        CheckResult check = await engine.CheckAsync(new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"),
                                                    prefixes, CloudFeedEngineTests.TestPick, Ct);
        CloudFeedPlan plan = engine.PlanWrite(check.Check!, prefixes.Select(prefix => prefix.Block).ToList());
        CloudFeedWriteResult written = await engine.WriteAsync(plan, [], Ct);
        Assert.Equal(CloudFeedWriteFailure.None, written.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
    }

    private static FeedPrefix Prefix(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return new FeedPrefix(block, "a");
    }

    private RetireResult Retire(IIpInfoService ipinfo, FeedKind kind, string key) =>
        RetiredFeedWriter.Retire(ipinfo, kind, key, _time, _temp.Paths.DefaultExportFolder);

    private static async Task<IReadOnlyList<HistoryRow>> HistoryAsync(IIpInfoService service)
    {
        HistoryRead read = await service.Records.SearchHistoryAsync(new HistoryQuery(null, null, null), 1000, Ct);
        return read.Rows;
    }

    private string Feed(string folder, FeedType type, string name) =>
        Path.Combine(folder, type == FeedType.Fortigate ? "fortigate" : "modsecurity", name);

    private string Feed(FeedType type, string name) => Feed(_temp.ExportFolder, type, name);

    private static void Plant(string path, string entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# PoorMansFeed - Fortigate Threat Feed\n" + entries);
    }

    private static byte[] Retired(FeedType type, string family, DateTimeOffset at)
    {
        string first = type == FeedType.Fortigate ? "# PoorMansFeed - Fortigate Threat Feed" : "# PoorMansFeed - ModSecurity IP list";
        return new UTF8Encoding(false).GetBytes(
            first + "\n" +
            "# Retired by hand: no address is listed until the feed is written again\n" +
            "# Address Family: " + family + "\n" +
            "# Generated: " + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC\n" +
            "# Entries: 0\n");
    }

    private static void AssertRetired(string path, FeedType type, string family, DateTimeOffset at)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(Retired(type, family, at), bytes);
        FeedFormat.AssertWellFormed(bytes);
    }

    [Fact]
    public async Task A_split_ASN_feed_is_retired_in_both_types_and_the_report_is_left_alone()
    {
        IpInfoService service = await ExportedServiceAsync(split: true);
        string report = Path.Combine(_temp.ExportFolder, "AS15169.txt");
        byte[] reportBytes = File.ReadAllBytes(report);
        _time.Advance(TimeSpan.FromMinutes(5));
        DateTimeOffset now = _time.GetUtcNow();

        RetireResult result = Retire(service, FeedKind.Asn, "AS15169");

        Assert.Equal(RetireFailure.None, result.Failure);
        Assert.Equal(_temp.ExportFolder, result.Folder);
        string[] files =
        [
            Feed(FeedType.Fortigate, "AS15169_ipv4.txt"), Feed(FeedType.Fortigate, "AS15169_ipv6.txt"),
            Feed(FeedType.ModSecurity, "AS15169_ipv4.txt"), Feed(FeedType.ModSecurity, "AS15169_ipv6.txt"),
        ];
        Assert.Equal(files, result.Files);
        Assert.Null(result.FailedFile);
        Assert.Empty(result.OldFilesLeft);
        AssertRetired(files[0], FeedType.Fortigate, "IPv4", now);
        AssertRetired(files[1], FeedType.Fortigate, "IPv6", now);
        AssertRetired(files[2], FeedType.ModSecurity, "IPv4", now);
        AssertRetired(files[3], FeedType.ModSecurity, "IPv6", now);
        Assert.Equal(reportBytes, File.ReadAllBytes(report));

        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        List<HistoryRow> retired = (await HistoryAsync(service)).Where(row => row.Cause == FeedChangeCause.Retire).ToList();
        Assert.Equal(["2001:4860::/32", "8.8.4.0/24", "8.8.8.0/24"],
                     retired.Select(row => row.Entry).Distinct().Order(StringComparer.Ordinal));
        Assert.All(retired, row =>
        {
            Assert.False(row.Added);
            Assert.Equal(FeedRemovalReason.NotTold, row.Reason);
            Assert.Equal("AS15169", row.FeedKey);
            Assert.Equal(FeedKind.Asn, row.Kind);
            Assert.Equal(now, row.AtUtc);
            Assert.Null(row.DatasetDate);
            Assert.Null(row.ListDownloadedUtc);
        });
        SavedFeed feed = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.True(feed.Retired);
    }

    [Fact]
    public async Task A_combined_ASN_feed_is_retired_as_both_families_inside_the_type_folders()
    {
        IpInfoService service = await ExportedServiceAsync(split: false);
        string report = Path.Combine(_temp.ExportFolder, "AS15169.txt");
        byte[] reportBytes = File.ReadAllBytes(report);
        _time.Advance(TimeSpan.FromMinutes(5));

        RetireResult result = Retire(service, FeedKind.Asn, "AS15169");

        Assert.Equal(RetireFailure.None, result.Failure);
        Assert.Equal([Feed(FeedType.Fortigate, "AS15169.txt"), Feed(FeedType.ModSecurity, "AS15169.txt")], result.Files);
        AssertRetired(result.Files[0], FeedType.Fortigate, "IPv4/IPv6", _time.GetUtcNow());
        AssertRetired(result.Files[1], FeedType.ModSecurity, "IPv4/IPv6", _time.GetUtcNow());
        Assert.Equal(reportBytes, File.ReadAllBytes(report));
        Assert.True(Assert.Single(service.Records.ReadSavedFeeds().Feeds).Retired);
    }

    [Fact]
    public async Task A_cloud_feed_is_retired_in_both_types()
    {
        await WriteCloudFeedAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        DateTimeOffset now = _time.GetUtcNow();

        RetireResult result = Retire(_ipinfo, FeedKind.Azure, "Azure_Test");

        Assert.Equal(RetireFailure.None, result.Failure);
        string[] files =
        [
            Feed(FeedType.Fortigate, "Azure_Test_ipv4.txt"), Feed(FeedType.Fortigate, "Azure_Test_ipv6.txt"),
            Feed(FeedType.ModSecurity, "Azure_Test_ipv4.txt"), Feed(FeedType.ModSecurity, "Azure_Test_ipv6.txt"),
        ];
        Assert.Equal(files, result.Files);
        AssertRetired(files[0], FeedType.Fortigate, "IPv4", now);
        AssertRetired(files[1], FeedType.Fortigate, "IPv6", now);
        AssertRetired(files[2], FeedType.ModSecurity, "IPv4", now);
        AssertRetired(files[3], FeedType.ModSecurity, "IPv6", now);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        List<HistoryRow> retired = (await HistoryAsync(_ipinfo)).Where(row => row.Cause == FeedChangeCause.Retire).ToList();
        Assert.Equal(["2001:db8::/32", "34.1.0.0/16"], retired.Select(row => row.Entry).Distinct().Order(StringComparer.Ordinal));
        Assert.All(retired, row => Assert.Equal(FeedKind.Azure, row.Kind));
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
    }

    [Fact]
    public void A_file_of_a_type_not_ticked_is_retired_too()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = true, WriteModSecurityLists = false };
        string modSecurity = Feed(FeedType.ModSecurity, "AS64500_ipv4.txt");
        Plant(modSecurity, "192.0.2.0/24\n");

        RetireResult result = Retire(_ipinfo, FeedKind.Asn, "AS64500");

        Assert.Equal(RetireFailure.None, result.Failure);
        Assert.Equal([modSecurity], result.Files);
        AssertRetired(modSecurity, FeedType.ModSecurity, "IPv4", Start);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
    }

    [Fact]
    public async Task No_file_of_the_feed_retires_nothing_makes_no_folder_and_records_nothing()
    {
        RetireResult result = Retire(_ipinfo, FeedKind.Asn, "AS64500");

        Assert.Equal(RetireFailure.NothingToRetire, result.Failure);
        Assert.Equal(_temp.ExportFolder, result.Folder);
        Assert.Empty(result.Files);
        Assert.Null(result.Record);
        Assert.False(Directory.Exists(_temp.ExportFolder));
        Assert.Empty(await HistoryAsync(_ipinfo));
        Assert.Empty(_store.Read().Feeds);
    }

    [Fact]
    public async Task Header_only_files_are_rewritten_record_no_row_and_the_feed_is_still_marked_retired()
    {
        await WriteCloudFeedAsync();
        Assert.Equal(RetireFailure.None, Retire(_ipinfo, FeedKind.Azure, "Azure_Test").Failure);
        int rows = (await HistoryAsync(_ipinfo)).Count;
        // Written again elsewhere so the saved feed is no longer retired, and its files put back header-only
        Assert.Equal(SavedFeedsFailure.None,
                     _store.Apply("Azure_Test", FeedKind.Azure, new SavedFeedIntent.Written(_store.Read().Feeds[0].Pick), Start));
        Assert.False(Assert.Single(_store.Read().Feeds).Retired);
        _time.Advance(TimeSpan.FromMinutes(5));

        RetireResult again = Retire(_ipinfo, FeedKind.Azure, "Azure_Test");

        Assert.Equal(RetireFailure.None, again.Failure);
        Assert.Equal(4, again.Files.Count);
        AssertRetired(again.Files[0], FeedType.Fortigate, "IPv4", _time.GetUtcNow());
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.NothingToRecord), again.Record);
        Assert.Equal(rows, (await HistoryAsync(_ipinfo)).Count);
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
    }

    [Fact]
    public async Task A_folder_at_a_feed_path_fails_the_retire_after_recording_the_file_moved_before_it()
    {
        await WriteCloudFeedAsync();
        string fg4 = Feed(FeedType.Fortigate, "Azure_Test_ipv4.txt");
        string fg6 = Feed(FeedType.Fortigate, "Azure_Test_ipv6.txt");
        File.Delete(fg6);
        Directory.CreateDirectory(fg6);
        _time.Advance(TimeSpan.FromMinutes(5));

        RetireResult result = Retire(_ipinfo, FeedKind.Azure, "Azure_Test");

        Assert.Equal(RetireFailure.WriteFailed, result.Failure);
        Assert.Equal([fg4], result.Files);
        Assert.Equal(fg6, result.FailedFile);
        Assert.Equal([Feed(FeedType.ModSecurity, "Azure_Test_ipv4.txt"), Feed(FeedType.ModSecurity, "Azure_Test_ipv6.txt")],
                     result.OldFilesLeft);
        AssertRetired(fg4, FeedType.Fortigate, "IPv4", _time.GetUtcNow());
        Assert.Empty(Directory.EnumerateFiles(_temp.ExportFolder, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(_temp.ExportFolder, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        List<HistoryRow> retired = (await HistoryAsync(_ipinfo)).Where(row => row.Cause == FeedChangeCause.Retire).ToList();
        Assert.Equal(["34.1.0.0/16"], retired.Select(row => row.Entry).Distinct());
        Assert.True(Assert.Single(_store.Read().Feeds).Retired);
    }

    [Fact]
    public async Task A_feed_file_that_cant_be_replaced_is_named_first_among_the_old_files_left()
    {
        await WriteCloudFeedAsync();
        string fg4 = Feed(FeedType.Fortigate, "Azure_Test_ipv4.txt");
        string fg6 = Feed(FeedType.Fortigate, "Azure_Test_ipv6.txt");
        byte[] before = File.ReadAllBytes(fg6);
        Directory.CreateDirectory(fg6 + ".tmp");

        RetireResult result = Retire(_ipinfo, FeedKind.Azure, "Azure_Test");

        // Its earlier copy is still whole, so FortiGate keeps reading it
        Assert.Equal(RetireFailure.WriteFailed, result.Failure);
        Assert.Equal([fg4], result.Files);
        Assert.Equal(fg6, result.FailedFile);
        Assert.Equal([fg6, Feed(FeedType.ModSecurity, "Azure_Test_ipv4.txt"), Feed(FeedType.ModSecurity, "Azure_Test_ipv6.txt")],
                     result.OldFilesLeft);
        Assert.Equal(before, File.ReadAllBytes(fg6));
    }

    [Theory]
    [InlineData(FeedKind.Asn, "as15169")]
    [InlineData(FeedKind.Asn, "../x")]
    [InlineData(FeedKind.Asn, "")]
    [InlineData(FeedKind.Asn, "AS12a")]
    [InlineData(FeedKind.Asn, "12")]
    [InlineData(FeedKind.Asn, "AS")]
    [InlineData(FeedKind.Azure, "Azure/Test")]
    [InlineData(FeedKind.Aws, "..")]
    [InlineData(FeedKind.GoogleCloud, "")]
    [InlineData(FeedKind.AbuseIpDbRolling, "AbuseIPDB_rolling")]
    public void An_unsafe_key_or_the_rolling_kind_is_not_retirable_and_touches_nothing(FeedKind kind, string key)
    {
        string rolling = Feed(FeedType.Fortigate, "AbuseIPDB_rolling_ipv4.txt");
        Plant(rolling, "8.8.8.8\n");
        byte[] before = File.ReadAllBytes(rolling);
        // No lock to take: a lock asked for throws
        _ipinfo.Records = new RecordingRecords();
        int reads = _ipinfo.SettingsReads;

        RetireResult result = Retire(_ipinfo, kind, key);

        Assert.Equal(RetireFailure.NotRetirable, result.Failure);
        Assert.Empty(result.Files);
        Assert.Null(result.Record);
        Assert.Equal(reads, _ipinfo.SettingsReads);
        Assert.Equal(before, File.ReadAllBytes(rolling));
    }

    [Fact]
    public async Task A_retire_waits_for_another_write_of_the_feed_and_reads_the_settings_once_it_has_the_lock()
    {
        string other = Path.Combine(_temp.Folder, "other");
        Plant(Feed(FeedType.Fortigate, "AS64500_ipv4.txt"), "192.0.2.0/24\n");
        Plant(Feed(other, FeedType.Fortigate, "AS64500_ipv4.txt"), "192.0.2.0/24\n");
        byte[] before = File.ReadAllBytes(Feed(FeedType.Fortigate, "AS64500_ipv4.txt"));
        IFeedRecords records = _ipinfo.Records;
        Task<IDisposable> entered;
        // The other write's own flow: one started inside the test's flow would carry its hold into the retire
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => records.EnterWrite("as64500"));
        IDisposable holder = await entered;

        Task<RetireResult> waiting = Task.Run(() => Retire(_ipinfo, FeedKind.Asn, "AS64500"));
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(2, records.WritesInFlight);
        Assert.False(waiting.IsCompleted);
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = other };
        holder.Dispose();
        RetireResult result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RetireFailure.None, result.Failure);
        Assert.Equal(other, result.Folder);
        Assert.Equal([Feed(other, FeedType.Fortigate, "AS64500_ipv4.txt")], result.Files);
        Assert.Equal(before, File.ReadAllBytes(Feed(FeedType.Fortigate, "AS64500_ipv4.txt")));
        Assert.Equal(0, records.WritesInFlight);
    }

    [Fact]
    public async Task After_the_writes_stopped_a_retire_fails_and_touches_nothing()
    {
        string path = Feed(FeedType.Fortigate, "AS64500_ipv4.txt");
        Plant(path, "192.0.2.0/24\n");
        byte[] before = File.ReadAllBytes(path);
        await _ipinfo.Records.StopWritesAsync(Ct);

        RetireResult result = Retire(_ipinfo, FeedKind.Asn, "AS64500");

        Assert.Equal(RetireFailure.WriteFailed, result.Failure);
        Assert.Empty(result.Files);
        Assert.Null(result.FailedFile);
        Assert.Null(result.Record);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_retire_works_while_the_exceptions_cant_be_read()
    {
        _ipinfo.Exceptions = ExceptionList.Unreadable;
        string path = Feed(FeedType.Fortigate, "Azure_Test_ipv4.txt");
        Plant(path, "34.1.0.0/16\n");

        RetireResult result = Retire(_ipinfo, FeedKind.Azure, "Azure_Test");

        Assert.Equal(RetireFailure.None, result.Failure);
        AssertRetired(path, FeedType.Fortigate, "IPv4", Start);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.NotAsked, HistoryOutcome.Recorded), result.Record);
        HistoryRow row = Assert.Single(await HistoryAsync(_ipinfo));
        Assert.Equal("34.1.0.0/16", row.Entry);
        Assert.Equal(FeedChangeCause.Retire, row.Cause);
    }
}
