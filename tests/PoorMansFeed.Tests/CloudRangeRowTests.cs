using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Tests;

/// <summary>The AS number a cloud range row's Copy AS number copies: one AS under every overlap, or none.</summary>
public sealed class CloudRangeRowTests
{
    [Fact]
    public void OneGroupWithAnAsGivesThatAs()
    {
        Assert.Equal("AS15169", RowWith(Group("AS15169")).SingleAsnText);
    }

    [Fact]
    public void OneAsWrittenTwoWaysInTwoGroupsIsStillOneAs()
    {
        CloudRangeRow row = RowWith(Group("AS15169", "US"), Group("15169", "BE"));

        Assert.Equal("AS15169", row.SingleAsnText);
        Assert.StartsWith("AS15169", row.AsText);
    }

    [Fact]
    public void TwoDifferentAsesGiveNone()
    {
        Assert.Null(RowWith(Group("AS15169"), Group("AS64500")).SingleAsnText);
    }

    [Fact]
    public void AGroupWithNoAsBesideOneWithAnAsGivesNone()
    {
        Assert.Null(RowWith(Group("AS15169"), Group(null)).SingleAsnText);
        Assert.Null(RowWith(Group(null), Group("AS15169")).SingleAsnText);
    }

    [Fact]
    public void NoGroupsGiveNone()
    {
        Assert.Null(RowWith().SingleAsnText);
    }

    [Theory]
    [InlineData("AS-1")]
    [InlineData("AS 15169")]
    [InlineData("AS4294967296")]
    [InlineData("Google")]
    public void AnAsTextThatIsNotAnAsNumberGivesNone(string asn)
    {
        Assert.Null(RowWith(Group(asn)).SingleAsnText);
    }

    private static OverlapGroup Group(string? asn, string country = "US") =>
        new(country, "Somewhere", asn, "Google LLC", "google.com", 1);

    private static CloudRangeRow RowWith(params OverlapGroup[] groups)
    {
        Assert.True(CidrBlock.TryParse("34.1.0.0/16", out CidrBlock? block));
        var range = new CheckedRange(new FeedPrefix(block, "us-central1"), RangeConfirmation.Confirmed, groups, isLarge: false);
        return CloudRangeRow.From(range);
    }
}
