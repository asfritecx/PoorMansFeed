using System.Numerics;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>How one network becomes a results row, and how a large range is sized in the dialog.</summary>
public sealed class ResultRowTests
{
    private const string Dot = "\u00A0\u00B7\u00A0";
    private const string Dash = "\u2014";

    [Fact]
    public void AFullRecordJoinsItsFactsWithAMiddleDot()
    {
        var row = ResultRow.From(new NetworkRecord("8.8.8.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com"));

        Assert.Equal("8.8.8.0/24", row.NetworkText);
        Assert.Equal("US" + Dot + "United States", row.CountryText);
        Assert.Equal("AS15169" + Dot + "Google LLC", row.AsText);
        Assert.Equal("google.com", row.DomainText);
        Assert.False(row.IsLargeRange);
        Assert.Equal("8.8.8.0/24, US" + Dot + "United States, AS15169" + Dot + "Google LLC, google.com", row.AccessibleText);
    }

    [Fact]
    public void AMissingCountryOrAsNameShowsADashForThatPart()
    {
        var row = ResultRow.From(new NetworkRecord("8.8.8.0/24", null, null, null, "AS15169", null, "google.com"));

        Assert.Equal(Dash, row.CountryText);
        Assert.Equal("AS15169", row.AsText);
        Assert.Equal("8.8.8.0/24, " + Dash + ", AS15169, google.com", row.AccessibleText);
    }

    [Fact]
    public void ANetworkWithNoCountryAndNoAsShowsDashesAndNoDomainShowsADash()
    {
        var row = ResultRow.From(new NetworkRecord("10.0.0.0/8", "", "  ", null, null, "", "   "));

        Assert.Equal(Dash, row.CountryText);
        Assert.Equal(Dash, row.AsText);
        Assert.Equal(Dash, row.DomainText);
        Assert.Equal("10.0.0.0/8, " + Dash + ", " + Dash + ", " + Dash, row.AccessibleText);
    }

    [Fact]
    public void ACountryNameWithoutACodeShowsOnlyTheName()
    {
        var row = ResultRow.From(new NetworkRecord("10.0.0.0/8", null, "Australia", null, null, null, null));

        Assert.Equal("Australia", row.CountryText);
    }

    [Fact]
    public void AnAsnWithoutThePrefixGetsIt()
    {
        var row = ResultRow.From(new NetworkRecord("10.0.0.0/8", null, null, null, "15169", "Google LLC", null));

        Assert.Equal("AS15169" + Dot + "Google LLC", row.AsText);
    }

    [Fact]
    public void SurroundingSpacesAreTrimmed()
    {
        var row = ResultRow.From(new NetworkRecord("10.0.0.0/8", " US ", " United States ", null, " AS15169 ", " Google LLC ", " google.com "));

        Assert.Equal("US" + Dot + "United States", row.CountryText);
        Assert.Equal("AS15169" + Dot + "Google LLC", row.AsText);
        Assert.Equal("google.com", row.DomainText);
    }

    [Fact]
    public void ALargeRangeIsFlaggedAndSaysSoInItsAccessibleText()
    {
        var row = ResultRow.From(new NetworkRecord("10.0.0.0/8", "US", "United States", null, "AS1", "First ISP", "first.example", IsLargeRange: true));

        Assert.True(row.IsLargeRange);
        Assert.EndsWith(", large range", row.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIPv4LargeRangeIsSizedInAddresses()
    {
        var choice = LargeRangeChoice.From(new LargeRange("10.0.0.0/8", false, 8, BigInteger.Pow(2, 24)));

        Assert.Equal("16,777,216 addresses", choice.SizeText);
        Assert.Equal("10.0.0.0/8, 16,777,216 addresses", choice.AccessibleText);
        Assert.False(choice.IsIncluded);
    }

    [Fact]
    public void ASingleIPv4AddressIsSizedAsOneAddress()
    {
        var choice = LargeRangeChoice.From(new LargeRange("192.0.2.1/32", false, 32, BigInteger.One));

        Assert.Equal("1 address", choice.SizeText);
    }

    [Fact]
    public void AnIPv6RangeAtOrBelowSixtyFourBitsIsSizedInSixtyFourBitSubnets()
    {
        var choice = LargeRangeChoice.From(new LargeRange("2001:db8::/29", true, 29, BigInteger.Pow(2, 99)));

        Assert.Equal("34,359,738,368 /64 subnets", choice.SizeText);
    }

    [Fact]
    public void AnIPv6RangeOfExactlyOneSixtyFourBitSubnetIsSizedAsOne()
    {
        var choice = LargeRangeChoice.From(new LargeRange("2001:db8::/64", true, 64, BigInteger.Pow(2, 64)));

        Assert.Equal("1 /64 subnet", choice.SizeText);
    }

    [Fact]
    public void AnIPv6RangeLongerThanSixtyFourBitsIsSizedInAddresses()
    {
        var choice = LargeRangeChoice.From(new LargeRange("2001:db8::/96", true, 96, BigInteger.Pow(2, 32)));

        Assert.Equal("4,294,967,296 addresses", choice.SizeText);
    }
}
