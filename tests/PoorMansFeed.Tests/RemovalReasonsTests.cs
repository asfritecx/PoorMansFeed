using System.Collections;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class RemovalReasonsTests
{
    private static ReadableExceptions Exceptions(params string[] networks) =>
        ReadableExceptions.WrapForFeed(RangeSubtractionTests.List(networks))!;

    private static (byte[] Start, byte[] End)[] Ranges(params string[] cidrs) =>
        cidrs.Select(cidr => Cidr.Parse(cidr)).Select(range => (range.Item1, range.Item2)).ToArray();

    private static (byte[] Start, byte[] End) Span(string first, string last) => (Cidr.Parse(first).Item1, Cidr.Parse(last).Item1);

    private static RemovalReasons Reasons(string[]? exceptions = null, string[]? source = null, string[]? leftOut = null,
                                          FeedFamilies? off = null,
                                          FeedRemovalReason dropped = FeedRemovalReason.SourceDropped) =>
        new(Exceptions(exceptions ?? []), Ranges(source ?? []), Ranges(leftOut ?? []), off, dropped);

    private sealed class CountingRanges((byte[] Start, byte[] End)[] ranges) : IEnumerable<(byte[] Start, byte[] End)>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<(byte[] Start, byte[] End)> GetEnumerator()
        {
            Enumerations++;
            return ((IEnumerable<(byte[] Start, byte[] End)>)ranges).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(FeedFamilies.IPv4, "8.8.8.0/24", FeedRemovalReason.FamilyOff)]
    [InlineData(FeedFamilies.IPv4, "2001:4860::/32", FeedRemovalReason.SourceDropped)]
    [InlineData(FeedFamilies.IPv6, "2001:4860::/32", FeedRemovalReason.FamilyOff)]
    [InlineData(FeedFamilies.IPv6, "8.8.8.8", FeedRemovalReason.SourceDropped)]
    public void An_entry_of_a_family_switched_off_is_FamilyOff(FeedFamilies off, string text, FeedRemovalReason expected)
    {
        Assert.Equal(expected, Reasons(off: off).For(text));
    }

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("8.8.8.0/26")]
    [InlineData("8.8.8.127")]
    [InlineData("2001:4860::/31")]
    [InlineData("2001:4860::8888")]
    public void An_entry_the_exceptions_cover_in_part_or_whole_is_Exception(string text)
    {
        RemovalReasons reasons = Reasons(exceptions: ["8.8.8.0/25", "2001:4860::/32"]);

        Assert.Equal(FeedRemovalReason.Exception, reasons.For(text));
        Assert.Equal(FeedRemovalReason.SourceDropped, reasons.For("8.8.8.128/25"));
    }

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860::/32")]
    [InlineData("2001:4860::8888")]
    public void An_entry_the_left_out_ranges_hold_is_LeftOut(string text)
    {
        Assert.Equal(FeedRemovalReason.LeftOut, Reasons(leftOut: ["8.8.8.0/24", "2001:4860::/32"]).For(text));
    }

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("8.8.0.0/16")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860::/31")]
    [InlineData("2001:4860::8888")]
    public void An_entry_the_source_still_holds_some_of_is_NotTold(string text)
    {
        Assert.Equal(FeedRemovalReason.NotTold,
                     Reasons(source: ["8.8.8.0/25", "2001:4860::/32"], dropped: FeedRemovalReason.LeftTheList).For(text));
    }

    [Theory]
    [InlineData(FeedRemovalReason.SourceDropped)]
    [InlineData(FeedRemovalReason.LeftTheList)]
    public void An_entry_no_rule_explains_takes_the_dropped_reason(FeedRemovalReason dropped)
    {
        RemovalReasons reasons = Reasons(source: ["9.9.9.0/24"], leftOut: ["9.9.10.0/24"], dropped: dropped);

        Assert.Equal(dropped, reasons.For("8.8.8.0/24"));
        Assert.Equal(dropped, reasons.For("2001:4860::8888"));
    }

    [Fact]
    public void A_family_switched_off_comes_before_an_exception()
    {
        Assert.Equal(FeedRemovalReason.FamilyOff, Reasons(exceptions: ["8.8.8.0/24"], off: FeedFamilies.IPv4).For("8.8.8.0/24"));
    }

    [Fact]
    public void An_exception_comes_before_left_out()
    {
        Assert.Equal(FeedRemovalReason.Exception, Reasons(exceptions: ["8.8.8.0/24"], leftOut: ["8.8.8.0/24"]).For("8.8.8.0/24"));
    }

    [Fact]
    public void Left_out_comes_before_the_source()
    {
        Assert.Equal(FeedRemovalReason.LeftOut, Reasons(source: ["8.8.8.0/24"], leftOut: ["8.8.8.0/24"]).For("8.8.8.0/24"));
    }

    [Fact]
    public void The_source_comes_before_the_dropped_reason()
    {
        Assert.Equal(FeedRemovalReason.NotTold,
                     Reasons(source: ["8.8.8.0/24"], dropped: FeedRemovalReason.LeftTheList).For("8.8.8.0/24"));
    }

    [Fact]
    public void An_entry_one_address_wider_than_its_left_out_range_is_not_LeftOut()
    {
        (byte[] Start, byte[] End)[] leftOut = [Span("8.8.8.0", "8.8.8.254")];
        var withSource = new RemovalReasons(Exceptions(), Ranges("8.8.8.0/24"), leftOut, null, FeedRemovalReason.LeftTheList);
        var withoutSource = new RemovalReasons(Exceptions(), Ranges(), leftOut, null, FeedRemovalReason.LeftTheList);

        Assert.Equal(FeedRemovalReason.NotTold, withSource.For("8.8.8.0/24"));
        Assert.Equal(FeedRemovalReason.LeftTheList, withoutSource.For("8.8.8.0/24"));
        Assert.Equal(FeedRemovalReason.LeftOut, withoutSource.For("8.8.8.0/25"));
        Assert.Equal(FeedRemovalReason.LeftOut, withoutSource.For("8.8.8.254"));
    }

    [Theory]
    [InlineData("8.8.8.128/25", "8.8.8.0/25")]
    [InlineData("8.8.8.0/25", "8.8.8.128/25")]
    [InlineData("2001:4860:8000::/33", "2001:4860::/33")]
    public void Touching_left_out_ranges_that_together_hold_the_entry_give_LeftOut(string first, string second)
    {
        RemovalReasons reasons = Reasons(leftOut: [first, second, "8.8.8.0/26"]);

        Assert.Equal(FeedRemovalReason.LeftOut, reasons.For(first.Contains(':') ? "2001:4860::/32" : "8.8.8.0/24"));
    }

    [Theory]
    [InlineData("8.8.7.255", FeedRemovalReason.LeftTheList)]
    [InlineData("8.8.8.0", FeedRemovalReason.NotTold)]
    [InlineData("8.8.8.255", FeedRemovalReason.NotTold)]
    [InlineData("8.8.9.0", FeedRemovalReason.LeftTheList)]
    [InlineData("2001:4860::ffff", FeedRemovalReason.NotTold)]
    [InlineData("2001:4860::1:0", FeedRemovalReason.LeftTheList)]
    public void The_source_holds_its_first_and_last_address_and_nothing_either_side(string text, FeedRemovalReason expected)
    {
        Assert.Equal(expected,
                     Reasons(source: ["8.8.8.0/24", "2001:4860::/112"], dropped: FeedRemovalReason.LeftTheList).For(text));
    }

    [Theory]
    [InlineData("::a00:0/104", "10.0.0.0/8")]
    [InlineData("::a00:0/104", "10.1.2.3")]
    [InlineData("10.0.0.0/8", "::a00:0/104")]
    [InlineData("10.0.0.0/8", "::a01:203")]
    public void The_source_and_left_out_never_match_the_other_family(string range, string text)
    {
        Assert.Equal(FeedRemovalReason.LeftTheList, Reasons(source: [range], dropped: FeedRemovalReason.LeftTheList).For(text));
        Assert.Equal(FeedRemovalReason.LeftTheList, Reasons(leftOut: [range], dropped: FeedRemovalReason.LeftTheList).For(text));
        Assert.Equal(FeedRemovalReason.LeftOut, Reasons(leftOut: [range, text]).For(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("words")]
    [InlineData("10.0.0.0/33")]
    [InlineData("010.0.0.1")]
    [InlineData("8.8.8.8 ")]
    [InlineData("8.8.8.0/24 ")]
    [InlineData(null)]
    public void Text_that_is_no_feed_entry_is_NotTold(string? text)
    {
        RemovalReasons reasons = Reasons(exceptions: ["8.8.8.0/24", "10.0.0.0/8"], leftOut: ["8.0.0.0/8"], off: FeedFamilies.Both,
                                         dropped: FeedRemovalReason.LeftTheList);

        Assert.Equal(FeedRemovalReason.NotTold, reasons.For(text!));
    }

    [Fact]
    public void No_family_is_off_without_familiesOff_and_both_are_off_with_Both()
    {
        RemovalReasons none = Reasons(dropped: FeedRemovalReason.LeftTheList);
        RemovalReasons both = Reasons(off: FeedFamilies.Both, dropped: FeedRemovalReason.LeftTheList);

        foreach (string text in new[] { "8.8.8.8", "8.8.8.0/24", "2001:4860::8888", "2001:4860::/32" })
        {
            Assert.Equal(FeedRemovalReason.LeftTheList, none.For(text));
            Assert.Equal(FeedRemovalReason.FamilyOff, both.For(text));
        }
    }

    [Fact]
    public void The_constructor_checks_its_arguments()
    {
        ReadableExceptions exceptions = Exceptions();
        (byte[] Start, byte[] End)[] none = [];

        Assert.Throws<ArgumentNullException>(() => new RemovalReasons(null!, none, none, null, FeedRemovalReason.LeftTheList));
        Assert.Throws<ArgumentNullException>(() => new RemovalReasons(exceptions, null!, none, null, FeedRemovalReason.LeftTheList));
        Assert.Throws<ArgumentNullException>(() => new RemovalReasons(exceptions, none, null!, null, FeedRemovalReason.LeftTheList));
        foreach (FeedRemovalReason dropped in new[]
                 {
                     FeedRemovalReason.NotTold, FeedRemovalReason.Exception, FeedRemovalReason.LeftOut,
                     FeedRemovalReason.FamilyOff, (FeedRemovalReason)99,
                 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new RemovalReasons(exceptions, none, none, null, dropped));
    }

    public static TheoryData<bool, int, int> UnbuildableSets => new()
    {
        { true, 5, 5 }, { true, 4, 16 }, { true, 16, 4 }, { true, 0, 0 }, { true, -1, 4 },
        { false, 5, 5 }, { false, 4, 16 }, { false, -1, -1 },
    };

    [Theory]
    [MemberData(nameof(UnbuildableSets))]
    public void Sets_that_cannot_be_built_make_every_answer_NotTold(bool inSource, int startLength, int endLength)
    {
        // A length of -1 stands for a null array
        (byte[] Start, byte[] End) bad = (startLength < 0 ? null! : new byte[startLength], endLength < 0 ? null! : new byte[endLength]);
        (byte[] Start, byte[] End)[] good = Ranges("9.9.9.0/24");
        (byte[] Start, byte[] End)[] withBad = [.. good, bad];
        var reasons = new RemovalReasons(Exceptions("8.8.8.0/24"), inSource ? withBad : good, inSource ? good : withBad,
                                         FeedFamilies.IPv6, FeedRemovalReason.LeftTheList);

        foreach (string text in new[] { "8.8.8.0/24", "9.9.9.0/24", "1.1.1.1", "2001:4860::8888", "words" })
            Assert.Equal(FeedRemovalReason.NotTold, reasons.For(text));
    }

    [Fact]
    public void A_set_that_throws_while_it_is_read_makes_every_answer_NotTold()
    {
        IEnumerable<(byte[] Start, byte[] End)> throwing = Throwing();
        var reasons = new RemovalReasons(Exceptions(), throwing, Ranges(), null, FeedRemovalReason.LeftTheList);

        Assert.Equal(FeedRemovalReason.NotTold, reasons.For("8.8.8.0/24"));
        Assert.Equal(FeedRemovalReason.NotTold, reasons.For("8.8.8.0/24"));

        static IEnumerable<(byte[] Start, byte[] End)> Throwing()
        {
            yield return Cidr.Parse("9.9.9.0/24");
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public void The_sets_are_read_once_and_only_at_the_first_answer()
    {
        var source = new CountingRanges(Ranges("8.8.8.0/24"));
        var leftOut = new CountingRanges(Ranges("9.9.9.0/24"));

        var reasons = new RemovalReasons(Exceptions(), source, leftOut, null, FeedRemovalReason.LeftTheList);
        Assert.Equal(0, source.Enumerations);
        Assert.Equal(0, leftOut.Enumerations);

        Assert.Equal(FeedRemovalReason.NotTold, reasons.For("8.8.8.8"));
        Assert.Equal(FeedRemovalReason.LeftOut, reasons.For("9.9.9.9"));
        Assert.Equal(FeedRemovalReason.LeftTheList, reasons.For("1.1.1.1"));
        Assert.Equal(1, source.Enumerations);
        Assert.Equal(1, leftOut.Enumerations);
    }
}
