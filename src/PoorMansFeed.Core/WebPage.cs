using System.Globalization;

namespace PoorMansFeed.Core;

/// <summary>
/// Windows hands the URL to ShellExecute, so a page is built only here: a fixed https host and a path from an ASN's
/// digits or a <see cref="PublicAddress"/>'s checked text. No other string reaches the URL.
/// </summary>
public sealed class WebPage
{
    private const string IpInfoRoot = "https://ipinfo.io/";
    private const string AbuseIpDbCheckRoot = "https://www.abuseipdb.com/check/";

    private WebPage(string uri)
    {
        Uri = new Uri(uri, UriKind.Absolute);
    }

    /// <summary>The absolute https address of the page.</summary>
    public Uri Uri { get; }

    /// <summary>The AS's page on ipinfo.io, such as https://ipinfo.io/AS13335.</summary>
    public static WebPage IpInfoAsn(uint asn) =>
        new(IpInfoRoot + "AS" + asn.ToString(CultureInfo.InvariantCulture));

    /// <summary>The address's page on ipinfo.io, such as https://ipinfo.io/8.8.8.8.</summary>
    public static WebPage IpInfoAddress(PublicAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new WebPage(IpInfoRoot + address.Text);
    }

    /// <summary>The address's check page on AbuseIPDB's website, such as https://www.abuseipdb.com/check/8.8.8.8.</summary>
    public static WebPage AbuseIpDbCheck(PublicAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new WebPage(AbuseIpDbCheckRoot + address.Text);
    }
}
