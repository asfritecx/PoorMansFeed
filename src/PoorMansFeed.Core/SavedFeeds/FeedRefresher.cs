using System.Diagnostics;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>
/// Refresh all: the dataset, each cloud source and the AbuseIPDB download, then every saved feed, then one save of the
/// stamps. It keeps nothing between runs and doesn't stop two runs at once: its caller runs one at a time.
/// </summary>
public sealed class FeedRefresher : IFeedRefresher
{
    private static readonly FeedKind[] CloudKinds = [FeedKind.GoogleCloud, FeedKind.Azure, FeedKind.Aws];

    private readonly IIpInfoService _ipinfo;
    private readonly IAbuseIpDbService _abuse;
    private readonly IGoogleCloudFeedService _google;
    private readonly IAzureFeedService _azure;
    private readonly IAwsFeedService _aws;
    private readonly TimeProvider _time;

    public FeedRefresher(IIpInfoService ipinfo, IAbuseIpDbService abuse, IGoogleCloudFeedService google,
                         IAzureFeedService azure, IAwsFeedService aws, TimeProvider time)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _abuse = abuse ?? throw new ArgumentNullException(nameof(abuse));
        _google = google ?? throw new ArgumentNullException(nameof(google));
        _azure = azure ?? throw new ArgumentNullException(nameof(azure));
        _aws = aws ?? throw new ArgumentNullException(nameof(aws));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<RefreshRunResult> RunAsync(IProgress<RefreshProgress>? progress, CancellationToken ct)
    {
        var run = new Run(progress);
        try
        {
            return await RunCoreAsync(run, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return run.Result(cancelled: true, FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Refresh all stopped ({ex.GetType().FullName})");
            return run.Result(cancelled: false, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed);
        }
    }

    private async Task<RefreshRunResult> RunCoreAsync(Run run, CancellationToken ct)
    {
        SavedFeedsRead read;
        try
        {
            read = await Task.Run(() => _ipinfo.Records.ReadSavedFeeds(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Refresh all couldn't read the saved feeds ({ex.GetType().FullName})");
            read = new SavedFeedsRead(false, []);
        }
        if (!read.Available)
            return new RefreshRunResult(false, null, [], null, [], false, false);
        run.Feeds = read.Feeds.OrderBy(feed => feed.Key, StringComparer.Ordinal).ToList();
        if (run.Feeds.Count == 0)
            return run.Result(cancelled: false, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed);

        // Only a feed not retired at the start asks for a step; each refresher still judges retired under its own lock
        var live = run.Feeds.Where(feed => !feed.Retired).Select(feed => feed.Kind).ToHashSet();
        if (live.Contains(FeedKind.Asn) || live.Overlaps(CloudKinds))
        {
            ct.ThrowIfCancellationRequested();
            run.Report(RefreshStep.Dataset, 0, 1);
            run.Dataset = await _ipinfo.UpdateDatasetAsync(new DatasetProgress(run), ct, IpInfoUpdatePolicy.RefreshAll);
        }

        List<FeedKind> providers = CloudKinds.Where(live.Contains).ToList();
        for (int i = 0; i < providers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            run.Report(RefreshStep.Sources, i, providers.Count);
            await LoadSourceAsync(run, providers[i], ct);
        }

        if (live.Contains(FeedKind.AbuseIpDbRolling))
        {
            ct.ThrowIfCancellationRequested();
            run.Report(RefreshStep.AbuseIpDb, 0, 1);
            // HasKey is a file check, so no key read happens unless a download is asked for
            if (!_abuse.HasKey)
            {
                run.AbuseIpDb = new AbuseRefreshDownload(AbuseIpDbFailure.NoKey, null);
            }
            else
            {
                AbuseDownloadResult download = await _abuse.DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, ct);
                run.AbuseIpDb = new AbuseRefreshDownload(download.Failure, download);
            }
        }

        bool cancelled = await RefreshFeedsAsync(run, ct);
        await CommitAsync(run);
        return run.Result(cancelled, FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled);
    }

    private async Task LoadSourceAsync(Run run, FeedKind provider, CancellationToken ct)
    {
        // Taken before the load, so a page write after the source was read supersedes this run's write
        DateTimeOffset started = _time.GetUtcNow();
        SourceRefreshState state;
        try
        {
            state = await LoadCatalogueAsync(run, provider, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // One provider's failure fails only its own feeds; the other steps and feeds go on
            Trace.TraceError($"Refresh all couldn't load a cloud source ({ex.GetType().FullName})");
            run.Google = provider == FeedKind.GoogleCloud ? null : run.Google;
            run.Azure = provider == FeedKind.Azure ? null : run.Azure;
            run.Aws = provider == FeedKind.Aws ? null : run.Aws;
            state = SourceRefreshState.Unavailable;
        }

        run.Sources.Add(new SourceRefresh(provider, state));
        run.Started[provider] = started;
    }

    private async Task<SourceRefreshState> LoadCatalogueAsync(Run run, FeedKind provider, CancellationToken ct)
    {
        SourceRefreshState state;
        switch (provider)
        {
            case FeedKind.GoogleCloud:
                CatalogueResult fetched = await _google.LoadCatalogueAsync(ct);
                if (fetched.Failure == CatalogueFailure.None && fetched.Catalogue is not null)
                {
                    run.Google = fetched.Catalogue;
                    state = SourceRefreshState.Fresh;
                    break;
                }
                SavedRanges<CloudRangeCatalogue> saved = await _google.LoadSavedAsync(ct);
                run.Google = saved.State == SavedRangesState.Read ? saved.Catalogue : null;
                state = run.Google is null ? SourceRefreshState.Unavailable : SourceRefreshState.Saved;
                break;
            case FeedKind.Azure:
                AzureLoadResult azure = await _azure.CheckForUpdateAsync(await _azure.LoadCachedAsync(ct), ct);
                run.Azure = azure.Catalogue;
                state = StateOf(azure.Catalogue is not null,
                                azure.Status is AzureUpdateStatus.UpToDate or AzureUpdateStatus.Updated);
                break;
            default:
                AwsLoadResult aws = await _aws.CheckForUpdateAsync(await _aws.LoadCachedAsync(ct), ct);
                run.Aws = aws.Catalogue;
                state = StateOf(aws.Catalogue is not null, aws.Status is AwsUpdateStatus.UpToDate or AwsUpdateStatus.Updated);
                break;
        }
        return state;
    }

    private static SourceRefreshState StateOf(bool hasCatalogue, bool checkedNow) =>
        !hasCatalogue ? SourceRefreshState.Unavailable : checkedNow ? SourceRefreshState.Fresh : SourceRefreshState.Saved;

    // True when the run's cancel stopped it before every feed was done
    private async Task<bool> RefreshFeedsAsync(Run run, CancellationToken ct)
    {
        if (run.Feeds.Count > 0)
            run.Report(RefreshStep.Feeds, 0, run.Feeds.Count);
        for (int i = 0; i < run.Feeds.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return true;
            SavedFeed feed = run.Feeds[i];
            try
            {
                (run.Results[i], run.Stamps[i]) = await RefreshFeedAsync(run, feed, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                run.Results[i] = NotWritten(feed, FeedRefreshOutcome.Skipped, FeedRefreshReason.Cancelled);
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"A saved feed's refresh failed ({ex.GetType().FullName})");
                run.Results[i] = NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed);
            }
            run.Report(RefreshStep.Feeds, i + 1, run.Feeds.Count, feed.Key);
        }

        return false;
    }

    private async Task<(FeedRefreshResult, SavedFeedStamp?)> RefreshFeedAsync(Run run, SavedFeed feed, CancellationToken ct)
    {
        switch (feed.Kind)
        {
            case FeedKind.Asn:
                AsnFeedRefresh asn = await _ipinfo.RefreshAsnFeedAsync(feed.Key, ct);
                return (FromAsn(feed, asn), asn.Stamp);
            case FeedKind.AbuseIpDbRolling:
                // Called after the run's download returned: the refresh takes the download gate, which isn't re-entrant
                RollingFeedRefresh rolling = await _abuse.RefreshFeedAsync(ct);
                return (FromRolling(feed, rolling), rolling.Stamp);
            case FeedKind.GoogleCloud or FeedKind.Azure or FeedKind.Aws:
                return await RefreshCloudAsync(run, feed, ct);
            default:
                return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed), null);
        }
    }

    private async Task<(FeedRefreshResult, SavedFeedStamp?)> RefreshCloudAsync(Run run, SavedFeed feed, CancellationToken ct)
    {
        // Retired at the start: its files stay header-only, whatever its pick or its provider's source says now
        if (feed.Retired)
            return (NotWritten(feed, FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired), null);
        if (!run.Started.TryGetValue(feed.Kind, out DateTimeOffset started))
            return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.SourceUnavailable), null);

        SavedFeedPick pick = feed.Pick;
        ICloudFeedService service;
        CheckResult check;
        switch (feed.Kind)
        {
            case FeedKind.GoogleCloud when run.Google is not null:
                if (!GoogleCloudScope.TryCreate(pick.GoogleScope, out GoogleCloudScope scope))
                    return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), null);
                service = _google;
                check = await _google.CheckAsync(run.Google, new RegionSelection(scope, pick.GoogleAddsGlobal), ct);
                break;
            case FeedKind.Azure when run.Azure is not null:
                if (pick.AzureTag is null)
                    return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), null);
                service = _azure;
                check = await _azure.CheckAsync(run.Azure, new AzureTagSelection(pick.AzureTag), ct);
                break;
            case FeedKind.Aws when run.Aws is not null:
                if (pick.AwsService is null)
                    return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone), null);
                service = _aws;
                check = await _aws.CheckAsync(run.Aws, new AwsSelection(pick.AwsService, pick.AwsRegion), ct);
                break;
            case FeedKind.GoogleCloud or FeedKind.Azure or FeedKind.Aws:
                return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.SourceUnavailable), null);
            default:
                return (NotWritten(feed, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed), null);
        }

        FeedRefreshReason? failed = check.Failure switch
        {
            CheckFailure.None when check.Check is not null => null,
            CheckFailure.PickGone => FeedRefreshReason.PickGone,
            CheckFailure.NoDataset => FeedRefreshReason.NoDataset,
            CheckFailure.NothingSelected or CheckFailure.Failed => FeedRefreshReason.SourceUnavailable,
            _ => FeedRefreshReason.Failed,
        };
        if (failed is not null)
            return (NotWritten(feed, FeedRefreshOutcome.Failed, failed.Value), null);

        CloudFeedRefresh refresh = await service.RefreshAsync(check.Check!, started, ct);
        return (FromCloud(feed, refresh), refresh.Stamp);
    }

    // After every refresher returned, so no feed lock is held; not cancelled, so the done feeds' stamps are kept
    private async Task CommitAsync(Run run)
    {
        List<int> stamped = Enumerable.Range(0, run.Feeds.Count).Where(i => run.Stamps[i] is not null).ToList();
        if (stamped.Count == 0)
            return;

        run.Report(RefreshStep.Saving, 0, 1);
        List<SavedFeedStamp> stamps = stamped.Select(i => run.Stamps[i]!).ToList();
        IReadOnlyList<SavedFeedStampOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(() => _ipinfo.Records.CommitRefresh(stamps), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Refresh all couldn't save the saved feeds ({ex.GetType().FullName})");
            outcomes = [];
        }
        for (int j = 0; j < stamped.Count; j++)
        {
            int i = stamped[j];
            SavedFeedStampOutcome outcome = j < outcomes.Count ? outcomes[j] : SavedFeedStampOutcome.StorageError;
            run.SaveFailed |= outcome is SavedFeedStampOutcome.TooMany or SavedFeedStampOutcome.Unavailable
                                  or SavedFeedStampOutcome.StorageError;
            run.Results[i] = Committed(run.Results[i]!, outcome);
        }
    }

    // A result that already failed keeps its own failure, whatever the commit of its stamp says
    private static FeedRefreshResult Committed(FeedRefreshResult result, SavedFeedStampOutcome outcome) => outcome switch
    {
        _ when result.Outcome == FeedRefreshOutcome.Failed => result,
        SavedFeedStampOutcome.Saved or SavedFeedStampOutcome.Unchanged => result,
        SavedFeedStampOutcome.Superseded => result with { Outcome = FeedRefreshOutcome.Skipped, Reason = FeedRefreshReason.Superseded },
        SavedFeedStampOutcome.Removed => result with { Outcome = FeedRefreshOutcome.Skipped, Reason = FeedRefreshReason.Removed },
        _ => result with { Outcome = FeedRefreshOutcome.Failed, Reason = FeedRefreshReason.NotSaved },
    };

    // ----- Each refresher's result -----

    private static FeedRefreshResult FromAsn(SavedFeed feed, AsnFeedRefresh refresh)
    {
        ExportResult? write = refresh.Export;
        FeedRefreshReason? reason = refresh.Outcome switch
        {
            AsnRefreshOutcome.Unchanged or AsnRefreshOutcome.Written => null,
            AsnRefreshOutcome.Retired => FeedRefreshReason.Retired,
            AsnRefreshOutcome.Missing => FeedRefreshReason.Removed,
            AsnRefreshOutcome.AsnFeedsOff => FeedRefreshReason.FeedsOff,
            AsnRefreshOutcome.SaveRefused => FeedRefreshReason.SaveRefused,
            AsnRefreshOutcome.SavedFeedsUnavailable => FeedRefreshReason.SavedFeedsUnavailable,
            AsnRefreshOutcome.SourceGone => FeedRefreshReason.SourceGone,
            AsnRefreshOutcome.NoDataset => FeedRefreshReason.NoDataset,
            AsnRefreshOutcome.NoFeedType => FeedRefreshReason.NoFeedType,
            _ => write?.Failure switch
            {
                ExportFailure.ExceptionsUnreadable => FeedRefreshReason.ExceptionsUnreadable,
                ExportFailure.FolderUnavailable => FeedRefreshReason.FolderUnavailable,
                ExportFailure.WriteFailed => FeedRefreshReason.WriteFailed,
                _ => FeedRefreshReason.Failed,
            },
        };
        return Build(feed, refresh.Outcome == AsnRefreshOutcome.Written, reason, write?.FeedChanges, 0, refresh.NewLarge.Count,
                     write?.FailedFile, write?.OldFilesLeft, write?.Record);
    }

    private static FeedRefreshResult FromCloud(SavedFeed feed, CloudFeedRefresh refresh)
    {
        CloudFeedWriteResult? write = refresh.Write;
        FeedRefreshReason? reason = refresh.Outcome switch
        {
            CloudRefreshOutcome.Unchanged or CloudRefreshOutcome.Written => null,
            CloudRefreshOutcome.Retired => FeedRefreshReason.Retired,
            CloudRefreshOutcome.Missing => FeedRefreshReason.Removed,
            CloudRefreshOutcome.Superseded => FeedRefreshReason.Superseded,
            CloudRefreshOutcome.SaveRefused => FeedRefreshReason.SaveRefused,
            CloudRefreshOutcome.SavedFeedsUnavailable => FeedRefreshReason.SavedFeedsUnavailable,
            _ => write?.Failure switch
            {
                CloudFeedWriteFailure.ExceptionsUnreadable => FeedRefreshReason.ExceptionsUnreadable,
                CloudFeedWriteFailure.FolderUnavailable => FeedRefreshReason.FolderUnavailable,
                CloudFeedWriteFailure.WriteFailed => FeedRefreshReason.WriteFailed,
                CloudFeedWriteFailure.SettingsChanged => FeedRefreshReason.SettingsChanged,
                CloudFeedWriteFailure.NoFeedType => FeedRefreshReason.NoFeedType,
                _ => FeedRefreshReason.Failed,
            },
        };
        return Build(feed, refresh.Outcome == CloudRefreshOutcome.Written, reason, write?.FeedChanges,
                     refresh.NewUnconfirmed.Count, refresh.NewLarge.Count, write?.FailedFile, write?.OldFilesLeft, write?.Record);
    }

    private static FeedRefreshResult FromRolling(SavedFeed feed, RollingFeedRefresh refresh)
    {
        AbuseFeedWriteResult? write = refresh.Write;
        FeedRefreshReason? reason = refresh.Outcome switch
        {
            RollingRefreshOutcome.Unchanged or RollingRefreshOutcome.Written => null,
            RollingRefreshOutcome.Retired => FeedRefreshReason.Retired,
            RollingRefreshOutcome.Missing => FeedRefreshReason.Removed,
            RollingRefreshOutcome.SaveRefused => FeedRefreshReason.SaveRefused,
            RollingRefreshOutcome.SavedFeedsUnavailable => FeedRefreshReason.SavedFeedsUnavailable,
            _ => write?.Failure switch
            {
                AbuseFeedWriteFailure.ExceptionsUnreadable => FeedRefreshReason.ExceptionsUnreadable,
                AbuseFeedWriteFailure.FolderUnavailable => FeedRefreshReason.FolderUnavailable,
                AbuseFeedWriteFailure.WriteFailed => FeedRefreshReason.WriteFailed,
                AbuseFeedWriteFailure.NoFeedType => FeedRefreshReason.NoFeedType,
                AbuseFeedWriteFailure.ListUnreadable or AbuseFeedWriteFailure.ListNewer or AbuseFeedWriteFailure.StorageError
                    or AbuseFeedWriteFailure.NoDownload => FeedRefreshReason.ListUnavailable,
                AbuseFeedWriteFailure.ClockWrong => FeedRefreshReason.ClockWrong,
                _ => FeedRefreshReason.Failed,
            },
        };
        return Build(feed, refresh.Outcome == RollingRefreshOutcome.Written, reason, write?.FeedChanges, 0, 0,
                     write?.FailedFile, write?.OldFilesLeft, write?.Record);
    }

    // A null reason is a refresher's Unchanged or Written; Changed needs an entry changed, not only a rewrite
    private static FeedRefreshResult Build(SavedFeed feed, bool written, FeedRefreshReason? reason,
                                           IReadOnlyList<FeedFileChange>? changes, int newUnconfirmed, int newLarge,
                                           string? failedFile, IReadOnlyList<string>? oldFilesLeft, FeedRecordResult? record)
    {
        (int added, int removed) = CountOneCopy(changes);
        FeedRefreshOutcome outcome = reason switch
        {
            null when written && added + removed > 0 => FeedRefreshOutcome.Changed,
            null => FeedRefreshOutcome.Unchanged,
            FeedRefreshReason.Retired or FeedRefreshReason.Removed or FeedRefreshReason.Superseded
                or FeedRefreshReason.FeedsOff => FeedRefreshOutcome.Skipped,
            _ => FeedRefreshOutcome.Failed,
        };
        return new FeedRefreshResult(feed.Key, feed.Kind, outcome, reason ?? FeedRefreshReason.None, written, added, removed,
                                     newUnconfirmed, newLarge, failedFile, oldFilesLeft ?? [], record);
    }

    // The FortiGate and ModSecurity copies hold the same entries, so only the first type written is counted
    private static (int Added, int Removed) CountOneCopy(IReadOnlyList<FeedFileChange>? changes)
    {
        if (changes is null || changes.Count == 0)
            return (0, 0);

        FeedType first = changes[0].Type;
        int added = 0, removed = 0;
        foreach (FeedFileChange change in changes.Where(change => change.Type == first))
        {
            added += change.IPv4.Added.Count + change.IPv6.Added.Count;
            removed += change.IPv4.Removed.Count + change.IPv6.Removed.Count;
        }
        return (added, removed);
    }

    private static FeedRefreshResult NotWritten(SavedFeed feed, FeedRefreshOutcome outcome, FeedRefreshReason reason) =>
        new(feed.Key, feed.Kind, outcome, reason, false, 0, 0, 0, 0, null, [], null);

    /// <summary>One run's state, so the boundary's catch can still answer for every feed.</summary>
    private sealed class Run(IProgress<RefreshProgress>? progress)
    {
        private List<SavedFeed> _feeds = [];

        public List<SavedFeed> Feeds
        {
            get => _feeds;
            set
            {
                _feeds = value;
                Results = new FeedRefreshResult?[value.Count];
                Stamps = new SavedFeedStamp?[value.Count];
            }
        }

        public FeedRefreshResult?[] Results { get; private set; } = [];
        public SavedFeedStamp?[] Stamps { get; private set; } = [];
        public UpdateResult? Dataset { get; set; }
        public List<SourceRefresh> Sources { get; } = [];
        public Dictionary<FeedKind, DateTimeOffset> Started { get; } = [];
        public CloudRangeCatalogue? Google { get; set; }
        public AzureTagCatalogue? Azure { get; set; }
        public AwsRangeCatalogue? Aws { get; set; }
        public AbuseRefreshDownload? AbuseIpDb { get; set; }
        public bool SaveFailed { get; set; }

        // A caller's handler that throws must not cost the feeds their commit
        public void Report(RefreshStep step, int done, int total, string? feedKey = null, UpdateProgress? dataset = null)
        {
            try
            {
                progress?.Report(new RefreshProgress(step, done, total, feedKey, dataset));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Refresh all's progress handler failed ({ex.GetType().FullName})");
            }
        }

        /// <summary>Every feed not done gets the outcome and reason given.</summary>
        public RefreshRunResult Result(bool cancelled, FeedRefreshOutcome notDone, FeedRefreshReason reason)
        {
            var feeds = new List<FeedRefreshResult>(_feeds.Count);
            for (int i = 0; i < _feeds.Count; i++)
                feeds.Add(Results[i] ?? NotWritten(_feeds[i], notDone, reason));
            return new RefreshRunResult(true, Dataset, Sources.ToList(), AbuseIpDb, feeds, cancelled, SaveFailed);
        }
    }

    /// <summary>Passes the update's progress on at once, on the update's thread, as the Dataset step's.</summary>
    private sealed class DatasetProgress(Run run) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => run.Report(RefreshStep.Dataset, 0, 1, null, value);
    }
}
