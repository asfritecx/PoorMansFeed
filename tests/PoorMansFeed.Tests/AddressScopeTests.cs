using System.Net;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// The never-send boundary: an address outside every deny block (and, for IPv6, inside 2000::/3) is public; any other
/// address, and any byte length but 4 and 16, is not.
/// </summary>
public sealed class AddressScopeTests
{
    private const string Outside2000 = "outside 2000::/3";

    /// <summary>
    /// The first and last address of every deny block and of 2000::/3, and the address either side. The rule is the
    /// deny block holding the address, "outside 2000::/3" or "public"; it is checked against the address as well, so
    /// a wrong row cannot pass by agreeing with a wrong verdict.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0", false, "0.0.0.0/8")]
    [InlineData("0.255.255.255", false, "0.0.0.0/8")]
    [InlineData("1.0.0.0", true, "public")]
    [InlineData("9.255.255.255", true, "public")]
    [InlineData("10.0.0.0", false, "10.0.0.0/8")]
    [InlineData("10.255.255.255", false, "10.0.0.0/8")]
    [InlineData("11.0.0.0", true, "public")]
    [InlineData("100.63.255.255", true, "public")]
    [InlineData("100.64.0.0", false, "100.64.0.0/10")]
    [InlineData("100.127.255.255", false, "100.64.0.0/10")]
    [InlineData("100.128.0.0", true, "public")]
    [InlineData("126.255.255.255", true, "public")]
    [InlineData("127.0.0.0", false, "127.0.0.0/8")]
    [InlineData("127.255.255.255", false, "127.0.0.0/8")]
    [InlineData("128.0.0.0", true, "public")]
    [InlineData("169.253.255.255", true, "public")]
    [InlineData("169.254.0.0", false, "169.254.0.0/16")]
    [InlineData("169.254.255.255", false, "169.254.0.0/16")]
    [InlineData("169.255.0.0", true, "public")]
    [InlineData("172.15.255.255", true, "public")]
    [InlineData("172.16.0.0", false, "172.16.0.0/12")]
    [InlineData("172.31.255.255", false, "172.16.0.0/12")]
    [InlineData("172.32.0.0", true, "public")]
    [InlineData("191.255.255.255", true, "public")]
    [InlineData("192.0.0.0", false, "192.0.0.0/24")]
    [InlineData("192.0.0.255", false, "192.0.0.0/24")]
    [InlineData("192.0.1.0", true, "public")]
    [InlineData("192.0.1.255", true, "public")]
    [InlineData("192.0.2.0", false, "192.0.2.0/24")]
    [InlineData("192.0.2.255", false, "192.0.2.0/24")]
    [InlineData("192.0.3.0", true, "public")]
    [InlineData("192.88.98.255", true, "public")]
    [InlineData("192.88.99.0", false, "192.88.99.0/24")]
    [InlineData("192.88.99.255", false, "192.88.99.0/24")]
    [InlineData("192.88.100.0", true, "public")]
    [InlineData("192.167.255.255", true, "public")]
    [InlineData("192.168.0.0", false, "192.168.0.0/16")]
    [InlineData("192.168.255.255", false, "192.168.0.0/16")]
    [InlineData("192.169.0.0", true, "public")]
    [InlineData("198.17.255.255", true, "public")]
    [InlineData("198.18.0.0", false, "198.18.0.0/15")]
    [InlineData("198.19.255.255", false, "198.18.0.0/15")]
    [InlineData("198.20.0.0", true, "public")]
    [InlineData("198.51.99.255", true, "public")]
    [InlineData("198.51.100.0", false, "198.51.100.0/24")]
    [InlineData("198.51.100.255", false, "198.51.100.0/24")]
    [InlineData("198.51.101.0", true, "public")]
    [InlineData("203.0.112.255", true, "public")]
    [InlineData("203.0.113.0", false, "203.0.113.0/24")]
    [InlineData("203.0.113.255", false, "203.0.113.0/24")]
    [InlineData("203.0.114.0", true, "public")]
    [InlineData("223.255.255.255", true, "public")]
    [InlineData("224.0.0.0", false, "224.0.0.0/4")]
    [InlineData("239.255.255.255", false, "224.0.0.0/4")]
    [InlineData("240.0.0.0", false, "240.0.0.0/4")]
    [InlineData("255.255.255.255", false, "240.0.0.0/4")]
    [InlineData("1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false, "outside 2000::/3")]
    [InlineData("2000::", true, "public")]
    [InlineData("3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true, "public")]
    [InlineData("4000::", false, "outside 2000::/3")]
    [InlineData("2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true, "public")]
    [InlineData("2001::", false, "2001::/23")]
    [InlineData("2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff", false, "2001::/23")]
    [InlineData("2001:200::", true, "public")]
    [InlineData("2001:db7:ffff:ffff:ffff:ffff:ffff:ffff", true, "public")]
    [InlineData("2001:db8::", false, "2001:db8::/32")]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", false, "2001:db8::/32")]
    [InlineData("2001:db9::", true, "public")]
    [InlineData("2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true, "public")]
    [InlineData("2002::", false, "2002::/16")]
    [InlineData("2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false, "2002::/16")]
    [InlineData("2003::", true, "public")]
    [InlineData("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true, "public")]
    [InlineData("3fff::", false, "3fff::/20")]
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false, "3fff::/20")]
    [InlineData("3fff:1000::", true, "public")]
    public void EdgeAddresses(string text, bool expected, string rule)
    {
        byte[] address = Bytes(text);

        Assert.Equal(expected, AddressScope.IsPublic(address));
        Assert.Equal(expected, rule == "public");
        if (rule == Outside2000)
        {
            Assert.Equal(16, address.Length);
            Assert.False(Contains("2000::/3", address));
        }
        else if (rule != "public")
        {
            Assert.True(Contains(rule, address));
        }
    }

    [Theory]
    [InlineData("0.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("2002:a00:1::")] // 6to4 carrying 10.0.0.1
    public void TrapsAreNotPublic(string text)
    {
        Assert.False(AddressScope.IsPublic(Bytes(text)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("32.1.13.184")] // the bytes of the 2001:db8 prefix, as an IPv4 address
    [InlineData("32.2.10.0")] // the bytes of 2002:a00::, as an IPv4 address
    [InlineData("2001:4860::1")]
    [InlineData("2620:4f:8000::1")] // AS112, RFC 7534
    [InlineData("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void PublicAddressesArePublic(string text)
    {
        Assert.True(AddressScope.IsPublic(Bytes(text)));
    }

    [Fact]
    public void AMappedAddressIsJudgedAsItsSixteenBytes()
    {
        byte[] mapped = Bytes("::ffff:8.8.8.8");

        Assert.Equal(16, mapped.Length);
        Assert.False(AddressScope.IsPublic(mapped));
        Assert.True(AddressScope.IsPublic(Bytes("8.8.8.8")));
    }

    [Fact]
    public void TheFourBytesOfTheDocumentationPrefixArePublicAndTheBlockIsNot()
    {
        Assert.True(AddressScope.IsPublic(new byte[] { 32, 1, 13, 184 }));
        Assert.False(AddressScope.IsPublic(Bytes("2001:db8::")));
    }

    /// <summary>
    /// Bytes that would read as public in either family, so a length that falls through to an IPv4 or an IPv6
    /// verdict shows up.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("080808")]
    [InlineData("0808080808")]
    [InlineData("200148600000000000000000000001")]
    [InlineData("2001486000000000000000000000000101")]
    public void OtherLengthsAreNotPublic(string hex)
    {
        byte[] address = Convert.FromHexString(hex);
        Assert.DoesNotContain(address.Length, new[] { 4, 16 });

        Assert.False(AddressScope.IsPublic(address));
    }

    private static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }

    private static bool Contains(string cidr, byte[] address)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        byte[] start = block.Start;
        byte[] end = block.End;
        return address.Length == start.Length
            && address.AsSpan().SequenceCompareTo(start) >= 0
            && address.AsSpan().SequenceCompareTo(end) <= 0;
    }
}
