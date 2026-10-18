using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
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

    private IpInfoService NewService(Func<bool>? isPrivileged = null)
    {
        var service = new IpInfoService(Paths, _http, new CoreOptions(), _time,
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
    public async Task Second_Instance_Is_Refused_Until_First_Is_Disposed()
    {
        IpInfoService first = NewService();
        Assert.Equal(StartupFailure.None, (await first.InitializeAsync(Ct)).Failure);

        IpInfoService second = NewService();
        Assert.Equal(StartupFailure.AlreadyRunning, (await second.InitializeAsync(Ct)).Failure);

        first.Dispose();

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

    // ----- Export and copy -----

    [Fact]
    public async Task Export_Leaves_Out_Large_Ranges_Unless_Included_And_Honours_Fortigate_Setting()
    {
        IpInfoService service = await InstalledServiceAsync();
        Assert.True(await service.SaveSettingsAsync(service.Settings with { ExportFolder = _temp.ExportFolder }));
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
        Assert.True(await service.SaveSettingsAsync(service.Settings with { IPv6Enabled = false, ExportFolder = _temp.ExportFolder }));
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
    }
}
