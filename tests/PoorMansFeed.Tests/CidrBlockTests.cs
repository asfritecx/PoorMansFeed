using System.Net;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>The canonical block the cloud tools check, and the ancestor starts the overlap query looks up.</summary>
public sealed class CidrBlockTests
{
    [Theory]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData(" 8.8.8.0/24 ", "8.8.8.0/24")]
    [InlineData("1.2.3.4/32", "1.2.3.4/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:DB8:0:0::1/32", "2001:db8::/32")]
    [InlineData("0a01:0200::/64", "a01:200::/64")]
    [InlineData("::/0", "::/0")]
    public void TextIsCanonical(string text, string canonical)
    {
        Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
        Assert.Equal(canonical, block.Text);
    }

    [Fact]
    public void TwoSpellingsOfOneBlockAreEqual()
    {
        Assert.True(CidrBlock.TryParse("10.1.2.3/8", out CidrBlock? left));
        Assert.True(CidrBlock.TryParse("10.0.0.0/8", out CidrBlock? right));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void StartAndEndCoverTheBlock()
    {
        Assert.True(CidrBlock.TryParse("2001:db8::ff/121", out CidrBlock? block));

        Assert.True(block.IsIpv6);
        Assert.Equal(121, block.PrefixLength);
        Assert.Equal(IPAddress.Parse("2001:db8::80").GetAddressBytes(), block.Start);
        Assert.Equal(IPAddress.Parse("2001:db8::ff").GetAddressBytes(), block.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.0/120")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("/8")]
    [InlineData("1.2.3/24")]
    [InlineData("010.0.0.0/8")]
    [InlineData("[2001:db8::]/32")]
    [InlineData("fe80::%5/64")]
    [InlineData("10.0.0.0/8\n9.9.9.0/24")]
    public void BadBlocksAreRejected(string? text)
    {
        Assert.False(CidrBlock.TryParse(text, out CidrBlock? block));
        Assert.Null(block);
    }

    [Fact]
    public void AncestorStartsAreEveryBroaderStartNarrowestFirst()
    {
        Assert.True(CidrBlock.TryParse("10.1.2.0/24", out CidrBlock? block));

        // /23 starts at the block's own start; /22 to /16 at 10.1.0.0; /15 to /7 at 10.0.0.0; /6 and /5 at 8.0.0.0
        Assert.Equal(["10.1.0.0", "10.0.0.0", "8.0.0.0", "0.0.0.0"], Starts(block));
    }

    [Fact]
    public void AnIPv6BlockHasOneAncestorStartForEachSetBitOfItsPrefix()
    {
        Assert.True(CidrBlock.TryParse("a000::/4", out CidrBlock? small));
        Assert.Equal(["8000::", "::"], Starts(small));

        // 2001:0db8 has 9 bits set, and clearing them one by one from the right gives 9 distinct broader starts
        Assert.True(CidrBlock.TryParse("2001:db8::/32", out CidrBlock? block));
        List<string> starts = Starts(block);
        Assert.Equal(9, starts.Count);
        Assert.Equal(9, starts.Distinct().Count());
        Assert.Equal("2001:db0::", starts[0]);
        Assert.Equal("::", starts[^1]);
    }

    [Theory]
    [InlineData("0.0.0.0/8")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void ABlockStartingAtZeroHasNoAncestorStarts(string text)
    {
        Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));

        Assert.Empty(block.AncestorStarts());
    }

    private static List<string> Starts(CidrBlock block) =>
        block.AncestorStarts().Select(start => new IPAddress(start).ToString()).ToList();
}
