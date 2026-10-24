using PoorMansFeed.Core;

namespace PoorMansFeed.Core.Azure;

/// <summary>
/// One Azure service tag, such as "Storage" or "Storage.SoutheastAsia". Region is the file's own region id ("" for
/// global), and Suffix, the part after the dot, often differs from it. Prefixes are unique, IPv4 first, by address.
/// </summary>
public sealed class AzureServiceTag
{
    public AzureServiceTag(string name, string region, long changeNumber, IReadOnlyList<CidrBlock> prefixes)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Region = region ?? throw new ArgumentNullException(nameof(region));
        ChangeNumber = changeNumber;
        Prefixes = prefixes ?? throw new ArgumentNullException(nameof(prefixes));

        int dot = name.IndexOf('.');
        IsBase = dot < 0;
        BaseName = IsBase ? name : name[..dot];
        Suffix = IsBase ? "" : name[(dot + 1)..];

        foreach (CidrBlock block in prefixes)
        {
            if (block.IsIpv6)
                Ipv6Count++;
            else
                Ipv4Count++;
        }
    }

    public string Name { get; }

    public string BaseName { get; }

    public string Suffix { get; }

    public string Region { get; }

    /// <summary>True for the tag with no dot in its name, which covers every region of the service.</summary>
    public bool IsBase { get; }

    public long ChangeNumber { get; }

    public IReadOnlyList<CidrBlock> Prefixes { get; }

    public int Ipv4Count { get; }

    public int Ipv6Count { get; }

    public int CountFor(bool includeIPv6) => includeIPv6 ? Ipv4Count + Ipv6Count : Ipv4Count;
}

/// <summary>Every tag that shares a base name. The base tag, when there is one, comes first.</summary>
public sealed record AzureServiceGroup(string BaseName, IReadOnlyList<AzureServiceTag> Tags);

/// <summary>The file the download page offers now.</summary>
public sealed record AzurePublishedFile(Uri Url, string FileName, DateOnly FileDate);

/// <summary>Where the cached file came from, and the checks that tell whether the cached copy is whole.</summary>
public sealed record AzureTagFileSource(Uri Url, string FileName, DateOnly FileDate, long ChangeNumber, string Cloud,
                                        DateTimeOffset DownloadedUtc, string Sha256, long Length);

/// <summary>Everything read from one service tags file. SkippedTags and SkippedPrefixes count what could not be used.</summary>
public sealed class AzureTagCatalogue
{
    private readonly Dictionary<string, AzureServiceTag> _byName = new(StringComparer.Ordinal);

    public AzureTagCatalogue(AzureTagFileSource source, IReadOnlyList<AzureServiceGroup> services, int skippedTags, int skippedPrefixes)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        SkippedTags = skippedTags;
        SkippedPrefixes = skippedPrefixes;
        foreach (AzureServiceGroup group in services)
        {
            foreach (AzureServiceTag tag in group.Tags)
                _byName[tag.Name] = tag;
        }
    }

    public AzureTagFileSource Source { get; }

    /// <summary>By base name, ignoring case.</summary>
    public IReadOnlyList<AzureServiceGroup> Services { get; }

    public int SkippedTags { get; }

    public int SkippedPrefixes { get; }

    public AzureServiceTag? Find(string? tagName) =>
        tagName is not null && _byName.TryGetValue(tagName, out AzureServiceTag? tag) ? tag : null;
}

/// <summary>The operator's pick: exactly one service tag.</summary>
public sealed record AzureTagSelection(string TagName);

public enum AzureUpdateStatus { UpToDate, Updated, CheckFailed, Unavailable }

public enum AzureSourceFailure { None, Offline, ServiceError, PageUnreadable, FileUnreadable }

/// <summary>
/// The outcome of looking for a newer file. Catalogue is null exactly when Status is Unavailable: there is no
/// cached file and none could be fetched. Failure is None unless Status is CheckFailed or Unavailable.
/// </summary>
public sealed record AzureLoadResult(AzureUpdateStatus Status, AzureSourceFailure Failure, AzureTagCatalogue? Catalogue,
                                     DateTimeOffset CheckedAt);
