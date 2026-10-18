using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData("AS15169", 15169u, "AS15169")]
    [InlineData("as15169", 15169u, "AS15169")]
    [InlineData(" 15169 ", 15169u, "AS15169")]
    [InlineData("AS0015169", 15169u, "AS15169")]
    [InlineData("4294967295", 4294967295u, "AS4294967295")]
    public void AsnInputsParseToAsnQueries(string input, uint expectedAsn, string expectedText)
    {
        bool ok = SearchQuery.TryParse(input, out SearchQuery? query);

        Assert.True(ok);
        Assert.NotNull(query);
        Assert.Equal(SearchKind.Asn, query.Kind);
        Assert.Equal(expectedAsn, query.Asn);
        Assert.Equal(expectedText, query.Text);
        Assert.Null(query.IpBytes);
    }

    [Fact]
    public void IPv4AddressParsesToFourBytes()
    {
        bool ok = SearchQuery.TryParse("8.8.8.8", out SearchQuery? query);

        Assert.True(ok);
        Assert.NotNull(query);
        Assert.Equal(SearchKind.IpAddress, query.Kind);
        Assert.Equal("8.8.8.8", query.Text);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, query.IpBytes);
        Assert.Equal(0u, query.Asn);
    }

    [Fact]
    public void IPv6AddressParsesToSixteenBytes()
    {
        bool ok = SearchQuery.TryParse("2001:db8::1", out SearchQuery? query);

        Assert.True(ok);
        Assert.NotNull(query);
        Assert.Equal(SearchKind.IpAddress, query.Kind);
        Assert.Equal("2001:db8::1", query.Text);
        Assert.Equal(16, query.IpBytes!.Length);
    }

    [Fact]
    public void IPv4MappedIPv6IsUnmappedToIPv4()
    {
        bool ok = SearchQuery.TryParse("::ffff:8.8.8.8", out SearchQuery? query);

        Assert.True(ok);
        Assert.NotNull(query);
        Assert.Equal(SearchKind.IpAddress, query.Kind);
        Assert.Equal("8.8.8.8", query.Text);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, query.IpBytes);
    }

    [Theory]
    [InlineData("010.0.0.1")]
    [InlineData("192.168.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fe80::1%5")]
    public void LegacyAndDecoratedAddressesAreNotSearchedAsAnotherAddress(string input)
    {
        bool ok = SearchQuery.TryParse(input, out SearchQuery? query);

        Assert.False(ok);
        Assert.Null(query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AS")]
    [InlineData("AS-1")]
    [InlineData("12345678901")]
    [InlineData("4294967296")]
    [InlineData("1.2.3.x")]
    [InlineData("hello")]
    public void InvalidInputsAreRejected(string? input)
    {
        bool ok = SearchQuery.TryParse(input, out SearchQuery? query);

        Assert.False(ok);
        Assert.Null(query);
    }
}
