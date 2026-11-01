using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class SavedPickCarryTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private static CidrBlock Block(string cidr)
    {
        Assert.True(CidrBlock.TryParse(cidr, out CidrBlock? block));
        return block;
    }

    private static SeenRange Seen(string cidr, bool ticked, bool startsIncluded, bool? largeAnswer = null, bool? written = null) =>
        new(Block(cidr), ticked, startsIncluded, written ?? ticked, largeAnswer);

    private static SavedFeedPick Prior(string[]? ticked = null, string[]? unticked = null, string[]? included = null,
                                       string[]? leftOut = null) =>
        new(null, null, false, "Test", null, null, ticked ?? [], unticked ?? [], included ?? [], leftOut ?? [],
            null, null, null);

    [Fact]
    public void A_switched_off_family_keeps_its_hand_unticks_and_large_answers_the_check_never_saw()
    {
        SavedFeedPick prior = Prior(unticked: ["2001:db8::/32"], included: ["2001:db8:1::/48"], leftOut: ["2001:db8:2::/48"]);

        PickLists lists = SavedPickCarry.Lists([Seen("10.0.0.0/8", ticked: true, startsIncluded: true)], prior,
                                               FeedFamilies.IPv6);

        Assert.Empty(lists.HandTicked);
        Assert.Equal(["2001:db8::/32"], lists.HandUnticked);
        Assert.Equal(["2001:db8:1::/48"], lists.LargeIncluded);
        Assert.Equal(["2001:db8:2::/48"], lists.LargeLeftOut);
    }

    [Fact]
    public void A_family_still_on_forgets_what_the_source_no_longer_has()
    {
        SavedFeedPick prior = Prior(ticked: ["2001:db8:9::/48"], unticked: ["2001:db8::/32"], included: ["2001:db8:1::/48"],
                                    leftOut: ["2001:db8:2::/48", "10.9.0.0/16"]);

        PickLists lists = SavedPickCarry.Lists([Seen("10.0.0.0/8", ticked: true, startsIncluded: true)], prior, null);

        AssertEmpty(lists);
    }

    [Fact]
    public void Only_the_switched_off_family_is_carried()
    {
        SavedFeedPick prior = Prior(unticked: ["10.9.0.0/16", "2001:db8::/32"]);

        PickLists lists = SavedPickCarry.Lists([], prior, FeedFamilies.IPv4);

        Assert.Equal(["10.9.0.0/16"], lists.HandUnticked);
    }

    [Fact]
    public void A_hand_untick_is_kept_for_a_range_now_mixed_and_still_unticked()
    {
        PickLists lists = SavedPickCarry.Lists([Seen("34.1.0.0/16", ticked: false, startsIncluded: false)],
                                               Prior(unticked: ["34.1.0.0/16"]), null);

        Assert.Equal(["34.1.0.0/16"], lists.HandUnticked);
        Assert.Empty(lists.HandTicked);
    }

    [Fact]
    public void A_hand_tick_is_kept_for_a_range_now_confirmed_and_still_ticked()
    {
        PickLists lists = SavedPickCarry.Lists([Seen("34.1.0.0/16", ticked: true, startsIncluded: true)],
                                               Prior(ticked: ["34.1.0.0/16"]), null);

        Assert.Equal(["34.1.0.0/16"], lists.HandTicked);
        Assert.Empty(lists.HandUnticked);
    }

    [Fact]
    public void A_saved_tick_the_user_now_disagrees_with_is_dropped()
    {
        PickLists lists = SavedPickCarry.Lists(
            [
                Seen("34.1.0.0/16", ticked: true, startsIncluded: true),
                Seen("35.1.0.0/16", ticked: false, startsIncluded: false),
            ],
            Prior(ticked: ["35.1.0.0/16"], unticked: ["34.1.0.0/16"]), FeedFamilies.IPv6);

        Assert.Empty(lists.HandTicked);
        Assert.Empty(lists.HandUnticked);
    }

    [Fact]
    public void A_large_answer_not_asked_again_is_kept_only_while_the_write_agrees_with_it()
    {
        SavedFeedPick prior = Prior(included: ["10.0.0.0/8", "11.0.0.0/8"], leftOut: ["12.0.0.0/8", "13.0.0.0/8"]);

        PickLists lists = SavedPickCarry.Lists(
            [
                Seen("10.0.0.0/8", ticked: true, startsIncluded: true),
                Seen("11.0.0.0/8", ticked: false, startsIncluded: true),
                Seen("12.0.0.0/8", ticked: false, startsIncluded: true),
                Seen("13.0.0.0/8", ticked: true, startsIncluded: true),
            ],
            prior, FeedFamilies.IPv6);

        Assert.Equal(["10.0.0.0/8"], lists.LargeIncluded);
        Assert.Equal(["12.0.0.0/8"], lists.LargeLeftOut);
    }

    [Fact]
    public void Seen_ranges_come_in_check_order_then_carried_entries_in_saved_order()
    {
        SavedFeedPick prior = Prior(unticked: ["2001:db8:b::/48", "10.9.0.0/16", "2001:db8:a::/48", "10.1.0.0/16"],
                                    included: ["2001:db8:c::/48"]);

        PickLists lists = SavedPickCarry.Lists(
            [
                Seen("10.2.0.0/16", ticked: false, startsIncluded: true),
                Seen("10.1.0.0/16", ticked: false, startsIncluded: false),
                Seen("8.0.0.0/8", ticked: true, startsIncluded: true, largeAnswer: true),
            ],
            prior, FeedFamilies.IPv6);

        Assert.Equal(["10.2.0.0/16", "10.1.0.0/16", "2001:db8:b::/48", "2001:db8:a::/48"], lists.HandUnticked);
        Assert.Equal(["8.0.0.0/8", "2001:db8:c::/48"], lists.LargeIncluded);
    }

    [Fact]
    public void Each_text_comes_once_and_a_saved_entry_that_does_not_parse_is_dropped()
    {
        SavedFeedPick prior = Prior(unticked: ["2001:DB8::/32", "2001:db8::/32", "junk", "10.0.0.1", null!, "2001:db8::1/32"],
                                    leftOut: ["2001:db8:2::/48", "10.0.0.0/8"]);

        PickLists lists = SavedPickCarry.Lists(
            [
                Seen("10.0.0.0/8", ticked: false, startsIncluded: true, largeAnswer: false),
                Seen("10.0.0.0/8", ticked: false, startsIncluded: true, largeAnswer: false),
            ],
            prior, FeedFamilies.IPv6);

        Assert.Equal(["10.0.0.0/8", "2001:db8::/32"], lists.HandUnticked);
        Assert.Equal(["10.0.0.0/8", "2001:db8:2::/48"], lists.LargeLeftOut);
    }

    [Fact]
    public void No_saved_pick_gives_this_writes_own_lists()
    {
        SeenRange[] seen =
        [
            Seen("10.0.0.0/8", ticked: true, startsIncluded: true, largeAnswer: true),
            Seen("11.0.0.0/8", ticked: false, startsIncluded: true, largeAnswer: false),
            Seen("34.1.0.0/16", ticked: false, startsIncluded: true),
            Seen("35.1.0.0/16", ticked: true, startsIncluded: false),
            Seen("36.1.0.0/16", ticked: false, startsIncluded: false),
        ];

        PickLists lists = SavedPickCarry.Lists(seen, null, FeedFamilies.IPv6);

        Assert.Equal(["35.1.0.0/16"], lists.HandTicked);
        Assert.Equal(["11.0.0.0/8", "34.1.0.0/16"], lists.HandUnticked);
        Assert.Equal(["10.0.0.0/8"], lists.LargeIncluded);
        Assert.Equal(["11.0.0.0/8"], lists.LargeLeftOut);
    }

    [Fact]
    public void A_saved_pick_with_missing_lists_never_throws()
    {
        var prior = new SavedFeedPick(null, null, false, null, null, null, null!, null!, null!, null!, null, null, null);

        PickLists lists = SavedPickCarry.Lists([Seen("10.0.0.0/8", ticked: true, startsIncluded: true)], prior,
                                               FeedFamilies.Both);

        AssertEmpty(lists);
    }

    [Fact]
    public void The_prior_pick_is_the_saved_feed_of_the_same_key_ignoring_case_and_the_same_kind()
    {
        SavedFeedPick pick = Prior(unticked: ["10.0.0.0/8"]);
        SavedFeedPick other = Prior(unticked: ["11.0.0.0/8"]);
        var read = new SavedFeedsRead(true,
        [
            new SavedFeed("AWS_Test", FeedKind.Aws, other, T, null, false),
            new SavedFeed("Azure_Test", FeedKind.Azure, pick, T, null, false),
        ]);

        Assert.Same(pick, SavedPickCarry.Prior(new FakeRecords(read), "AZURE_test", FeedKind.Azure));
        Assert.Null(SavedPickCarry.Prior(new FakeRecords(read), "Azure_Test", FeedKind.Aws));
        Assert.Null(SavedPickCarry.Prior(new FakeRecords(read), "Azure_Other", FeedKind.Azure));
        Assert.Null(SavedPickCarry.Prior(new FakeRecords(read with { Available = false }), "Azure_Test", FeedKind.Azure));
        Assert.Null(SavedPickCarry.Prior(new FakeRecords(null), "Azure_Test", FeedKind.Azure));
    }

    private static void AssertEmpty(PickLists lists)
    {
        Assert.Empty(lists.HandTicked);
        Assert.Empty(lists.HandUnticked);
        Assert.Empty(lists.LargeIncluded);
        Assert.Empty(lists.LargeLeftOut);
    }

    [Fact]
    public void The_seen_list_holds_every_unconfirmed_range_in_source_order_ticked_or_not()
    {
        PickLists lists = SavedPickCarry.Lists(
            [Seen("36.1.0.0/16", ticked: false, startsIncluded: false), Seen("34.1.0.0/16", ticked: true, startsIncluded: true),
             Seen("35.1.0.0/16", ticked: true, startsIncluded: false), Seen("2001:db8::/32", ticked: false, startsIncluded: false)],
            Prior() with { SeenUnconfirmed = ["9.9.0.0/16", "2001:db8:5::/48"] }, null);

        Assert.Equal(["36.1.0.0/16", "35.1.0.0/16", "2001:db8::/32"], lists.SeenUnconfirmed);
    }

    [Fact]
    public void The_seen_list_carries_the_prior_entries_of_a_switched_off_family_only()
    {
        SavedFeedPick prior = Prior() with { SeenUnconfirmed = ["35.1.0.0/16", "2001:db8::/32", "10.9.0.0/16", "2001:db8:5::/48"] };

        PickLists lists = SavedPickCarry.Lists(
            [Seen("36.1.0.0/16", ticked: false, startsIncluded: false), Seen("35.1.0.0/16", ticked: true, startsIncluded: true)],
            prior, FeedFamilies.IPv6);

        Assert.Equal(["36.1.0.0/16", "2001:db8::/32", "2001:db8:5::/48"], lists.SeenUnconfirmed);
        Assert.Empty(SavedPickCarry.Lists([], prior with { SeenUnconfirmed = null }, FeedFamilies.Both).SeenUnconfirmed);
    }

    private sealed class FakeRecords(SavedFeedsRead? read) : IFeedRecords
    {
        FeedRecordResult IFeedRecords.Record(FeedRecordRequest request) => throw new NotSupportedException();

        public IDisposable EnterWrite(string feedKey) => throw new NotSupportedException();

        public int WritesInFlight => 0;

        public Task StopWritesAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<PoorMansFeed.Core.History.HistoryRead> SearchHistoryAsync(PoorMansFeed.Core.History.HistoryQuery query,
                                                                             int limit, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PoorMansFeed.Core.History.HistoryState> ClearHistoryAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        // Null stands for a store that throws, which the lookup must survive
        public SavedFeedsRead ReadSavedFeeds() => read ?? throw new InvalidOperationException();

        IReadOnlyList<SavedFeedStampOutcome> IFeedRecords.CommitRefresh(IReadOnlyList<SavedFeedStamp> stamps) =>
            throw new NotSupportedException();
        bool IFeedRecords.SaveRefusedThisSession(string feedKey) => false;

        public Task<SavedFeedsFailure> RemoveSavedFeedAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
