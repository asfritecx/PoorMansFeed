using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Tests;

public sealed class RefreshPickTests
{
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 4, "abc", DateTimeOffset.UnixEpoch, true);

    private const RangeConfirmation Confirmed = RangeConfirmation.Confirmed;
    private const RangeConfirmation Mixed = RangeConfirmation.Mixed;
    private const RangeConfirmation NotIn = RangeConfirmation.NotInDatabase;

    private static CloudFeedCheck Check(params (string Text, RangeConfirmation Status, bool IsLarge)[] ranges) =>
        new(new FeedDescription("microsoft.com", ["Ranges: Test source"], "Azure_Test"), Dataset, includesIPv6: true, 16, 32,
            ranges.Select(range =>
            {
                Assert.True(CidrBlock.TryParse(range.Text, out CidrBlock? block));
                return new CheckedRange(new FeedPrefix(block, "a"), range.Status, [], range.IsLarge);
            }).ToList(),
            CloudFeedEngineTests.TestPick);

    private static SavedFeedPick Pick(string[]? ticked = null, string[]? unticked = null, string[]? largeIncluded = null,
                                      string[]? largeLeftOut = null, string[]? seen = null) =>
        new(null, null, false, "Test", null, null, ticked ?? [], unticked ?? [], largeIncluded ?? [], largeLeftOut ?? [],
            null, null, null, seen ?? []);

    private static string[] Texts(IEnumerable<CidrBlock> blocks) => blocks.Select(block => block.Text).ToArray();

    [Fact]
    public void An_empty_pick_ticks_exactly_the_confirmed_ranges()
    {
        CloudFeedCheck check = Check(("34.1.0.0/16", Confirmed, false), ("35.1.0.0/16", Mixed, false),
                                     ("36.1.0.0/16", NotIn, false), ("2001:db8::/32", Confirmed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick());

        Assert.Equal(["34.1.0.0/16", "2001:db8::/32"], Texts(picked.Ticked));
        Assert.Empty(picked.LargeIncluded);
        Assert.Empty(picked.NewLarge);
    }

    [Fact]
    public void A_confirmed_range_unticked_by_hand_is_out()
    {
        CloudFeedCheck check = Check(("34.1.0.0/16", Confirmed, false), ("34.2.0.0/16", Confirmed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(unticked: ["34.1.0.0/16"]));

        Assert.Equal(["34.2.0.0/16"], Texts(picked.Ticked));
    }

    [Fact]
    public void A_mixed_or_not_in_database_range_ticked_by_hand_is_in()
    {
        CloudFeedCheck check = Check(("35.1.0.0/16", Mixed, false), ("36.1.0.0/16", NotIn, false), ("37.1.0.0/16", Mixed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["35.1.0.0/16", "36.1.0.0/16"]));

        Assert.Equal(["35.1.0.0/16", "36.1.0.0/16"], Texts(picked.Ticked));
    }

    [Fact]
    public void A_range_in_both_hand_lists_is_ticked()
    {
        CloudFeedCheck check = Check(("34.1.0.0/16", Confirmed, false), ("35.1.0.0/16", Mixed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["34.1.0.0/16", "35.1.0.0/16"],
                                                             unticked: ["34.1.0.0/16", "35.1.0.0/16"]));

        Assert.Equal(["34.1.0.0/16", "35.1.0.0/16"], Texts(picked.Ticked));
        Assert.Empty(picked.NewUnconfirmed);
    }

    [Fact]
    public void A_large_ticked_range_keeps_its_answer_and_an_unanswered_one_is_new()
    {
        CloudFeedCheck check = Check(("10.0.0.0/8", Confirmed, true), ("11.0.0.0/8", Confirmed, true),
                                     ("12.0.0.0/8", Confirmed, true), ("34.1.0.0/16", Confirmed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(largeIncluded: ["10.0.0.0/8"], largeLeftOut: ["11.0.0.0/8"]));

        Assert.Equal(["10.0.0.0/8", "11.0.0.0/8", "12.0.0.0/8", "34.1.0.0/16"], Texts(picked.Ticked));
        Assert.Equal(["10.0.0.0/8"], Texts(picked.LargeIncluded));
        Assert.Equal(["12.0.0.0/8"], picked.NewLarge);
    }

    [Fact]
    public void A_large_range_unticked_is_in_no_large_list()
    {
        CloudFeedCheck check = Check(("10.0.0.0/8", Confirmed, true), ("11.0.0.0/8", Confirmed, true), ("12.0.0.0/8", Mixed, true));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(unticked: ["10.0.0.0/8", "11.0.0.0/8"], largeIncluded: ["10.0.0.0/8"],
                                                             seen: ["12.0.0.0/8"]));

        Assert.Empty(picked.Ticked);
        Assert.Empty(picked.LargeIncluded);
        Assert.Empty(picked.NewLarge);
    }

    [Fact]
    public void An_unconfirmed_range_is_new_until_seen_or_answered_by_hand()
    {
        CloudFeedCheck check = Check(("35.1.0.0/16", Mixed, false), ("36.1.0.0/16", NotIn, false), ("37.1.0.0/16", Mixed, false),
                                     ("38.1.0.0/16", NotIn, false), ("34.1.0.0/16", Confirmed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["37.1.0.0/16"], unticked: ["38.1.0.0/16"],
                                                             seen: ["35.1.0.0/16"]));

        Assert.Equal(["36.1.0.0/16"], picked.NewUnconfirmed);
    }

    [Fact]
    public void A_pick_with_no_seen_list_lists_every_unanswered_unconfirmed_range()
    {
        CloudFeedCheck check = Check(("35.1.0.0/16", Mixed, false), ("34.1.0.0/16", Confirmed, false), ("36.1.0.0/16", NotIn, false),
                                     ("37.1.0.0/16", Mixed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["37.1.0.0/16"]) with { SeenUnconfirmed = null });

        Assert.Equal(["35.1.0.0/16", "36.1.0.0/16"], picked.NewUnconfirmed);
    }

    [Fact]
    public void Pick_entries_that_match_no_range_are_ignored()
    {
        CloudFeedCheck check = Check(("34.1.0.0/16", Confirmed, false), ("35.1.0.0/16", Mixed, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["99.1.0.0/16"], unticked: ["98.1.0.0/16"],
                                                             largeIncluded: ["10.0.0.0/8"], largeLeftOut: ["11.0.0.0/8"],
                                                             seen: ["97.1.0.0/16"]));

        Assert.Equal(["34.1.0.0/16"], Texts(picked.Ticked));
        Assert.Empty(picked.LargeIncluded);
        Assert.Equal(["35.1.0.0/16"], picked.NewUnconfirmed);
    }

    [Fact]
    public void Every_list_keeps_the_check_order_with_no_duplicates()
    {
        CloudFeedCheck check = Check(("12.0.0.0/8", Confirmed, true), ("36.1.0.0/16", NotIn, false), ("10.0.0.0/8", Mixed, true),
                                     ("35.1.0.0/16", Mixed, false), ("11.0.0.0/8", Confirmed, true), ("34.1.0.0/16", Confirmed, false),
                                     ("12.0.0.0/8", Confirmed, true), ("36.1.0.0/16", NotIn, false));

        RefreshPicked picked = RefreshPick.Apply(check, Pick(ticked: ["10.0.0.0/8", "10.0.0.0/8"],
                                                             largeIncluded: ["11.0.0.0/8", "12.0.0.0/8", "10.0.0.0/8", "11.0.0.0/8"]));

        Assert.Equal(["12.0.0.0/8", "10.0.0.0/8", "11.0.0.0/8", "34.1.0.0/16"], Texts(picked.Ticked));
        Assert.Equal(["12.0.0.0/8", "10.0.0.0/8", "11.0.0.0/8"], Texts(picked.LargeIncluded));
        Assert.Equal(["36.1.0.0/16", "35.1.0.0/16"], picked.NewUnconfirmed);
    }
}
