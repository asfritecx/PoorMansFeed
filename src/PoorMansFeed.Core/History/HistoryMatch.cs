using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PoorMansFeed.Core.History;

/// <summary>An address, as its /32 or /128, or a network that a history entry must contain or overlap.</summary>
public sealed record HistoryMatch
{
    private HistoryMatch(CidrBlock block) => Block = block;

    public string Text => Block.Text;

    internal CidrBlock Block { get; }

    /// <summary>
    /// An IPv4-mapped address reads as IPv4.
    /// An IPv4-mapped network is refused, as CidrBlock refuses it.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HistoryMatch? match)
    {
        match = null;
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
            return false;

        CidrBlock? block;
        if (value.Contains('/'))
        {
            if (!CidrBlock.TryParse(value, out block))
                return false;
        }
        else
        {
            if (!IpAddressText.TryParse(value, out IPAddress? address))
                return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            int bits = address.GetAddressBytes().Length * 8;
            if (!CidrBlock.TryParse(address + "/" + bits.ToString(CultureInfo.InvariantCulture), out block))
                return false;
        }

        match = new HistoryMatch(block);
        return true;
    }
}
