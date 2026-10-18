using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PoorMansFeed.Core;

/// <summary>
/// A CIDR parsed once at a boundary. Text is canonical: host bits cleared, explicit prefix. Equality is by Text,
/// so two spellings of the same block are one block.
/// </summary>
public sealed class CidrBlock : IEquatable<CidrBlock>
{
    private readonly byte[] _start;
    private readonly byte[] _end;

    private CidrBlock(string text, byte[] start, byte[] end, int prefixLength)
    {
        Text = text;
        _start = start;
        _end = end;
        PrefixLength = prefixLength;
        IsIpv6 = start.Length == 16;
    }

    public string Text { get; }

    public byte[] Start => (byte[])_start.Clone();

    public byte[] End => (byte[])_end.Clone();

    public int PrefixLength { get; }

    public bool IsIpv6 { get; }

    /// <summary>
    /// False for a blank value, a bare address, a bad prefix, an address not in its plain form (IpAddressText) or an
    /// IPv4-mapped IPv6 address.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CidrBlock? block)
    {
        block = null;
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
            return false;

        int slash = value.IndexOf('/');
        if (slash <= 0 || value.IndexOf('/', slash + 1) >= 0)
            return false;

        string addressText = value[..slash];
        string prefixText = value[(slash + 1)..];
        if (!IpAddressText.TryParse(addressText, out IPAddress? address) || address.IsIPv4MappedToIPv6)
            return false;

        bool wantsIpv6 = addressText.Contains(':');
        if (wantsIpv6 != (address.AddressFamily == AddressFamily.InterNetworkV6))
            return false;

        int totalBits = address.GetAddressBytes().Length * 8;
        if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out int prefixLength)
            || prefixLength < 0 || prefixLength > totalBits)
            return false;

        byte[] start;
        byte[] end;
        try
        {
            (start, end) = Cidr.Parse(value);
        }
        catch (FormatException)
        {
            return false;
        }

        string canonical = new IPAddress(start).ToString() + "/" + prefixLength.ToString(CultureInfo.InvariantCulture);
        block = new CidrBlock(canonical, start, end, prefixLength);
        return true;
    }

    /// <summary>
    /// Start addresses of every strictly broader block that contains this one, narrowest first, excluding any
    /// equal to Start. A CIDR overlapping this block either starts inside it or is one of these.
    /// </summary>
    public IReadOnlyList<byte[]> AncestorStarts()
    {
        var starts = new List<byte[]>();
        for (int prefix = PrefixLength - 1; prefix >= 0; prefix--)
        {
            byte[] masked = Mask(_start, prefix);
            if (BytesEqual(masked, _start) || starts.Any(existing => BytesEqual(existing, masked)))
                continue;

            starts.Add(masked);
        }

        return starts;
    }

    public bool Equals(CidrBlock? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is CidrBlock other && Equals(other);

    public override int GetHashCode() => string.GetHashCode(Text, StringComparison.Ordinal);

    public override string ToString() => Text;

    private static byte[] Mask(byte[] start, int prefixLength)
    {
        byte[] masked = (byte[])start.Clone();
        int totalBits = masked.Length * 8;
        for (int bit = prefixLength; bit < totalBits; bit++)
        {
            int byteIndex = bit / 8;
            int mask = 1 << (7 - (bit % 8));
            masked[byteIndex] = (byte)(masked[byteIndex] & ~mask);
        }

        return masked;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }
}
