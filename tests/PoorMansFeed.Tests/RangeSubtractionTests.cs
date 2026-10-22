using System.Net;
using System.Numerics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>Cutting exceptions out of a range: the exact pieces, the edges of a range and of the address space, and a brute-force check.</summary>
public sealed class RangeSubtractionTests
{
    [Fact]
    public void A_range_no_exception_touches_is_left_as_it_was()
    {
        ExceptionList list = List("10.0.1.0/24", "2001:db8::/32");

        Assert.Null(Cut(list, "10.0.0.0/24"));
        Assert.Null(Cut(list, "10.0.2.0/24"));
        Assert.Null(Cut(ExceptionList.Empty, "10.0.0.0/8"));
    }

    [Theory]
    [InlineData("10.0.0.0/24", "10.0.1.0/32")]   // the address after the range's last
    [InlineData("10.0.1.0/24", "10.0.0.255/32")] // the address before the range's first
    [InlineData("2001:db8::/64", "2001:db8:0:1::/64")]
    [InlineData("2001:db8:0:1::/64", "2001:db8::ffff:ffff:ffff:ffff/128")]
    public void An_exception_next_to_the_range_does_not_cut_it(string range, string exception)
    {
        Assert.Null(Cut(List(exception), range));
    }

    [Theory]
    [InlineData("10.0.0.0/24", "10.0.0.0/24")]
    [InlineData("10.0.0.0/24", "10.0.0.0/16")]
    [InlineData("10.0.0.7/32", "10.0.0.0/29")]
    [InlineData("2001:db8::/48", "2001:db8::/32")]
    public void A_range_an_exception_covers_is_removed(string range, string exception)
    {
        IReadOnlyList<string>? pieces = Cut(List(exception), range);

        Assert.NotNull(pieces);
        Assert.Empty(pieces);
    }

    [Fact]
    public void A_slash_16_less_one_address_is_sixteen_blocks()
    {
        IReadOnlyList<string>? pieces = Cut(List("10.0.0.0"), "10.0.0.0/16");

        Assert.Equal(
            [
                "10.0.0.1/32", "10.0.0.2/31", "10.0.0.4/30", "10.0.0.8/29", "10.0.0.16/28", "10.0.0.32/27", "10.0.0.64/26",
                "10.0.0.128/25", "10.0.1.0/24", "10.0.2.0/23", "10.0.4.0/22", "10.0.8.0/21", "10.0.16.0/20", "10.0.32.0/19",
                "10.0.64.0/18", "10.0.128.0/17",
            ],
            pieces);
    }

    [Fact]
    public void An_exception_in_the_middle_leaves_both_sides()
    {
        Assert.Equal(["192.0.2.0/26", "192.0.2.128/25"], Cut(List("192.0.2.64/26"), "192.0.2.0/24"));
        Assert.Equal(["192.0.2.0/25", "192.0.2.192/26"], Cut(List("192.0.2.128/26"), "192.0.2.0/24"));
        Assert.Equal(["2001:db8::/33"], Cut(List("2001:db8:8000::/33"), "2001:db8::/32"));
    }

    [Fact]
    public void The_last_address_of_a_range_can_be_cut()
    {
        Assert.Equal(
            ["198.51.100.0/25", "198.51.100.128/26", "198.51.100.192/27", "198.51.100.224/28", "198.51.100.240/29",
             "198.51.100.248/30", "198.51.100.252/31", "198.51.100.254/32"],
            Cut(List("198.51.100.255"), "198.51.100.0/24"));
    }

    [Fact]
    public void Several_exceptions_in_one_range_are_cut_in_one_pass()
    {
        ExceptionList list = List("10.0.0.0/26", "10.0.0.128/27", "10.0.0.255", "10.1.0.0/16");

        Assert.Equal(["10.0.0.64/26", "10.0.0.160/27", "10.0.0.192/27", "10.0.0.224/28", "10.0.0.240/29", "10.0.0.248/30",
                      "10.0.0.252/31", "10.0.0.254/32"],
                     Cut(list, "10.0.0.0/24"));
    }

    [Fact]
    public void Exceptions_that_overlap_or_touch_are_merged()
    {
        List<AddressInterval> merged = RangeSubtraction.Merge(
        [
            new AddressInterval(20, 29),
            new AddressInterval(0, 9),
            new AddressInterval(10, 12), // touches the first
            new AddressInterval(25, 40), // overlaps 20-29
            new AddressInterval(22, 23), // inside 20-29
            new AddressInterval(42, 50), // one address apart from 25-40, so it stays separate
        ]);

        Assert.Equal([new AddressInterval(0, 12), new AddressInterval(20, 40), new AddressInterval(42, 50)], merged);
    }

    [Fact]
    public void The_top_of_the_address_space_merges_without_overflowing()
    {
        List<AddressInterval> merged = RangeSubtraction.Merge(
        [
            new AddressInterval(UInt128.MaxValue - 10, UInt128.MaxValue),
            new AddressInterval(UInt128.MaxValue - 3, UInt128.MaxValue),
            new AddressInterval(UInt128.MaxValue, UInt128.MaxValue),
        ]);

        Assert.Equal([new AddressInterval(UInt128.MaxValue - 10, UInt128.MaxValue)], merged);
    }

    [Fact]
    public void The_whole_IPv4_space_less_its_first_and_last_address()
    {
        IReadOnlyList<string>? lessFirst = Cut(List("0.0.0.0"), "0.0.0.0/0");
        IReadOnlyList<string>? lessLast = Cut(List("255.255.255.255"), "0.0.0.0/0");

        Assert.NotNull(lessFirst);
        Assert.Equal(32, lessFirst.Count);
        Assert.Equal("0.0.0.1/32", lessFirst[0]);
        Assert.Equal("128.0.0.0/1", lessFirst[^1]);
        Assert.NotNull(lessLast);
        Assert.Equal(32, lessLast.Count);
        Assert.Equal("0.0.0.0/1", lessLast[0]);
        Assert.Equal("255.255.255.254/32", lessLast[^1]);
        AssertCovers("0.0.0.0/0", ["0.0.0.0/32"], lessFirst);
        AssertCovers("0.0.0.0/0", ["255.255.255.255/32"], lessLast);
    }

    [Fact]
    public void The_whole_IPv6_space_less_its_first_and_last_address()
    {
        IReadOnlyList<string>? lessFirst = Cut(List("::"), "::/0");
        IReadOnlyList<string>? lessLast = Cut(List("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), "::/0");

        Assert.NotNull(lessFirst);
        Assert.Equal(128, lessFirst.Count);
        Assert.Equal("::1/128", lessFirst[0]);
        Assert.Equal("8000::/1", lessFirst[^1]);
        Assert.NotNull(lessLast);
        Assert.Equal(128, lessLast.Count);
        Assert.Equal("::/1", lessLast[0]);
        Assert.Equal("ffff:ffff:ffff:ffff:ffff:ffff:ffff:fffe/128", lessLast[^1]);
        AssertCovers("::/0", ["::/128"], lessFirst);
        AssertCovers("::/0", ["ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/128"], lessLast);
    }

    [Fact]
    public void An_exception_of_the_whole_space_removes_everything_of_its_family_only()
    {
        ExceptionList list = List("::/0");

        Assert.Empty(Cut(list, "::/0")!);
        Assert.Empty(Cut(list, "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff/128")!);
        Assert.Null(Cut(list, "0.0.0.0/0"));
    }

    [Fact]
    public void An_exception_of_the_other_family_with_the_same_number_does_not_cut()
    {
        // ::a00:0/104 is 0x0A000000 to 0x0AFFFFFF as a number, the same numbers as 10.0.0.0/8
        ExceptionList ipv6 = List("::a00:0/104");
        ExceptionList ipv4 = List("10.0.0.0/8");

        Assert.Null(Cut(ipv6, "10.0.0.0/8"));
        Assert.Null(Cut(ipv4, "::a00:0/104"));
        Assert.False(ipv6.Covers(Address("10.1.2.3")));
        Assert.False(ipv4.Covers(Address("::a01:203")));
    }

    [Fact]
    public void Covers_is_true_from_the_first_to_the_last_address_only()
    {
        ExceptionList list = List("192.0.2.16/28", "2001:db8::/126");

        Assert.False(list.Covers(Address("192.0.2.15")));
        Assert.True(list.Covers(Address("192.0.2.16")));
        Assert.True(list.Covers(Address("192.0.2.31")));
        Assert.False(list.Covers(Address("192.0.2.32")));
        Assert.True(list.Covers(Address("2001:db8::3")));
        Assert.False(list.Covers(Address("2001:db8::4")));
        Assert.False(ExceptionList.Empty.Covers(Address("192.0.2.16")));
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("2001:db8::/32")]
    public void An_unreadable_list_refuses_to_cut_rather_than_cutting_nothing(string range)
    {
        // Unreadable holds no networks, so cutting with it would write every line whole
        Assert.Throws<InvalidOperationException>(() => Cut(ExceptionList.Unreadable, range));
    }

    [Theory]
    [InlineData("192.0.2.16")]
    [InlineData("2001:db8::3")]
    public void An_unreadable_list_refuses_to_say_whether_it_covers_an_address(string address)
    {
        Assert.Throws<InvalidOperationException>(() => ExceptionList.Unreadable.Covers(Address(address)));
    }

    [Fact]
    public void An_unreadable_list_refuses_to_give_its_entries()
    {
        // An empty list of entries would let a writer that skips the cut when there are none write every line whole
        Assert.Throws<InvalidOperationException>(() => ExceptionList.Unreadable.Entries);
        Assert.Empty(ExceptionList.Empty.Entries);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    public void Random_exceptions_leave_exactly_the_addresses_outside_them(bool ipv6, int seed)
    {
        // A /20 of 4,096 addresses, with exceptions inside it, across its edges and outside it
        var random = new Random(seed);
        string rangeText = ipv6 ? "2001:db8::f000/116" : "10.20.16.0/20";
        Assert.True(CidrBlock.TryParse(rangeText, out CidrBlock? range));
        UInt128 first = RangeSubtraction.ToNumber(range.Start);
        int bits = ipv6 ? 128 : 32;

        for (int round = 0; round < 200; round++)
        {
            var exceptions = new List<string>();
            int count = random.Next(1, 8);
            for (int i = 0; i < count; i++)
            {
                int prefix = bits - random.Next(0, 14);
                long offset = random.Next(-2048, 4096 + 2048);
                UInt128 address = offset < 0 ? first - (UInt128)(-offset) : first + (UInt128)offset;
                exceptions.Add(Format(address, ipv6) + "/" + prefix);
            }

            ExceptionList list = List([.. exceptions]);
            IReadOnlyList<string>? pieces = Cut(list, rangeText);

            // Worked out from the exception blocks one by one, not from the merged list the code uses
            var blocks = list.Entries
                .Select(entry => (Start: RangeSubtraction.ToNumber(entry.Network.Start), End: RangeSubtraction.ToNumber(entry.Network.End)))
                .ToList();
            var expected = new HashSet<UInt128>();
            for (UInt128 address = first; address <= first + 4095; address++)
            {
                if (!blocks.Any(block => block.Start <= address && address <= block.End))
                    expected.Add(address);
            }

            if (expected.Count == 4096)
            {
                Assert.Null(pieces);
                continue;
            }

            Assert.NotNull(pieces);
            var actual = new HashSet<UInt128>();
            UInt128? previousEnd = null;
            foreach (string piece in pieces)
            {
                // Canonical, inside the range, lowest first and not overlapping
                Assert.True(CidrBlock.TryParse(piece, out CidrBlock? block), piece);
                Assert.Equal(block.Text, piece);
                UInt128 start = RangeSubtraction.ToNumber(block.Start);
                UInt128 end = RangeSubtraction.ToNumber(block.End);
                Assert.True(previousEnd is null || start > previousEnd, piece);
                previousEnd = end;
                for (UInt128 address = start; address <= end; address++)
                    Assert.True(actual.Add(address));
            }

            Assert.True(expected.SetEquals(actual), string.Join(", ", exceptions));
            AssertNoTwoPiecesJoin(pieces);
        }
    }

    // The greedy split gives the fewest blocks: two neighbouring blocks of one size never make an aligned block twice the size
    private static void AssertNoTwoPiecesJoin(IReadOnlyList<string> pieces)
    {
        for (int i = 1; i < pieces.Count; i++)
        {
            Assert.True(CidrBlock.TryParse(pieces[i - 1], out CidrBlock? left));
            Assert.True(CidrBlock.TryParse(pieces[i], out CidrBlock? right));
            if (left.PrefixLength != right.PrefixLength || left.PrefixLength == 0)
                continue;

            bool adjacent = RangeSubtraction.ToNumber(left.End) + 1 == RangeSubtraction.ToNumber(right.Start);
            int hostBits = (left.IsIpv6 ? 128 : 32) - left.PrefixLength + 1;
            bool alignedPair = (RangeSubtraction.ToNumber(left.Start) & ((UInt128.One << hostBits) - 1)) == 0;
            Assert.False(adjacent && alignedPair, $"{pieces[i - 1]} and {pieces[i]} make one block");
        }
    }

    // The pieces and the exceptions together are the range: no address lost, none counted twice
    private static void AssertCovers(string rangeText, IReadOnlyList<string> exceptions, IReadOnlyList<string> pieces)
    {
        BigInteger total = 0;
        foreach (string text in pieces.Concat(exceptions))
        {
            Assert.True(CidrBlock.TryParse(text, out CidrBlock? block));
            total += BigInteger.One << ((block.IsIpv6 ? 128 : 32) - block.PrefixLength);
        }

        Assert.True(CidrBlock.TryParse(rangeText, out CidrBlock? range));
        Assert.Equal(BigInteger.One << ((range.IsIpv6 ? 128 : 32) - range.PrefixLength), total);
    }

    internal static ExceptionList List(params string[] networks)
    {
        var entries = new List<ExceptionEntry>();
        foreach (string text in networks)
        {
            Assert.True(ExceptionEntry.TryParseNetwork(text, out CidrBlock? network, out _), text);
            entries.Add(new ExceptionEntry(network, null));
        }

        return ExceptionList.Create(entries);
    }

    private static IReadOnlyList<string>? Cut(ExceptionList list, string range)
    {
        (byte[] start, byte[] end) = Cidr.Parse(range);
        return list.Cut(start, end);
    }

    private static byte[] Address(string text) => IPAddress.Parse(text).GetAddressBytes();

    private static byte[] Bytes(UInt128 value, bool ipv6)
    {
        byte[] bytes = new byte[ipv6 ? 16 : 4];
        if (ipv6)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        else
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    private static string Format(UInt128 value, bool ipv6) => new IPAddress(Bytes(value, ipv6)).ToString();
}
