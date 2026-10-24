using System.Diagnostics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

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
    public async Task<CheckResult> CheckAsync(FeedDescription description, IReadOnlyList<FeedPrefix> ordered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(ordered);
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
            ranges);
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
        foreach (CheckedRange range in check.Ranges)
        {
            if (wanted.Contains(range.Prefix.Block.Text))
                included.Add(range);
        }

        UserSettings settings = _ipinfo.Settings;
        var large = new List<LargeRange>();
        foreach (CheckedRange range in included)
        {
            if (LargeRangePolicy.IsLarge(range.Prefix.Block.Text, settings)
                && LargeRangePolicy.TryDescribe(range.Prefix.Block.Text, out LargeRange described))
            {
                large.Add(described);
            }
        }

        return new CloudFeedPlan(check, included, large);
    }

    public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(includedLargeRanges);
        return Task.Run(() => Write(plan, includedLargeRanges, ct));
    }

    private CloudFeedWriteResult Write(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UserSettings settings = _ipinfo.Settings;
        if (!settings.TicksAnyFeedType())
            return new CloudFeedWriteResult(CloudFeedWriteFailure.NoFeedType, [], 0, 0, 0);

        if (!plan.Check.MatchesWriteSettings(settings))
            return new CloudFeedWriteResult(CloudFeedWriteFailure.SettingsChanged, [], 0, 0, 0);

        // A feed must never be written as if there were no exceptions
        ExceptionList exceptions = _ipinfo.Exceptions;
        if (!exceptions.IsReadable)
            return new CloudFeedWriteResult(CloudFeedWriteFailure.ExceptionsUnreadable, [], 0, 0, 0);

        var allowedLarge = new HashSet<string>(StringComparer.Ordinal);
        foreach (CidrBlock block in includedLargeRanges)
            allowedLarge.Add(block.Text);

        var toWrite = new List<CheckedRange>();
        int leftOut = 0;
        foreach (CheckedRange range in plan.Included)
        {
            bool large = LargeRangePolicy.IsLarge(range.Prefix.Block.Text, settings);
            if (large && !allowedLarge.Contains(range.Prefix.Block.Text))
            {
                leftOut++;
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
            // A range covering every address is never written; it stays in toWrite, so its family counts as planned
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

        IReadOnlyList<FeedType> types = FeedTypes.Ticked(settings);
        int wholeLeftOut = ipv4Whole + ipv6Whole;
        string folder = ExportFolders.Resolve(settings, _defaultExportFolder);
        List<string> oldFilesLeft = OldFilesLeft(folder, types, plan.Check, toWrite, lines);
        if (lines.Count == 0)
        {
            return new CloudFeedWriteResult(CloudFeedWriteFailure.NothingToWrite, [], 0, 0, leftOut, tally.Cut, tally.Removed, oldFilesLeft,
                                            WholeRangesLeftOut: wholeLeftOut);
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError($"The export folder could not be created: {ex.Message}");
            return new CloudFeedWriteResult(CloudFeedWriteFailure.FolderUnavailable, [], 0, 0, leftOut);
        }

        var progress = new CloudFeedWriter.Progress();
        try
        {
            IReadOnlyList<string> files = CloudFeedWriter.Write(folder, types, plan.Check, lines, tally, ipv4Whole, ipv6Whole,
                                                                _time.GetUtcNow(), progress);
            int unconfirmed = 0;
            foreach (FeedLine line in lines)
            {
                if (!line.Confirmed)
                    unconfirmed++;
            }

            return new CloudFeedWriteResult(CloudFeedWriteFailure.None, files.ToList(), lines.Count, unconfirmed, leftOut, tally.Cut,
                                            tally.Removed, oldFilesLeft, ModSecurityFiles: ModSecurityWritten(progress),
                                            WholeRangesLeftOut: wholeLeftOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The feed could not be written: {ex.Message}");
            oldFilesLeft.AddRange(LeftByFailure(folder, types, plan.Check, lines, progress.Attempting));
            return new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, progress.Written.ToList(), 0, 0, leftOut,
                                            OldFilesLeft: oldFilesLeft, FailedFile: progress.Attempting,
                                            ModSecurityFiles: ModSecurityWritten(progress));
        }
    }

    private static List<string>? ModSecurityWritten(CloudFeedWriter.Progress progress) =>
        progress.ModSecurityWritten.Count > 0 ? progress.ModSecurityWritten.ToList() : null;

    // The failed file's earlier copy stays whole, because its move never ran, and no later file is tried; a type folder
    // that can't be made fails the file being written. Untried files are named by File.Exists, never Path.Exists.
    private static IEnumerable<string> LeftByFailure(string folder, IReadOnlyList<FeedType> types, CloudFeedCheck check,
                                                     IReadOnlyList<FeedLine> lines, string? attempting)
    {
        bool reached = attempting is null;
        foreach ((FeedType type, bool ipv6) in CloudFeedWriter.WriteOrder(types))
        {
            if (!lines.Any(line => line.IsIpv6 == ipv6))
                continue;

            string path = CloudFeedWriter.PathFor(folder, type, check, ipv6);
            reached |= path == attempting;
            if (reached && File.Exists(path))
                yield return path;
        }
    }

    // A family the exceptions or the whole-range guard emptied gets no file, as one with no ranges never did. An old
    // file of a ticked type is not replaced, and its reader keeps using it, so the operator is told.
    private static List<string> OldFilesLeft(string folder, IReadOnlyList<FeedType> types, CloudFeedCheck check,
                                             IReadOnlyList<CheckedRange> toWrite, IReadOnlyList<FeedLine> lines)
    {
        var left = new List<string>();
        foreach ((FeedType type, bool ipv6) in CloudFeedWriter.WriteOrder(types))
        {
            bool hadRanges = toWrite.Any(range => range.Prefix.Block.IsIpv6 == ipv6);
            bool hasLines = lines.Any(line => line.IsIpv6 == ipv6);
            if (!hadRanges || hasLines)
                continue;

            string path = CloudFeedWriter.PathFor(folder, type, check, ipv6);
            if (File.Exists(path))
                left.Add(path);
        }

        return left;
    }
}
