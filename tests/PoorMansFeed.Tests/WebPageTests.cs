using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// The only pages the browser is handed: a fixed https host, and a path made from an ASN's digits or an address
/// <see cref="PublicAddress"/> judged public.
/// </summary>
public sealed class WebPageTests
{
    private static PublicAddress Public(params byte[] bytes)
    {
        Assert.True(PublicAddress.TryCreate(bytes, out PublicAddress? address));
        return address;
    }

    private static readonly PublicAddress GoogleDnsV4 = Public(8, 8, 8, 8);

    // 2001:4860::8888
    private static readonly PublicAddress GoogleDnsV6 = Public(0x20, 0x01, 0x48, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x88, 0x88);

    [Theory]
    [InlineData(13335u, "https://ipinfo.io/AS13335")]
    [InlineData(0u, "https://ipinfo.io/AS0")]
    [InlineData(uint.MaxValue, "https://ipinfo.io/AS4294967295")]
    public void An_ASN_gives_its_ipinfo_page(uint asn, string expected)
    {
        Assert.Equal(expected, WebPage.IpInfoAsn(asn).Uri.AbsoluteUri);
    }

    [Fact]
    public void An_IPv4_address_gives_its_pages_on_both_sites()
    {
        Assert.Equal("https://ipinfo.io/8.8.8.8", WebPage.IpInfoAddress(GoogleDnsV4).Uri.AbsoluteUri);
        Assert.Equal("https://www.abuseipdb.com/check/8.8.8.8", WebPage.AbuseIpDbCheck(GoogleDnsV4).Uri.AbsoluteUri);
    }

    [Fact]
    public void An_IPv6_address_gives_its_compressed_text_on_both_sites()
    {
        Assert.Equal("https://ipinfo.io/2001:4860::8888", WebPage.IpInfoAddress(GoogleDnsV6).Uri.AbsoluteUri);
        Assert.Equal("https://www.abuseipdb.com/check/2001:4860::8888", WebPage.AbuseIpDbCheck(GoogleDnsV6).Uri.AbsoluteUri);
    }

    [Fact]
    public void Every_page_is_https_on_one_of_the_two_hosts()
    {
        WebPage[] pages =
        [
            WebPage.IpInfoAsn(15169),
            WebPage.IpInfoAddress(GoogleDnsV4),
            WebPage.IpInfoAddress(GoogleDnsV6),
            WebPage.AbuseIpDbCheck(GoogleDnsV4),
            WebPage.AbuseIpDbCheck(GoogleDnsV6),
        ];

        Assert.All(pages, page =>
        {
            Assert.True(page.Uri.IsAbsoluteUri);
            Assert.Equal(Uri.UriSchemeHttps, page.Uri.Scheme);
            Assert.True(page.Uri.IsDefaultPort);
            Assert.Equal("", page.Uri.UserInfo);
            Assert.Equal("", page.Uri.Query);
            Assert.Equal("", page.Uri.Fragment);
        });
        Assert.Equal(["ipinfo.io", "ipinfo.io", "ipinfo.io", "www.abuseipdb.com", "www.abuseipdb.com"],
            pages.Select(page => page.Uri.Host));
    }

    [Fact]
    public void An_address_page_needs_an_address()
    {
        Assert.Throws<ArgumentNullException>(() => WebPage.IpInfoAddress(null!));
        Assert.Throws<ArgumentNullException>(() => WebPage.AbuseIpDbCheck(null!));
    }
}
