using System.Diagnostics;
using PoorMansFeed.Core;

namespace PoorMansFeed.Core.CloudFeeds;

/// <summary>
/// Confirms a provider's ranges against the dataset and writes the Fortigate feed. The provider's service
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
    /// Drops IPv6 when IPv6Enabled is off, confirms each remaining range against the dataset and classifies it
    /// by the description's domain. The dataset is asked even for an empty list, so NoDataset wins over
    /// NothingSelected.
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
        if (!plan.Check.MatchesWriteSettings(settings))
            return new CloudFeedWriteResult(CloudFeedWriteFailure.SettingsChanged, [], 0, 0, 0);

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

        if (toWrite.Count == 0)
            return new CloudFeedWriteResult(CloudFeedWriteFailure.NothingToWrite, [], 0, 0, leftOut);

        string folder = ExportFolders.Resolve(settings, _defaultExportFolder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError($"The export folder could not be created: {ex.Message}");
            return new CloudFeedWriteResult(CloudFeedWriteFailure.FolderUnavailable, [], 0, 0, leftOut);
        }

        try
        {
            IReadOnlyList<string> files = CloudFeedWriter.Write(folder, plan.Check, toWrite, _time.GetUtcNow());
            int unconfirmed = 0;
            foreach (CheckedRange range in toWrite)
            {
                if (range.Status != RangeConfirmation.Confirmed)
                    unconfirmed++;
            }

            return new CloudFeedWriteResult(CloudFeedWriteFailure.None, files, toWrite.Count, unconfirmed, leftOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"The feed could not be written: {ex.Message}");
            return new CloudFeedWriteResult(CloudFeedWriteFailure.WriteFailed, [], 0, 0, leftOut);
        }
    }
}
