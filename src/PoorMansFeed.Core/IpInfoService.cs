using System.Diagnostics;
using Microsoft.Data.Sqlite;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.FeedExceptions;
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
    private readonly TokenStore _tokenStore;
    private readonly ConfigKeyStore _configKeys;
    private readonly IpDatabase _database;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    // Two saves could commit in one order and swap the fields in the other, leaving in use what config.db doesn't hold
    private readonly Lock _saveGate = new();
    // Start-up, Start fresh and Start without the old exceptions run one at a time
    private readonly Lock _startGate = new();
    private volatile UserSettings _settings = new();
    // Unreadable until a launch has opened config.db, so no feed can be written before then
    private volatile ExceptionList _exceptions = ExceptionList.Unreadable;
    private volatile DatasetInfo? _dataset;
    private volatile ConfigStore? _config;
    private ConfigOpenState _lastConfigState;
    private StartupResult? _lastStartup;
    private InstanceLock? _lock;

    /// <summary>The service the app uses: the token is kept in the operating system's store for the current user.</summary>
    public static IpInfoService ForCurrentUser(AppPaths paths, HttpClient http, CoreOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new IpInfoService(paths, http, options, time, SecretStores.ForCurrentUser(paths.TokenPath),
                                 SecretStores.ConfigKeyForCurrentUser(paths));
    }

    // Tests pass in-memory stores here, so they never reach the real Keychain items, which are one per user account
    internal IpInfoService(AppPaths paths, HttpClient http, CoreOptions options, TimeProvider time, ISecretStore secretStore,
                           ISecretStore configKeyStore, Func<bool>? isPrivileged = null,
                           Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(configKeyStore);

        _paths = paths;
        _options = options;
        _time = time;
        _isPrivileged = isPrivileged ?? new Func<bool>(IsPrivilegedProcess);
        _client = new IpInfoClient(http, options, delay);
        _tokenStore = new TokenStore(secretStore);
        _configKeys = new ConfigKeyStore(configKeyStore);
        _database = new IpDatabase(paths.DatabasePath);
        AbuseAccounting = new ConfigAccounting(this);
    }

    /// <summary>The AbuseIPDB download accounting in config.db. Before a launch succeeds, Current is Empty and every write fails.</summary>
    internal IAbuseAccounting AbuseAccounting { get; }

    /// <summary>The open config store, for tests that make a save fail through its BeforeCommit.</summary>
    internal ConfigStore? Config => _config;

    public string DataFolder => _paths.DataFolder;

    public UserSettings Settings => _settings;

    public bool HasToken => _tokenStore.HasToken;

    public DatasetInfo? Dataset => _dataset;

    public ExceptionList Exceptions => _exceptions;

    public string ExportFolder => ExportFolders.Resolve(Settings, _paths.DefaultExportFolder);

    public async Task<StartupResult> InitializeAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => Initialize(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return StartupFailed(nameof(InitializeAsync), ex);
        }
    }

    public async Task<StartupResult> StartFreshAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => StartFresh(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return StartupFailed(nameof(StartFreshAsync), ex);
        }
    }

    public async Task<StartupResult> StartWithoutOldExceptionsAsync(CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => StartWithoutOldExceptions(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return StartupFailed(nameof(StartWithoutOldExceptionsAsync), ex);
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
            lock (_saveGate)
            {
                try
                {
                    UserSettings normalised = SettingsStore.Normalise(settings);

                    ConfigStore? config = _config;
                    if (config is null || !config.SaveSettings(normalised))
                        return false;

                    _settings = normalised;
                    return true;
                }
                catch (Exception ex)
                {
                    TraceUnexpected(nameof(SaveSettingsAsync), ex);
                    return false;
                }
            }
        });
    }

    public Task<ExceptionsSaveOutcome> SaveExceptionsAsync(IReadOnlyList<ExceptionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Task.Run(() =>
        {
            lock (_saveGate)
            {
                try
                {
                    ExceptionList list = ExceptionList.Create(entries);

                    // Before a launch has opened config.db there is nowhere to save to
                    ConfigStore? config = _config;
                    if (config is null || !config.SaveExceptions(list))
                        return ExceptionsSaveOutcome.Failed;

                    _exceptions = list;
                    return ExceptionsSaveOutcome.Saved;
                }
                catch (Exception ex)
                {
                    TraceUnexpected(nameof(SaveExceptionsAsync), ex);
                    return ExceptionsSaveOutcome.Failed;
                }
            }
        });
    }

    public void Dispose()
    {
        // After any start-up step still running, so the instance lock never goes while config.db is being opened.
        // config.db's lock goes first, then the instance lock.
        lock (_startGate)
        {
            CloseConfig();
            ReleaseLock();
        }
    }

    // ----- Startup -----

    private StartupResult Initialize(CancellationToken ct)
    {
        lock (_startGate)
        {
            ct.ThrowIfCancellationRequested();

            // This instance has already launched (or stopped on config.db): the answer is the same, and nothing runs again
            if (_lock is not null)
                return _lastStartup!;

            StartupFailure preflight = Preflight();
            if (preflight != StartupFailure.None)
                return Remember(new StartupResult(preflight, null, HasToken));

            // config.db is opened before anything else is touched, so a launch that stops on it changes nothing more
            StartupResult? stopped = OpenConfig(ConfigStore.TryOpen(_paths, _configKeys, _time, withoutOldExceptions: false,
                                                                    out ConfigStore? store), store);
            return stopped ?? FinishStartup();
        }
    }

    private StartupResult StartFresh(CancellationToken ct)
    {
        lock (_startGate)
        {
            // Observed once, before acting: the steps below are never cut short
            ct.ThrowIfCancellationRequested();

            if (_lock is null || _config is not null
                || _lastConfigState is not (ConfigOpenState.Newer or ConfigOpenState.Unreadable or ConfigOpenState.KeyMismatch
                                            or ConfigOpenState.Missing))
                return NothingToStart();

            // config.db is opened again first, so whatever comes back, Opened or another failure, is the state from here
            StartupResult? stopped = OpenConfig(ConfigStore.StartFresh(_paths, _configKeys, _time, _lastConfigState,
                                                                       out ConfigStore? store), store);
            return stopped ?? FinishStartup();
        }
    }

    private StartupResult StartWithoutOldExceptions(CancellationToken ct)
    {
        lock (_startGate)
        {
            // Observed once, before acting: the steps below are never cut short
            ct.ThrowIfCancellationRequested();

            if (_lock is null || _config is not null || _lastConfigState != ConfigOpenState.ExceptionsImportUnreadable)
                return NothingToStart();

            StartupResult? stopped = OpenConfig(ConfigStore.TryOpen(_paths, _configKeys, _time, withoutOldExceptions: true,
                                                                    out ConfigStore? store), store);
            return stopped ?? FinishStartup();
        }
    }

    /// <summary>What a start-up step in the wrong state answers: the last result, and nothing is changed.</summary>
    private StartupResult NothingToStart() =>
        _lastStartup ?? new StartupResult(StartupFailure.StorageUnavailable, null, HasToken);

    /// <summary>Privileged, then the data folder, then the instance lock, which is held from here on.</summary>
    private StartupFailure Preflight()
    {
        if (_isPrivileged())
            return StartupFailure.Privileged;

        // The lock file lives in the data folder, so the folder must exist before the lock is taken.
        try
        {
            Directory.CreateDirectory(_paths.DataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The data folder could not be prepared: {ex.Message}");
            return StartupFailure.StorageUnavailable;
        }

        InstanceLock? instanceLock;
        try
        {
            instanceLock = InstanceLock.TryAcquire(_paths.LockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The instance lock could not be opened: {ex.Message}");
            return StartupFailure.StorageUnavailable;
        }

        if (instanceLock is null)
            return StartupFailure.AlreadyRunning;

        _lock = instanceLock;
        return StartupFailure.None;
    }

    /// <summary>
    /// Takes what opening config.db gave. Opened puts the store and its values in use and gives null, so start-up goes
    /// on; any other state gives its failure, with the instance lock still held, no store, the default settings and an
    /// unreadable exceptions list.
    /// </summary>
    private StartupResult? OpenConfig(ConfigOpenState state, ConfigStore? store)
    {
        _lastConfigState = state;
        if (state != ConfigOpenState.Opened || store is null)
        {
            store?.Dispose();
            return Remember(new StartupResult(StartupFailureOf(state), null, HasToken));
        }

        lock (_saveGate)
        {
            ConfigSnapshot snapshot = store.Snapshot;
            _settings = snapshot.Settings;
            _exceptions = snapshot.Exceptions;
            _config = store;
        }

        return null;
    }

    /// <summary>The start-up failure for a config.db that didn't open. Opened has none, so it throws.</summary>
    internal static StartupFailure StartupFailureOf(ConfigOpenState state) => state switch
    {
        ConfigOpenState.Newer => StartupFailure.ConfigNewer,
        ConfigOpenState.Unreadable => StartupFailure.ConfigUnreadable,
        ConfigOpenState.KeyMismatch => StartupFailure.ConfigKeyMismatch,
        ConfigOpenState.Locked => StartupFailure.ConfigLocked,
        ConfigOpenState.StorageError => StartupFailure.ConfigStorageError,
        ConfigOpenState.KeyUnavailable => StartupFailure.ConfigKeyUnavailable,
        ConfigOpenState.NoKeyStore => StartupFailure.ConfigKeyStoreMissing,
        ConfigOpenState.ExceptionsImportUnreadable => StartupFailure.ExceptionsImportUnreadable,
        ConfigOpenState.Missing => StartupFailure.ConfigMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Opened has no start-up failure."),
    };

    /// <summary>The rest of start-up, once config.db is open: the leftovers, the legacy token and the database.</summary>
    private StartupResult FinishStartup()
    {
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
                return Remember(new StartupResult(StartupFailure.DatabaseNewer, null, HasToken));
            case DatabaseStatus.Usable:
                _dataset = _database.ReadInfo();
                break;
            default:
                _dataset = null;
                break;
        }

        return Remember(new StartupResult(StartupFailure.None, _dataset, HasToken));
    }

    private StartupResult Remember(StartupResult result)
    {
        _lastStartup = result;
        return result;
    }

    /// <summary>The catch-all of the three start-up methods: nothing stays open or in use, and the instance lock goes.</summary>
    private StartupResult StartupFailed(string operation, Exception ex)
    {
        TraceUnexpected(operation, ex);
        lock (_startGate)
        {
            CloseConfig();
            _exceptions = ExceptionList.Unreadable;
            ReleaseLock();
            return Remember(new StartupResult(StartupFailure.StorageUnavailable, null, HasToken));
        }
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
            List<NetworkOverlap> overlaps = _database.QueryOverlaps(blocks, _settings.IPv6Enabled, out DatasetInfo? answeredBy);
            ct.ThrowIfCancellationRequested();
            // The label comes from the connection that answered: _dataset is set again only after Promote has released
            // the database, so a query on the new file can run while it still names the old one
            return new OverlapResult(OverlapOutcome.Found, overlaps, answeredBy ?? _dataset);
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

            // Exceptions change only the feeds, but a feed must never be written as if there were none
            ExceptionList exceptions = _exceptions;
            if (settings.FortigateEnabled && !exceptions.IsReadable)
                return new ExportResult(ExportFailure.ExceptionsUnreadable, [], 0, 0);

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
            FortigateFeedWrite feeds;
            try
            {
                files.Add(ExportWriter.WriteAsnReport(folder, query.Text, asName, toWrite, nowUtc));
                feeds = ExportWriter.WriteFortigateFeeds(folder, query.Text, asName, toWrite, settings, nowUtc, exceptions);
                files.AddRange(feeds.Files);
            }
            catch (ExportFileException ex)
            {
                Trace.TraceError($"The export could not be written: {ex.Message}");
                // The report failed when nothing was written before the feeds, so no feed was tried: every earlier feed
                // this export would have written or noted is still there, and FortiGate keeps reading it
                IReadOnlyList<string> oldFilesLeft = files.Count == 0
                    ? [.. ex.OldFilesLeft, .. ExportWriter.ExistingFeedFiles(folder, query.Text, toWrite, settings)]
                    : ex.OldFilesLeft;
                files.AddRange(ex.WrittenBefore);
                return new ExportResult(ExportFailure.WriteFailed, files, 0, leftOut.Count,
                                        OldFilesLeft: oldFilesLeft.Count > 0 ? oldFilesLeft : null, FailedFile: ex.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError($"The export could not be written: {ex.Message}");
                return new ExportResult(ExportFailure.WriteFailed, [], 0, leftOut.Count);
            }

            return new ExportResult(ExportFailure.None, files, toWrite.Count, leftOut.Count, feeds.ExceptionCuts, feeds.ExceptionRemovals,
                                    feeds.OldFilesLeft);
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

    private void CloseConfig()
    {
        lock (_saveGate)
        {
            ConfigStore? held = _config;
            _config = null;
            held?.Dispose();
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

    /// <summary>
    /// The download accounting over whichever config store is open. It is made with the service, before start-up, so it
    /// never throws for a missing store: it reads Empty, is not available, and every write fails until a launch has
    /// opened config.db.
    /// </summary>
    private sealed class ConfigAccounting(IpInfoService owner) : IAbuseAccounting
    {
        public PoorMansFeed.Core.AbuseIpDb.AbuseAccounting Current =>
            owner._config?.Current ?? PoorMansFeed.Core.AbuseIpDb.AbuseAccounting.Empty;

        public bool IsAvailable => owner._config?.IsAvailable ?? false;

        public bool TryRecordAnswer(AbuseRateLimit rateLimit, DateTimeOffset answeredAt, bool countDownload)
        {
            ArgumentNullException.ThrowIfNull(rateLimit);
            return owner._config?.TryRecordAnswer(rateLimit, answeredAt, countDownload) ?? false;
        }
    }
}
