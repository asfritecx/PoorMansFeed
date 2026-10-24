using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PoorMansFeed.Core;

public sealed partial record SearchQuery
{
    private static readonly Regex AsnPattern = new(@"^(?i:AS)?[0-9]{1,10}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// An ASN ("AS" optional, then digits) or a plain address. A legacy form such as "010.0.0.1" (octal, so 8.0.0.1)
    /// is rejected rather than searched as a different address.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SearchQuery? query)
    {
        query = null;
        string text = input?.Trim() ?? "";
        if (text.Length == 0)
            return false;

        if (AsnPattern.IsMatch(text))
        {
            string digits = text.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint asn))
            {
                query = new SearchQuery(SearchKind.Asn, "AS" + asn.ToString(CultureInfo.InvariantCulture), asn, null);
                return true;
            }
            return false;
        }

        if (IpAddressText.TryParse(text, out IPAddress? address))
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            query = new SearchQuery(SearchKind.IpAddress, address.ToString(), 0, address.GetAddressBytes());
            return true;
        }

        return false;
    }
}
