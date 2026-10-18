using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Core;

/// <summary>
/// The Core facade the UI talks to. Disk, SQLite and network work runs on the thread pool. Failures come back as
/// result values; the only exception that escapes is OperationCanceledException for the caller's own token.
/// </summary>
public sealed class IpInfoService : IIpInfoService
{
    private readonly AppPaths _paths;
    private readonly CoreOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<bool> _isPrivileged;
    private readonly IpInfoClient _client;
    private readonly SettingsStore _settingsStore;
    private readonly TokenStore _tokenStore;
    private readonly IpDatabase _database;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private volatile UserSettings _settings = new();
    private volatile DatasetInfo? _dataset;
    private InstanceLock? _lock;

    /// <summary>The service the app uses: the token is kept in the operating system's store for the current user.</summary>
    public static IpInfoService ForCurrentUser(AppPaths paths, HttpClient http, CoreOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new IpInfoService(paths, http, options, time, SecretStores.ForCurrentUser(paths.TokenPath));
    }

    // Tests pass an in-memory store here, so they never reach the real Keychain item, which is one per user account
    internal IpInfoService(AppPaths paths, HttpClient http, CoreOptions options, TimeProvider time, ISecretStore secretStore,
                           Func<bool>? isPrivileged = null,
                           Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(secretStore);

        _paths = paths;
        _options = options;
        _time = time;
        _isPrivileged = isPrivileged ?? new Func<bool>(IsPrivilegedProcess);
        _client = new IpInfoClient(http, options, delay);
        _settingsStore = new SettingsStore(paths.SettingsPath);
        _tokenStore = new TokenStore(secretStore);
        _database = new IpDatabase(paths.DatabasePath);
    }

    public string DataFolder => _paths.DataFolder;

    public UserSettings Settings => _settings;

    public bool HasToken => _tokenStore.HasToken;

    public DatasetInfo? Dataset => _dataset;

    public string ExportFolder => ExportFolders.Resolve(Settings, _paths.DefaultExportFolder);

    public async Task<StartupResult> InitializeAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => Initialize(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(InitializeAsync), ex);
            ReleaseLock();
            return new StartupResult(StartupFailure.StorageUnavailable, null, HasToken);
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => CheckForUpdateCoreAsync(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(CheckForUpdateAsync), ex);
            return new UpdateCheckResult(UpdateCheckOutcome.ServiceError);
        }
    }

    public async Task<UpdateResult> UpdateDatasetAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        // One update at a time. A second caller waits, then normally finds the dataset already current.
        await _updateGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => RunUpdateAsync(progress, ct));
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public Task<SearchResult> SearchAsync(string input, int displayLimit, CancellationToken ct)
    {
        return Task.Run(() => Search(input, displayLimit, ct));
    }

    public Task<OverlapResult> LookUpOverlapsAsync(IReadOnlyList<CidrBlock> blocks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return Task.Run(() => LookUpOverlaps(blocks, ct));
    }

    public Task<ExportPlan> PrepareExportAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => PrepareExport(query, ct));
    }

    public Task<ExportResult> ExportAsync(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(includedLargeNetworks);
        return Task.Run(() => Export(query, includedLargeNetworks, ct));
    }

    public Task<string> GetResultsTextAsync(SearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() => ResultsText(query, ct));
    }

    public Task<bool> SaveTokenAsync(string token)
    {
        return Task.Run(() =>
        {
            try
            {
                return _tokenStore.Save((token ?? "").Trim());
            }
            catch (Exception ex)
            {
                TraceUnexpected(nameof(SaveTokenAsync), ex);
                return false;
            }
        });
    }

    public Task<bool> ClearTokenAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                return _tokenStore.Clear();
            }
            catch (Exception ex)
            {
                TraceUnexpected(nameof(ClearTokenAsync), ex);
                return false;
            }
        });
    }

    public Task<bool> SaveSettingsAsync(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Task.Run(() =>
        {
            try
            {
                UserSettings normalised = settings with
                {
                    ExportFolder = settings.ExportFolder ?? "",
                    IPv4MinPrefix = Math.Clamp(settings.IPv4MinPrefix, 1, 32),
                    IPv6MinPrefix = Math.Clamp(settings.IPv6MinPrefix, 1, 128),
                };

                if (!_settingsStore.Save(normalised))
                    return false;

                _settings = normalised;
                return true;
            }
            catch (Exception ex)
            {
                TraceUnexpected(nameof(SaveSettingsAsync), ex);
                return false;
            }
        });
    }

    public void Dispose()
    {
        ReleaseLock();
    }

    // ----- Startup -----

    private StartupResult Initialize(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_isPrivileged())
            return new StartupResult(StartupFailure.Privileged, null, HasToken);

        // The lock file lives in the data folder, so the folder must exist before the lock is taken.
        try
        {
            Directory.CreateDirectory(_paths.DataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The data folder could not be prepared: {ex.Message}");
            return new StartupResult(StartupFailure.StorageUnavailable, null, HasToken);
        }

        InstanceLock? instanceLock;
        try
        {
            instanceLock = InstanceLock.TryAcquire(_paths.LockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The instance lock could not be opened: {ex.Message}");
            return new StartupResult(StartupFailure.StorageUnavailable, null, HasToken);
        }

        if (instanceLock is null)
            return new StartupResult(StartupFailure.AlreadyRunning, null, HasToken);

        _lock = instanceLock;

        _settings = _settingsStore.Load();

        // Leftovers from an interrupted update. The live database is never touched here.
        DeleteDatabaseFilesQuietly(_paths.NextDatabasePath);
        DeleteFileQuietly(_paths.DownloadPath);

        // The token file of earlier builds. Anyone with the source could decrypt it, and nothing reads it now.
        DeleteFileQuietly(_paths.LegacyTokenPath);
        DeleteFileQuietly(_paths.LegacyTokenPath + ".tmp");

        switch (_database.GetStatus())
        {
            case DatabaseStatus.Newer:
                _dataset = null;
                return new StartupResult(StartupFailure.DatabaseNewer, null, HasToken);
            case DatabaseStatus.Usable:
                _dataset = _database.ReadInfo();
                break;
            default:
                _dataset = null;
                break;
        }

        return new StartupResult(StartupFailure.None, _dataset, HasToken);
    }

    // ----- Update check -----

    private async Task<UpdateCheckResult> CheckForUpdateCoreAsync(CancellationToken ct)
    {
        string? token = LoadToken();
        if (token is null)
            return new UpdateCheckResult(UpdateCheckOutcome.NoToken);

        ChecksumFetch fetch = await _client.FetchChecksumAsync(token, ct);
        if (fetch.Sha256 is null)
            return new UpdateCheckResult(fetch.Outcome, fetch.RetryAfter);

        DatasetInfo? current = _dataset;
        bool checksumMatches = current is not null && string.Equals(current.Checksum, fetch.Sha256, StringComparison.OrdinalIgnoreCase);
        // IPv6 is wanted but the dataset was built without it: a rebuild is the only way to add it
        bool missingIPv6 = _settings.IPv6Enabled && current is { IncludesIPv6: false };
        if (checksumMatches && !missingIPv6)
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);

        // Reaching here with a matching checksum means the only reason left is the missing IPv6 networks
        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, null, AddsIPv6: checksumMatches);
    }

    // ----- Update -----

    private async Task<UpdateResult> RunUpdateAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await UpdateStepsAsync(progress, ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            UpdateFailure failure = ex switch
            {
                InvalidDataException => UpdateFailure.CorruptData,
                IOException or UnauthorizedAccessException or SqliteException => UpdateFailure.StorageError,
                _ => UpdateFailure.ServiceError,
            };
            TraceUnexpected(nameof(UpdateDatasetAsync), ex);
            return new UpdateResult(failure, false, _dataset);
        }
        finally
        {
            // Failure, cancellation or success: the build file and the download are disposable.
            // Connections are unpooled, so the builder's connection is already closed and holds no lock on the file.
            DeleteDatabaseFilesQuietly(_paths.NextDatabasePath);
            DeleteFileQuietly(_paths.DownloadPath);
        }
    }

    private async Task<UpdateResult> UpdateStepsAsync(IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new UpdateProgress(UpdatePhase.Checking, 0, 0));

        string? token = LoadToken();
        if (token is null)
            return new UpdateResult(UpdateFailure.NoToken, false, _dataset);

        ChecksumFetch fetch = await _client.FetchChecksumAsync(token, ct);
        if (fetch.Sha256 is null)
            return new UpdateResult(MapCheckFailure(fetch.Outcome), false, _dataset, fetch.RetryAfter);

        string remoteHash = fetch.Sha256;
        // Read once, so the check and the build below agree even if Settings are saved meanwhile
        bool includeIPv6 = _settings.IPv6Enabled;
        DatasetInfo? current = _dataset;
        bool ipv6Missing = includeIPv6 && current is { IncludesIPv6: false };
        if (current is not null && !ipv6Missing && string.Equals(current.Checksum, remoteHash, StringComparison.OrdinalIgnoreCase))
            return new UpdateResult(UpdateFailure.None, true, current);

        DownloadOutcome download = await _client.DownloadAsync(token, _paths.DownloadPath, progress, ct);
        if (download.Failure != UpdateFailure.None)
            return new UpdateResult(download.Failure, false, _dataset, download.RetryAfter);

        if (!string.Equals(download.Sha256, remoteHash, StringComparison.OrdinalIgnoreCase))
        {
            DeleteFileQuietly(_paths.DownloadPath);
            return new UpdateResult(UpdateFailure.ChecksumMismatch, false, _dataset);
        }

        ct.ThrowIfCancellationRequested();

        DateTimeOffset nowUtc = _time.GetUtcNow();
        using (IpDatabase.Builder builder = IpDatabase.CreateBuilder(_paths.NextDatabasePath, _options.BatchSize, includeIPv6))
        {
            await DatasetImporter.ImportAsync(_paths.DownloadPath, builder, progress, ct);

            progress?.Report(new UpdateProgress(UpdatePhase.Indexing, 0, 0));
            ct.ThrowIfCancellationRequested();

            builder.Complete(remoteHash, DateOnly.FromDateTime(nowUtc.UtcDateTime), nowUtc);
        }

        progress?.Report(new UpdateProgress(UpdatePhase.Finishing, 0, 0));
        ct.ThrowIfCancellationRequested();

        _database.Promote(_paths.NextDatabasePath);
        DeleteFileQuietly(_paths.DownloadPath);
        _dataset = _database.ReadInfo();

        return new UpdateResult(UpdateFailure.None, false, _dataset);
    }

    private static UpdateFailure MapCheckFailure(UpdateCheckOutcome outcome) => outcome switch
    {
        UpdateCheckOutcome.TokenRejected => UpdateFailure.TokenRejected,
        UpdateCheckOutcome.RateLimited => UpdateFailure.RateLimited,
        UpdateCheckOutcome.Offline => UpdateFailure.Offline,
        _ => UpdateFailure.ServiceError,
    };

    // ----- Search and export -----

    private OverlapResult LookUpOverlaps(IReadOnlyList<CidrBlock> blocks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_dataset is null)
            return new OverlapResult(OverlapOutcome.NoDataset, [], null);

        try
        {
            List<NetworkOverlap> overlaps = _database.QueryOverlaps(blocks, _settings.IPv6Enabled);
            ct.ThrowIfCancellationRequested();
            return new OverlapResult(OverlapOutcome.Found, overlaps, _dataset);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(LookUpOverlapsAsync), ex);
            return new OverlapResult(OverlapOutcome.Failed, [], null);
        }
    }

    private SearchResult Search(string input, int displayLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!SearchQuery.TryParse(input, out SearchQuery? query))
            return new SearchResult(SearchOutcome.InvalidInput, null, [], 0);

        if (_dataset is null)
            return new SearchResult(SearchOutcome.NoDataset, query, [], 0);

        UserSettings settings = _settings;
        if (IsIPv6Disabled(query, settings))
            return new SearchResult(SearchOutcome.IPv6Disabled, query, [], 0);

        try
        {
            List<NetworkRecord> rows;
            int total;
            if (query.Kind == SearchKind.Asn)
            {
                rows = _database.QueryAsn(query.Asn, displayLimit, settings.IPv6Enabled);
                total = _database.CountAsn(query.Asn, settings.IPv6Enabled);
            }
            else
            {
                rows = _database.QueryIp(query.IpBytes!, displayLimit);
                total = rows.Count;
            }

            ct.ThrowIfCancellationRequested();

            List<NetworkRecord> marked = rows
                .Select(row => row with { IsLargeRange = LargeRangePolicy.IsLarge(row.Network, settings) })
                .ToList();

            return marked.Count == 0
                ? new SearchResult(SearchOutcome.NoResults, query, [], 0)
                : new SearchResult(SearchOutcome.Found, query, marked, total);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(SearchAsync), ex);
            return new SearchResult(SearchOutcome.Failed, query, [], 0);
        }
    }

    private ExportPlan PrepareExport(SearchQuery query, CancellationToken ct)
    {
        if (query.Kind != SearchKind.Asn)
            return new ExportPlan(0, "", []);

        try
        {
            ct.ThrowIfCancellationRequested();
            List<NetworkRecord> rows = _database.QueryAsn(query.Asn, null, _settings.IPv6Enabled);
            ct.ThrowIfCancellationRequested();

            return new ExportPlan(rows.Count, AsNameOf(rows), LargeRangePolicy.Find(rows, _settings));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(PrepareExportAsync), ex);
            return new ExportPlan(0, "", []);
        }
    }

    private ExportResult Export(SearchQuery query, IReadOnlyCollection<string> includedLargeNetworks, CancellationToken ct)
    {
        if (query.Kind != SearchKind.Asn)
            return new ExportResult(ExportFailure.NothingToExport, [], 0, 0);

        try
        {
            ct.ThrowIfCancellationRequested();
            UserSettings settings = _settings;
            List<NetworkRecord> rows = _database.QueryAsn(query.Asn, null, settings.IPv6Enabled);
            ct.ThrowIfCancellationRequested();

            var included = new HashSet<string>(includedLargeNetworks, StringComparer.Ordinal);
            var leftOut = new HashSet<string>(StringComparer.Ordinal);
            var toWrite = new List<NetworkRecord>(rows.Count);
            foreach (NetworkRecord row in rows)
            {
                bool large = LargeRangePolicy.IsLarge(row.Network, settings);
                if (large && !included.Contains(row.Network))
                {
                    leftOut.Add(row.Network);
                    continue;
                }
                toWrite.Add(row);
            }

            if (toWrite.Count == 0)
                return new ExportResult(ExportFailure.NothingToExport, [], 0, leftOut.Count);

            string folder = ExportFolder;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Trace.TraceError($"The export folder could not be created: {ex.Message}");
                return new ExportResult(ExportFailure.FolderUnavailable, [], 0, leftOut.Count);
            }

            string asName = AsNameOf(rows);
            DateTimeOffset nowUtc = _time.GetUtcNow();
            var files = new List<string>();
            try
            {
                files.Add(ExportWriter.WriteAsnReport(folder, query.Text, asName, toWrite, nowUtc));
                files.AddRange(ExportWriter.WriteFortigateFeeds(folder, query.Text, asName, toWrite, settings, nowUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"The export could not be written: {ex.Message}");
                return new ExportResult(ExportFailure.WriteFailed, [], 0, leftOut.Count);
            }

            return new ExportResult(ExportFailure.None, files, toWrite.Count, leftOut.Count);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(ExportAsync), ex);
            return new ExportResult(ExportFailure.WriteFailed, [], 0, 0);
        }
    }

    private string ResultsText(SearchQuery query, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            UserSettings settings = _settings;
            List<NetworkRecord> rows = query.Kind == SearchKind.Asn
                ? _database.QueryAsn(query.Asn, null, settings.IPv6Enabled)
                : IsIPv6Disabled(query, settings) ? [] : _database.QueryIp(query.IpBytes!, int.MaxValue);
            ct.ThrowIfCancellationRequested();

            return ExportWriter.BuildClipboardText(rows);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(GetResultsTextAsync), ex);
            return "";
        }
    }

    // ----- Helpers -----

    private string? LoadToken()
    {
        string? token = _tokenStore.TryLoad();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string AsNameOf(IReadOnlyList<NetworkRecord> rows)
    {
        string? name = rows.Count > 0 ? rows[0].AsName : null;
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    /// <summary>An IPv6 address search while IPv6 is off. It is answered without querying the database.</summary>
    private static bool IsIPv6Disabled(SearchQuery query, UserSettings settings) =>
        !settings.IPv6Enabled && query.Kind == SearchKind.IpAddress && query.IpBytes?.Length == IPv6AddressLength;

    private const int IPv6AddressLength = 16;

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private static bool IsPrivilegedProcess()
    {
        try
        {
            return Environment.IsPrivilegedProcess;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private void ReleaseLock()
    {
        InstanceLock? held = _lock;
        _lock = null;
        held?.Dispose();
    }

    private static void TraceUnexpected(string operation, Exception ex) =>
        Trace.TraceError($"{operation} failed unexpectedly ({ex.GetType().FullName}): {ex.Message}");

    private static void DeleteDatabaseFilesQuietly(string databasePath)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
            DeleteFileQuietly(databasePath + suffix);
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"A temporary file could not be deleted: {ex.Message}");
        }
    }
}
