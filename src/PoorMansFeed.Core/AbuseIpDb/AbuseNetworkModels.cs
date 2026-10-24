namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// Why the offending networks couldn't be found. <see cref="DatasetChanged"/>: the dataset or the IPv6 switch changed
/// during the lookup twice running. <see cref="ClockWrong"/>: this clock is too far behind the last download.
/// </summary>
public enum AbuseNetworksFailure
{
    None,
    ListUnreadable,
    ListNewer,
    StorageError,
    NoDownload,
    NoDataset,
    LookupFailed,
    DatasetChanged,
    ClockWrong,
}

/// <summary>An IPinfo country code and how many of the listed addresses IPinfo puts in it.</summary>
public sealed record AbuseCountryCount(string CountryCode, int Addresses);

/// <summary>
/// <see cref="AsName"/> and <see cref="AsDomain"/> are the values most of the group's addresses carry, null when
/// IPinfo gives none. <see cref="Countries"/> are IPinfo's, never AbuseIPDB's, the most addresses first.
/// </summary>
public sealed record AbuseNetworkGroup(uint Asn, string? AsName, string? AsDomain, int Addresses,
                                       IReadOnlyList<AbuseCountryCount> Countries);

/// <summary>
/// A report, not the feed: no exceptions apply, so its counts can differ from the written feed. A failure carries no
/// groups and zero counts; an unexpected one also claims nothing read (<see cref="KeepDays"/> 0, no dataset).
/// </summary>
public sealed record AbuseNetworksResult(AbuseNetworksFailure Failure, IReadOnlyList<AbuseNetworkGroup> Networks,
                                         int NoAsn, IReadOnlyList<AbuseCountryCount> NoAsnCountries, int NotInIpinfo,
                                         int IPv4Addresses, int IPv6Addresses, bool IncludesIPv6, int KeepDays,
                                         DateTimeOffset? LastDownloadAt, DatasetInfo? Dataset, int LeftOut);

/// <summary>Addresses looked up so far of those being looked up. Never goes backwards, a restart included.</summary>
public readonly record struct GroupingProgress(int Done, int Total);
