using System.Diagnostics;
using System.Globalization;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// Only a public address is ever sent, and the key is read only when a request will be made: for a download, only
/// after every local refusal has passed and the pre-send write has succeeded. Every failure is a result value.
/// </summary>
public sealed class AbuseIpDbService : IAbuseIpDbService
{
    private const int MinKeyLength = 16;
    private const int MaxKeyLength = 256;

    private readonly IIpInfoService _ipinfo;
    private readonly TimeProvider _time;
    private readonly TokenStore _keys;
    private readonly AbuseIpDbClient _client;
    private readonly AbuseIpDbList _list;
    private readonly string _listPath;
    private readonly string _dataFolder;
    private readonly IAbuseAccounting _accounting;
    private readonly Func<string, long?> _freeBytes;
    private readonly string _defaultExportFolder;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    // Set once a write of the accounting to config.db has failed, with every answer of the session applied, so it
    // refuses at least as soon as the store would. Written only under the download gate; the status reads it unguarded
    private volatile AbuseAccounting? _unrecorded;

    /// <summary>The service for the current user, with the key in the operating system's store and its own HTTP client.</summary>
    public static AbuseIpDbService ForCurrentUser(AppPaths paths, IpInfoService ipinfo, CoreOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ipinfo);
        // Its own client, because .NET forwards a custom header such as Key when it follows a redirect to another host.
        // The timeout is infinite because the client applies its own per-request limit.
        var http = new HttpClient(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        return new AbuseIpDbService(http, ipinfo, paths, options, time, SecretStores.AbuseIpDbKeyForCurrentUser(paths),
                                    ipinfo.AbuseAccounting);
    }

    /// <summary>The handler under the service's own client: automatic redirects are off, so the key goes only where it was sent.</summary>
    internal static SocketsHttpHandler CreateHandler() =>
        new() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <param name="freeBytes">
    /// The free bytes on a folder's disk, or null when they can't be measured, which skips the free-space check. Null
    /// measures the real disk.
    /// </param>
    internal AbuseIpDbService(HttpClient http, IIpInfoService ipinfo, AppPaths paths, CoreOptions options, TimeProvider time,
                              ISecretStore keyStore, IAbuseAccounting accounting, Func<string, long?>? freeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(ipinfo);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(keyStore);
        ArgumentNullException.ThrowIfNull(accounting);
        _ipinfo = ipinfo;
        _time = time;
        _keys = new TokenStore(keyStore, "AbuseIPDB key");
        _client = new AbuseIpDbClient(http, options, time);
        _list = new AbuseIpDbList(paths.AbuseIpDbDatabasePath);
        _listPath = paths.AbuseIpDbDatabasePath;
        _dataFolder = paths.DataFolder;
        _accounting = accounting;
        _freeBytes = freeBytes ?? FreeBytesOn;
        _defaultExportFolder = paths.DefaultExportFolder;
    }

    /// <inheritdoc/>
    public bool HasKey => _keys.HasToken;

    /// <inheritdoc/>
    public Task<AbuseKeySave> SaveKeyAsync(string key)
    {
        return Task.Run(() =>
        {
            try
            {
                string trimmed = (key ?? "").Trim();
                if (!IsValidKey(trimmed))
                    return AbuseKeySave.NotAKey;

                return _keys.Save(trimmed) ? AbuseKeySave.Saved : AbuseKeySave.Failed;
            }
            catch (Exception ex)
            {
                TraceUnexpected(nameof(SaveKeyAsync), ex);
                return AbuseKeySave.Failed;
            }
        });
    }

    /// <inheritdoc/>
    public Task<bool> ClearKeyAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                return _keys.Clear();
            }
            catch (Exception ex)
            {
                TraceUnexpected(nameof(ClearKeyAsync), ex);
                return false;
            }
        });
    }

    /// <inheritdoc/>
    public async Task<AbuseCheckResult> CheckAsync(SearchQuery query, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => CheckCoreAsync(query, ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(CheckAsync), ex);
            return Failure(AbuseIpDbFailure.ServiceError);
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseListStatus> GetStatusAsync(CancellationToken ct)
    {
        // Not behind the download gate: the list serialises its own calls
        try
        {
            return await Task.Run(() => StatusCore(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(GetStatusAsync), ex);
            return AccountingStatus(AbuseListState.StorageError, EffectiveAccounting(), _time.GetUtcNow());
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct)
    {
        // Not behind the download gate: the list serialises its own calls
        try
        {
            return await Task.Run(() => NewAddressesCore(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(GetNewAddressesAsync), ex);
            return NewAddressesFailure(AbuseNewAddressesFailure.StorageError);
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseDownloadResult> DownloadBlacklistAsync(CancellationToken ct)
    {
        // One download at a time, so the second sees the answer the first stored before it decides to send
        await _downloadGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => DownloadCoreAsync(ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(DownloadBlacklistAsync), ex);
            return DownloadFailure(AbuseIpDbFailure.ServiceError, null);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return _list.Delete() ? AbuseIpDbFailure.None : AbuseIpDbFailure.StorageError;
            });
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(ClearListAsync), ex);
            return AbuseIpDbFailure.StorageError;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct)
    {
        // Behind the download gate, so a write pressed during a download writes the list it merged, and two writes
        // never share a .tmp file
        await _downloadGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return WriteFeedCore();
            });
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(WriteFeedAsync), ex);
            return FeedFailure(AbuseFeedWriteFailure.WriteFailed);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct)
    {
        // Behind the download gate, as a write is: the same files and the same .tmp names
        await _downloadGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return EmptyFeedCore();
            });
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(EmptyFeedAsync), ex);
            return FeedFailure(AbuseFeedWriteFailure.WriteFailed);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct)
    {
        // Not behind the download gate: the list serialises its own calls, so the read is the list before or after a
        // merge, and a lookup of many seconds never holds up a download, a clear or a feed write
        try
        {
            return await Task.Run(() => NetworksCoreAsync(progress, ct));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            TraceUnexpected(nameof(FindOffendingNetworksAsync), ex);
            return NetworksFailure(AbuseNetworksFailure.LookupFailed, false, 0, null);
        }
    }

    /// <summary>16 to 256 characters, each a printable ASCII character other than the space ('!' to '~').</summary>
    internal static bool IsValidKey(string key)
    {
        if (key is null || key.Length is < MinKeyLength or > MaxKeyLength)
            return false;

        foreach (char character in key)
        {
            if (character is < '!' or > '~')
                return false;
        }

        return true;
    }

    private async Task<AbuseCheckResult> CheckCoreAsync(SearchQuery? query, CancellationToken ct)
    {
        // The address is judged before the key is read, so nothing private ever costs a keychain prompt or a request
        if (query is not { Kind: SearchKind.IpAddress, IpBytes: { } bytes }
            || !PublicAddress.TryCreate(bytes, out PublicAddress? address))
        {
            return Failure(AbuseIpDbFailure.NotPublic);
        }

        ct.ThrowIfCancellationRequested();

        AbuseIpDbFailure keyFailure = TryLoadKey(out string? key);
        if (keyFailure != AbuseIpDbFailure.None)
            return Failure(keyFailure);

        AbuseFetch fetch = await _client.CheckAsync(address, key!, ct);
        if (fetch.Failure != AbuseIpDbFailure.None)
            return new AbuseCheckResult(fetch.Failure, null, fetch.RetryAt);

        AbuseCheck? check = fetch.Body is { } body ? AbuseIpDbParser.TryParseCheck(body, address) : null;
        return check is null ? Failure(AbuseIpDbFailure.BadResponse) : new AbuseCheckResult(AbuseIpDbFailure.None, check, null);
    }

    private async Task<AbuseDownloadResult> DownloadCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _time.GetUtcNow();
        // One read of the settings, so the keep days and the IPv6 switch the request is sent with come from the same snapshot
        UserSettings settings = _ipinfo.Settings;
        TimeSpan keep = TimeSpan.FromDays(KeepDayCount(settings));

        // A list this version can't use is reported before anything is sent or the key is read
        ListRead<ListFacts> facts = _list.ReadFacts(now, keep);
        if (ListFailure(facts.State) is AbuseIpDbFailure unusable)
            return DownloadFailure(unusable, null);

        // A clock more than a day behind the last download would stamp this download before the last one, so nothing
        // is written, read or sent, and nothing is counted
        if (IsClockBehind(facts.Value?.LastDownloadAt, now))
        {
            Trace.TraceWarning("The computer's clock is behind the AbuseIPDB list's last download, so the blacklist was not downloaded.");
            return DownloadFailure(AbuseIpDbFailure.ClockBehind, null);
        }

        // The accounting is unknown while no config store is open, so nothing may be sent: a refusal it holds can't be
        // seen, and the answer couldn't be counted
        if (!_accounting.IsAvailable)
            return DownloadFailure(AbuseIpDbFailure.StorageError, null);

        if (RefusedUntil(EffectiveAccounting(), now) is DateTimeOffset until)
            return DownloadFailure(AbuseIpDbFailure.LimitReached, until);

        // Before the pre-send write, so a disk too full to take the answer and its merge spends no download and writes nothing
        if (IsDiskNearlyFull())
            return DownloadFailure(AbuseIpDbFailure.DiskNearlyFull, null);

        ct.ThrowIfCancellationRequested();
        // Written before the key is read or anything is sent, because the read alone can't tell a list that can't be written
        AbuseListState attempt = _list.RecordAttempt(now);
        if (attempt != AbuseListState.Usable)
            return DownloadFailure(ListFailure(attempt) ?? AbuseIpDbFailure.StorageError, null);

        AbuseIpDbFailure keyFailure = TryLoadKey(out string? key);
        if (keyFailure != AbuseIpDbFailure.None)
            return DownloadFailure(keyFailure, null);

        // While IPv6 is off the list asks for IPv4 alone, so the whole allowance goes to addresses the feed can use
        AbuseFetch fetch = await _client.GetBlacklistAsync(key!, ipv4Only: !settings.IPv6Enabled, ct);
        DateTimeOffset answeredAt = _time.GetUtcNow();

        // Only a 2xx is a download, and AbuseIPDB has counted it, so every result from here says so whatever stops the
        // merge. A 401's headers are not recorded: a rejected key doesn't identify the account
        bool counted = fetch.RateLimit is not null && fetch.Failure is (AbuseIpDbFailure.None or AbuseIpDbFailure.BadResponse);
        DateTimeOffset? storedRefusal = null;
        if (fetch.RateLimit is { } rateLimit && fetch.Failure != AbuseIpDbFailure.KeyRejected)
        {
            AbuseRateLimit stored = rateLimit;
            // A 429 that leaves nothing refuses until the later of the reset and Retry-After (RefusedBy bounds it).
            // A throttle that leaves downloads keeps the reset as sent, because the facts line reads it as the renewal
            if (fetch.Failure == AbuseIpDbFailure.RateLimited)
            {
                int remaining = stored.Remaining ?? 0;
                DateTimeOffset? retryAt = fetch.RetryAt > answeredAt ? fetch.RetryAt : null;
                DateTimeOffset? resetAt = remaining <= 0 ? Latest(stored.ResetAt, retryAt) : stored.ResetAt ?? fetch.RetryAt;
                stored = stored with { Remaining = remaining, ResetAt = resetAt };
            }
            RecordAnswer(stored, answeredAt, counted);
            storedRefusal = RefusedBy(stored, answeredAt);
        }

        if (fetch.Failure != AbuseIpDbFailure.None)
        {
            // A 429 names the later of the retry time and the stored refusal, so it never names a time before the next
            // press will be sent
            return DownloadFailure(fetch.Failure,
                                   fetch.Failure == AbuseIpDbFailure.RateLimited ? Latest(fetch.RetryAt, storedRefusal) : null,
                                   counted);
        }

        // The list's times come from this computer's clock, so a clock far from AbuseIPDB's would keep or expire the
        // wrong addresses. The download is spent either way, and its answer stays recorded
        if (fetch.ServerTime is DateTimeOffset serverTime
            && (answeredAt - serverTime).Duration() > TimeSpan.FromHours(AbuseIpDbLimits.MaxClockDifferenceHours))
        {
            long hours = (long)(answeredAt - serverTime).Duration().TotalHours;
            Trace.TraceWarning(string.Create(CultureInfo.InvariantCulture,
                $"The computer's clock is {hours} hours from AbuseIPDB's, so the downloaded blacklist was not merged."));
            return DownloadFailure(AbuseIpDbFailure.ClockWrong, null, counted);
        }

        // The download is spent, so the caller's token is no longer used. generatedAt is AbuseIPDB's time, so it is
        // clamped on AbuseIPDB's clock (the guard above accepted any Date left); the list's times stay this computer's
        Blacklist? list = fetch.Body is { } body ? AbuseIpDbParser.TryParseBlacklist(body, fetch.ServerTime ?? answeredAt) : null;
        if (list is null)
            return DownloadFailure(AbuseIpDbFailure.BadResponse, null, counted);

        ListRead<ListMerge> merged = _list.Merge(list, answeredAt, answeredAt - keep, CancellationToken.None);
        if (ListFailure(merged.State) is AbuseIpDbFailure unmerged)
            return DownloadFailure(unmerged, null, counted);

        // A merge that wrote is Usable, and its value is set
        ListMerge merge = merged.Value!;

        int skipped = list.Unreadable + list.NotPublic;
        Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
            $"The AbuseIPDB blacklist was merged: {list.Entries.Count} listed, {skipped} skipped, {list.Duplicates} duplicates, "
            + $"{merge.Added} added, {merge.Lapsed} lapsed, {merge.Expired} expired, new generation {merge.NewGeneration}."));
        return new AbuseDownloadResult(AbuseIpDbFailure.None, merge.NewGeneration, list.Entries.Count, skipped,
                                       merge.Added, merge.Lapsed, merge.Expired, list.GeneratedAt, null, counted);
    }

    private AbuseFeedWriteResult WriteFeedCore()
    {
        // One read of the settings, so the keep days, the IPv6 switch and the folder come from the same snapshot
        UserSettings settings = _ipinfo.Settings;
        // With no type ticked there is nowhere to write, so nothing is read, not even the exceptions or the list
        if (!settings.TicksAnyFeedType())
            return FeedFailure(AbuseFeedWriteFailure.NoFeedType);

        ExceptionList exceptions = _ipinfo.Exceptions;
        // A feed written as if there were no exceptions could block the operator's own networks, so nothing is read
        // or written while they can't be read
        if (!exceptions.IsReadable)
            return FeedFailure(AbuseFeedWriteFailure.ExceptionsUnreadable);

        DateTimeOffset now = _time.GetUtcNow();
        int keepDays = KeepDayCount(settings);
        // The keep days count back from the earlier of now and the last download, so a clock set ahead leaves the
        // feed as the last download left it
        ListRead<ListFeed> read = _list.ReadFeed(now, TimeSpan.FromDays(keepDays), settings.IPv6Enabled);
        if (FeedListFailure(read.State) is AbuseFeedWriteFailure unusable)
            return FeedFailure(unusable);

        // A missing file reads as the empty feed, and every row was judged public when it was read
        ListFeed list = read.Value ?? ListFeed.Empty;

        // A list with no download (no file, a cleared list, only failed attempts) says nothing about what to block, so
        // no file or folder is touched: an existing feed keeps its bytes rather than being emptied
        if (list.LastDownloadAt is null)
            return FeedFailure(AbuseFeedWriteFailure.NoDownload);

        // A clock more than a day behind the last download is refused: the header would say the feed was written before
        // the list it holds was downloaded, and the cutoff would keep addresses for longer than the keep days
        if (IsClockBehind(list.LastDownloadAt, now))
        {
            Trace.TraceWarning("The computer's clock is behind the AbuseIPDB list's last download, so the feed was not written.");
            return FeedFailure(AbuseFeedWriteFailure.ClockWrong);
        }

        AbuseFeedFamily ipv4 = AbuseIpDbFeedWriter.Select(list.Entries, exceptions, ipv6: false);
        AbuseFeedFamily ipv6 = settings.IPv6Enabled
            ? AbuseIpDbFeedWriter.Select(list.Entries, exceptions, ipv6: true)
            : new AbuseFeedFamily(true, [], 0, 0);
        int removed = ipv4.ExceptionRemovals + ipv6.ExceptionRemovals;

        // In each ticked type a family with addresses is written; one with none is written header-only when that type's
        // file exists, so the reader follows the list down to empty, and gets no file when it never had one
        string folder = ExportFolders.Resolve(settings, _defaultExportFolder);
        var header = new AbuseFeedHeader(keepDays, now, list.LastDownloadAt, list.GeneratedAt);
        // Rendered once per family, so the copies can differ only in their first line
        AbuseFeedBody[] bodies = [AbuseFeedBody.Of(ipv4, header), AbuseFeedBody.Of(ipv6, header)];
        var plan = new List<PlannedFeedFile>();
        foreach (FeedType type in FeedTypes.Ticked(settings))
        {
            foreach (AbuseFeedBody body in bodies)
            {
                string path = AbuseIpDbFeedWriter.PathFor(folder, type, body.Family.IsIpv6);
                if (body.Family.Addresses.Count == 0 && !File.Exists(path))
                    continue;
                plan.Add(new PlannedFeedFile(type, path, body));
            }
        }
        if (plan.Count == 0)
            return new AbuseFeedWriteResult(AbuseFeedWriteFailure.NothingToWrite, [], 0, 0, 0, removed, []);

        // Only the export folder is made here; a type's folder is made by its first file, so one that can't be made
        // fails that file, and the FortiGate copies, written first, stay written when the ModSecurity folder fails
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError($"The export folder could not be created: {ex.Message}");
            return new AbuseFeedWriteResult(AbuseFeedWriteFailure.FolderUnavailable, [], 0, 0, ipv4.LeftOut + ipv6.LeftOut,
                                            removed, []);
        }

        // Both families' removals and left-out counts, written or not: a family with no file still had them
        return WritePlan(plan, ipv4.LeftOut + ipv6.LeftOut, removed, "The feed could not be written");
    }

    private AbuseFeedWriteResult EmptyFeedCore()
    {
        // Only the settings are read, so the feed can be retired while the list or the exceptions can't be read.
        // A header-only file holds no address an exception could cover
        UserSettings settings = _ipinfo.Settings;
        DateTimeOffset now = _time.GetUtcNow();
        string folder = ExportFolders.Resolve(settings, _defaultExportFolder);

        // No download line and no keep days: the file no longer holds anything from a download, and says it was
        // emptied by hand
        var header = new AbuseFeedHeader(null, now, null, null);
        // Both types whatever is ticked and both families whatever the IPv6 switch, so any file left is retired.
        // A folder at a feed's path counts, so it fails the write rather than reading as nothing to empty
        AbuseFeedBody[] bodies =
        [
            AbuseFeedBody.Of(new AbuseFeedFamily(false, [], 0, 0), header),
            AbuseFeedBody.Of(new AbuseFeedFamily(true, [], 0, 0), header),
        ];
        var plan = new List<PlannedFeedFile>();
        foreach (FeedType type in FeedTypes.All)
        {
            foreach (AbuseFeedBody body in bodies)
            {
                string path = AbuseIpDbFeedWriter.PathFor(folder, type, body.Family.IsIpv6);
                if (!Path.Exists(path))
                    continue;
                plan.Add(new PlannedFeedFile(type, path, body));
            }
        }
        if (plan.Count == 0)
            return FeedFailure(AbuseFeedWriteFailure.NothingToWrite);

        return WritePlan(plan, 0, 0, "The feed could not be emptied");
    }

    /// <summary>
    /// Writes the planned files in order. A file written before a failure stays written, and the result names it, the
    /// failed file and the planned files left as they were.
    /// </summary>
    private static AbuseFeedWriteResult WritePlan(IReadOnlyList<PlannedFeedFile> plan, int leftOut, int removed,
                                                  string failureTrace)
    {
        var done = new List<PlannedFeedFile>();
        try
        {
            foreach (PlannedFeedFile file in plan)
            {
                AbuseIpDbFeedWriter.WriteFile(file.Path, file.Type, file.Body);
                done.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"{failureTrace}: {ex.Message}");
            // The failed file's earlier copy is whole, because the move never ran, and a file after it is never tried;
            // the readers keep reading both. A folder at a feed's path is not a file and isn't named
            List<string> left = plan.Skip(done.Count).Select(file => file.Path).Where(File.Exists).ToList();
            return WrittenResult(AbuseFeedWriteFailure.WriteFailed, done, leftOut, removed)
                with
                {
                    FailedFile = plan[done.Count].Path,
                    OldFilesLeft = left.Count > 0 ? left : null,
                };
        }

        return WrittenResult(AbuseFeedWriteFailure.None, done, leftOut, removed);
    }

    /// <summary>
    /// The files written, the header-only and ModSecurity ones named again, and each family's addresses counted once
    /// however many copies were written; the left-out and removal totals are both families', written or not.
    /// </summary>
    private static AbuseFeedWriteResult WrittenResult(AbuseFeedWriteFailure failure,
                                                      IReadOnlyList<PlannedFeedFile> done, int leftOut, int removed)
    {
        AbuseFeedFamily[] families = done.Select(file => file.Body.Family).DistinctBy(family => family.IsIpv6).ToArray();
        List<string> modSecurity = done.Where(file => file.Type == FeedType.ModSecurity)
                                       .Select(file => file.Path).ToList();
        return new AbuseFeedWriteResult(failure, done.Select(file => file.Path).ToList(),
            families.Where(family => !family.IsIpv6).Sum(family => family.Addresses.Count),
            families.Where(family => family.IsIpv6).Sum(family => family.Addresses.Count),
            leftOut, removed,
            done.Where(file => file.Body.Family.Addresses.Count == 0).Select(file => file.Path).ToList())
        {
            ModSecurityFiles = modSecurity.Count > 0 ? modSecurity : null,
        };
    }

    /// <summary>One file of a write, in write order: the feed types in turn, each with IPv4 then IPv6.</summary>
    private readonly record struct PlannedFeedFile(FeedType Type, string Path, AbuseFeedBody Body);

    private async Task<AbuseNetworksResult> NetworksCoreAsync(IProgress<GroupingProgress>? progress, CancellationToken ct)
    {
        var reporter = new ForwardProgress(progress);
        AbuseNetworksResult first = await FindNetworksOnceAsync(reporter, ct);
        if (first.Failure != AbuseNetworksFailure.DatasetChanged)
            return first;

        // An update or the IPv6 switch changed what answered, so the whole lookup runs again from the settings and the
        // list. A second change is reported rather than chased
        Trace.TraceInformation("The IPinfo dataset changed while the AbuseIPDB addresses were looked up, so the lookup started again.");
        AbuseNetworksResult second = await FindNetworksOnceAsync(reporter, ct);
        if (second.Failure == AbuseNetworksFailure.DatasetChanged)
            Trace.TraceWarning("The IPinfo dataset changed again while the AbuseIPDB addresses were looked up.");
        return second;
    }

    /// <summary>
    /// One lookup of the list's addresses: DatasetChanged when the dataset or the IPv6 switch changed under it, which
    /// the caller runs again once.
    /// </summary>
    private async Task<AbuseNetworksResult> FindNetworksOnceAsync(ForwardProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // One read of the settings, so the keep days and the IPv6 switch come from the same snapshot
        UserSettings settings = _ipinfo.Settings;
        int keepDays = KeepDayCount(settings);
        bool includeIPv6 = settings.IPv6Enabled;

        // The feed's read: the same keep-days cutoff and IPv6 switch the rolling feed reads with
        DateTimeOffset now = _time.GetUtcNow();
        ListRead<ListFeed> read = _list.ReadFeed(now, TimeSpan.FromDays(keepDays), includeIPv6);
        if (NetworksListFailure(read.State) is AbuseNetworksFailure unusable)
            return NetworksFailure(unusable, includeIPv6, keepDays, null);

        // A missing file reads as the empty feed, with no download
        ListFeed list = read.Value ?? ListFeed.Empty;
        if (list.LastDownloadAt is not DateTimeOffset lastDownload)
            return NetworksFailure(AbuseNetworksFailure.NoDownload, includeIPv6, keepDays, null);

        // The feed's clock rule: the cutoff would hold addresses older than the keep days, so nothing is looked up
        if (IsClockBehind(lastDownload, now))
            return NetworksFailure(AbuseNetworksFailure.ClockWrong, includeIPv6, keepDays, lastDownload);

        // A report, so no exceptions: above the cap, or where exceptions cover listed addresses, it differs from the
        // written feed. The families are kept apart so no chunk mixes them
        AbuseReportFamily ipv4 = AbuseIpDbFeedWriter.SelectForReport(list.Entries, ipv6: false);
        AbuseReportFamily ipv6 = AbuseIpDbFeedWriter.SelectForReport(list.Entries, ipv6: true);
        int leftOut = ipv4.LeftOut + ipv6.LeftOut;
        var ipv4Blocks = new List<CidrBlock>(ipv4.Addresses.Count);
        var ipv6Blocks = new List<CidrBlock>(ipv6.Addresses.Count);
        int unmade = 0;
        foreach (PublicAddress address in ipv4.Addresses.Concat(ipv6.Addresses))
        {
            if (AbuseIpDbNetworks.TryBlockOf(address, out CidrBlock? block))
                (block.IsIpv6 ? ipv6Blocks : ipv4Blocks).Add(block);
            else
                unmade++;
        }

        if (unmade > 0)
        {
            Trace.TraceError(string.Create(CultureInfo.InvariantCulture,
                $"{unmade} AbuseIPDB list addresses couldn't be made into blocks, so none were looked up."));
            return NetworksFailure(AbuseNetworksFailure.LookupFailed, includeIPv6, keepDays, lastDownload);
        }

        int total = ipv4Blocks.Count + ipv6Blocks.Count;
        if (total == 0)
        {
            return new AbuseNetworksResult(AbuseNetworksFailure.None, [], 0, [], 0, 0, 0, includeIPv6, keepDays, lastDownload,
                                           _ipinfo.Dataset, 0);
        }

        DatasetInfo? before = _ipinfo.Dataset;
        if (before is null)
            return NetworksFailure(AbuseNetworksFailure.NoDataset, includeIPv6, keepDays, lastDownload);

        var answers = new List<IReadOnlyList<OverlapGroup>>(total);
        int done = 0;
        progress.Report(done, total);
        IEnumerable<CidrBlock[]> chunks = ipv4Blocks.Chunk(AbuseIpDbNetworks.IPv4ChunkSize)
            .Concat(ipv6Blocks.Chunk(AbuseIpDbNetworks.IPv6ChunkSize));
        foreach (CidrBlock[] chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            // Each chunk is its own call through the database gate, so a search beside one lookup waits one chunk at most
            OverlapResult answer = await _ipinfo.LookUpOverlapsAsync(chunk, ct);
            if (answer.Outcome == OverlapOutcome.NoDataset)
                return NetworksFailure(AbuseNetworksFailure.NoDataset, includeIPv6, keepDays, lastDownload);
            if (answer.Outcome != OverlapOutcome.Found || answer.Dataset is null)
                return NetworksFailure(AbuseNetworksFailure.LookupFailed, includeIPv6, keepDays, lastDownload);
            // By value: a rebuild for IPv6 keeps the checksum, and every read of the dataset makes a new instance
            if (answer.Dataset != before)
                return NetworksFailure(AbuseNetworksFailure.DatasetChanged, includeIPv6, keepDays, lastDownload);

            AbuseNetworksFailure matched = MatchChunk(chunk, answer.Overlaps, answers);
            if (matched != AbuseNetworksFailure.None)
                return NetworksFailure(matched, includeIPv6, keepDays, lastDownload);

            done += chunk.Length;
            progress.Report(done, total);
        }

        // Each answer names the file that answered, so this check exists only so the result is current when it is
        // returned: an update or an IPv6 switch change that landed after the last chunk starts the lookup again
        if (_ipinfo.Dataset != before || _ipinfo.Settings.IPv6Enabled != includeIPv6)
            return NetworksFailure(AbuseNetworksFailure.DatasetChanged, includeIPv6, keepDays, lastDownload);

        AbuseNetworksGrouping grouping = AbuseIpDbNetworks.Group(answers);
        if (grouping.MultiAsnAddresses > 0 || grouping.UnreadableAsns > 0)
        {
            Trace.TraceInformation(string.Create(CultureInfo.InvariantCulture,
                $"The AbuseIPDB addresses were grouped: {grouping.MultiAsnAddresses} under more than one ASN, "
                + $"{grouping.UnreadableAsns} unreadable ASNs."));
        }

        return new AbuseNetworksResult(AbuseNetworksFailure.None, grouping.Networks, grouping.NoAsn, grouping.NoAsnCountries,
                                       grouping.NotInIpinfo, ipv4Blocks.Count, ipv6Blocks.Count, includeIPv6, keepDays,
                                       lastDownload, before, leftOut);
    }

    /// <summary>
    /// Each block asked about must come back exactly once. A block missing from an IPv6 chunk because IPv6 was
    /// turned off since the settings were read is DatasetChanged; any other mismatch is LookupFailed.
    /// </summary>
    private AbuseNetworksFailure MatchChunk(CidrBlock[] chunk, IReadOnlyList<NetworkOverlap> overlaps,
                                            List<IReadOnlyList<OverlapGroup>> answers)
    {
        // Null until the block's answer is seen; the list's addresses are distinct, so the chunk's blocks are too
        var found = new Dictionary<CidrBlock, IReadOnlyList<OverlapGroup>?>(chunk.Length);
        foreach (CidrBlock block in chunk)
            found[block] = null;

        foreach (NetworkOverlap overlap in overlaps)
        {
            if (!found.TryGetValue(overlap.Block, out IReadOnlyList<OverlapGroup>? seen) || seen is not null)
            {
                Trace.TraceError("The IPinfo lookup answered for a block it wasn't asked about, or twice for one.");
                return AbuseNetworksFailure.LookupFailed;
            }

            found[overlap.Block] = overlap.Groups;
        }

        int missing = found.Values.Count(groups => groups is null);
        if (missing > 0)
        {
            // A chunk holds one family, and IPv6 blocks are asked about only while IPv6 was on in the settings read
            if (chunk[0].IsIpv6 && !_ipinfo.Settings.IPv6Enabled)
                return AbuseNetworksFailure.DatasetChanged;

            Trace.TraceError(string.Create(CultureInfo.InvariantCulture,
                $"The IPinfo lookup left out {missing} of {chunk.Length} blocks."));
            return AbuseNetworksFailure.LookupFailed;
        }

        answers.AddRange(found.Values.OfType<IReadOnlyList<OverlapGroup>>());
        return AbuseNetworksFailure.None;
    }

    private AbuseListStatus StatusCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now = _time.GetUtcNow();
        ListRead<ListFacts> read = _list.ReadFacts(now, KeepDays(), countNew: true);
        // Asked before the accounting is read, so a store opened between the two can't pair "open" with the empty
        // accounting read before it
        bool available = _accounting.IsAvailable;
        // Read once, after the list: a download records its answer before it merges, so a status that shows the merge
        // shows the answer too, and the limit, the count and the refusal come from one accounting
        AbuseAccounting accounting = EffectiveAccounting();
        // The accounting is in config.db, so a list that can't be used still shows the limit and when a download may go
        if (ListFailure(read.State) is not null || read.Value is not { } facts)
            return AccountingStatus(read.State, accounting, now);

        // With no config store open the accounting is unknown, so nothing may say a download can go: it would be
        // refused as a storage error
        if (!available)
            return AccountingStatus(AbuseListState.StorageError, AbuseAccounting.Empty, now);

        AbuseListStatus status = AccountingStatus(read.State, accounting, now);
        // Counted from a clock more than a day behind the last download, the counts would hold addresses older than the
        // keep days, so they are withheld; the times, the new addresses and the accounting still show
        if (IsClockBehind(facts.LastDownloadAt, now))
        {
            return status with
            {
                LastDownloadAt = facts.LastDownloadAt, GeneratedAt = facts.GeneratedAt, NewCount = facts.NewCount,
                ClockBehind = true,
            };
        }

        return status with
        {
            IPv4Count = facts.IPv4Count, IPv6Count = facts.IPv6Count,
            LastDownloadAt = facts.LastDownloadAt, GeneratedAt = facts.GeneratedAt, NewCount = facts.NewCount,
        };
    }

    private AbuseNewAddresses NewAddressesCore(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ListRead<ListNew> read = _list.ReadNew();
        if (ListFailure(read.State) is not null || read.Value is not { } set)
        {
            return NewAddressesFailure(read.State switch
            {
                AbuseListState.Newer => AbuseNewAddressesFailure.ListNewer,
                AbuseListState.Unreadable => AbuseNewAddressesFailure.ListUnreadable,
                _ => AbuseNewAddressesFailure.StorageError,
            });
        }
        // A missing file, or one no download has been merged into
        if (set.LastDownloadAt is not DateTimeOffset asOf)
            return NewAddressesFailure(AbuseNewAddressesFailure.NoDownload);
        if (set.Entries is not { } entries)
            return NewAddressesFailure(AbuseNewAddressesFailure.NotKnown);

        var addresses = new AbuseNewAddress[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            addresses[i] = new AbuseNewAddress(entries[i].Address, entries[i].Score, entries[i].LastReportedAt);
        return new AbuseNewAddresses(AbuseNewAddressesFailure.None, addresses, asOf);
    }

    /// <summary>
    /// Records one answer in config.db. A write that fails is traced and the answer kept in the session copy, which from
    /// then on takes every answer, so the refusals this session follow it. Called only under the download gate.
    /// </summary>
    private void RecordAnswer(AbuseRateLimit stored, DateTimeOffset answeredAt, bool countDownload)
    {
        // Taken before the write, so a copy made after a failure is the accounting this answer was applied to
        AbuseAccounting basis = _unrecorded ?? _accounting.Current;
        bool recorded = _accounting.TryRecordAnswer(stored, answeredAt, countDownload);
        if (!recorded)
            Trace.TraceWarning("The AbuseIPDB download answer couldn't be recorded, so it is kept for this session only.");
        if (!recorded || _unrecorded is not null)
            _unrecorded = basis.After(stored, answeredAt, countDownload);
    }

    /// <summary>The accounting refusals and the status use: the session copy once a write has failed, else the store's.</summary>
    private AbuseAccounting EffectiveAccounting() => _unrecorded ?? _accounting.Current;

    /// <summary>
    /// Whether the data folder's disk has less free space than <see cref="AbuseIpDbLimits.MinFreeBytesForDownload"/> beyond
    /// the rolling list's size now. Free space that can't be measured lets the download go.
    /// </summary>
    private bool IsDiskNearlyFull()
    {
        if (_freeBytes(_dataFolder) is not long free)
            return false;

        return free < AbuseIpDbLimits.MinFreeBytesForDownload + ListBytes();
    }

    /// <summary>The rolling list file's length now; 0 when it is missing or its length can't be read.</summary>
    private long ListBytes()
    {
        try
        {
            var file = new FileInfo(_listPath);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>The free bytes on the disk that holds the folder, or null when they can't be measured.</summary>
    private static long? FreeBytesOn(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return null;

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key the check and the download send. None saved, or saved text that isn't a key, is NoKey; a key saved but
    /// unreadable now is KeyUnavailable. Both are kept.
    /// </summary>
    private AbuseIpDbFailure TryLoadKey(out string? key)
    {
        // A key that might be readable later is kept; TokenStore removes only one that can never be read
        key = _keys.TryLoad();
        if (key is null)
            return _keys.HasToken ? AbuseIpDbFailure.KeyUnavailable : AbuseIpDbFailure.NoKey;

        // A saved text that isn't a key is kept too: the user replaces it by saving another
        if (!IsValidKey(key))
        {
            key = null;
            return AbuseIpDbFailure.NoKey;
        }

        return AbuseIpDbFailure.None;
    }

    /// <summary>The keep days from the settings, clamped again here because the file can be edited by hand.</summary>
    private static int KeepDayCount(UserSettings settings) =>
        Math.Clamp(settings.AbuseIpDbKeepDays, AbuseIpDbLimits.MinKeepDays, AbuseIpDbLimits.MaxKeepDays);

    /// <summary>The keep days of <see cref="KeepDayCount"/> from the settings now, as a span.</summary>
    private TimeSpan KeepDays() => TimeSpan.FromDays(KeepDayCount(_ipinfo.Settings));

    /// <summary>
    /// When a download may be sent again, or null when one may be sent now. A stored limit decides alone: nothing left
    /// with a reset still ahead refuses until the reset, and anything else lets AbuseIPDB's own answer decide.
    /// </summary>
    private static DateTimeOffset? RefusedUntil(AbuseAccounting accounting, DateTimeOffset now)
    {
        if (accounting.RateLimit is { } limit)
            return RefusedBy(limit, now);

        // Runs only until AbuseIPDB first sends X-RateLimit-Remaining
        DateOnly today = UtcDay(now);
        return accounting.DownloadsDay == today && accounting.DownloadsOnDay >= AbuseIpDbLimits.FreeBlacklistDownloadsPerDay
            ? new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            : null;
    }

    /// <summary>
    /// A reset more than <see cref="AbuseIpDbLimits.MaxResetAheadHours"/> hours ahead is unknown and refuses nothing,
    /// so a clock set back since the answer, or a list from another computer, can't refuse for days.
    /// </summary>
    private static DateTimeOffset? RefusedBy(AbuseRateLimit limit, DateTimeOffset now) =>
        limit.Remaining is <= 0 && limit.ResetAt is DateTimeOffset reset && reset > now
        && reset - now <= TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours)
            ? reset
            : null;

    /// <summary>The later of two times; null only when both are.</summary>
    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is DateTimeOffset a && second is DateTimeOffset b ? (a >= b ? a : b) : first ?? second;

    private static DateOnly UtcDay(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    /// <summary>
    /// The one clock rule the status, the download, the feed and the offending networks share. Counting from now, the
    /// earlier time, would keep addresses longer than the keep days; a clock ahead changes nothing.
    /// </summary>
    private static bool IsClockBehind(DateTimeOffset? lastDownload, DateTimeOffset now) =>
        lastDownload is { } last && last - now > TimeSpan.FromHours(AbuseIpDbLimits.MaxClockDifferenceHours);

    /// <summary>The failure for a list this version can't use now; null when it can (usable, or no file yet).</summary>
    private static AbuseIpDbFailure? ListFailure(AbuseListState state) => state switch
    {
        AbuseListState.Newer => AbuseIpDbFailure.ListNewer,
        AbuseListState.Unreadable => AbuseIpDbFailure.ListUnreadable,
        AbuseListState.StorageError => AbuseIpDbFailure.StorageError,
        _ => null,
    };

    /// <summary>The feed's failure for a list this version can't use now; null when it can (usable, or no file yet).</summary>
    private static AbuseFeedWriteFailure? FeedListFailure(AbuseListState state) => state switch
    {
        AbuseListState.Newer => AbuseFeedWriteFailure.ListNewer,
        AbuseListState.Unreadable => AbuseFeedWriteFailure.ListUnreadable,
        AbuseListState.StorageError => AbuseFeedWriteFailure.StorageError,
        _ => null,
    };

    private static AbuseFeedWriteResult FeedFailure(AbuseFeedWriteFailure failure) => new(failure, [], 0, 0, 0, 0, []);

    /// <summary>The offending networks' failure for a list this version can't use now; null when it can (usable, or no file yet).</summary>
    private static AbuseNetworksFailure? NetworksListFailure(AbuseListState state) => state switch
    {
        AbuseListState.Newer => AbuseNetworksFailure.ListNewer,
        AbuseListState.Unreadable => AbuseNetworksFailure.ListUnreadable,
        AbuseListState.StorageError => AbuseNetworksFailure.StorageError,
        _ => null,
    };

    /// <summary>A failure: no groups and zero counts, the settings and the last download as far as they were read, and no dataset.</summary>
    private static AbuseNetworksResult NetworksFailure(AbuseNetworksFailure failure, bool includesIPv6, int keepDays,
                                                       DateTimeOffset? lastDownloadAt) =>
        new(failure, [], 0, [], 0, 0, 0, includesIPv6, keepDays, lastDownloadAt, null, 0);

    private static AbuseDownloadResult DownloadFailure(AbuseIpDbFailure failure, DateTimeOffset? retryAt, bool counted = false) =>
        new(failure, false, 0, 0, 0, 0, 0, null, retryAt, counted);

    /// <summary>A status with no addresses and no download, and the accounting's limit, today's count and refusal.</summary>
    private static AbuseListStatus AccountingStatus(AbuseListState state, AbuseAccounting accounting, DateTimeOffset now) =>
        new(state, 0, 0, null, null, accounting.RateLimit,
            accounting.DownloadsDay == UtcDay(now) ? accounting.DownloadsOnDay : 0, RefusedUntil(accounting, now));

    private static AbuseCheckResult Failure(AbuseIpDbFailure failure) => new(failure, null, null);

    private static AbuseNewAddresses NewAddressesFailure(AbuseNewAddressesFailure failure) => new(failure, [], null);

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>The exception type only: a message on this path can hold the request address or a header.</summary>
    private static void TraceUnexpected(string operation, Exception ex) =>
        Trace.TraceError($"The AbuseIPDB {operation} failed unexpectedly ({ex.GetType().FullName}).");

    /// <summary>
    /// Reports addresses looked up only when the share done is above the last one reported, so a restart, which starts
    /// again from none and can change the total, never makes the progress go backwards.
    /// </summary>
    private sealed class ForwardProgress(IProgress<GroupingProgress>? progress)
    {
        private bool _reported;
        private long _done;
        private long _total;

        public void Report(int done, int total)
        {
            if (progress is null || total <= 0)
                return;

            // done / total above _done / _total, cross-multiplied so nothing is rounded
            if (_reported && (long)done * _total <= _done * total)
                return;

            _reported = true;
            _done = done;
            _total = total;
            progress.Report(new GroupingProgress(done, total));
        }
    }
}
