using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>One line of the results list, already formatted for display.</summary>
public sealed record ResultRow(string NetworkText, string CountryText, string AsText, string DomainText,
                               bool IsLargeRange, string AccessibleText)
{
    public static ResultRow From(NetworkRecord record)
    {
        string? countryCode = Clean(record.CountryCode);
        string? country = Clean(record.Country);
        string? asn = Clean(record.Asn);
        if (asn is not null && !asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            asn = "AS" + asn;
        }

        string? asName = Clean(record.AsName);
        string? domain = Clean(record.AsDomain);

        string countryText = Join(countryCode, country);
        string asText = Join(asn, asName);
        string domainText = domain ?? AppText.Missing;
        string accessible = string.Join(", ", record.Network, countryText, asText, domainText)
            + (record.IsLargeRange ? AppText.LargeRangeSpoken : "");

        return new ResultRow(record.Network, countryText, asText, domainText, record.IsLargeRange, accessible);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // "US \u00B7 United States", or just the part that exists, or a dash when neither does
    private static string Join(string? first, string? second) => (first, second) switch
    {
        (not null, not null) => first + AppText.Joiner + second,
        (not null, null) => first,
        (null, not null) => second,
        _ => AppText.Missing,
    };
}
