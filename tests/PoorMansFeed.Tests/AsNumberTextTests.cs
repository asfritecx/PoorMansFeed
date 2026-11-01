namespace PoorMansFeed.Tests;

/// <summary>Which dataset ASN texts give an AS number to copy, and the form it is copied in.</summary>
public sealed class AsNumberTextTests
{
    [Theory]
    [InlineData("AS15169", "AS15169")]
    [InlineData("as15169", "AS15169")]
    [InlineData("As15169", "AS15169")]
    [InlineData("15169", "AS15169")]
    [InlineData(" AS15169 ", "AS15169")]
    [InlineData("\tAS15169\n", "AS15169")]
    [InlineData("AS015169", "AS15169")]
    [InlineData("AS0", "AS0")]
    [InlineData("AS4294967295", "AS4294967295")]
    [InlineData("4294967295", "AS4294967295")]
    [InlineData("AS0000015169", "AS15169")]
    public void AnAsnTheDatasetCouldHoldIsCopiedAsAsAndItsNumber(string text, string expected)
    {
        Assert.Equal(expected, AsNumberText.TryNormalise(text));
    }

    [Theory]
    [InlineData("AS")]
    [InlineData("as")]
    [InlineData("AS12345678901")]
    [InlineData("12345678901")]
    [InlineData("AS00000015169")]
    [InlineData("AS4294967296")]
    [InlineData("AS9999999999")]
    [InlineData("AS15\n169")]
    [InlineData("AS15169\r\nAS1")]
    [InlineData("AS15 169")]
    [InlineData("AS 15169")]
    [InlineData("AS\u0661\u0665\u0661\u0666\u0669")]
    [InlineData("AS\uFF11\uFF15")]
    [InlineData("AS-15169")]
    [InlineData("AS+15169")]
    [InlineData("ASN15169")]
    [InlineData("AS15169x")]
    [InlineData("AS1.5")]
    [InlineData("ASAS15169")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingElseGivesNoAsNumber(string text)
    {
        Assert.Null(AsNumberText.TryNormalise(text));
    }

    [Fact]
    public void NoTextGivesNoAsNumber()
    {
        Assert.Null(AsNumberText.TryNormalise(null));
    }
}
