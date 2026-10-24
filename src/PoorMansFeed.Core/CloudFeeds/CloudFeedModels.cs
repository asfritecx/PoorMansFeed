using PoorMansFeed.Core;

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

    private static bool IsSafeStem(string? stem)
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
                            bool includesIPv6, int ipv4MinPrefix, int ipv6MinPrefix, IReadOnlyList<CheckedRange> ranges)
    {
        Description = description;
        Dataset = dataset;
        IncludesIPv6 = includesIPv6;
        IPv4MinPrefix = ipv4MinPrefix;
        IPv6MinPrefix = ipv6MinPrefix;
        Ranges = ranges;
    }

    public FeedDescription Description { get; }

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

public enum CheckFailure { None, NoDataset, NothingSelected, Failed }

/// <summary>Check is non-null exactly when Failure is None.</summary>
public sealed record CheckResult(CheckFailure Failure, CloudFeedCheck? Check);

/// <summary>
/// The ranges the operator ticked, ready for the large-range dialog. Constructed only inside Core.
/// LargeRanges lists every ticked range broader than the threshold, in list order. The dialog starts them unticked.
/// </summary>
public sealed class CloudFeedPlan
{
    internal CloudFeedPlan(CloudFeedCheck check, IReadOnlyList<CheckedRange> included, IReadOnlyList<LargeRange> largeRanges)
    {
        Check = check;
        Included = included;
        LargeRanges = largeRanges;
    }

    public CloudFeedCheck Check { get; }

    public IReadOnlyList<CheckedRange> Included { get; }

    public IReadOnlyList<LargeRange> LargeRanges { get; }
}

public enum CloudFeedWriteFailure { None, NothingToWrite, FolderUnavailable, WriteFailed, SettingsChanged, ExceptionsUnreadable, NoFeedType }

/// <summary>
/// RangesWritten counts one copy's lines, so a cut range counts once per piece. Files holds every type's files in write
/// order; OldFilesLeft (empty or null for none) names old files left in place; FailedFile is null unless one failed.
/// </summary>
public sealed record CloudFeedWriteResult(CloudFeedWriteFailure Failure, IReadOnlyList<string> Files, int RangesWritten,
                                          int UnconfirmedWritten, int LargeRangesLeftOut,
                                          int ExceptionCuts = 0, int ExceptionRemovals = 0,
                                          IReadOnlyList<string>? OldFilesLeft = null, string? FailedFile = null,
                                          IReadOnlyList<string>? ModSecurityFiles = null, int WholeRangesLeftOut = 0);

/// <summary>One line of a cloud feed: a ticked range, or a piece of one an exception cut, with the range's status.</summary>
internal readonly record struct FeedLine(string Text, bool IsIpv6, bool Confirmed);
