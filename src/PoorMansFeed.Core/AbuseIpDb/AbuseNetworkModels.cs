namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// Why the offending networks couldn't be found. <see cref="None"/> means they were. <see cref="ListUnreadable"/>:
/// <c>abuseipdb.db</c> isn't a database, is damaged, or isn't this application's list. <see cref="ListNewer"/>: it was
/// written by a newer version. <see cref="StorageError"/>: it couldn't be read now. <see cref="NoDownload"/>: the list
/// has had no successful download (no file, a cleared list, or only failed attempts), so nothing was looked up.
/// <see cref="NoDataset"/>: there is no usable IPinfo dataset. <see cref="LookupFailed"/>: a lookup failed, answered
/// without a block it was asked about, or something unexpected failed. <see cref="DatasetChanged"/>: the dataset, or
/// the IPv6 switch, changed while the addresses were looked up, twice running. <see cref="ClockWrong"/>: this computer's
/// clock is more than <see cref="AbuseIpDbLimits.MaxClockDifferenceHours"/> hours behind the list's last download, so
/// nothing was looked up.
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
/// The listed addresses IPinfo puts under one ASN. <see cref="AsName"/> and <see cref="AsDomain"/> are the values
/// carried by the most of those addresses, and null when IPinfo gives none. <see cref="Countries"/> are IPinfo's
/// countries for those addresses, the most addresses first.
/// </summary>
public sealed record AbuseNetworkGroup(uint Asn, string? AsName, string? AsDomain, int Addresses,
                                       IReadOnlyList<AbuseCountryCount> Countries);

/// <summary>
/// The rolling list's addresses within the keep days, as IPinfo places them. <see cref="Networks"/> are grouped by ASN,
/// the most addresses first; <see cref="NoAsn"/> counts the addresses IPinfo holds with no ASN (their countries in
/// <see cref="NoAsnCountries"/>), and <see cref="NotInIpinfo"/> those it doesn't hold at all. An address under two
/// ASNs is counted under each. <see cref="IPv4Addresses"/> and <see cref="IPv6Addresses"/> count the addresses looked
/// up, at most <see cref="AbuseIpDbLimits.MaxFeedEntries"/> each, the newest by the feed's ranking, with no exceptions
/// applied, because this is a report: it isn't the written feed, which the exceptions cut. <see cref="LeftOut"/>
/// counts the addresses within the keep days the cap left out, both families together. The countries are IPinfo's,
/// never AbuseIPDB's. A failure carries no groups and zero counts, with
/// <see cref="IncludesIPv6"/>, <see cref="KeepDays"/> and <see cref="LastDownloadAt"/> as far as the list was read, and
/// no <see cref="Dataset"/>; an unexpected failure (<see cref="AbuseNetworksFailure.LookupFailed"/> from the service's
/// last catch) claims nothing read: <see cref="KeepDays"/> 0, <see cref="IncludesIPv6"/> false and
/// <see cref="LastDownloadAt"/> and <see cref="Dataset"/> null. <see cref="AbuseNetworksFailure.None"/> carries the
/// dataset that answered.
/// </summary>
public sealed record AbuseNetworksResult(AbuseNetworksFailure Failure, IReadOnlyList<AbuseNetworkGroup> Networks,
                                         int NoAsn, IReadOnlyList<AbuseCountryCount> NoAsnCountries, int NotInIpinfo,
                                         int IPv4Addresses, int IPv6Addresses, bool IncludesIPv6, int KeepDays,
                                         DateTimeOffset? LastDownloadAt, DatasetInfo? Dataset, int LeftOut);

/// <summary>Addresses looked up so far of those being looked up. Never goes backwards, a restart included.</summary>
public readonly record struct GroupingProgress(int Done, int Total);
