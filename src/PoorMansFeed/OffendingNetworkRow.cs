using System.Globalization;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed;

/// <summary>One offending network line, already formatted, so the list's templates bind text only.</summary>
public sealed class OffendingNetworkRow
{
    private OffendingNetworkRow(uint asn, string asnText, string nameText, string domainText, string addressesText,
                                string countriesText, string lookUpName, string accessibleText)
    {
        Asn = asn;
        AsnText = asnText;
        NameText = nameText;
        DomainText = domainText;
        AddressesText = addressesText;
        CountriesText = countriesText;
        LookUpName = lookUpName;
        AccessibleText = accessibleText;
    }

    public static OffendingNetworkRow From(AbuseNetworkGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        string asn = "AS" + group.Asn.ToString(CultureInfo.InvariantCulture);
        string name = OrMissing(group.AsName);
        string domain = OrMissing(group.AsDomain);

        // Invariant, so the count reads the same as the summary's and the feed headers' whatever the user's culture
        string addresses = group.Addresses.ToString("N0", CultureInfo.InvariantCulture);
        string countries = AppText.AbuseNetworkCountries(group.Countries);
        string accessible = string.Join(", ", asn, name, domain, addresses, countries);

        return new OffendingNetworkRow(group.Asn, asn, name, domain, addresses, countries, AppText.LookUpNetworkName(group.Asn),
            accessible);
    }

    /// <summary>The grouping's AS number, from which the row's ipinfo.io page is built.</summary>
    public uint Asn { get; }

    /// <summary>"AS15169": also what Look up searches for.</summary>
    public string AsnText { get; }

    public string NameText { get; }

    public string DomainText { get; }

    public string AddressesText { get; }

    /// <summary>IPinfo's top three countries by addresses, then how many more.</summary>
    public string CountriesText { get; }

    /// <summary>The tooltip and automation name of the row's Look up button.</summary>
    public string LookUpName { get; }

    /// <summary>The row's automation name: its cells in order.</summary>
    public string AccessibleText { get; }

    private static string OrMissing(string? value) => string.IsNullOrWhiteSpace(value) ? AppText.Missing : value;
}
