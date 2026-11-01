using System.Globalization;

namespace PoorMansFeed;

/// <summary>The AS number a results row copies, read from the dataset's ASN text.</summary>
internal static class AsNumberText
{
    private const int MaxDigits = 10;

    internal static string? TryNormalise(string? text)
    {
        if (text is null)
        {
            return null;
        }

        ReadOnlySpan<char> digits = text.AsSpan().Trim();
        if (digits.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        if (digits.IsEmpty || digits.Length > MaxDigits)
        {
            return null;
        }

        // NumberStyles.None reads ASCII digits alone: no sign, space or other script's digits
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint asn)
            ? "AS" + asn.ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
