using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// What the offending networks are made of: the block each listed address is looked up as, the ASN text IPinfo
/// stores, and the grouping of what IPinfo holds for each address. The lookup itself is the service's.
/// </summary>
internal static class AbuseIpDbNetworks
{
    /// <summary>The IPv4 blocks of one lookup: about 80 to 117 ms of the database gate on the real dataset.</summary>
    public const int IPv4ChunkSize = 1000;

    /// <summary>The IPv6 blocks of one lookup, which take longer each than IPv4 ones.</summary>
    public const int IPv6ChunkSize = 250;

    /// <summary>
    /// The /32 or /128 block of an address, from its text and the prefix of its family. The family comes from the
    /// number of bytes, never from the text.
    /// </summary>
    public static bool TryBlockOf(PublicAddress address, [NotNullWhen(true)] out CidrBlock? block)
    {
        block = null;
        if (address is null)
            return false;

        int length = address.Bytes.Length;
        int prefix = length switch { 4 => 32, 16 => 128, _ => -1 };
        if (prefix < 0
            || !CidrBlock.TryParse(address.Text + "/" + prefix.ToString(CultureInfo.InvariantCulture), out CidrBlock? parsed)
            || parsed.IsIpv6 != (length == 16))
        {
            return false;
        }

        block = parsed;
        return true;
    }

    /// <summary>
    /// IPinfo's ASN text, such as "AS15169": trimmed, an optional "AS" in any case, then digits only that fit a
    /// <see cref="uint"/>. Null, blank or anything else is false.
    /// </summary>
    public static bool TryParseAsn(string? text, out uint asn)
    {
        asn = 0;
        string value = text?.Trim() ?? "";
        if (value.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
            value = value[2..];

        return value.Length > 0 && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out asn);
    }

    /// <summary>
    /// Groups what IPinfo holds for each address (one list of groups per address, in any order) by ASN. An address
    /// with no group is not in IPinfo; one whose groups have no ASN that reads goes in the no-ASN bucket; one under
    /// several ASNs is counted under each.
    /// </summary>
    public static AbuseNetworksGrouping Group(IReadOnlyList<IReadOnlyList<OverlapGroup>> perAddress)
    {
        ArgumentNullException.ThrowIfNull(perAddress);
        var networks = new Dictionary<uint, NetworkTally>();
        var noAsnCountries = new Dictionary<string, int>(StringComparer.Ordinal);
        int noAsn = 0;
        int notInIpinfo = 0;
        int multiAsn = 0;
        int unreadableAsns = 0;

        foreach (IReadOnlyList<OverlapGroup> groups in perAddress)
        {
            if (groups.Count == 0)
            {
                notInIpinfo++;
                continue;
            }

            // The groups of each ASN the address is under, so each counts it once
            var byAsn = new Dictionary<uint, List<OverlapGroup>>();
            foreach (OverlapGroup group in groups)
            {
                if (TryParseAsn(group.Asn, out uint asn))
                {
                    if (!byAsn.TryGetValue(asn, out List<OverlapGroup>? own))
                        byAsn[asn] = own = [];
                    own.Add(group);
                }
                else if (!string.IsNullOrWhiteSpace(group.Asn))
                {
                    unreadableAsns++;
                }
            }

            if (byAsn.Count == 0)
            {
                noAsn++;
                CountOnce(noAsnCountries, groups.Select(group => group.CountryCode));
                continue;
            }

            if (byAsn.Count > 1)
                multiAsn++;

            foreach ((uint asn, List<OverlapGroup> own) in byAsn)
            {
                if (!networks.TryGetValue(asn, out NetworkTally? tally))
                    networks[asn] = tally = new NetworkTally();
                tally.Addresses++;
                CountOnce(tally.Names, own.Select(group => group.AsName));
                CountOnce(tally.Domains, own.Select(group => group.AsDomain));
                CountOnce(tally.Countries, own.Select(group => group.CountryCode));
            }
        }

        AbuseNetworkGroup[] sorted = networks
            .Select(pair => new AbuseNetworkGroup(pair.Key, MostCommon(pair.Value.Names), MostCommon(pair.Value.Domains),
                                                  pair.Value.Addresses, Sorted(pair.Value.Countries)))
            .OrderByDescending(network => network.Addresses)
            .ThenBy(network => network.Asn)
            .ToArray();
        return new AbuseNetworksGrouping(sorted, noAsn, Sorted(noAsnCountries), notInIpinfo, multiAsn, unreadableAsns);
    }

    /// <summary>Counts one address once under each distinct trimmed value it has; blank values count nowhere.</summary>
    private static void CountOnce(Dictionary<string, int> counts, IEnumerable<string?> values)
    {
        IEnumerable<string> distinct = values
            .Select(value => value?.Trim())
            .OfType<string>()
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal);
        foreach (string value in distinct)
            counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    /// <summary>The value carried by the most addresses, ties to the ordinal-first; null when there is none.</summary>
    private static string? MostCommon(Dictionary<string, int> counts) =>
        counts.Count == 0
            ? null
            : counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

    /// <summary>The countries by address count, most first, then by code in ordinal order.</summary>
    private static AbuseCountryCount[] Sorted(Dictionary<string, int> counts) =>
        counts.OrderByDescending(pair => pair.Value)
              .ThenBy(pair => pair.Key, StringComparer.Ordinal)
              .Select(pair => new AbuseCountryCount(pair.Key, pair.Value))
              .ToArray();

    private sealed class NetworkTally
    {
        public int Addresses { get; set; }

        public Dictionary<string, int> Names { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Domains { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Countries { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// The grouping of the looked-up addresses. <see cref="MultiAsnAddresses"/> counts the addresses under more than one
/// ASN, and <see cref="UnreadableAsns"/> the groups whose ASN text isn't blank and doesn't read; both are traced only.
/// </summary>
internal sealed record AbuseNetworksGrouping(IReadOnlyList<AbuseNetworkGroup> Networks, int NoAsn,
                                             IReadOnlyList<AbuseCountryCount> NoAsnCountries, int NotInIpinfo,
                                             int MultiAsnAddresses, int UnreadableAsns);
