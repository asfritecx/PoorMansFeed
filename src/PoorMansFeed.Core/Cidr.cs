using System.Globalization;
using System.Net;

namespace PoorMansFeed.Core;

public static class Cidr
{
    /// <summary>
    /// Parses "a.b.c.d/nn" or an IPv6 CIDR. A bare address is treated as /32 or /128. The address must be in its plain
    /// form (IpAddressText) and the prefix digits only. The start address has its host bits cleared; the end address has them set.
    /// </summary>
    public static (byte[] Start, byte[] End) Parse(string network)
    {
        ArgumentNullException.ThrowIfNull(network);

        int slash = network.IndexOf('/');
        string addressText = slash < 0 ? network : network[..slash];

        if (!IpAddressText.TryParse(addressText, out IPAddress? address))
            throw new FormatException($"Invalid IP address: {addressText}");

        byte[] addressBytes = address.GetAddressBytes();
        int totalBits = addressBytes.Length * 8;

        int prefixLength;
        if (slash < 0)
        {
            prefixLength = totalBits;
        }
        else if (!int.TryParse(network.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength))
        {
            throw new FormatException($"Invalid prefix length in: {network}");
        }

        if (prefixLength < 0 || prefixLength > totalBits)
            throw new FormatException($"Prefix length {prefixLength} out of range for {(totalBits == 32 ? "IPv4" : "IPv6")}: {network}");

        byte[] start = (byte[])addressBytes.Clone();
        byte[] end = (byte[])addressBytes.Clone();

        for (int i = prefixLength; i < totalBits; i++)
        {
            int byteIndex = i / 8;
            int mask = 1 << (7 - (i % 8));
            start[byteIndex] = (byte)(start[byteIndex] & ~mask);
            end[byteIndex] = (byte)(end[byteIndex] | mask);
        }

        return (start, end);
    }

    /// <summary>
    /// The start of every CIDR that can hold the address, highest first and without repeats: a CIDR's start is any of
    /// its addresses with the host bits cleared. At most 33 for IPv4 and 129 for IPv6.
    /// </summary>
    public static List<byte[]> ContainingStarts(byte[] address)
    {
        ArgumentNullException.ThrowIfNull(address);

        byte[] current = (byte[])address.Clone();
        var starts = new List<byte[]> { (byte[])current.Clone() };

        // Clearing bit i (0 is the most significant) masks the address to a /i prefix
        for (int bit = current.Length * 8 - 1; bit >= 0; bit--)
        {
            int byteIndex = bit / 8;
            int mask = 1 << (7 - (bit % 8));
            if ((current[byteIndex] & mask) == 0)
                continue;

            current[byteIndex] = (byte)(current[byteIndex] & ~mask);
            starts.Add((byte[])current.Clone());
        }

        return starts;
    }
}
