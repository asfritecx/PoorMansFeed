using System.Globalization;

namespace PoorMansFeed.Core;

/// <summary>
/// ModSecurity fails a whole IP list on a CR after an address, on an IPv4 /32 (v2.9) and on an IPv6 /128 (v3), so
/// every feed ends its lines with LF on every OS and writes a single host as its bare address.
/// </summary>
internal static class FeedText
{
    public const string LineEnd = "\n";

    // The entry as a feed line: a single host is its bare address, anything else is returned unchanged.
    public static string Entry(string network)
    {
        int slash = network.IndexOf('/');
        if (slash < 0)
            return network;

        ReadOnlySpan<char> address = network.AsSpan(0, slash);
        int hostPrefix = address.Contains(':') ? 128 : 32;
        return int.TryParse(network.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int prefix)
            && prefix == hostPrefix
            ? network[..slash]
            : network;
    }
}
