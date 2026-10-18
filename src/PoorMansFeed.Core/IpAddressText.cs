using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace PoorMansFeed.Core;

/// <summary>
/// Reads an address only in its plain form. IPAddress.TryParse also reads legacy and decorated forms that name a
/// different address or carry extra text: "010.0.0.1" is 8.0.0.1 (octal), "10.1" is 10.0.0.1, "1.2.3" is 1.2.0.3,
/// "0x7f.0.0.1" is 127.0.0.1, and "[::1]:80" and "fe80::1%5" have a port or a zone. Every one of those is rejected.
/// </summary>
public static class IpAddressText
{
    /// <summary>
    /// IPv4 is exactly four parts of decimal digits, each 0 to 255 with no leading zero. IPv6 is hex digits and colons,
    /// optionally ending in an IPv4 part held to the same rule. Text with a ':' is IPv6, so the family always matches
    /// the text. No whitespace, brackets, port or zone is accepted.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (text.Contains(':'))
            return TryParseIPv6(text, out address);

        Span<byte> bytes = stackalloc byte[4];
        if (!TryParseIPv4(text, bytes))
            return false;

        address = new IPAddress(bytes);
        return true;
    }

    private static bool TryParseIPv6(ReadOnlySpan<char> text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        int lastColon = text.LastIndexOf(':');
        if (!IsHexOrColon(text[..lastColon]))
            return false;

        // An embedded IPv4 part can only be the last one
        ReadOnlySpan<char> last = text[(lastColon + 1)..];
        if (last.Contains('.'))
        {
            Span<byte> ipv4 = stackalloc byte[4];
            if (!TryParseIPv4(last, ipv4))
                return false;
        }
        else if (!IsHexOrColon(last))
        {
            return false;
        }

        if (!IPAddress.TryParse(text, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        address = parsed;
        return true;
    }

    private static bool TryParseIPv4(ReadOnlySpan<char> text, Span<byte> bytes)
    {
        int part = 0;
        int partStart = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '.')
                continue;

            if (part == 4 || !TryParseOctet(text[partStart..i], out byte value))
                return false;

            bytes[part++] = value;
            partStart = i + 1;
        }

        return part == 4;
    }

    private static bool TryParseOctet(ReadOnlySpan<char> text, out byte value)
    {
        value = 0;
        if (text.Length is 0 or > 3 || (text.Length > 1 && text[0] == '0'))
            return false;

        int number = 0;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
            number = number * 10 + (c - '0');
        }

        if (number > 255)
            return false;

        value = (byte)number;
        return true;
    }

    private static bool IsHexOrColon(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c) && c != ':')
                return false;
        }

        return true;
    }
}
