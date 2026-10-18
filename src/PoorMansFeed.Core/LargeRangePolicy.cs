using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace PoorMansFeed.Core;

public static class LargeRangePolicy
{
    public const int DefaultIPv4MinPrefix = 16;
    public const int DefaultIPv6MinPrefix = 32;

    /// <summary>
    /// Parses "a.b.c.d/nn" or "x::/nn". Returns false for a blank value, a bare IP (no prefix), a bad prefix or an
    /// address not in its plain form (IpAddressText).
    /// </summary>
    public static bool TryDescribe(string? network, out LargeRange range)
    {
        range = default!;
        string text = network?.Trim() ?? "";
        if (text.Length == 0)
            return false;

        int slash = text.IndexOf('/');
        if (slash < 0 || text.IndexOf('/', slash + 1) >= 0)
            return false;

        string addressText = text[..slash];
        string prefixText = text[(slash + 1)..];
        if (!IpAddressText.TryParse(addressText, out IPAddress? address))
            return false;

        bool isIpv6 = text.Contains(':');
        if (isIpv6 != (address.AddressFamily == AddressFamily.InterNetworkV6))
            return false;

        int bits = isIpv6 ? 128 : 32;
        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
            || prefix < 0 || prefix > bits)
            return false;

        BigInteger addressCount = BigInteger.One << (bits - prefix);
        range = new LargeRange(text, isIpv6, prefix, addressCount);
        return true;
    }

    /// <summary>
    /// True when the check is on and the network is broader than the configured threshold. A /16 is not large
    /// at the default IPv4 threshold; /15 is.
    /// </summary>
    public static bool IsLarge(string? network, UserSettings settings)
    {
        if (!settings.LargeRangeCheckEnabled)
            return false;

        return TryDescribe(network, out LargeRange range) && IsLarge(range, settings);
    }

    /// <summary>Returns the large ranges among the rows, in input order. Empty when the check is off.</summary>
    public static List<LargeRange> Find(IEnumerable<NetworkRecord> rows, UserSettings settings)
    {
        var found = new List<LargeRange>();
        if (!settings.LargeRangeCheckEnabled)
            return found;

        foreach (NetworkRecord row in rows)
        {
            if (TryDescribe(row.Network, out LargeRange range) && IsLarge(range, settings))
                found.Add(range);
        }
        return found;
    }

    private static bool IsLarge(LargeRange range, UserSettings settings)
    {
        int threshold = range.IsIpv6 ? settings.IPv6MinPrefix : settings.IPv4MinPrefix;
        return range.PrefixLength < threshold;
    }
}
