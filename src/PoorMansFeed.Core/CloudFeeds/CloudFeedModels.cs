using System.Diagnostics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core.CloudFeeds;

/// <summary>One range of a cloud feed. Scope is the provider's region label, shown in the list; "global" has no region.</summary>
public sealed record FeedPrefix(CidrBlock Block, string Scope);

/// <summary>
/// What makes one provider's feed different from another's: the domains that confirm a range, the header lines
/// written after the title, and the file name (without the family suffix). Built by the provider's service.
/// </summary>
public sealed record FeedDescription
{
    public FeedDescription(string confirmDomain, IReadOnlyList<string> headerLines, string fileStem)
        : this([confirmDomain], headerLines, fileStem)
    {
    }

    /// <summary>
    /// A provider whose networks sit under more than one IPinfo domain. A range is confirmed when every
    /// overlapping network's domain is one of them.
    /// </summary>
    public FeedDescription(IReadOnlyList<string> confirmDomains, IReadOnlyList<string> headerLines, string fileStem)
    {
        ArgumentNullException.ThrowIfNull(confirmDomains);
        if (confirmDomains.Count == 0)
            throw new ArgumentException("At least one confirmation domain is required.", nameof(confirmDomains));

        var domains = new List<string>(confirmDomains.Count);
        foreach (string domain in confirmDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
                throw new ArgumentException("The confirmation domain is required.", nameof(confirmDomains));
            domains.Add(domain.Trim());
        }

        ArgumentNullException.ThrowIfNull(headerLines);
        foreach (string line in headerLines)
        {
            if (line is null || line.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException("A header line must be a single line of text.", nameof(headerLines));
        }

        if (!IsSafeStem(fileStem))
            throw new ArgumentException("The file stem may only hold letters, digits, '.', '_' and '-'.", nameof(fileStem));

        ConfirmDomains = domains;
        HeaderLines = headerLines.ToArray();
        FileStem = fileStem;
    }

    /// <summary>The first confirmation domain.</summary>
    public string ConfirmDomain => ConfirmDomains[0];

    /// <summary>Every domain that confirms a range, trimmed. Never empty.</summary>
    public IReadOnlyList<string> ConfirmDomains { get; }

    /// <summary>Written as "# {line}" after the title line.</summary>
    public IReadOnlyList<string> HeaderLines { get; }

    /// <summary>The feed file is {FileStem}_ipv4.txt or {FileStem}_ipv6.txt.</summary>
    public string FileStem { get; }

    internal static bool IsSafeStem(string? stem)
    {
        if (string.IsNullOrEmpty(stem) || stem.AsSpan().Trim('.').IsEmpty)
            return false;

        foreach (char character in stem)
        {
            bool allowed = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!allowed)
                return false;
        }

        return true;
    }
}

/// <summary>
/// The values that name a cloud feed's pick: only its kind's own fields are set, and a null AwsRegion is every region.
/// </summary>
public sealed record CloudFeedPick(FeedKind Kind, string? GoogleScope, bool GoogleAddsGlobal, string? AzureTag,
                                   string? AwsService, string? AwsRegion);

public enum RangeConfirmation { Confirmed, Mixed, NotInDatabase }

/// <summary>One range after confirmation. Constructed only inside Core.</summary>
public sealed class CheckedRange
{
    internal CheckedRange(FeedPrefix prefix, RangeConfirmation status, IReadOnlyList<OverlapGroup> overlaps, bool isLarge)
    {
        Prefix = prefix;
        Status = status;
        Overlaps = overlaps;
        IsLarge = isLarge;
    }

    public FeedPrefix Prefix { get; }

    public RangeConfirmation Status { get; }

    public IReadOnlyList<OverlapGroup> Overlaps { get; }

    public bool IsLarge { get; }

    public bool StartsIncluded => Status == RangeConfirmation.Confirmed;
}

/// <summary>
/// A completed confirmation. It exists only when the dataset answered. Constructed only inside Core.
/// Ranges are in the order the provider's service asked for them.
/// </summary>
public sealed class CloudFeedCheck
{
    internal CloudFeedCheck(FeedDescription description, DatasetInfo dataset,
                            bool includesIPv6, int ipv4MinPrefix, int ipv6MinPrefix, IReadOnlyList<CheckedRange> ranges,
                            CloudFeedPick pick)
    {
        Description = description;
        Dataset = dataset;
        IncludesIPv6 = includesIPv6;
        IPv4MinPrefix = ipv4MinPrefix;
        IPv6MinPrefix = ipv6MinPrefix;
        Ranges = ranges;
        Pick = pick;
    }

    public FeedDescription Description { get; }

    public CloudFeedPick Pick { get; }

    public DatasetInfo Dataset { get; }

    /// <summary>The IPv6Enabled setting at check time. IPv6 prefixes were dropped before the query when this is false.</summary>
    public bool IncludesIPv6 { get; }

    public int IPv4MinPrefix { get; }

    public int IPv6MinPrefix { get; }

    public IReadOnlyList<CheckedRange> Ranges { get; }

    public int Count(RangeConfirmation status)
    {
        int count = 0;
        foreach (CheckedRange range in Ranges)
        {
            if (range.Status == status)
                count++;
        }

        return count;
    }

    public bool MatchesWriteSettings(UserSettings settings) =>
        settings.IPv6Enabled == IncludesIPv6
        && settings.IPv4MinPrefix == IPv4MinPrefix
        && settings.IPv6MinPrefix == IPv6MinPrefix;
}

public enum CheckFailure { None, NoDataset, NothingSelected, Failed, PickGone }

/// <summary>Check is non-null exactly when Failure is None.</summary>
public sealed record CheckResult(CheckFailure Failure, CloudFeedCheck? Check);

/// <summary>
/// The ranges the operator ticked, ready for the large-range dialog. Constructed only inside Core.
/// LargeRanges: the ticked ranges past the threshold, in list order; the dialog starts each as the saved feed has it.
/// </summary>
public sealed class CloudFeedPlan
{
    internal CloudFeedPlan(CloudFeedCheck check, IReadOnlyList<CheckedRange> included, IReadOnlyList<LargeRange> largeRanges,
                           IReadOnlyList<string> handTicked, IReadOnlyList<string> handUnticked)
    {
        Check = check;
        Included = included;
        LargeRanges = largeRanges;
        HandTicked = handTicked;
        HandUnticked = handUnticked;
    }

    public CloudFeedCheck Check { get; }

    public IReadOnlyList<CheckedRange> Included { get; }

    public IReadOnlyList<LargeRange> LargeRanges { get; }

    /// <summary>The ticked ranges that don't start ticked, in check order.</summary>
    public IReadOnlyList<string> HandTicked { get; }

    /// <summary>The confirmed ranges left unticked, in check order.</summary>
    public IReadOnlyList<string> HandUnticked { get; }
}

public enum CloudFeedWriteFailure { None, NothingToWrite, FolderUnavailable, WriteFailed, SettingsChanged, ExceptionsUnreadable, NoFeedType }

/// <summary>
/// RangesWritten counts one copy's lines. Files, FeedChanges and EmptiedFiles (rewritten header-only, on success only)
/// follow the write order, the last two and Record null when none; OldFilesLeft and FailedFile name a failure's files.
/// </summary>
public sealed record CloudFeedWriteResult(CloudFeedWriteFailure Failure, IReadOnlyList<string> Files, int RangesWritten,
                                          int UnconfirmedWritten, int LargeRangesLeftOut,
                                          int ExceptionCuts = 0, int ExceptionRemovals = 0,
                                          IReadOnlyList<string>? OldFilesLeft = null, string? FailedFile = null,
                                          IReadOnlyList<string>? ModSecurityFiles = null, int WholeRangesLeftOut = 0,
                                          IReadOnlyList<string>? LeftOutLargeRanges = null,
                                          IReadOnlyList<FeedFileChange>? FeedChanges = null,
                                          FeedRecordResult? Record = null, IReadOnlyList<string>? EmptiedFiles = null);

public enum CloudRefreshOutcome { Unchanged, Written, Missing, Retired, SaveRefused, Superseded, SavedFeedsUnavailable, Failed }

/// <summary>
/// One saved cloud feed's refresh. Stamp is what the caller commits with the other feeds'; NewUnconfirmed and NewLarge
/// list the ranges the saved pick has never answered, which the refresh left out.
/// </summary>
public sealed record CloudFeedRefresh(CloudRefreshOutcome Outcome, CloudFeedWriteResult? Write, SavedFeedStamp? Stamp,
                                      IReadOnlyList<string> NewUnconfirmed, IReadOnlyList<string> NewLarge);

/// <summary>
/// NotSaved: no saved file at all. Unusable: a file is there, but the pair is incomplete, damaged or unreadable.
/// </summary>
public enum SavedRangesState { Read, NotSaved, Unusable }

/// <summary>Catalogue and SavedUtc (the record's download time) are set exactly when State is Read.</summary>
public sealed record SavedRanges<T>(SavedRangesState State, T? Catalogue, DateTimeOffset? SavedUtc) where T : class;

/// <summary>Raised once a call has replaced the saved source file with other content.</summary>
public sealed record CloudSourceUpdated<TCatalogue>(TCatalogue Catalogue, DateTimeOffset UpdatedUtc) where TCatalogue : class;

internal static class CloudSourceEvents
{
    // Each listener on its own, so one that throws neither costs the caller its typed result nor silences the rest
    public static void Tell<TCatalogue>(object sender, EventHandler<CloudSourceUpdated<TCatalogue>>? handler,
                                        CloudSourceUpdated<TCatalogue> update) where TCatalogue : class
    {
        if (handler is null)
            return;
        foreach (EventHandler<CloudSourceUpdated<TCatalogue>> listener
                 in handler.GetInvocationList().Cast<EventHandler<CloudSourceUpdated<TCatalogue>>>())
        {
            try
            {
                listener(sender, update);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"A cloud source listener failed ({ex.GetType().FullName}).");
            }
        }
    }
}

/// <summary>One line of a cloud feed: a ticked range, or a piece of one an exception cut, with the range's status.</summary>
internal readonly record struct FeedLine(string Text, bool IsIpv6, bool Confirmed);
