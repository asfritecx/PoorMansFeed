using System.Diagnostics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core.CloudFeeds;

/// <summary>
/// Confirms a provider's ranges against the dataset and writes the feed of each ticked type. The provider's service
/// chooses and orders the ranges; the engine keeps that order.
/// </summary>
internal sealed class CloudFeedEngine : ICloudFeedService
{
    private readonly IIpInfoService _ipinfo;
    private readonly TimeProvider _time;
    private readonly string _defaultExportFolder;

    public CloudFeedEngine(IIpInfoService ipinfo, TimeProvider time, string defaultExportFolder)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _defaultExportFolder = defaultExportFolder ?? throw new ArgumentNullException(nameof(defaultExportFolder));
    }

    /// <summary>
    /// Drops IPv6 when IPv6Enabled is off and classifies each range by the description's domain. The dataset is asked
    /// even for an empty list, so NoDataset wins over NothingSelected.
    /// </summary>
    public async Task<CheckResult> CheckAsync(FeedDescription description, IReadOnlyList<FeedPrefix> ordered, CloudFeedPick pick,
                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(pick);
        ct.ThrowIfCancellationRequested();

        UserSettings settings = _ipinfo.Settings;
        var selected = new List<FeedPrefix>(ordered.Count);
        foreach (FeedPrefix prefix in ordered)
        {
            if (!settings.IPv6Enabled && prefix.Block.IsIpv6)
                continue;
            selected.Add(prefix);
        }

        var blocks = new List<CidrBlock>(selected.Count);
        foreach (FeedPrefix prefix in selected)
            blocks.Add(prefix.Block);

        OverlapResult overlaps = await _ipinfo.LookUpOverlapsAsync(blocks, ct);
        if (overlaps.Outcome == OverlapOutcome.NoDataset)
            return new CheckResult(CheckFailure.NoDataset, null);
        if (overlaps.Outcome != OverlapOutcome.Found || overlaps.Dataset is null || overlaps.Overlaps.Count != selected.Count)
            return new CheckResult(CheckFailure.Failed, null);
        if (selected.Count == 0)
            return new CheckResult(CheckFailure.NothingSelected, null);

        var ranges = new List<CheckedRange>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            IReadOnlyList<OverlapGroup> groups = overlaps.Overlaps[i].Groups;
            RangeConfirmation status = DomainConfirmation.Classify(groups, description.ConfirmDomains);
            bool large = LargeRangePolicy.IsLarge(selected[i].Block.Text, settings);
            ranges.Add(new CheckedRange(selected[i], status, groups, large));
        }

        var check = new CloudFeedCheck(
            description,
            overlaps.Dataset,
            settings.IPv6Enabled,
            settings.IPv4MinPrefix,
            settings.IPv6MinPrefix,
            ranges,
            pick);
        return new CheckResult(CheckFailure.None, check);
    }

    public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(ticked);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (CidrBlock block in ticked)
            wanted.Add(block.Text);

        var included = new List<CheckedRange>();
        var handTicked = new List<string>();
        var handUnticked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CheckedRange range in check.Ranges)
        {
            string text = range.Prefix.Block.Text;
            bool isTicked = wanted.Contains(text);
            if (isTicked)
                included.Add(range);
            // A hand tick is a difference from the default, in which only the confirmed ranges start ticked
            if (isTicked != range.StartsIncluded && seen.Add(text))
                (isTicked ? handTicked : handUnticked).Add(text);
        }

        UserSettings settings = _ipinfo.Settings;
        // The dialog starts a large range ticked only when the saved feed includes it
        SavedFeedPick? saved = SavedPickCarry.Prior(_ipinfo.Records, check.Description.FileStem, check.Pick.Kind);
        var savedIncluded = new HashSet<string>(saved?.LargeIncluded ?? [], StringComparer.Ordinal);
        var large = new List<LargeRange>();
        foreach (CheckedRange range in included)
        {
            if (LargeRangePolicy.IsLarge(range.Prefix.Block.Text, settings)
                && LargeRangePolicy.TryDescribe(range.Prefix.Block.Text, out LargeRange described))
            {
                large.Add(described with { SavedIncluded = savedIncluded.Contains(described.Network) });
            }
        }

        return new CloudFeedPlan(check, included, large, handTicked, handUnticked);
    }

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(includedLargeRanges);
        return Task.Run(() =>
        {
            IDisposable hold;
            try
            {
                // Taken before the settings are read, so a write that waited uses the settings current when it enters
                hold = _ipinfo.Records.EnterWrite(plan.Check.Description.FileStem);
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceError($"The feed write wasn't admitted ({ex.GetType().FullName})");
                return new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0);
            }

            using (hold)
            {
                UserSettings settings = _ipinfo.Settings;
                return Write(plan, includedLargeRanges, settings, FeedOptions.ForCloudFeed(settings), ct);
            }
        });
    }

    /// <summary>Writes the saved feed of the check's file stem from the caller's check, under the feed lock.</summary>
    // checkStartedUtc is taken before the source behind the check is read, so a page write after it supersedes this
    internal Task<CloudFeedRefresh> RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        return Task.Run(() =>
        {
            IDisposable hold;
            try
            {
                hold = _ipinfo.Records.EnterWrite(check.Description.FileStem);
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceError($"The feed refresh wasn't admitted ({ex.GetType().FullName})");
                return new CloudFeedRefresh(CloudRefreshOutcome.Failed,
                                            new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, 0), null, [], []);
            }

            using (hold)
                return Refresh(check, checkStartedUtc, ct);
        });
    }

    Task<CloudFeedRefresh> ICloudFeedService.RefreshAsync(CloudFeedCheck check, DateTimeOffset checkStartedUtc,
                                                         CancellationToken ct) =>
        RefreshAsync(check, checkStartedUtc, ct);

    // The saved feed is read inside the lock, so a Remove, retire or page save before it is seen
    private CloudFeedRefresh Refresh(CloudFeedCheck check, DateTimeOffset checkStartedUtc, CancellationToken ct)
    {
        SavedFeedsRead read = _ipinfo.Records.ReadSavedFeeds();
        if (!read.Available)
            return NotRefreshed(CloudRefreshOutcome.SavedFeedsUnavailable);

        SavedFeed? stored = read.Feeds.FirstOrDefault(feed =>
            feed.Kind == check.Pick.Kind && string.Equals(feed.Key, check.Description.FileStem, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
            return NotRefreshed(CloudRefreshOutcome.Missing);
        if (stored.Retired)
            return NotRefreshed(CloudRefreshOutcome.Retired);
        // The store kept an older pick than the files hold, so writing it would undo the page's write
        if (_ipinfo.Records.SaveRefusedThisSession(stored.Key))
            return NotRefreshed(CloudRefreshOutcome.SaveRefused);
        if (!SameSelection(stored.Pick, check.Pick))
            return NotRefreshed(CloudRefreshOutcome.Superseded);
        // A page write since the check started holds a newer source; stored to the second, so a tie counts as newer.
        // A write time later than now was stored while the clock ran ahead and can't be ordered, so it doesn't stop it.
        if (stored.LastWriteUtc >= DateTimeOffset.FromUnixTimeSeconds(checkStartedUtc.ToUnixTimeSeconds())
            && stored.LastWriteUtc <= _time.GetUtcNow())
            return NotRefreshed(CloudRefreshOutcome.Superseded);

        RefreshPicked picked = RefreshPick.Apply(check, stored.Pick);
        CloudFeedPlan plan = PlanWrite(check, picked.Ticked);
        UserSettings settings = _ipinfo.Settings;
        // The check's large flags used the switch of its time, so the pick's large answers are offered whatever it said
        var includedLarge = new HashSet<string>(stored.Pick.LargeIncluded, StringComparer.Ordinal);
        List<CidrBlock> allowedLarge = picked.Ticked.Where(block => includedLarge.Contains(block.Text)).ToList();
        var refresh = new RefreshWrite(stored);
        CloudFeedWriteResult result = Write(plan, allowedLarge, settings, FeedOptions.ForCloudFeed(settings), ct, refresh);
        DateTimeOffset nowUtc = _time.GetUtcNow();

        // Only the write's own filter reads the live large-range switch, so it alone says which ranges it left out
        var answered = new HashSet<string>(stored.Pick.LargeLeftOut, StringComparer.Ordinal);
        answered.UnionWith(includedLarge);
        List<string> newLarge = (result.LeftOutLargeRanges ?? []).Where(answered.Add).ToList();
        (CloudRefreshOutcome outcome, SavedFeedStamp? stamp) = result.Failure switch
        {
            CloudFeedWriteFailure.None when refresh.Skipped =>
                (CloudRefreshOutcome.Unchanged, new SavedFeedStamp(stored, null, stored.Proof, nowUtc)),
            CloudFeedWriteFailure.NothingToWrite =>
                (CloudRefreshOutcome.Unchanged, new SavedFeedStamp(stored, null, stored.Proof, nowUtc)),
            CloudFeedWriteFailure.None => (CloudRefreshOutcome.Written, new SavedFeedStamp(stored, null, refresh.Proof, nowUtc)),
            // A file moved, so the stored proof no longer describes the files
            CloudFeedWriteFailure.WriteFailed when result.FeedChanges is not null =>
                (CloudRefreshOutcome.Failed, new SavedFeedStamp(stored, null, null, null)),
            _ => (CloudRefreshOutcome.Failed, (SavedFeedStamp?)null),
        };
        return new CloudFeedRefresh(outcome, refresh.Skipped ? null : result, stamp, picked.NewUnconfirmed, newLarge);
    }

    private static CloudFeedRefresh NotRefreshed(CloudRefreshOutcome outcome) => new(outcome, null, null, [], []);

    private static bool SameSelection(SavedFeedPick saved, CloudFeedPick pick) =>
        string.Equals(saved.GoogleScope, pick.GoogleScope, StringComparison.Ordinal)
        && saved.GoogleAddsGlobal == pick.GoogleAddsGlobal
        && string.Equals(saved.AzureTag, pick.AzureTag, StringComparison.Ordinal)
        && string.Equals(saved.AwsService, pick.AwsService, StringComparison.Ordinal)
        && string.Equals(saved.AwsRegion, pick.AwsRegion, StringComparison.Ordinal);

    internal CloudFeedWriteResult Write(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                        UserSettings settings, FeedOptions options, CancellationToken ct,
                                        RefreshWrite? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(includedLargeRanges);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        if (options.Types.Count == 0)
            return new CloudFeedWriteResult(CloudFeedWriteFailure.NoFeedType, [], 0, 0, 0);

        if (!plan.Check.MatchesWriteSettings(settings))
            return new CloudFeedWriteResult(CloudFeedWriteFailure.SettingsChanged, [], 0, 0, 0);

        ReadableExceptions? exceptions = IpInfoService.ExceptionsForFeed(_ipinfo);
        if (exceptions is null)
            return new CloudFeedWriteResult(CloudFeedWriteFailure.ExceptionsUnreadable, [], 0, 0, 0);

        var allowedLarge = new HashSet<string>(StringComparer.Ordinal);
        foreach (CidrBlock block in includedLargeRanges)
            allowedLarge.Add(block.Text);

        var toWrite = new List<CheckedRange>();
        var leftOut = new List<string>();
        foreach (CheckedRange range in plan.Included)
        {
            bool large = LargeRangePolicy.IsLarge(range.Prefix.Block.Text, settings);
            if (large && !allowedLarge.Contains(range.Prefix.Block.Text))
            {
                leftOut.Add(range.Prefix.Block.Text);
                continue;
            }

            toWrite.Add(range);
        }

        // Each piece of a cut range takes the range's place and its status, so the provider's order holds
        var lines = new List<FeedLine>(toWrite.Count);
        var tally = new ExceptionTally();
        int ipv4Whole = 0;
        int ipv6Whole = 0;
        foreach (CheckedRange range in toWrite)
        {
            CidrBlock block = range.Prefix.Block;
            bool confirmed = range.Status == RangeConfirmation.Confirmed;
            // A range covering every address is never written, only counted in its family's header
            if (ExportWriter.CoversEveryAddress(block.Start, block.End))
            {
                if (block.IsIpv6) ipv6Whole++; else ipv4Whole++;
                continue;
            }

            IReadOnlyList<string>? pieces = exceptions.Cut(block.Start, block.End);
            tally.Count(block.IsIpv6, pieces);
            if (pieces is null)
            {
                lines.Add(new FeedLine(block.Text, block.IsIpv6, confirmed));
                continue;
            }

            foreach (string piece in pieces)
                lines.Add(new FeedLine(piece, block.IsIpv6, confirmed));
        }

        IReadOnlyList<string>? leftOutNames = leftOut.Count > 0 ? leftOut : null;
        int wholeLeftOut = ipv4Whole + ipv6Whole;
        string folder = ExportFolders.Resolve(settings, _defaultExportFolder);
        DateTimeOffset nowUtc = _time.GetUtcNow();
        IReadOnlyList<PlannedFeedFile> planned = CloudFeedWriter.Plan(folder, options, plan.Check, lines, tally, ipv4Whole,
                                                                      ipv6Whole, nowUtc);
        if (planned.Count == 0)
        {
            return new CloudFeedWriteResult(CloudFeedWriteFailure.NothingToWrite, [], 0, 0, leftOut.Count, tally.Cut, tally.Removed,
                                            WholeRangesLeftOut: wholeLeftOut, LeftOutLargeRanges: leftOutNames);
        }

        if (refresh is not null && FeedSkip.IsUnchanged(folder, planned, SavedFeedWriteProof.FingerprintOf(options), refresh.Of.Proof))
        {
            refresh.Skipped = true;
            return new CloudFeedWriteResult(CloudFeedWriteFailure.None, [], 0, 0, leftOut.Count, tally.Cut, tally.Removed,
                                            WholeRangesLeftOut: wholeLeftOut, LeftOutLargeRanges: leftOutNames);
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError($"The export folder could not be created: {ex.Message}");
            return new CloudFeedWriteResult(CloudFeedWriteFailure.FolderUnavailable, [], 0, 0, leftOut.Count,
                                            LeftOutLargeRanges: leftOutNames);
        }

        var progress = new CloudFeedWriter.Progress();
        try
        {
            IReadOnlyList<string> files = CloudFeedWriter.Write(planned, progress);
            List<FeedFileChange> changes = progress.Changes.ToList();
            SavedFeedWriteProof? proof = SavedFeedWriteProof.Of(folder, options, changes);
            if (refresh is not null)
                refresh.Proof = proof;
            FeedRecordResult record = RecordWrite(plan, toWrite, leftOut, exceptions, settings, options, nowUtc, changes, proof,
                                                  refresh is not null);
            int unconfirmed = 0;
            foreach (FeedLine line in lines)
            {
                if (!line.Confirmed)
                    unconfirmed++;
            }

            return new CloudFeedWriteResult(CloudFeedWriteFailure.None, files.ToList(), lines.Count, unconfirmed, leftOut.Count, tally.Cut,
                                            tally.Removed, ModSecurityFiles: ModSecurityWritten(progress),
                                            WholeRangesLeftOut: wholeLeftOut, LeftOutLargeRanges: leftOutNames,
                                            FeedChanges: ChangesMade(progress), Record: record, EmptiedFiles: EmptiedWritten(progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The feed could not be written: {ex.Message}");
            List<string> oldFilesLeft = LeftByFailure(planned, progress.Attempting).ToList();
            FeedRecordResult? record = progress.Changes.Count > 0
                ? RecordWrite(plan, toWrite, leftOut, exceptions, settings, options, nowUtc, progress.Changes.ToList(), null,
                              refresh is not null)
                : null;
            return new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, progress.Written.ToList(), 0, 0, leftOut.Count,
                                            OldFilesLeft: oldFilesLeft, FailedFile: progress.Attempting,
                                            ModSecurityFiles: ModSecurityWritten(progress), LeftOutLargeRanges: leftOutNames,
                                            FeedChanges: ChangesMade(progress), Record: record);
        }
    }

    // Called with the feed lock held, so a Remove waiting for the lock is ordered after this save
    private FeedRecordResult RecordWrite(CloudFeedPlan plan, IReadOnlyList<CheckedRange> toWrite, IReadOnlyList<string> notChosen,
                                         ReadableExceptions exceptions, UserSettings settings, FeedOptions options,
                                         DateTimeOffset nowUtc, IReadOnlyList<FeedFileChange> changes,
                                         SavedFeedWriteProof? proof, bool isRefresh)
    {
        var written = new HashSet<CheckedRange>(toWrite);
        var ticked = new HashSet<CheckedRange>(plan.Included);
        var leftOutLarge = new HashSet<string>(notChosen, StringComparer.Ordinal);
        var source = new List<(byte[] Start, byte[] End)>(plan.Check.Ranges.Count);
        var leftOut = new List<(byte[] Start, byte[] End)>();
        var seen = new List<SeenRange>(plan.Check.Ranges.Count);
        foreach (CheckedRange range in plan.Check.Ranges)
        {
            CidrBlock block = range.Prefix.Block;
            source.Add((block.Start, block.End));
            bool isWritten = written.Contains(range);
            if (!isWritten || ExportWriter.CoversEveryAddress(block.Start, block.End))
                leftOut.Add((block.Start, block.End));
            bool answered = leftOutLarge.Contains(block.Text) || (isWritten && LargeRangePolicy.IsLarge(block.Text, settings));
            seen.Add(new SeenRange(block, ticked.Contains(range), range.StartsIncluded, isWritten, answered ? isWritten : null));
        }

        CloudFeedPick cloud = plan.Check.Pick;
        FeedFamilies? familiesOff = options.WritesIPv6 ? null : FeedFamilies.IPv6;
        string key = plan.Check.Description.FileStem;
        var reasons = new RemovalReasons(exceptions, source, leftOut, familiesOff, FeedRemovalReason.SourceDropped);
        // A refresh never saves a pick: its stamp is committed with every other feed's after the refresh
        SavedFeedIntent? intent = null;
        if (!isRefresh)
        {
            PickLists lists = SavedPickCarry.Lists(seen, SavedPickCarry.Prior(_ipinfo.Records, key, cloud.Kind), familiesOff);
            var pick = new SavedFeedPick(null, cloud.GoogleScope, cloud.GoogleAddsGlobal, cloud.AzureTag, cloud.AwsService,
                                         cloud.AwsRegion, lists.HandTicked, lists.HandUnticked, lists.LargeIncluded,
                                         lists.LargeLeftOut, null, null, null, lists.SeenUnconfirmed);
            intent = new SavedFeedIntent.Written(pick, proof);
        }

        var record = new FeedWriteRecord(key, cloud.Kind, isRefresh ? FeedChangeCause.RefreshAll : FeedChangeCause.PageWrite,
                                         nowUtc, options.Types, changes, reasons, plan.Check.Dataset.DownloadedOn, null, intent);
        return IpInfoService.RecordFeedWrite(_ipinfo, record);
    }

    private static List<string>? ModSecurityWritten(CloudFeedWriter.Progress progress) =>
        progress.ModSecurityWritten.Count > 0 ? progress.ModSecurityWritten.ToList() : null;

    private static List<string>? EmptiedWritten(CloudFeedWriter.Progress progress) =>
        progress.Emptied.Count > 0 ? progress.Emptied.ToList() : null;

    private static List<FeedFileChange>? ChangesMade(CloudFeedWriter.Progress progress) =>
        progress.Changes.Count > 0 ? progress.Changes.ToList() : null;

    // The failed file's earlier copy stays whole, as its move never ran, and no later file, emptied or not, is tried; a
    // type folder that can't be made fails the file being written. Named by File.Exists, never Path.Exists.
    private static IEnumerable<string> LeftByFailure(IReadOnlyList<PlannedFeedFile> planned, string? attempting)
    {
        bool reached = attempting is null;
        foreach (PlannedFeedFile file in planned)
        {
            reached |= file.Path == attempting;
            if (reached && File.Exists(file.Path))
                yield return file.Path;
        }
    }
}
