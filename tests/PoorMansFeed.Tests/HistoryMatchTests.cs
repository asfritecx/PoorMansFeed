using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class HistoryMatchTests
{
    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8/32")]
    [InlineData("2001:4860::8888", "2001:4860::8888/128")]
    [InlineData("2001:4860:0:0::8888", "2001:4860::8888/128")]
    [InlineData("::ffff:8.8.8.8", "8.8.8.8/32")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("2001:4860::1/32", "2001:4860::/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("  192.168.4.0/22\t", "192.168.4.0/22")]
    [InlineData(" 9.9.9.9 ", "9.9.9.9/32")]
    public void An_address_or_network_reads_as_its_canonical_block(string text, string expected)
    {
        Assert.True(HistoryMatch.TryParse(text, out HistoryMatch? match));

        Assert.Equal(expected, match.Text);
        Assert.Equal(expected, match.Block.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.0.0.0/+24")]
    [InlineData("10.0.0.0/ 24")]
    [InlineData("10.0.0.0/33")]
    [InlineData("010.0.0.1")]
    [InlineData("192.168.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("::ffff:8.8.8.0/120")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("AS15169")]
    [InlineData("15169")]
    [InlineData("fe80::1%5")]
    [InlineData("[::1]")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(HistoryMatch.TryParse(text, out HistoryMatch? match));
        Assert.Null(match);
    }

    [Fact]
    public void Two_spellings_of_one_block_are_one_match()
    {
        Assert.True(HistoryMatch.TryParse("10.1.2.3/8", out HistoryMatch? first));
        Assert.True(HistoryMatch.TryParse("10.0.0.0/8", out HistoryMatch? second));

        Assert.Equal(first, second);
    }
}
