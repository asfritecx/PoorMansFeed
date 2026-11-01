using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;
using PoorMansFeed.Core.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

/// <summary>The ASN export's feed lock, its history record and its saved feed, on an installed service.</summary>
public sealed class AsnExportRecordTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly string[] SampleLines =
    [
        Line("8.8.8.0/24"),
        Line("8.8.4.0/24"),
        Line("8.0.0.0/8"),
        Line("2001:4860::/32"),
        Line("1.1.1.0/24", "AS13335", "Cloudflare, Inc."),
        Line("1.0.0.0/24", "AS13335", "Cloudflare, Inc."),
        Line("0.0.0.0/0", "AS64500", "Example"),
        Line("192.0.2.0/24", "AS64500", "Example"),
    ];

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 16, 5, 0, TimeSpan.Zero));
    private readonly HttpClient _http;
    private readonly List<IpInfoService> _services = [];
    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemorySecretStore _configKeys = new();
    private readonly byte[] _body = GzBody(SampleLines);

    public AsnExportRecordTests()
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

    private async Task<IpInfoService> InstalledServiceAsync(bool split = true)
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, _secrets, _configKeys,
            () => false, (duration, token) => Task.CompletedTask);
        _services.Add(service);
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("test-token"));
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.NotNull(service.Dataset);
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = split,
            IPv4MinPrefix = 12,
            IPv6MinPrefix = 24,
        }));
        return service;
    }

    private static SearchQuery Query(string text)
    {
        Assert.True(SearchQuery.TryParse(text, out SearchQuery? query));
        return query;
    }

    private static async Task<IReadOnlyList<HistoryRow>> HistoryAsync(IpInfoService service)
    {
        HistoryRead read = await service.Records.SearchHistoryAsync(new HistoryQuery(null, null, null), 1000, Ct);
        return read.Rows;
    }

    private string Fortigate(string name) => Path.Combine(_temp.ExportFolder, "fortigate", name);

    [Fact]
    public async Task A_first_export_saves_its_feed_and_records_the_entries_against_the_dataset()
    {
        IpInfoService service = await InstalledServiceAsync();
        DateOnly downloaded = service.Dataset!.DownloadedOn;

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        SavedFeedsRead saved = service.Records.ReadSavedFeeds();
        Assert.True(saved.Available);
        SavedFeed feed = Assert.Single(saved.Feeds);
        Assert.Equal("AS15169", feed.Key);
        Assert.Equal(FeedKind.Asn, feed.Kind);
        Assert.Equal("15169", feed.Pick.Asn);
        Assert.Empty(feed.Pick.LargeIncluded);
        Assert.Equal(["8.0.0.0/8"], feed.Pick.LargeLeftOut);
        Assert.True(feed.Pick.LargeRangeCheckEnabled);
        Assert.Equal(12, feed.Pick.IPv4MinPrefix);
        Assert.Equal(24, feed.Pick.IPv6MinPrefix);
        Assert.Null(feed.Pick.AzureTag);
        Assert.Empty(feed.Pick.HandTicked);
        Assert.Equal(_time.GetUtcNow(), feed.LastWriteUtc);

        IReadOnlyList<HistoryRow> rows = await HistoryAsync(service);
        Assert.Equal(["2001:4860::/32", "8.8.4.0/24", "8.8.8.0/24"], rows.Select(r => r.Entry).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.True(row.Added);
            Assert.Equal("AS15169", row.FeedKey);
            Assert.Equal(FeedKind.Asn, row.Kind);
            Assert.Equal(FeedChangeCause.PageWrite, row.Cause);
            Assert.Equal(downloaded, row.DatasetDate);
            Assert.Null(row.ListDownloadedUtc);
            Assert.Equal(_time.GetUtcNow(), row.AtUtc);
        });

        // The large range chosen this time moves to the included list; a choice the rows don't hold is not kept
        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult again = await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8", "9.0.0.0/8"], Ct);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), again.Record);
        SavedFeed rewritten = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(["8.0.0.0/8"], rewritten.Pick.LargeIncluded);
        Assert.Empty(rewritten.Pick.LargeLeftOut);
    }

    [Fact]
    public async Task The_history_names_the_file_that_answered_and_not_the_dataset_last_read()
    {
        IpInfoService service = await InstalledServiceAsync();
        DateOnly lastRead = service.Dataset!.DownloadedOn;
        var replaced = new DateOnly(2026, 9, 1);
        Assert.NotEqual(lastRead, replaced);
        // Another file put in place without the service reading it, as just after a swap
        string next = Path.Combine(_temp.Folder, "other.db");
        using (IpDatabase.Builder builder = IpDatabase.CreateBuilder(next, 10))
        {
            (byte[] start, byte[] end) = Cidr.Parse("8.8.8.0/24");
            builder.Add(new IpInfoEntry { Network = "8.8.8.0/24", Asn = "AS15169", AsName = "Google LLC", StartIp = start, EndIp = end });
            builder.Complete("other", replaced, _time.GetUtcNow());
        }
        File.Move(next, _temp.Paths.DatabasePath, overwrite: true);

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(HistoryOutcome.Recorded, result.Record?.History);
        Assert.Equal(lastRead, service.Dataset!.DownloadedOn);
        Assert.All(await HistoryAsync(service), row => Assert.Equal(replaced, row.DatasetDate));
    }

    [Fact]
    public async Task An_export_with_IPv6_off_keeps_the_saved_IPv6_large_answer()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6MinPrefix = 40 }));
        ExportResult first = await service.ExportAsync(Query("AS15169"), ["2001:4860::/32"], Ct);
        Assert.Equal(FeedSaveOutcome.Saved, first.Record?.Save);
        SavedFeedPick before = Assert.Single(service.Records.ReadSavedFeeds().Feeds).Pick;
        Assert.Equal(["2001:4860::/32"], before.LargeIncluded);
        Assert.Equal(["8.0.0.0/8"], before.LargeLeftOut);

        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult again = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(FeedSaveOutcome.Saved, again.Record?.Save);
        SavedFeed after = Assert.Single(service.Records.ReadSavedFeeds().Feeds);
        Assert.Equal(_time.GetUtcNow(), after.LastWriteUtc);
        Assert.Equal(["2001:4860::/32"], after.Pick.LargeIncluded);
        Assert.Equal(["8.0.0.0/8"], after.Pick.LargeLeftOut);
    }

    [Fact]
    public async Task An_answering_file_with_no_label_records_no_dataset_date()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.NotNull(service.Dataset);
        string next = Path.Combine(_temp.Folder, "unlabelled.db");
        using (IpDatabase.Builder builder = IpDatabase.CreateBuilder(next, 10))
        {
            (byte[] start, byte[] end) = Cidr.Parse("8.8.8.0/24");
            builder.Add(new IpInfoEntry { Network = "8.8.8.0/24", Asn = "AS15169", AsName = "Google LLC", StartIp = start, EndIp = end });
            builder.Complete("unlabelled", new DateOnly(2026, 9, 1), _time.GetUtcNow());
        }
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = next, Pooling = false }.ToString()))
        {
            conn.Open();
            using SqliteCommand drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE metadata;";
            drop.ExecuteNonQuery();
        }
        File.Move(next, _temp.Paths.DatabasePath, overwrite: true);

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(HistoryOutcome.Recorded, result.Record?.History);
        IReadOnlyList<HistoryRow> rows = await HistoryAsync(service);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Null(row.DatasetDate));
    }

    [Fact]
    public async Task A_rewrite_says_why_each_entry_was_removed()
    {
        // One file for both families, so the exceptions emptying IPv6 still rewrites the file that held it
        IpInfoService service = await InstalledServiceAsync(split: false);
        Assert.Equal(ExportFailure.None, (await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8"], Ct)).Failure);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(ExceptionsSaveOutcome.Saved,
            await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("2001:4860::/32", null)]));
        ExportResult excepted = await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8"], Ct);
        Assert.Equal(HistoryOutcome.Recorded, excepted.Record?.History);

        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult leftOut = await service.ExportAsync(Query("AS15169"), [], Ct);
        Assert.Equal(HistoryOutcome.Recorded, leftOut.Record?.History);

        HistoryRow[] removed = [.. (await HistoryAsync(service)).Where(r => !r.Added)];
        Assert.Equal(2, removed.Length);
        Assert.Equal(FeedRemovalReason.Exception, Assert.Single(removed, r => r.Entry == "2001:4860::/32").Reason);
        Assert.Equal(FeedRemovalReason.LeftOut, Assert.Single(removed, r => r.Entry == "8.0.0.0/8").Reason);
    }

    [Fact]
    public async Task A_family_switched_off_is_the_reason_its_entries_went()
    {
        IpInfoService service = await InstalledServiceAsync(split: false);
        Assert.Equal(ExportFailure.None, (await service.ExportAsync(Query("AS15169"), [], Ct)).Failure);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateIncludeIPv6 = false }));

        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(HistoryOutcome.Recorded, result.Record?.History);
        HistoryRow removed = Assert.Single(await HistoryAsync(service), r => !r.Added);
        Assert.Equal("2001:4860::/32", removed.Entry);
        Assert.Equal(FeedRemovalReason.FamilyOff, removed.Reason);
    }

    [Fact]
    public async Task A_range_covering_every_address_that_goes_was_left_out()
    {
        IpInfoService service = await InstalledServiceAsync();
        // Written by hand: no export writes a range covering every address
        Directory.CreateDirectory(Path.GetDirectoryName(Fortigate("AS64500_ipv4.txt"))!);
        File.WriteAllText(Fortigate("AS64500_ipv4.txt"), "0.0.0.0/0\n192.0.2.0/24\n");

        ExportResult result = await service.ExportAsync(Query("AS64500"), ["0.0.0.0/0"], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(HistoryOutcome.Recorded, result.Record?.History);
        HistoryRow removed = Assert.Single(await HistoryAsync(service), r => !r.Added);
        Assert.Equal("0.0.0.0/0", removed.Entry);
        Assert.Equal(FeedRemovalReason.LeftOut, removed.Reason);
    }

    [Fact]
    public async Task The_report_only_export_saves_and_records_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateEnabled = false }));

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Single(result.Files);
        Assert.Null(result.Record);
        Assert.Empty(service.Records.ReadSavedFeeds().Feeds);
        Assert.Empty(await HistoryAsync(service));
    }

    [Fact]
    public async Task An_export_after_the_writes_stopped_writes_nothing_and_records_nothing()
    {
        IpInfoService service = await InstalledServiceAsync();
        await service.Records.StopWritesAsync(Ct);

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Null(result.Record);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(_temp.ExportFolder));
        Assert.Empty(service.Records.ReadSavedFeeds().Feeds);
    }

    [Fact]
    public async Task An_export_waits_for_another_write_of_the_same_feed_and_not_for_another_feed()
    {
        IpInfoService service = await InstalledServiceAsync();
        Task<IDisposable> entered;
        // The other write's own flow: one started inside the test's flow would carry its hold into the export
        using (ExecutionContext.SuppressFlow())
            entered = Task.Run(() => service.Records.EnterWrite("AS15169"));
        IDisposable other = await entered;

        Task<ExportResult> waiting = service.ExportAsync(Query("15169"), [], Ct);
        ExportResult another = await service.ExportAsync(Query("AS13335"), [], Ct).WaitAsync(TimeSpan.FromSeconds(10));
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (service.Records.WritesInFlight < 2 && DateTime.UtcNow < giveUp)
            await Task.Delay(5);

        Assert.Equal(ExportFailure.None, another.Failure);
        Assert.Equal(2, service.Records.WritesInFlight);
        Assert.False(waiting.IsCompleted);
        Assert.False(File.Exists(Fortigate("AS15169_ipv4.txt")));
        // The write runs on the settings current when it gets the lock
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateSplitByFamily = false }));
        other.Dispose();
        ExportResult released = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ExportFailure.None, released.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, released.Record?.Save);
        Assert.True(File.Exists(Fortigate("AS15169.txt")));
        Assert.False(File.Exists(Fortigate("AS15169_ipv4.txt")));
    }

    [Fact]
    public async Task A_feed_file_failing_after_an_earlier_one_moved_still_saves_and_records()
    {
        IpInfoService service = await InstalledServiceAsync();
        Directory.CreateDirectory(Fortigate("AS15169_ipv6.txt") + ".tmp");

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(Fortigate("AS15169_ipv6.txt"), result.FailedFile);
        Assert.Equal(new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded), result.Record);
        Assert.Equal("AS15169", Assert.Single(service.Records.ReadSavedFeeds().Feeds).Key);
        Assert.Equal(["8.8.4.0/24", "8.8.8.0/24"], (await HistoryAsync(service)).Select(r => r.Entry).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("AS15169.txt")]
    [InlineData("fortigate/AS15169_ipv4.txt")]
    public async Task A_failure_before_any_feed_file_moved_saves_and_records_nothing(string blocked)
    {
        IpInfoService service = await InstalledServiceAsync();
        Directory.CreateDirectory(Path.Combine(_temp.ExportFolder, blocked.Replace('/', Path.DirectorySeparatorChar)) + ".tmp");

        ExportResult result = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Null(result.FeedChanges);
        Assert.Null(result.Record);
        Assert.Empty(service.Records.ReadSavedFeeds().Feeds);
        Assert.Empty(await HistoryAsync(service));
    }

    [Fact]
    public async Task A_page_write_saves_a_proof_of_its_files_a_failed_one_clears_it_and_each_switch_changes_it()
    {
        IpInfoService service = await InstalledServiceAsync();

        ExportResult first = await service.ExportAsync(Query("AS15169"), [], Ct);

        Assert.Equal(ExportFailure.None, first.Failure);
        SavedFeedWriteProof? proof = Assert.Single(service.Records.ReadSavedFeeds().Feeds).Proof;
        SavedFeedWriteProofTests.AssertProofOfDisk(_temp.ExportFolder, first.FeedChanges!, proof,
                                                   FeedOptions.ForAsnExport(service.Settings));
        Assert.Equal(["fortigate/AS15169_ipv4.txt", "fortigate/AS15169_ipv6.txt"], proof!.FileSha256.Keys.Order(StringComparer.Ordinal));

        string blocked = Fortigate("AS15169_ipv6.txt") + ".tmp";
        Directory.CreateDirectory(blocked);
        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult failed = await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8"], Ct);
        Assert.Equal(ExportFailure.WriteFailed, failed.Failure);
        Assert.Equal(FeedSaveOutcome.Saved, failed.Record?.Save);
        Assert.Null(Assert.Single(service.Records.ReadSavedFeeds().Feeds).Proof);
        Directory.Delete(blocked);

        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateSplitByFamily = false }));
        _time.Advance(TimeSpan.FromMinutes(1));
        ExportResult combined = await service.ExportAsync(Query("AS15169"), [], Ct);
        Assert.Equal(ExportFailure.None, combined.Failure);
        SavedFeedWriteProof? again = Assert.Single(service.Records.ReadSavedFeeds().Feeds).Proof;
        SavedFeedWriteProofTests.AssertProofOfDisk(_temp.ExportFolder, combined.FeedChanges!, again,
                                                   FeedOptions.ForAsnExport(service.Settings));
        Assert.NotEqual(proof.SettingsFingerprint, again!.SettingsFingerprint);
    }

    [Fact]
    public async Task The_export_plan_marks_only_the_large_ranges_the_saved_feed_includes()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6MinPrefix = 48 }));

        ExportPlan unsaved = await service.PrepareExportAsync(Query("AS15169"), Ct);
        Assert.Equal(["2001:4860::/32", "8.0.0.0/8"], unsaved.LargeRanges.Select(range => range.Network).Order(StringComparer.Ordinal));
        Assert.All(unsaved.LargeRanges, range => Assert.False(range.SavedIncluded));

        ExportResult written = await service.ExportAsync(Query("AS15169"), ["8.0.0.0/8"], Ct);
        Assert.Equal(FeedSaveOutcome.Saved, written.Record?.Save);
        SavedFeedPick pick = Assert.Single(service.Records.ReadSavedFeeds().Feeds).Pick;
        Assert.Equal(["8.0.0.0/8"], pick.LargeIncluded);
        Assert.Equal(["2001:4860::/32"], pick.LargeLeftOut);

        // The two /24 ranges become large only now, so the saved feed never answered them
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv4MinPrefix = 25 }));
        ExportPlan plan = await service.PrepareExportAsync(Query("AS15169"), Ct);

        Assert.Equal(4, plan.LargeRanges.Count);
        Assert.Equal(["8.0.0.0/8"], plan.LargeRanges.Where(range => range.SavedIncluded).Select(range => range.Network));
        ExportPlan other = await service.PrepareExportAsync(Query("AS64500"), Ct);
        Assert.NotEmpty(other.LargeRanges);
        Assert.All(other.LargeRanges, range => Assert.False(range.SavedIncluded));
    }
}
