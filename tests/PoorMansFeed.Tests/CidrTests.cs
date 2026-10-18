using System.Globalization;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class CidrTests
{
    [Fact]
    public void Parses_IPv4_Prefix()
    {
        var (start, end) = Cidr.Parse("10.0.0.0/8");

        Assert.Equal(new byte[] { 10, 0, 0, 0 }, start);
        Assert.Equal(new byte[] { 10, 255, 255, 255 }, end);
    }

    [Fact]
    public void Masks_Host_Bits_For_Start()
    {
        var (start, end) = Cidr.Parse("192.168.1.77/24");

        Assert.Equal(new byte[] { 192, 168, 1, 0 }, start);
        Assert.Equal(new byte[] { 192, 168, 1, 255 }, end);
    }

    [Fact]
    public void Bare_IPv4_Is_Single_Address()
    {
        var (start, end) = Cidr.Parse("8.8.8.8");

        Assert.Equal(new byte[] { 8, 8, 8, 8 }, start);
        Assert.Equal(start, end);
    }

    [Fact]
    public void Default_Route_Covers_Everything()
    {
        var (start, end) = Cidr.Parse("0.0.0.0/0");

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, start);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, end);
    }

    [Fact]
    public void Slash_32_Is_Single_Address()
    {
        var (start, end) = Cidr.Parse("1.2.3.4/32");

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, start);
        Assert.Equal(start, end);
    }

    [Fact]
    public void Parses_IPv6_Prefix_To_16_Bytes()
    {
        var (start, end) = Cidr.Parse("2001:db8::/32");

        Assert.Equal(16, start.Length);
        Assert.Equal(16, end.Length);
        Assert.Equal(new byte[] { 0x20, 0x01, 0x0d, 0xb8 }, start[..4]);
        Assert.All(start[4..], b => Assert.Equal((byte)0, b));
        Assert.Equal(new byte[] { 0x20, 0x01, 0x0d, 0xb8 }, end[..4]);
        Assert.All(end[4..], b => Assert.Equal((byte)0xFF, b));
    }

    [Fact]
    public void Bare_IPv6_Is_Single_Address()
    {
        var (start, end) = Cidr.Parse("::1");

        Assert.Equal(16, start.Length);
        Assert.Equal(start, end);
        Assert.Equal((byte)1, start[15]);
    }

    [Fact]
    public void Masks_IPv6_Host_Bits_Inside_A_Byte()
    {
        var (start, end) = Cidr.Parse("2001:db8::ff/121");

        Assert.Equal(System.Net.IPAddress.Parse("2001:db8::80").GetAddressBytes(), start);
        Assert.Equal(System.Net.IPAddress.Parse("2001:db8::ff").GetAddressBytes(), end);
    }

    [Theory]
    [InlineData("1.2.3.0/33")]
    [InlineData("1.2.3.0/x")]
    [InlineData("1.2.3.0/-1")]
    [InlineData("999.1.1.1/8")]
    [InlineData("nonsense")]
    [InlineData("1.2.3.0/+24")]
    [InlineData("1.2.3.0/ 24")]
    [InlineData("1.2.3/24")]
    [InlineData("010.0.0.0/8")]
    [InlineData(" 1.2.3.0/24")]
    [InlineData("[2001:db8::]/32")]
    [InlineData("fe80::%5/64")]
    [InlineData("1.2.3.0/24\n9.9.9.0/24")]
    public void Rejects_Bad_Input(string network)
    {
        Assert.Throws<FormatException>(() => { Cidr.Parse(network); });
    }

    [Fact]
    public void Containing_Starts_Clear_One_Set_Bit_At_A_Time_From_The_Right()
    {
        // 10.1.2.3 has 6 bits set, and clearing them one at a time from the right gives 6 starts below the address
        Assert.Equal(["10.1.2.3", "10.1.2.2", "10.1.2.0", "10.1.0.0", "10.0.0.0", "8.0.0.0", "0.0.0.0"], Starts("10.1.2.3"));
        Assert.Equal(["0.0.0.0"], Starts("0.0.0.0"));
        Assert.Equal(["::1", "::"], Starts("::1"));
        Assert.Equal(["::"], Starts("::"));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("1.1.1.1")]
    [InlineData("223.5.5.5")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("2001:db8::1")]
    [InlineData("2a02:26f0:ffff::8001")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void Containing_Starts_Are_The_Start_Of_Every_Prefix_Once_Highest_First(string address)
    {
        byte[] bytes = Cidr.Parse(address).Start;
        int totalBits = bytes.Length * 8;
        List<byte[]> starts = Cidr.ContainingStarts(bytes);

        var expected = new List<byte[]>();
        for (int prefix = totalBits; prefix >= 0; prefix--)
        {
            byte[] start = Cidr.Parse(address + "/" + prefix.ToString(CultureInfo.InvariantCulture)).Start;
            if (expected.Count == 0 || !expected[^1].AsSpan().SequenceEqual(start))
                expected.Add(start);
        }

        Assert.Equal(expected, starts);
        int setBits = bytes.Sum(b => System.Numerics.BitOperations.PopCount(b));
        Assert.Equal(setBits + 1, starts.Count);
        Assert.All(starts, start => Assert.Equal(bytes.Length, start.Length));
    }

    [Fact]
    public void Containing_Starts_Leave_The_Address_Unchanged()
    {
        byte[] address = [223, 5, 5, 5];

        Cidr.ContainingStarts(address);

        Assert.Equal(new byte[] { 223, 5, 5, 5 }, address);
    }

    private static List<string> Starts(string address) =>
        Cidr.ContainingStarts(Cidr.Parse(address).Start).Select(start => new System.Net.IPAddress(start).ToString()).ToList();
}
