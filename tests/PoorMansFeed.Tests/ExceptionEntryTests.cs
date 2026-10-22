using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>What the Settings page's exceptions editor and the saved list accept as a network, and how a note is kept to one line.</summary>
public sealed class ExceptionEntryTests
{
    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData(" 203.0.113.0/24 ", "203.0.113.0/24")]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("2001:DB8::/32", "2001:db8::/32")]
    [InlineData("2001:0db8:0000::/48", "2001:db8::/48")]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1/32")]
    [InlineData("::/0", "::/0")]
    public void A_network_or_address_is_read_as_its_canonical_block(string text, string expected)
    {
        Assert.True(ExceptionEntry.TryParseNetwork(text, out CidrBlock? network, out bool hostBitsCleared));

        Assert.Equal(expected, network.Text);
        Assert.False(hostBitsCleared);
    }

    [Theory]
    [InlineData("203.0.113.7/24", "203.0.113.0/24")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")]
    public void Host_bits_are_cleared_and_reported(string text, string expected)
    {
        Assert.True(ExceptionEntry.TryParseNetwork(text, out CidrBlock? network, out bool hostBitsCleared));

        Assert.Equal(expected, network.Text);
        Assert.True(hostBitsCleared);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("010.0.0.1")]
    [InlineData("10.1")]
    [InlineData("192.168.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("3232235777")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("[::1]")]
    [InlineData("[::1]:80")]
    [InlineData("fe80::1%5")]
    [InlineData("::ffff:192.0.2.0/120")]
    [InlineData("example.com")]
    [InlineData("AS15169")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(ExceptionEntry.TryParseNetwork(text, out CidrBlock? network, out bool hostBitsCleared));

        Assert.Null(network);
        Assert.False(hostBitsCleared);
    }

    [Fact]
    public void A_note_is_kept_to_one_trimmed_line()
    {
        Assert.Equal("Head office  VPN", ExceptionEntry.CleanNote("  Head office\r\nVPN\t "));
        Assert.Equal("a b c", ExceptionEntry.CleanNote("a\u2028b\u2029c"));
        Assert.Equal("", ExceptionEntry.CleanNote(null));
        Assert.Equal("", ExceptionEntry.CleanNote(" \n "));
    }

    [Fact]
    public void A_long_note_is_cut_without_splitting_a_surrogate_pair()
    {
        string exact = new('a', ExceptionEntry.MaxNoteLength);
        string split = new string('a', ExceptionEntry.MaxNoteLength - 1) + "\uD83D\uDE00";

        Assert.Equal(exact, ExceptionEntry.CleanNote(exact + "bcd"));
        Assert.Equal(new string('a', ExceptionEntry.MaxNoteLength - 1), ExceptionEntry.CleanNote(split));
    }

    [Fact]
    public void The_entry_keeps_the_cleaned_note()
    {
        Assert.True(ExceptionEntry.TryParseNetwork("192.0.2.0/24", out CidrBlock? network, out _));

        var entry = new ExceptionEntry(network, " Payments\npartner ");

        Assert.Equal("Payments partner", entry.Note);
        Assert.Throws<ArgumentNullException>(() => new ExceptionEntry(null!, "note"));
    }

    [Fact]
    public void A_list_keeps_the_first_entry_for_a_network_and_its_order()
    {
        ExceptionList list = ExceptionList.Create(
        [
            Entry("192.0.2.0/24", "first"),
            Entry("2001:db8::/32", "second"),
            Entry("192.0.2.9/24", "same network, other spelling"),
        ]);

        Assert.True(list.IsReadable);
        Assert.Equal(["192.0.2.0/24", "2001:db8::/32"], list.Entries.Select(entry => entry.Network.Text));
        Assert.Equal("first", list.Entries[0].Note);
    }

    [Fact]
    public void A_list_holds_at_most_the_cap()
    {
        var entries = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
            .Select(i => Entry($"10.{i / 256}.{i % 256}.0/24", null))
            .ToList();

        Assert.Equal(ExceptionList.MaxEntries, ExceptionList.Create(entries.Take(ExceptionList.MaxEntries)).Entries.Count);
        Assert.Throws<ArgumentException>(() => ExceptionList.Create(entries));
    }

    [Fact]
    public void The_empty_list_holds_nothing_and_the_unreadable_one_refuses_to_say_what_it_holds()
    {
        Assert.True(ExceptionList.Empty.IsReadable);
        Assert.Empty(ExceptionList.Empty.Entries);
        Assert.False(ExceptionList.Unreadable.IsReadable);
        Assert.Throws<InvalidOperationException>(() => ExceptionList.Unreadable.Entries);
    }

    internal static ExceptionEntry Entry(string network, string? note)
    {
        Assert.True(ExceptionEntry.TryParseNetwork(network, out CidrBlock? block, out _), network);
        return new ExceptionEntry(block, note);
    }
}
