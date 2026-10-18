using System.Numerics;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class LargeRangePolicyTests
{
    private static readonly UserSettings Defaults = new();

    [Fact]
    public void IPv4SixteenIsNotLargeButFifteenIs()
    {
        Assert.False(LargeRangePolicy.IsLarge("10.0.0.0/16", Defaults));
        Assert.True(LargeRangePolicy.IsLarge("10.0.0.0/15", Defaults));
    }

    [Fact]
    public void IPv6ThirtyTwoIsNotLargeButThirtyOneIs()
    {
        Assert.False(LargeRangePolicy.IsLarge("2001:db8::/32", Defaults));
        Assert.True(LargeRangePolicy.IsLarge("2001:db8::/31", Defaults));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BareIpsAndBlankValuesAreNotLarge(string? network)
    {
        Assert.False(LargeRangePolicy.IsLarge(network, Defaults));
    }

    [Fact]
    public void CheckDisabledMakesNothingLarge()
    {
        var settings = new UserSettings { LargeRangeCheckEnabled = false };

        Assert.False(LargeRangePolicy.IsLarge("10.0.0.0/8", settings));
        Assert.Empty(LargeRangePolicy.Find(
            [new NetworkRecord("10.0.0.0/8", null, null, null, null, null, null)], settings));
    }

    [Fact]
    public void CustomThresholdsAreRespected()
    {
        var settings = new UserSettings { IPv4MinPrefix = 24, IPv6MinPrefix = 48 };

        Assert.True(LargeRangePolicy.IsLarge("10.0.0.0/16", settings));
        Assert.False(LargeRangePolicy.IsLarge("10.0.0.0/24", settings));
        Assert.True(LargeRangePolicy.IsLarge("2001:db8::/40", settings));
        Assert.False(LargeRangePolicy.IsLarge("2001:db8::/48", settings));
    }

    [Fact]
    public void AddressCountIsTwoToTheHostBits()
    {
        Assert.True(LargeRangePolicy.TryDescribe("10.0.0.0/8", out LargeRange v4));
        Assert.Equal(16777216, v4.AddressCount);
        Assert.False(v4.IsIpv6);
        Assert.Equal(8, v4.PrefixLength);

        Assert.True(LargeRangePolicy.TryDescribe("2001:db8::/32", out LargeRange v6));
        Assert.Equal(BigInteger.Pow(2, 96), v6.AddressCount);
        Assert.True(v6.IsIpv6);
        Assert.Equal(32, v6.PrefixLength);
    }

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("010.0.0.0/8")]
    [InlineData("10.0/8")]
    [InlineData("[2001:db8::]/32")]
    public void BadPrefixesAreRejected(string network)
    {
        Assert.False(LargeRangePolicy.TryDescribe(network, out _));
    }

    [Fact]
    public void FindReturnsOnlyLargeRowsInInputOrder()
    {
        var rows = new List<NetworkRecord>
        {
            new("10.0.0.0/8", null, null, null, null, null, null),
            new("8.8.8.0/24", null, null, null, null, null, null),
            new("2001:db8::/16", null, null, null, null, null, null),
            new("1.1.1.1", null, null, null, null, null, null),
            new("203.0.113.0/16", null, null, null, null, null, null),
        };

        List<LargeRange> found = LargeRangePolicy.Find(rows, Defaults);

        Assert.Equal(["10.0.0.0/8", "2001:db8::/16"], found.Select(r => r.Network));
    }
}
