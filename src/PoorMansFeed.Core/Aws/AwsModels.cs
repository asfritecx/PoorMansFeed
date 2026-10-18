using PoorMansFeed.Core;

namespace PoorMansFeed.Core.Aws;

/// <summary>One range of the AWS file. BorderGroup is the network border group, which differs from the region for a Local Zone.</summary>
public sealed record AwsPrefix(CidrBlock Block, string BorderGroup);

/// <summary>One service's ranges in one region. Prefixes are unique by CIDR, IPv4 first, then by address.</summary>
public sealed class AwsRegionRanges
{
    public AwsRegionRanges(string region, IReadOnlyList<AwsPrefix> prefixes)
    {
        Region = region ?? throw new ArgumentNullException(nameof(region));
        Prefixes = prefixes ?? throw new ArgumentNullException(nameof(prefixes));

        foreach (AwsPrefix prefix in prefixes)
        {
            if (prefix.Block.IsIpv6)
                Ipv6Count++;
            else
                Ipv4Count++;
        }
    }

    /// <summary>The region id as the file writes it, or "GLOBAL".</summary>
    public string Region { get; }

    public IReadOnlyList<AwsPrefix> Prefixes { get; }

    public int Ipv4Count { get; }

    public int Ipv6Count { get; }

    public int CountFor(bool includeIPv6) => includeIPv6 ? Ipv4Count + Ipv6Count : Ipv4Count;
}

/// <summary>
/// One AWS service ("EC2", "S3"...). Regions are sorted by id. AllRegionsPrefixes is every region's ranges once each,
/// unique by CIDR, IPv4 first, then by address.
/// </summary>
public sealed class AwsService
{
    private readonly Dictionary<string, AwsRegionRanges> _byRegion = new(StringComparer.Ordinal);

    public AwsService(string name, IReadOnlyList<AwsRegionRanges> regions, IReadOnlyList<AwsPrefix> allRegionsPrefixes)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Regions = regions ?? throw new ArgumentNullException(nameof(regions));
        AllRegionsPrefixes = allRegionsPrefixes ?? throw new ArgumentNullException(nameof(allRegionsPrefixes));

        foreach (AwsRegionRanges region in regions)
            _byRegion[region.Region] = region;

        foreach (AwsPrefix prefix in allRegionsPrefixes)
        {
            if (prefix.Block.IsIpv6)
                Ipv6Count++;
            else
                Ipv4Count++;
        }
    }

    public string Name { get; }

    public IReadOnlyList<AwsRegionRanges> Regions { get; }

    public IReadOnlyList<AwsPrefix> AllRegionsPrefixes { get; }

    public int Ipv4Count { get; }

    public int Ipv6Count { get; }

    public int CountFor(bool includeIPv6) => includeIPv6 ? Ipv4Count + Ipv6Count : Ipv4Count;

    /// <summary>By exact region id.</summary>
    public AwsRegionRanges? Find(string? region) =>
        region is not null && _byRegion.TryGetValue(region, out AwsRegionRanges? found) ? found : null;
}

/// <summary>
/// Where the saved file came from, and the checks that tell whether the saved copy is whole. ETag is stored as the
/// server sent it (quotes and any weak prefix included), so it can be sent back unchanged. Null when the server sent none.
/// </summary>
public sealed record AwsFileSource(string SyncToken, DateTimeOffset CreatedUtc, string? ETag, DateTimeOffset DownloadedUtc,
                                   string Sha256, long Length);

/// <summary>Everything read from one ip-ranges.json. SkippedPrefixes counts the entries that could not be used.</summary>
public sealed class AwsRangeCatalogue
{
    private readonly Dictionary<string, AwsService> _byName = new(StringComparer.Ordinal);

    public AwsRangeCatalogue(AwsFileSource source, IReadOnlyList<AwsService> services, int skippedPrefixes)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        SkippedPrefixes = skippedPrefixes;
        foreach (AwsService service in services)
            _byName[service.Name] = service;
    }

    public AwsFileSource Source { get; }

    /// <summary>Sorted by name.</summary>
    public IReadOnlyList<AwsService> Services { get; }

    public int SkippedPrefixes { get; }

    /// <summary>By exact service name.</summary>
    public AwsService? Find(string? serviceName) =>
        serviceName is not null && _byName.TryGetValue(serviceName, out AwsService? service) ? service : null;

    /// <summary>The same ranges under a different source record.</summary>
    public AwsRangeCatalogue WithSource(AwsFileSource source) => new(source, Services, SkippedPrefixes);
}

/// <summary>The operator's pick: one service, and one region, or every region when Region is null.</summary>
public sealed record AwsSelection(string Service, string? Region);

public enum AwsUpdateStatus { UpToDate, Updated, CheckFailed, Unavailable }

public enum AwsSourceFailure { None, Offline, ServiceError, FileUnreadable }

/// <summary>
/// The outcome of looking for a newer file. Catalogue is null exactly when Status is Unavailable: there is no
/// saved file and none could be fetched. Failure is None unless Status is CheckFailed or Unavailable.
/// </summary>
public sealed record AwsLoadResult(AwsUpdateStatus Status, AwsSourceFailure Failure, AwsRangeCatalogue? Catalogue,
                                   DateTimeOffset CheckedAt);
