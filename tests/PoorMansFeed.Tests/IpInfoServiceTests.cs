using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class IpInfoServiceTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string FakeHash = new('f', 64);

    private static readonly string[] SampleLines =
    [
        Line("8.8.8.0/24"),
        Line("8.8.4.0/24"),
        Line("8.0.0.0/8"),
        Line("2001:4860::/32"),
        Line("1.1.1.0/24", "AS13335", "Cloudflare, Inc."),
        Line("1.0.0.0/24", "AS13335", "Cloudflare, Inc."),
    ];

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 16, 5, 0, TimeSpan.Zero));
    private readonly RemoteSource _remote = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;
    private readonly List<IpInfoService> _services = [];
    // One store for every instance a test makes, as the OS store is one per user account
    private readonly InMemorySecretStore _secrets = new();
    // One store for every instance a test makes, as the OS store is one per user account
    private readonly InMemorySecretStore _configKeys = new();

    public IpInfoServiceTests()
    {
        _handler = new StubHttpHandler(Respond);
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        foreach (IpInfoService service in _services)
            service.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    // ----- Helpers -----

    private AppPaths Paths => _temp.Paths;

    private byte[] LiveBytes => File.ReadAllBytes(Paths.DatabasePath);

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

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private HttpResponseMessage Respond(HttpRequestMessage request, int callNumber)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/checksums", StringComparison.Ordinal))
        {
            if (_remote.ChecksumStatus != HttpStatusCode.OK)
                return new HttpResponseMessage(_remote.ChecksumStatus);

            string sha = _remote.AdvertisedSha256 ?? Sha256Hex(_remote.Body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"checksums\":{\"sha256\":\"" + sha + "\"}}", Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_remote.Body),
        };
    }

    private int DownloadRequests => _handler.RequestedUris.Count(u => !u.AbsolutePath.EndsWith("/checksums", StringComparison.Ordinal));

    private IpInfoService NewService(Func<bool>? isPrivileged = null, ISecretStore? configKeys = null)
    {
        var service = new IpInfoService(Paths, _http, new CoreOptions(), _time, _secrets, configKeys ?? _configKeys,
            isPrivileged ?? (() => false),
            (duration, token) => Task.CompletedTask);
        _services.Add(service);
        return service;
    }

    /// <summary>A service on the temp folder with the sample dataset installed and a token saved.</summary>
    private async Task<IpInfoService> InstalledServiceAsync()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("test-token"));
        _remote.Body = GzBody(SampleLines);

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);
        Assert.Equal(UpdateFailure.None, result.Failure);
        Assert.NotNull(service.Dataset);
        return service;
    }

    /// <summary>
    /// A service whose launch stopped on config.db with the given failure: nothing past the preflight ran (a stale
    /// build file written first is still there), no store is open, and neither save can be made.
    /// </summary>
    private async Task<IpInfoService> StoppedServiceAsync(StartupFailure expected, ISecretStore? configKeys = null)
    {
        File.WriteAllText(Paths.NextDatabasePath, "stale build");
        IpInfoService service = NewService(configKeys: configKeys);

        StartupResult result = await service.InitializeAsync(Ct);

        Assert.Equal(expected, result.Failure);
        Assert.Null(service.Config);
        Assert.False(service.Exceptions.IsReadable);
        Assert.False(await service.SaveSettingsAsync(service.Settings with { IPv4MinPrefix = 12 }));
        Assert.Equal(new UserSettings(), service.Settings);
        Assert.Equal(ExceptionsSaveOutcome.Failed, await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("192.0.2.0/24", null)]));
        Assert.False(service.Exceptions.IsReadable);
        Assert.True(File.Exists(Paths.NextDatabasePath));
        return service;
    }

    /// <summary>Launches once and disposes, so config.db exists, closed, with its key in <see cref="_configKeys"/>.</summary>
    private async Task LaunchOnceAsync()
    {
        IpInfoService first = NewService();
        Assert.Equal(StartupFailure.None, (await first.InitializeAsync(Ct)).Failure);
        first.Dispose();
    }

    /// <summary>Runs SQL on config.db through a connection of its own. Only while no service holds the file.</summary>
    private void ExecuteOnConfig(string sql)
    {
        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = Paths.ConfigPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ConnectionString;
        using var conn = new SqliteConnection(connection);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static readonly byte[] NotADatabase = Encoding.UTF8.GetBytes("this is not a database, only some text");

    private void AssertNoLeftovers()
    {
        Assert.False(File.Exists(Paths.NextDatabasePath));
        Assert.False(File.Exists(Paths.DownloadPath));
    }

    private sealed class RemoteSource
    {
        public byte[] Body { get; set; } = [];

        /// <summary>The hash the checksum endpoint advertises. Null means the hash of Body.</summary>
        public string? AdvertisedSha256 { get; set; }

        public HttpStatusCode ChecksumStatus { get; set; } = HttpStatusCode.OK;
    }

    private sealed class RecordingProgress(Action<UpdateProgress>? onReport = null) : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = [];

        public void Report(UpdateProgress value)
        {
            Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    // ----- Startup -----

    [Fact]
    public async Task Initialize_On_Empty_Folder_Reports_No_Dataset()
    {
        IpInfoService service = NewService();

        StartupResult result = await service.InitializeAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        Assert.Null(result.Dataset);
        Assert.False(result.HasToken);
        Assert.Null(service.Dataset);
    }

    [Fact]
    public async Task Privileged_Process_Is_Refused()
    {
        IpInfoService service = NewService(() => true);

        StartupResult result = await service.InitializeAsync(Ct);

        Assert.Equal(StartupFailure.Privileged, result.Failure);
    }

    [Fact]
    public async Task No_Feed_Can_Be_Written_Until_A_Successful_Start_Up_Has_Read_The_Exceptions()
    {
        // A successful start-up with no file gives a readable empty list (Saved_Exceptions_Are_Seen_By_A_New_Instance).
        // Before it the settings tick no feed type, so the export is not refused for the list and finds nothing to write
        IpInfoService first = NewService();
        Assert.False(first.Exceptions.IsReadable);
        Assert.False(first.Settings.TicksAnyFeedType());
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        ExportResult refused = await first.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.NothingToExport, refused.Failure);
        Assert.True(refused.NoFeedType);
        Assert.Empty(refused.Files);
        Assert.False(Directory.Exists(first.ExportFolder));

        Assert.Equal(StartupFailure.None, (await first.InitializeAsync(Ct)).Failure);
        Assert.True(first.Exceptions.IsReadable);

        IpInfoService second = NewService();
        Assert.False(second.Exceptions.IsReadable);
        Assert.Equal(StartupFailure.AlreadyRunning, (await second.InitializeAsync(Ct)).Failure);
        Assert.False(second.Exceptions.IsReadable);
        first.Dispose();
    }

    [Fact]
    public async Task Second_Instance_Is_Refused_Until_First_Is_Disposed()
    {
        IpInfoService first = NewService();
        Assert.Equal(StartupFailure.None, (await first.InitializeAsync(Ct)).Failure);

        IpInfoService second = NewService();
        Assert.Equal(StartupFailure.AlreadyRunning, (await second.InitializeAsync(Ct)).Failure);

        first.Dispose();

        // Dispose released config.db's lock as well: a held file would give ConfigLocked here
        IpInfoService third = NewService();
        Assert.Equal(StartupFailure.None, (await third.InitializeAsync(Ct)).Failure);
    }

    [Fact]
    public async Task Stale_Next_Database_And_Download_Are_Removed_On_Initialize()
    {
        File.WriteAllText(Paths.NextDatabasePath, "stale build");
        File.WriteAllText(Paths.DownloadPath, "stale download");

        StartupResult result = await NewService().InitializeAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        AssertNoLeftovers();
    }

    [Fact]
    public async Task Token_File_Of_Earlier_Builds_Is_Removed_On_Initialize()
    {
        File.WriteAllText(Paths.LegacyTokenPath, "old token");
        File.WriteAllText(Paths.LegacyTokenPath + ".tmp", "old token");

        StartupResult result = await NewService().InitializeAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        Assert.False(File.Exists(Paths.LegacyTokenPath));
        Assert.False(File.Exists(Paths.LegacyTokenPath + ".tmp"));
    }

    [Fact]
    public async Task Newer_Schema_Database_Gives_DatabaseNewer()
    {
        string next = Paths.NextDatabasePath;
        using (IpDatabase.Builder builder = IpDatabase.CreateBuilder(next, 10))
        {
            var (start, end) = Cidr.Parse("8.8.8.0/24");
            builder.Add(new IpInfoEntry { Network = "8.8.8.0/24", Asn = "AS15169", AsName = "Google LLC", StartIp = start, EndIp = end });
            builder.Complete("checksum", new DateOnly(2026, 10, 7), new DateTimeOffset(2026, 10, 7, 16, 5, 0, TimeSpan.Zero));
        }
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = next, Pooling = false }.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE metadata SET value = '3' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        File.Move(next, Paths.DatabasePath, overwrite: true);

        StartupResult result = await NewService().InitializeAsync(Ct);

        Assert.Equal(StartupFailure.DatabaseNewer, result.Failure);
        Assert.Null(result.Dataset);
    }

    // ----- Update -----

    [Fact]
    public async Task Update_Without_Token_Gives_NoToken()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);

        Assert.Equal(UpdateFailure.NoToken, result.Failure);
        Assert.Null(result.Dataset);
    }

    [Fact]
    public async Task Update_Installs_Dataset_And_Reports_Phases_In_Order()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("  test-token  "));
        _remote.Body = GzBody(SampleLines);
        var progress = new RecordingProgress();

        UpdateResult result = await service.UpdateDatasetAsync(progress, Ct);

        Assert.Equal(UpdateFailure.None, result.Failure);
        Assert.False(result.AlreadyCurrent);
        Assert.NotNull(result.Dataset);
        Assert.Equal(SampleLines.Length, result.Dataset.RangeCount);
        Assert.Equal(Sha256Hex(_remote.Body), result.Dataset.Checksum);
        Assert.Same(result.Dataset, service.Dataset);
        AssertNoLeftovers();

        UpdatePhase[] phases = progress.Reports.Select(p => p.Phase).DistinctBy(p => p).ToArray();
        Assert.Equal(
            new[] { UpdatePhase.Checking, UpdatePhase.Downloading, UpdatePhase.Importing, UpdatePhase.Indexing, UpdatePhase.Finishing },
            phases);

        // The token is trimmed before it is used.
        Assert.Equal("?token=test-token", _handler.RequestedUris.Single(u => !u.AbsolutePath.EndsWith("/checksums", StringComparison.Ordinal)).Query);
    }

    [Fact]
    public async Task Update_With_Same_Body_Is_Already_Current_And_Does_Not_Download_Again()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.Equal(1, DownloadRequests);

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);

        Assert.Equal(UpdateFailure.None, result.Failure);
        Assert.True(result.AlreadyCurrent);
        Assert.Equal(1, DownloadRequests);
    }

    [Fact]
    public async Task CheckForUpdate_Reports_UpToDate_UpdateAvailable_TokenRejected_And_NoToken()
    {
        IpInfoService service = await InstalledServiceAsync();

        Assert.Equal(UpdateCheckOutcome.UpToDate, (await service.CheckForUpdateAsync(Ct)).Outcome);

        _remote.AdvertisedSha256 = FakeHash;
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await service.CheckForUpdateAsync(Ct)).Outcome);

        _remote.ChecksumStatus = HttpStatusCode.Unauthorized;
        Assert.Equal(UpdateCheckOutcome.TokenRejected, (await service.CheckForUpdateAsync(Ct)).Outcome);

        Assert.True(await service.ClearTokenAsync());
        Assert.Equal(UpdateCheckOutcome.NoToken, (await service.CheckForUpdateAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task Checksum_Mismatch_Leaves_Live_Database_Untouched()
    {
        IpInfoService service = await InstalledServiceAsync();
        byte[] before = LiveBytes;

        _remote.Body = GzBody([Line("9.9.9.0/24", "AS19281", "Quad9")]);
        _remote.AdvertisedSha256 = FakeHash;

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);

        Assert.Equal(UpdateFailure.ChecksumMismatch, result.Failure);
        Assert.Equal(before, LiveBytes);
        AssertNoLeftovers();

        SearchResult search = await service.SearchAsync("AS15169", 50, Ct);
        Assert.Equal(SearchOutcome.Found, search.Outcome);
        Assert.Equal(4, search.TotalCount);
    }

    [Fact]
    public async Task Cancelled_Import_Leaves_Live_Database_Untouched_And_Cleans_Up()
    {
        IpInfoService service = await InstalledServiceAsync();
        byte[] before = LiveBytes;

        _remote.Body = GzBody(Enumerable.Repeat(Line("8.8.8.0/24"), 20_000));
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress(p =>
        {
            if (p.Phase == UpdatePhase.Importing)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.UpdateDatasetAsync(progress, cts.Token));

        Assert.Equal(before, LiveBytes);
        AssertNoLeftovers();
        Assert.Equal(SampleLines.Length, service.Dataset!.RangeCount);

        SearchResult search = await service.SearchAsync("AS15169", 50, Ct);
        Assert.Equal(SearchOutcome.Found, search.Outcome);
    }

    [Fact]
    public async Task Corrupt_Data_Gives_CorruptData_And_Leaves_Live_Database_Untouched()
    {
        IpInfoService service = await InstalledServiceAsync();
        byte[] before = LiveBytes;

        _remote.Body = GzBody(Enumerable.Repeat("{broken", 1500).Concat(Enumerable.Repeat(Line("8.8.8.0/24"), 10)));

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);

        Assert.Equal(UpdateFailure.CorruptData, result.Failure);
        Assert.Equal(before, LiveBytes);
        AssertNoLeftovers();
    }

    // ----- Search -----

    [Fact]
    public async Task Search_Before_Any_Dataset_Gives_NoDataset_With_Parsed_Query()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);

        SearchResult result = await service.SearchAsync("AS15169", 50, Ct);

        Assert.Equal(SearchOutcome.NoDataset, result.Outcome);
        Assert.NotNull(result.Query);
    }

    [Fact]
    public async Task Search_By_Asn_Ip_And_Bare_Number_Works_And_Marks_Large_Ranges()
    {
        IpInfoService service = await InstalledServiceAsync();

        SearchResult full = await service.SearchAsync("AS15169", 50, Ct);
        Assert.Equal(SearchOutcome.Found, full.Outcome);
        Assert.Equal(4, full.Rows.Count);
        Assert.Equal(4, full.TotalCount);
        Assert.True(full.Rows.Single(r => r.Network == "8.0.0.0/8").IsLargeRange);
        Assert.False(full.Rows.Single(r => r.Network == "8.8.8.0/24").IsLargeRange);

        SearchResult limited = await service.SearchAsync("AS15169", 2, Ct);
        Assert.Equal(2, limited.Rows.Count);
        Assert.Equal(4, limited.TotalCount);

        SearchResult bare = await service.SearchAsync("15169", 50, Ct);
        Assert.Equal(SearchOutcome.Found, bare.Outcome);
        Assert.Equal(4, bare.TotalCount);

        SearchResult ip = await service.SearchAsync("8.8.8.8", 50, Ct);
        Assert.Equal(SearchOutcome.Found, ip.Outcome);
        Assert.Contains(ip.Rows, r => r.Network == "8.8.8.0/24");
    }

    [Fact]
    public async Task Search_Invalid_And_Unknown_Inputs()
    {
        IpInfoService service = await InstalledServiceAsync();

        SearchResult invalid = await service.SearchAsync("nonsense", 50, Ct);
        Assert.Equal(SearchOutcome.InvalidInput, invalid.Outcome);
        Assert.Null(invalid.Query);

        SearchResult unknown = await service.SearchAsync("AS64500", 50, Ct);
        Assert.Equal(SearchOutcome.NoResults, unknown.Outcome);
        Assert.Equal(0, unknown.TotalCount);
    }

    [Fact]
    public async Task An_Overlap_Lookup_Names_The_File_That_Answered_Not_The_Dataset_Read_Before()
    {
        IpInfoService service = await InstalledServiceAsync();
        DatasetInfo installed = service.Dataset!;

        // A Promote without the dataset being read again: the file is replaced behind the service
        string next = Paths.NextDatabasePath;
        using (IpDatabase.Builder builder = IpDatabase.CreateBuilder(next, 10))
        {
            var (start, end) = Cidr.Parse("8.8.8.0/24");
            builder.Add(new IpInfoEntry { Network = "8.8.8.0/24", Asn = "AS15169", AsName = "Google LLC", StartIp = start, EndIp = end });
            builder.Complete("replacement-checksum", new DateOnly(2026, 10, 8), new DateTimeOffset(2026, 10, 8, 16, 5, 0, TimeSpan.Zero));
        }
        File.Move(next, Paths.DatabasePath, overwrite: true);
        Assert.True(CidrBlock.TryParse("8.8.8.8/32", out CidrBlock? block));

        OverlapResult result = await service.LookUpOverlapsAsync([block], Ct);

        Assert.Equal(OverlapOutcome.Found, result.Outcome);
        Assert.Equal("replacement-checksum", result.Dataset?.Checksum);
        Assert.NotEqual("replacement-checksum", installed.Checksum);
        Assert.Equal(installed, service.Dataset);
    }

    // ----- Export and copy -----

    [Fact]
    public async Task Export_Leaves_Out_Large_Ranges_Unless_Included_And_Honours_Fortigate_Setting()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { ExportFolder = _temp.ExportFolder, WriteFortigateFeeds = true }));
        Assert.Equal(_temp.ExportFolder, service.ExportFolder);
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportPlan plan = await service.PrepareExportAsync(query, Ct);
        Assert.Equal(4, plan.TotalCount);
        Assert.Equal("Google LLC", plan.AsName);
        LargeRange large = Assert.Single(plan.LargeRanges);
        Assert.Equal("8.0.0.0/8", large.Network);

        ExportResult withoutLarge = await service.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.None, withoutLarge.Failure);
        Assert.Equal(3, withoutLarge.NetworksWritten);
        Assert.Equal(1, withoutLarge.LargeRangesLeftOut);
        Assert.Equal(3, withoutLarge.Files.Count);
        string report = File.ReadAllText(withoutLarge.Files.Single(f => f.EndsWith("AS15169.txt", StringComparison.Ordinal)));
        Assert.DoesNotContain("8.0.0.0/8", report);
        Assert.Contains("8.8.8.0/24", report);
        Assert.All(withoutLarge.Files, f => Assert.DoesNotContain("8.0.0.0/8", File.ReadAllText(f)));

        ExportResult withLarge = await service.ExportAsync(query, ["8.0.0.0/8"], Ct);
        Assert.Equal(ExportFailure.None, withLarge.Failure);
        Assert.Equal(4, withLarge.NetworksWritten);
        Assert.Equal(0, withLarge.LargeRangesLeftOut);
        Assert.Contains("8.0.0.0/8", File.ReadAllText(withLarge.Files.Single(f => f.EndsWith("AS15169.txt", StringComparison.Ordinal))));

        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateEnabled = false }));
        ExportResult reportOnly = await service.ExportAsync(query, ["8.0.0.0/8"], Ct);
        Assert.Equal(ExportFailure.None, reportOnly.Failure);
        Assert.Single(reportOnly.Files);
    }

    [Fact]
    public async Task Export_Cuts_Exceptions_From_The_Feeds_And_The_Report_Still_Lists_Every_Network()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { ExportFolder = _temp.ExportFolder, WriteFortigateFeeds = true }));
        Assert.Equal(ExceptionsSaveOutcome.Saved, await service.SaveExceptionsAsync(
            [ExceptionEntryTests.Entry("8.8.8.0/25", "Resolver"), ExceptionEntryTests.Entry("2001:4860::/32", null)]));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(3, result.NetworksWritten);
        Assert.Equal(1, result.ExceptionCuts);
        Assert.Equal(1, result.ExceptionRemovals);
        Assert.Empty(result.OldFilesLeft!);
        string report = File.ReadAllText(result.Files.Single(f => f.EndsWith("AS15169.txt", StringComparison.Ordinal)));
        Assert.Contains("8.8.8.0/24", report);
        Assert.Contains("2001:4860::/32", report);
        // Every IPv6 network was removed, so there is a report and an IPv4 feed only
        string feed = File.ReadAllText(Assert.Single(result.Files, f => f.EndsWith("AS15169_ipv4.txt", StringComparison.Ordinal)));
        Assert.Equal(2, result.Files.Count);
        Assert.Contains("\n" + "8.8.8.128/25" + "\n", feed);
        Assert.DoesNotContain("8.8.8.0/24", feed);
    }

    [Theory]
    [InlineData(true, "fortigate/AS15169_ipv4.txt", new[] { "AS15169.txt" })]
    [InlineData(true, "fortigate/AS15169_ipv6.txt", new[] { "AS15169.txt", "fortigate/AS15169_ipv4.txt" })]
    [InlineData(true, "AS15169.txt", new string[0])]
    [InlineData(false, "fortigate/AS15169.txt", new[] { "AS15169.txt" })]
    public async Task Export_Names_The_File_That_Failed_And_The_Files_Written_Before_It(bool split, string blocked, string[] before)
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = split
        }));
        // A folder where the file should go makes that one write fail
        string blockedPath = Path.Combine(_temp.ExportFolder, blocked.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(blockedPath);
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(blockedPath, result.FailedFile);
        Assert.Equal(
            before.Select(name => Path.Combine(_temp.ExportFolder, name.Replace('/', Path.DirectorySeparatorChar))).ToArray(),
            result.Files.ToArray());
        Assert.All(result.Files, file => Assert.True(File.Exists(file)));
        Assert.Null(result.OldFilesLeft);
        // The temporary file was written and its move failed, so it was deleted
        Assert.Empty(Directory.GetFiles(_temp.ExportFolder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Export_Names_The_Old_Files_Left_When_A_Feed_Write_Fails()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = true
        }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        Assert.Equal(ExportFailure.None, (await service.ExportAsync(query, [], Ct)).Failure);
        string ipv6 = Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv6.txt");
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // A folder where the feed's temporary file should go makes that one write fail; the move never runs
        Directory.CreateDirectory(ipv6 + ".tmp");

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(ipv6, result.FailedFile);
        Assert.Equal([ipv6], result.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Export_Names_The_Old_Feeds_When_The_Report_Fails(bool split, bool fortigate)
    {
        IpInfoService service = await InstalledServiceAsync();
        // An earlier export with the feeds on leaves every file this layout has
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateEnabled = true,
            FortigateSplitByFamily = split
        }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        ExportResult earlier = await service.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.None, earlier.Failure);
        string report = Path.Combine(_temp.ExportFolder, "AS15169.txt");
        string[] feeds = split
            ? [Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv4.txt"), Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv6.txt")]
            : [Path.Combine(_temp.ExportFolder, "fortigate", "AS15169.txt")];
        Assert.Equal([report, .. feeds], earlier.Files);
        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateEnabled = fortigate }));
        // The report's write fails, so no feed is tried; with the feeds on, FortiGate keeps reading the old ones
        Directory.CreateDirectory(report + ".tmp");

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(report, result.FailedFile);
        Assert.Empty(result.Files);
        Assert.Equal(fortigate ? [report, .. feeds] : [report], result.OldFilesLeft);
    }

    [Fact]
    public async Task Export_Names_The_First_Feed_When_The_Feed_Folder_Cannot_Be_Made()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = true
        }));
        // A file where the fortigate folder should go fails each feed's folder, so the first feed is named
        Directory.CreateDirectory(_temp.ExportFolder);
        File.WriteAllText(Path.Combine(_temp.ExportFolder, "fortigate"), "");
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv4.txt"), result.FailedFile);
        Assert.Equal([Path.Combine(_temp.ExportFolder, "AS15169.txt")], result.Files.ToArray());
        Assert.True(File.Exists(result.Files[0]));
    }

    [Fact]
    public async Task Export_Writes_No_Feed_After_A_Launch_That_Stopped_On_The_Old_Exceptions()
    {
        // No settings save is possible before a launch succeeds, so Fortigate can't be off while the list is unreadable;
        // ExportWriterTests pins the writer's report-alone rule
        byte[] unreadable = Encoding.UTF8.GetBytes("{ not json");
        File.WriteAllBytes(Paths.ExceptionsPath, unreadable);
        IpInfoService service = NewService();
        Assert.Equal(StartupFailure.ExceptionsImportUnreadable, (await service.InitializeAsync(Ct)).Failure);
        Assert.False(service.Exceptions.IsReadable);
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult refused = await service.ExportAsync(query, [], Ct);

        // The stopped launch ticks no feed type, so nothing reaches the list and there is no dataset to export
        Assert.Equal(ExportFailure.NothingToExport, refused.Failure);
        Assert.True(refused.NoFeedType);
        Assert.Empty(refused.Files);
        Assert.False(Directory.Exists(service.ExportFolder));
        service.Dispose();
        Assert.Equal(unreadable, File.ReadAllBytes(Paths.ExceptionsPath));
    }

    [Fact]
    public async Task A_Launch_On_An_Empty_Folder_Ticks_No_Feed_Type()
    {
        IpInfoService service = NewService();

        Assert.Equal(StartupFailure.None, (await service.InitializeAsync(Ct)).Failure);

        Assert.False(service.Settings.WriteFortigateFeeds);
        Assert.False(service.Settings.WriteModSecurityLists);
    }

    [Fact]
    public async Task An_Export_With_No_Feed_Type_Ticked_Writes_The_Report_Alone_And_Says_So()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { ExportFolder = _temp.ExportFolder }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        string report = Path.Combine(_temp.ExportFolder, "AS15169.txt");

        ExportResult none = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.None, none.Failure);
        Assert.True(none.NoFeedType);
        Assert.Equal([report], none.Files);
        Assert.Null(none.ModSecurityFiles);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "modsecurity")));

        Assert.True(await service.SaveSettingsAsync(service.Settings with { FortigateEnabled = false }));
        ExportResult off = await service.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.None, off.Failure);
        Assert.False(off.NoFeedType);
        Assert.Equal([report], off.Files);
    }

    [Fact]
    public async Task An_Export_With_Both_Types_Ticked_Writes_Each_Copy_After_The_Report()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
        }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));
        string[] names = ["AS15169_ipv4.txt", "AS15169_ipv6.txt"];
        string[] modSecurity = names.Select(n => Path.Combine(_temp.ExportFolder, "modsecurity", n)).ToArray();

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.False(result.NoFeedType);
        Assert.Equal(3, result.NetworksWritten);
        Assert.Equal(0, result.WholeRangesLeftOut);
        Assert.Equal(
            [Path.Combine(_temp.ExportFolder, "AS15169.txt"), .. names.Select(n => Path.Combine(_temp.ExportFolder, "fortigate", n)), .. modSecurity],
            result.Files);
        Assert.Equal(modSecurity, result.ModSecurityFiles);
    }

    [Fact]
    public async Task A_ModSecurity_Folder_That_Cannot_Be_Made_Fails_After_Every_FortiGate_File()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
        }));
        Directory.CreateDirectory(_temp.ExportFolder);
        File.WriteAllText(Path.Combine(_temp.ExportFolder, "modsecurity"), "");
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(Path.Combine(_temp.ExportFolder, "modsecurity", "AS15169_ipv4.txt"), result.FailedFile);
        Assert.Equal(
            [
                Path.Combine(_temp.ExportFolder, "AS15169.txt"),
                Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv4.txt"),
                Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv6.txt"),
            ],
            result.Files);
        Assert.All(result.Files, file => Assert.True(File.Exists(file)));
        Assert.Null(result.ModSecurityFiles);
        Assert.False(result.NoFeedType);
        Assert.Equal(0, result.WholeRangesLeftOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Failed_Export_With_No_Feed_Type_Ticked_Still_Says_So(bool reportFails)
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            FortigateEnabled = true,
            WriteFortigateFeeds = false,
            WriteModSecurityLists = false,
        }));
        if (reportFails)
            Directory.CreateDirectory(Path.Combine(_temp.ExportFolder, "AS15169.txt.tmp"));
        else
            File.WriteAllText(_temp.ExportFolder, "");
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(reportFails ? ExportFailure.WriteFailed : ExportFailure.FolderUnavailable, result.Failure);
        Assert.True(result.NoFeedType);
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "fortigate")));
        Assert.False(Directory.Exists(Path.Combine(_temp.ExportFolder, "modsecurity")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Failed_Report_Names_The_Old_Feeds_Of_Each_Ticked_Type_FortiGate_First(bool modSecurity)
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = modSecurity,
            FortigateSplitByFamily = true,
        }));
        string[] fortigate = ["AS15169_ipv4.txt", "AS15169_ipv6.txt"];
        string[] old =
        [
            .. fortigate.Select(n => Path.Combine(_temp.ExportFolder, "fortigate", n)),
            .. fortigate.Select(n => Path.Combine(_temp.ExportFolder, "modsecurity", n)),
        ];
        foreach (string file in old)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "old " + file + "\n");
        }
        Directory.CreateDirectory(Path.Combine(_temp.ExportFolder, "AS15169.txt.tmp"));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, [], Ct);

        Assert.Equal(ExportFailure.WriteFailed, result.Failure);
        Assert.Equal(Path.Combine(_temp.ExportFolder, "AS15169.txt"), result.FailedFile);
        Assert.Equal(modSecurity ? old : old[..2], result.OldFilesLeft);
        Assert.All(old, file => Assert.Equal("old " + file + "\n", File.ReadAllText(file)));
    }

    [Fact]
    public async Task An_Exported_Range_Covering_Every_Address_Stays_In_The_Report_And_Is_Left_Out_Of_The_Feed()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("test-token"));
        _remote.Body = GzBody([.. SampleLines, Line("0.0.0.0/0")]);
        Assert.Equal(UpdateFailure.None, (await service.UpdateDatasetAsync(null, Ct)).Failure);
        Assert.True(await service.SaveSettingsAsync(service.Settings with
        {
            ExportFolder = _temp.ExportFolder,
            WriteFortigateFeeds = true,
            FortigateSplitByFamily = true,
        }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportResult result = await service.ExportAsync(query, ["0.0.0.0/0"], Ct);

        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(1, result.WholeRangesLeftOut);
        Assert.Contains("| 0.0.0.0/0 ", File.ReadAllText(Path.Combine(_temp.ExportFolder, "AS15169.txt")));
        string feed = File.ReadAllText(Path.Combine(_temp.ExportFolder, "fortigate", "AS15169_ipv4.txt"));
        Assert.DoesNotContain("\n0.0.0.0/0\n", feed);
        Assert.Contains("\n# Left out: 1 range covering every address: 0.0.0.0/0\n", feed);
        Assert.Contains("\n8.8.8.0/24\n", feed);
    }

    [Fact]
    public async Task Results_Text_Has_Header_And_One_Line_Per_Result()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        string text = await service.GetResultsTextAsync(query, Ct);

        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Network\t", lines[0]);
        Assert.Equal(5, lines.Length);
    }

    // ----- IPv6 turned off -----

    /// <summary>A service with IPv6 switched off before the first download, so the dataset is IPv4 only.</summary>
    private async Task<IpInfoService> InstalledWithIPv6OffAsync()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.True(await service.SaveTokenAsync("test-token"));
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false, ExportFolder = _temp.ExportFolder }));
        _remote.Body = GzBody(SampleLines);

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);
        Assert.Equal(UpdateFailure.None, result.Failure);
        return service;
    }

    [Fact]
    public async Task Fresh_Install_With_IPv6_Off_Builds_An_IPv4_Only_Dataset()
    {
        IpInfoService service = await InstalledWithIPv6OffAsync();

        Assert.NotNull(service.Dataset);
        Assert.False(service.Dataset.IncludesIPv6);
        Assert.Equal(5L, service.Dataset.RangeCount);
        Assert.Equal(1, DownloadRequests);
    }

    [Fact]
    public async Task Search_With_IPv6_Off_Leaves_Out_IPv6_Networks()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));

        SearchResult result = await service.SearchAsync("AS15169", 50, Ct);

        Assert.Equal(SearchOutcome.Found, result.Outcome);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Rows.Count);
        Assert.DoesNotContain(result.Rows, r => r.Network.Contains(':', StringComparison.Ordinal));
    }

    [Fact]
    public async Task IPv6_Address_Search_With_IPv6_Off_Gives_IPv6Disabled()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));

        SearchResult result = await service.SearchAsync("2001:4860::1", 50, Ct);

        Assert.Equal(SearchOutcome.IPv6Disabled, result.Outcome);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        Assert.NotNull(result.Query);
    }

    [Fact]
    public async Task IPv4_Address_Search_With_IPv6_Off_Still_Works()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));

        SearchResult result = await service.SearchAsync("8.8.8.8", 50, Ct);

        Assert.Equal(SearchOutcome.Found, result.Outcome);
        Assert.Contains(result.Rows, r => r.Network == "8.8.8.0/24");
    }

    [Fact]
    public async Task Export_With_IPv6_Off_Writes_No_IPv6_Networks_Or_IPv6_Feed()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false, ExportFolder = _temp.ExportFolder, WriteFortigateFeeds = true }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        ExportPlan plan = await service.PrepareExportAsync(query, Ct);
        Assert.Equal(3, plan.TotalCount);

        ExportResult result = await service.ExportAsync(query, [], Ct);
        Assert.Equal(ExportFailure.None, result.Failure);
        Assert.Equal(2, result.NetworksWritten);
        Assert.Equal(2, result.Files.Count);
        Assert.DoesNotContain(result.Files, f => f.EndsWith("_ipv6.txt", StringComparison.Ordinal));
        Assert.All(result.Files, f => Assert.DoesNotContain("2001:4860", File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Results_Text_With_IPv6_Off_Has_No_IPv6_Lines()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));
        Assert.True(SearchQuery.TryParse("AS15169", out SearchQuery? query));

        string text = await service.GetResultsTextAsync(query, Ct);

        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain("2001:4860", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_IPv6_Back_On_Makes_An_IPv4_Only_Dataset_Need_A_Rebuild()
    {
        IpInfoService service = await InstalledWithIPv6OffAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = true }));

        UpdateCheckResult check = await service.CheckForUpdateAsync(Ct);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, check.Outcome);
        Assert.True(check.AddsIPv6);

        UpdateResult update = await service.UpdateDatasetAsync(null, Ct);
        Assert.Equal(UpdateFailure.None, update.Failure);
        Assert.False(update.AlreadyCurrent);
        Assert.True(service.Dataset!.IncludesIPv6);
        Assert.Equal(2, DownloadRequests);

        SearchResult search = await service.SearchAsync("AS15169", 50, Ct);
        Assert.Equal(4, search.TotalCount);
    }

    [Fact]
    public async Task Check_With_IPv6_Off_Is_Up_To_Date_When_Checksum_Matches()
    {
        IpInfoService service = await InstalledWithIPv6OffAsync();

        UpdateCheckResult check = await service.CheckForUpdateAsync(Ct);

        Assert.Equal(UpdateCheckOutcome.UpToDate, check.Outcome);
    }

    [Fact]
    public async Task Update_With_IPv6_Off_On_An_IPv6_Dataset_Does_Not_Rebuild()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false }));

        UpdateResult result = await service.UpdateDatasetAsync(null, Ct);

        Assert.Equal(UpdateFailure.None, result.Failure);
        Assert.True(result.AlreadyCurrent);
        Assert.Equal(1, DownloadRequests);
    }

    // ----- Settings and token -----

    [Fact]
    public async Task Saved_Exceptions_Are_Seen_By_A_New_Instance()
    {
        IpInfoService first = NewService();
        await first.InitializeAsync(Ct);
        Assert.True(first.Exceptions.IsReadable);
        Assert.Empty(first.Exceptions.Entries);
        Assert.Equal(ExceptionsSaveOutcome.Saved, await first.SaveExceptionsAsync(
            [ExceptionEntryTests.Entry("192.0.2.0/24", "Head office"), ExceptionEntryTests.Entry("2001:db8::/32", null)]));
        Assert.Equal(2, first.Exceptions.Entries.Count);
        first.Dispose();

        IpInfoService second = NewService();
        await second.InitializeAsync(Ct);

        Assert.True(second.Exceptions.IsReadable);
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], second.Exceptions.Entries.Select(entry => entry.Network.Text));
        Assert.Equal("Head office", second.Exceptions.Entries[0].Note);
        // config.db is the only place the list is saved
        Assert.False(File.Exists(Paths.ExceptionsPath));
    }

    [Fact]
    public async Task A_Failed_Exceptions_Save_Keeps_The_List_In_Use()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.Equal(ExceptionsSaveOutcome.Saved, await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("192.0.2.0/24", null)]));
        ExceptionList before = service.Exceptions;

        var tooMany = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
            .Select(i => ExceptionEntryTests.Entry($"10.{i / 256}.{i % 256}.0/24", null))
            .ToList();
        Assert.Equal(ExceptionsSaveOutcome.Failed, await service.SaveExceptionsAsync(tooMany));
        Assert.Same(before, service.Exceptions);

        // A write that fails inside config.db's transaction changes nothing in use
        UserSettings settingsBefore = service.Settings;
        service.Config!.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);
        Assert.Equal(ExceptionsSaveOutcome.Failed, await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("198.51.100.0/24", null)]));
        Assert.Same(before, service.Exceptions);
        Assert.False(await service.SaveSettingsAsync(settingsBefore with { IPv4MinPrefix = 12 }));
        Assert.Same(settingsBefore, service.Settings);
    }

    [Fact]
    public async Task A_List_Too_Long_To_Save_Fails_And_Leaves_The_Old_Files()
    {
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");
        File.WriteAllText(Paths.UnreadableExceptionsPath, "an older copy");
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.False(service.Exceptions.IsReadable);

        var tooMany = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
            .Select(i => ExceptionEntryTests.Entry($"10.{i / 256}.{i % 256}.0/24", null))
            .ToList();
        ExceptionsSaveOutcome outcome = await service.SaveExceptionsAsync(tooMany);

        Assert.Equal(ExceptionsSaveOutcome.Failed, outcome);
        Assert.Equal("an older copy", File.ReadAllText(Paths.UnreadableExceptionsPath));
        Assert.Equal("{ not json", File.ReadAllText(Paths.ExceptionsPath));
    }

    [Fact]
    public async Task A_Save_Leaves_An_Older_Unreadable_Copy_Alone()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        Assert.Equal(ExceptionsSaveOutcome.Saved, await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("192.0.2.0/24", null)]));
        File.WriteAllText(Paths.UnreadableExceptionsPath, "an older copy");

        ExceptionsSaveOutcome outcome = await service.SaveExceptionsAsync([ExceptionEntryTests.Entry("198.51.100.0/24", null)]);

        Assert.Equal(ExceptionsSaveOutcome.Saved, outcome);
        Assert.Equal("an older copy", File.ReadAllText(Paths.UnreadableExceptionsPath));
    }

    [Fact]
    public async Task Saved_Settings_Are_Seen_By_A_New_Instance_And_Token_Can_Be_Cleared()
    {
        IpInfoService first = NewService();
        await first.InitializeAsync(Ct);
        Assert.True(await first.SaveSettingsAsync(first.Settings with { IPv4MinPrefix = 12, FortigateEnabled = false }));
        Assert.True(await first.SaveTokenAsync("token"));
        first.Dispose();

        IpInfoService second = NewService();
        StartupResult result = await second.InitializeAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        Assert.Equal(12, second.Settings.IPv4MinPrefix);
        Assert.False(second.Settings.FortigateEnabled);
        Assert.True(second.HasToken);

        Assert.True(await second.ClearTokenAsync());
        Assert.False(second.HasToken);
        // config.db is the only place the settings are saved
        Assert.False(File.Exists(Paths.SettingsPath));
    }

    [Fact]
    public async Task SaveSettingsAsync_Clamps_The_Keep_Days_In_Memory_And_In_Config()
    {
        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);

        Assert.True(await service.SaveSettingsAsync(service.Settings with { AbuseIpDbKeepDays = 0 }));

        Assert.Equal(1, service.Settings.AbuseIpDbKeepDays);
        service.Dispose();

        IpInfoService next = NewService();
        Assert.Equal(StartupFailure.None, (await next.InitializeAsync(Ct)).Failure);
        Assert.Equal(1, next.Settings.AbuseIpDbKeepDays);
    }

    [Fact]
    public async Task Startup_Leaves_The_AbuseIpDb_Database_And_Its_Journal_Untouched()
    {
        byte[] database = [0x53, 0x51, 0x4c, 0x00, 0x01, 0x02, 0xff];
        byte[] journal = [0x0a, 0x0b, 0x0c];
        string journalPath = Paths.AbuseIpDbDatabasePath + "-journal";
        File.WriteAllBytes(Paths.AbuseIpDbDatabasePath, database);
        File.WriteAllBytes(journalPath, journal);

        IpInfoService service = NewService();
        await service.InitializeAsync(Ct);
        service.Dispose();

        Assert.Equal(database, File.ReadAllBytes(Paths.AbuseIpDbDatabasePath));
        Assert.Equal(journal, File.ReadAllBytes(journalPath));
    }

    // ----- config.db at start-up -----

    [Theory]
    [InlineData((int)ConfigOpenState.Newer, StartupFailure.ConfigNewer)]
    [InlineData((int)ConfigOpenState.Unreadable, StartupFailure.ConfigUnreadable)]
    [InlineData((int)ConfigOpenState.KeyMismatch, StartupFailure.ConfigKeyMismatch)]
    [InlineData((int)ConfigOpenState.Locked, StartupFailure.ConfigLocked)]
    [InlineData((int)ConfigOpenState.StorageError, StartupFailure.ConfigStorageError)]
    [InlineData((int)ConfigOpenState.KeyUnavailable, StartupFailure.ConfigKeyUnavailable)]
    [InlineData((int)ConfigOpenState.NoKeyStore, StartupFailure.ConfigKeyStoreMissing)]
    [InlineData((int)ConfigOpenState.ExceptionsImportUnreadable, StartupFailure.ExceptionsImportUnreadable)]
    [InlineData((int)ConfigOpenState.Missing, StartupFailure.ConfigMissing)]
    public void Each_Config_State_But_Opened_Has_Its_Startup_Failure(int state, StartupFailure expected)
    {
        Assert.Equal(expected, IpInfoService.StartupFailureOf((ConfigOpenState)state));
    }

    [Fact]
    public void Every_Config_Startup_Failure_Comes_From_A_Config_State_And_Opened_Has_None()
    {
        StartupFailure[] notFromConfig =
        [
            StartupFailure.None, StartupFailure.Privileged, StartupFailure.AlreadyRunning,
            StartupFailure.StorageUnavailable, StartupFailure.DatabaseNewer,
        ];
        var image = Enum.GetValues<ConfigOpenState>()
            .Where(state => state != ConfigOpenState.Opened)
            .Select(IpInfoService.StartupFailureOf)
            .ToHashSet();

        Assert.Equal(Enum.GetValues<StartupFailure>().Except(notFromConfig).Order(), image.Order());
        Assert.Throws<ArgumentOutOfRangeException>(() => IpInfoService.StartupFailureOf(ConfigOpenState.Opened));
    }

    [Fact]
    public async Task A_Config_File_That_Is_Not_A_Database_Stops_Launch_As_ConfigUnreadable()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);

        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);
        service.Dispose();

        Assert.Equal(NotADatabase, File.ReadAllBytes(Paths.ConfigPath));
    }

    [Fact]
    public async Task A_Config_File_Of_A_Newer_Version_Stops_Launch_As_ConfigNewer()
    {
        await LaunchOnceAsync();
        ExecuteOnConfig("UPDATE metadata SET value = '2' WHERE key = 'schema_version';");

        await StoppedServiceAsync(StartupFailure.ConfigNewer);
    }

    [Fact]
    public async Task A_Config_Key_That_Does_Not_Open_The_File_Stops_Launch_As_ConfigKeyMismatch()
    {
        await LaunchOnceAsync();

        await StoppedServiceAsync(StartupFailure.ConfigKeyMismatch, new InMemorySecretStore());
    }

    [Fact]
    public async Task A_Config_Key_That_Cannot_Be_Read_Now_Stops_Launch_As_ConfigKeyUnavailable()
    {
        await LaunchOnceAsync();
        byte[] key = _configKeys.Secret!.ToArray();
        _configKeys.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        await StoppedServiceAsync(StartupFailure.ConfigKeyUnavailable);

        Assert.Equal(key, _configKeys.Secret);
    }

    [Fact]
    public async Task A_System_With_No_Secret_Store_Stops_Launch_As_ConfigKeyStoreMissing()
    {
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigKeyStoreMissing, new SecretStores.UnavailableSecretStore());
        service.Dispose();

        Assert.False(File.Exists(Paths.ConfigPath));
    }

    [Fact]
    public async Task An_Old_Exceptions_File_That_Cannot_Be_Read_Stops_Launch_As_ExceptionsImportUnreadable()
    {
        byte[] unreadable = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"not a network\" } ] }");
        File.WriteAllBytes(Paths.ExceptionsPath, unreadable);

        IpInfoService service = await StoppedServiceAsync(StartupFailure.ExceptionsImportUnreadable);
        service.Dispose();

        Assert.Equal(unreadable, File.ReadAllBytes(Paths.ExceptionsPath));
        Assert.False(File.Exists(Paths.ConfigPath));
        Assert.False(File.Exists(Paths.UnreadableExceptionsPath));
    }

    [Fact]
    public async Task The_Instance_Lock_Is_Held_After_A_Config_Failure()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        IpInfoService first = NewService();
        Assert.Equal(StartupFailure.ConfigUnreadable, (await first.InitializeAsync(Ct)).Failure);

        IpInfoService second = NewService();
        Assert.Equal(StartupFailure.AlreadyRunning, (await second.InitializeAsync(Ct)).Failure);
        first.Dispose();

        IpInfoService third = NewService();
        Assert.Equal(StartupFailure.ConfigUnreadable, (await third.InitializeAsync(Ct)).Failure);
        third.Dispose();
        Assert.Equal(NotADatabase, File.ReadAllBytes(Paths.ConfigPath));
    }

    [Fact]
    public async Task A_Second_Initialize_Answers_What_The_First_Did_And_Runs_Nothing_Again()
    {
        IpInfoService service = NewService();
        StartupResult first = await service.InitializeAsync(Ct);
        Assert.Equal(StartupFailure.None, first.Failure);
        File.WriteAllText(Paths.NextDatabasePath, "stale build");

        Assert.Same(first, await service.InitializeAsync(Ct));
        Assert.True(File.Exists(Paths.NextDatabasePath));
    }

    [Fact]
    public async Task A_Second_Initialize_After_A_Config_Failure_Answers_The_Same_Failure()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);

        StartupResult again = await service.InitializeAsync(Ct);

        Assert.Equal(StartupFailure.ConfigUnreadable, again.Failure);
        Assert.True(File.Exists(Paths.NextDatabasePath));
        Assert.Null(service.Config);
    }

    [Fact]
    public async Task Start_Fresh_After_ConfigUnreadable_Keeps_The_Old_File_And_Starts_With_The_Defaults()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);

        StartupResult fresh = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.None, fresh.Failure);
        Assert.NotNull(service.Config);
        Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, service.Settings);
        Assert.True(service.Exceptions.IsReadable);
        Assert.Empty(service.Exceptions.Entries);
        Assert.Equal(AbuseAccounting.Empty, service.AbuseAccounting.Current);
        AssertNoLeftovers();

        // A second press finds nothing to do
        Assert.Same(fresh, await service.StartFreshAsync(Ct));
        service.Dispose();
        Assert.Equal(NotADatabase, File.ReadAllBytes(Paths.UnreadableConfigPath));
    }

    [Fact]
    public async Task Start_Fresh_After_ConfigKeyMismatch_Keeps_A_Key_That_Reads()
    {
        await LaunchOnceAsync();
        byte[] wrong = RandomNumberGenerator.GetBytes(32);
        _configKeys.Secret = wrong.ToArray();
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigKeyMismatch);

        StartupResult fresh = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.None, fresh.Failure);
        Assert.True(service.Exceptions.IsReadable);
        Assert.Equal(wrong, _configKeys.Secret);
        Assert.True(File.Exists(Paths.UnreadableConfigPath));
    }

    [Fact]
    public async Task Start_Fresh_After_ConfigKeyMismatch_Replaces_A_Key_That_Can_Never_Be_Read()
    {
        await LaunchOnceAsync();
        byte[] original = _configKeys.Secret!.ToArray();
        byte[] unusable = RandomNumberGenerator.GetBytes(31);
        _configKeys.Secret = unusable.ToArray();
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigKeyMismatch);

        StartupResult fresh = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.None, fresh.Failure);
        Assert.True(service.Exceptions.IsReadable);
        byte[] key = Assert.IsType<byte[]>(_configKeys.Secret);
        Assert.Equal(32, key.Length);
        Assert.NotEqual(original, key);
    }

    [Fact]
    public async Task Start_Fresh_After_A_Launch_That_Succeeded_Does_Nothing()
    {
        IpInfoService service = NewService();
        StartupResult launched = await service.InitializeAsync(Ct);
        Assert.Equal(StartupFailure.None, launched.Failure);
        ConfigStore? config = service.Config;

        Assert.Same(launched, await service.StartFreshAsync(Ct));

        Assert.Same(config, service.Config);
        Assert.False(File.Exists(Paths.UnreadableConfigPath));
    }

    [Fact]
    public async Task Start_Fresh_After_ExceptionsImportUnreadable_Does_Nothing()
    {
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ExceptionsImportUnreadable);
        StartupResult stopped = await service.InitializeAsync(Ct);

        StartupResult result = await service.StartFreshAsync(Ct);

        // The very result the launch gave: nothing ran again
        Assert.Same(stopped, result);
        Assert.Equal(StartupFailure.ExceptionsImportUnreadable, result.Failure);
        Assert.Null(service.Config);
        Assert.True(File.Exists(Paths.NextDatabasePath));
        service.Dispose();
        Assert.False(File.Exists(Paths.UnreadableConfigPath));
        Assert.False(File.Exists(Paths.ConfigPath));
        Assert.Equal("{ not json", File.ReadAllText(Paths.ExceptionsPath));
    }

    [Fact]
    public async Task Start_Fresh_Before_Initialize_Does_Nothing()
    {
        IpInfoService service = NewService();

        StartupResult result = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.StorageUnavailable, result.Failure);
        Assert.Null(service.Config);
        Assert.False(File.Exists(Paths.ConfigPath));
        Assert.False(File.Exists(Paths.UnreadableConfigPath));
        Assert.False(File.Exists(Paths.LockPath));
    }

    [Fact]
    public async Task Start_Without_The_Old_Exceptions_Keeps_Them_Aside_And_Imports_The_Settings()
    {
        byte[] unreadable = Encoding.UTF8.GetBytes("{ not json");
        File.WriteAllBytes(Paths.ExceptionsPath, unreadable);
        File.WriteAllBytes(Paths.SettingsPath, SettingsStore.ToJson(new UserSettings { IPv4MinPrefix = 12 }));
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ExceptionsImportUnreadable);

        StartupResult result = await service.StartWithoutOldExceptionsAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        Assert.True(service.Exceptions.IsReadable);
        Assert.Empty(service.Exceptions.Entries);
        Assert.Equal(12, service.Settings.IPv4MinPrefix);
        Assert.True(File.Exists(Paths.SettingsPath + AppPaths.MigratedSuffix));
        Assert.False(File.Exists(Paths.ExceptionsPath));
        AssertNoLeftovers();
        service.Dispose();
        Assert.Equal(unreadable, File.ReadAllBytes(Paths.UnreadableExceptionsPath));
    }

    [Fact]
    public async Task When_The_Old_Exceptions_Cannot_Be_Set_Aside_Nothing_Is_Imported()
    {
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ExceptionsImportUnreadable);
        // A folder where the copy should go: the move fails
        Directory.CreateDirectory(Paths.UnreadableExceptionsPath);

        StartupResult result = await service.StartWithoutOldExceptionsAsync(Ct);

        Assert.Equal(StartupFailure.ExceptionsImportUnreadable, result.Failure);
        Assert.Null(service.Config);
        Assert.False(service.Exceptions.IsReadable);
        Assert.True(File.Exists(Paths.NextDatabasePath));
        service.Dispose();
        Assert.Equal("{ not json", File.ReadAllText(Paths.ExceptionsPath));
        Assert.False(File.Exists(Paths.ConfigPath));
    }

    [Fact]
    public async Task Start_Without_The_Old_Exceptions_After_A_Launch_That_Succeeded_Does_Nothing()
    {
        IpInfoService service = NewService();
        StartupResult launched = await service.InitializeAsync(Ct);
        Assert.Equal(StartupFailure.None, launched.Failure);
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");

        Assert.Same(launched, await service.StartWithoutOldExceptionsAsync(Ct));

        Assert.Equal("{ not json", File.ReadAllText(Paths.ExceptionsPath));
        Assert.False(File.Exists(Paths.UnreadableExceptionsPath));
    }

    [Fact]
    public async Task Start_Without_The_Old_Exceptions_After_ConfigUnreadable_Does_Nothing()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);
        StartupResult stopped = await service.InitializeAsync(Ct);

        StartupResult result = await service.StartWithoutOldExceptionsAsync(Ct);

        // The very result the launch gave: nothing ran again
        Assert.Same(stopped, result);
        Assert.Equal(StartupFailure.ConfigUnreadable, result.Failure);
        Assert.Null(service.Config);
        service.Dispose();
        Assert.Equal("{ not json", File.ReadAllText(Paths.ExceptionsPath));
        Assert.False(File.Exists(Paths.UnreadableExceptionsPath));
        Assert.Equal(NotADatabase, File.ReadAllBytes(Paths.ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Config_File_Deleted_After_A_Launch_Stops_Launch_Until_Start_Fresh(bool olderCopy)
    {
        await LaunchOnceAsync();
        byte[] key = _configKeys.Secret!.ToArray();
        File.Delete(Paths.ConfigPath);
        byte[] older = Encoding.UTF8.GetBytes("an older copy");
        if (olderCopy)
            File.WriteAllBytes(Paths.UnreadableConfigPath, older);

        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigMissing);
        Assert.False(File.Exists(Paths.ConfigPath));

        StartupResult fresh = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.None, fresh.Failure);
        Assert.NotNull(service.Config);
        Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, service.Settings);
        Assert.True(service.Exceptions.IsReadable);
        Assert.Empty(service.Exceptions.Entries);
        Assert.Equal(key, _configKeys.Secret);
        service.Dispose();
        if (olderCopy)
            Assert.Equal(older, File.ReadAllBytes(Paths.UnreadableConfigPath));
        else
            Assert.False(File.Exists(Paths.UnreadableConfigPath));
    }

    [Theory]
    [InlineData(StartupFailure.ConfigNewer)]
    [InlineData(StartupFailure.ConfigUnreadable)]
    [InlineData(StartupFailure.ConfigKeyMismatch)]
    [InlineData(StartupFailure.ConfigMissing)]
    public async Task Start_Fresh_With_A_Key_That_Cannot_Be_Read_Touches_Nothing_And_Offers_Nothing_More(StartupFailure stopped)
    {
        switch (stopped)
        {
            case StartupFailure.ConfigNewer:
                await LaunchOnceAsync();
                ExecuteOnConfig("UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
                break;
            case StartupFailure.ConfigUnreadable:
                File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
                break;
            case StartupFailure.ConfigKeyMismatch:
                await LaunchOnceAsync();
                _configKeys.Secret = RandomNumberGenerator.GetBytes(32);
                break;
            default:
                await LaunchOnceAsync();
                File.Delete(Paths.ConfigPath);
                break;
        }
        IpInfoService service = await StoppedServiceAsync(stopped);
        byte[]? before = File.Exists(Paths.ConfigPath) ? File.ReadAllBytes(Paths.ConfigPath) : null;
        _configKeys.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        StartupResult first = await service.StartFreshAsync(Ct);

        Assert.Equal(StartupFailure.ConfigKeyUnavailable, first.Failure);
        Assert.Null(service.Config);
        Assert.False(service.Exceptions.IsReadable);

        // The store can be read again, but this run offers nothing more: the very same answer, and nothing ran
        _configKeys.ReadFailure = null;
        int reads = _configKeys.Reads;
        Assert.Same(first, await service.StartFreshAsync(Ct));
        Assert.Equal(reads, _configKeys.Reads);
        Assert.Null(service.Config);
        Assert.True(File.Exists(Paths.NextDatabasePath));
        service.Dispose();
        Assert.Equal(before, File.Exists(Paths.ConfigPath) ? File.ReadAllBytes(Paths.ConfigPath) : null);
        Assert.False(File.Exists(Paths.UnreadableConfigPath));
    }

    [Fact]
    public async Task The_Download_Accounting_Is_Available_Only_While_Config_Is_Open()
    {
        IpInfoService service = NewService();
        Assert.False(service.AbuseAccounting.IsAvailable);

        Assert.Equal(StartupFailure.None, (await service.InitializeAsync(Ct)).Failure);
        Assert.True(service.AbuseAccounting.IsAvailable);

        service.Dispose();
        Assert.False(service.AbuseAccounting.IsAvailable);
    }

    [Fact]
    public async Task The_Download_Accounting_Is_Not_Available_After_A_Failed_Launch()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);

        Assert.False(service.AbuseAccounting.IsAvailable);
    }

    [Fact]
    public async Task Dispose_Releases_The_Config_File()
    {
        await LaunchOnceAsync();

        // Only a connection that could take the file's lock gets this far without BUSY
        ExecuteOnConfig("BEGIN EXCLUSIVE; COMMIT;");
    }

    [Fact]
    public async Task Dispose_During_A_Start_Up_Step_Waits_For_It_Then_Frees_Both_Locks()
    {
        await LaunchOnceAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The key read inside the open of config.db holds the step
        _configKeys.BeforeRead = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        IpInfoService service = NewService();

        Task<StartupResult> starting = Task.Run(() => service.InitializeAsync(Ct));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        _configKeys.BeforeRead = null;
        Task disposing = Task.Run(service.Dispose);

        // Still inside the step, so Dispose waits for it
        Assert.NotSame(disposing, await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromMilliseconds(300), Ct)));
        release.Set();
        Assert.Equal(StartupFailure.None, (await starting).Failure);
        await disposing.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Neither config.db nor the instance lock is held now
        IpInfoService next = NewService();
        Assert.Equal(StartupFailure.None, (await next.InitializeAsync(Ct)).Failure);
    }

    [Fact]
    public async Task Start_Without_The_Old_Exceptions_After_A_First_Launch_That_Saved_Only_The_Key_Opens()
    {
        // A first launch that saved the key and then couldn't make the file (a folder in its place)
        Directory.CreateDirectory(Paths.ConfigPath);
        IpInfoService failed = NewService();
        Assert.Equal(StartupFailure.ConfigStorageError, (await failed.InitializeAsync(Ct)).Failure);
        failed.Dispose();
        Directory.Delete(Paths.ConfigPath);
        Assert.True(_configKeys.Marker);
        File.WriteAllText(Paths.ExceptionsPath, "{ not json");

        IpInfoService service = await StoppedServiceAsync(StartupFailure.ExceptionsImportUnreadable);
        StartupResult result = await service.StartWithoutOldExceptionsAsync(Ct);

        Assert.Equal(StartupFailure.None, result.Failure);
        Assert.NotNull(service.Config);
        Assert.True(service.Exceptions.IsReadable);
        Assert.Empty(service.Exceptions.Entries);
        service.Dispose();
        Assert.Equal("{ not json", File.ReadAllText(Paths.UnreadableExceptionsPath));
    }

    [Fact]
    public async Task The_Download_Accounting_Is_Kept_In_Config_Once_A_Launch_Succeeds()
    {
        var limit = new AbuseRateLimit(5, 4, new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        IpInfoService service = NewService();
        Assert.Equal(AbuseAccounting.Empty, service.AbuseAccounting.Current);
        Assert.False(service.AbuseAccounting.TryRecordAnswer(limit, _time.GetUtcNow(), countDownload: true));

        await service.InitializeAsync(Ct);
        Assert.True(service.AbuseAccounting.TryRecordAnswer(limit, _time.GetUtcNow(), countDownload: true));
        var expected = new AbuseAccounting(limit, new DateOnly(2026, 10, 7), 1);
        Assert.Equal(expected, service.AbuseAccounting.Current);
        service.Dispose();

        IpInfoService next = NewService();
        await next.InitializeAsync(Ct);
        Assert.Equal(expected, next.AbuseAccounting.Current);
    }

    [Fact]
    public async Task The_Download_Accounting_Writes_Nothing_After_A_Failed_Launch()
    {
        File.WriteAllBytes(Paths.ConfigPath, NotADatabase);
        IpInfoService service = await StoppedServiceAsync(StartupFailure.ConfigUnreadable);

        Assert.False(service.AbuseAccounting.TryRecordAnswer(new AbuseRateLimit(5, 4, null), _time.GetUtcNow(), countDownload: true));
        Assert.Equal(AbuseAccounting.Empty, service.AbuseAccounting.Current);
    }
}
