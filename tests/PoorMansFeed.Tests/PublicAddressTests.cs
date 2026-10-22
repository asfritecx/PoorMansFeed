using System.Net;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// The only form an address may take to leave the machine: made only from bytes <see cref="AddressScope"/> judged
/// public, with its text formatted from those bytes and nothing outside able to change them.
/// </summary>
public sealed class PublicAddressTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860::1")]
    [InlineData("2620:4f:8000::1")]
    public void A_public_address_is_made(string text)
    {
        byte[] bytes = Bytes(text);
        Assert.True(AddressScope.IsPublic(bytes));

        Assert.True(PublicAddress.TryCreate(bytes, out PublicAddress? value));
        Assert.Equal(bytes, value.Bytes);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("0.255.255.255")]
    [InlineData("::1")]
    [InlineData("64:ff9b::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:a00:1::")]
    public void An_address_that_is_not_public_is_refused(string text)
    {
        byte[] bytes = Bytes(text);
        Assert.False(AddressScope.IsPublic(bytes));

        Assert.False(PublicAddress.TryCreate(bytes, out PublicAddress? value));
        Assert.Null(value);
    }

    [Fact]
    public void An_IPv4_mapped_address_is_refused_as_its_sixteen_bytes()
    {
        byte[] mapped = new byte[16];
        mapped[10] = 0xff;
        mapped[11] = 0xff;
        Bytes("8.8.8.8").CopyTo(mapped, 12);
        Assert.False(AddressScope.IsPublic(mapped));

        Assert.False(PublicAddress.TryCreate(mapped, out PublicAddress? value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(17)]
    public void Any_other_length_is_refused(int length)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, (byte)8);

        Assert.False(PublicAddress.TryCreate(bytes, out PublicAddress? value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("2001:4860:0:0:0:0:0:8888", "2001:4860::8888")]
    public void Text_is_the_canonical_form_and_reads_back_as_the_same_bytes(string input, string expected)
    {
        byte[] bytes = Bytes(input);
        Assert.True(PublicAddress.TryCreate(bytes, out PublicAddress? value));

        Assert.Equal(expected, value.Text);
        Assert.True(IpAddressText.TryParse(value.Text, out IPAddress? reread));
        Assert.Equal(bytes, reread.GetAddressBytes());
    }

    [Fact]
    public void Bytes_is_a_copy_each_read()
    {
        Assert.True(PublicAddress.TryCreate(Bytes("8.8.8.8"), out PublicAddress? value));

        byte[] first = value.Bytes;
        first[0] = 10;

        Assert.Equal(Bytes("8.8.8.8"), value.Bytes);
        Assert.Equal("8.8.8.8", value.Text);
        Assert.NotSame(value.Bytes, value.Bytes);
    }

    [Fact]
    public void The_bytes_passed_in_are_copied()
    {
        byte[] source = Bytes("2001:4860::8888");
        Assert.True(PublicAddress.TryCreate(source, out PublicAddress? value));

        source[0] = 0xfe;
        source[1] = 0x80;

        Assert.Equal(Bytes("2001:4860::8888"), value.Bytes);
        Assert.Equal("2001:4860::8888", value.Text);
    }

    private static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }
}
