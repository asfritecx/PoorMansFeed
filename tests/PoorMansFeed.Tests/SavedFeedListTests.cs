using System.Text;
using System.Text.Json.Nodes;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedListTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_793_361_600);
    private static readonly DateTimeOffset T1 = T0.AddHours(5);

    private const string V1Json =
        """{"version":1,"feeds":[{"key":"AS15169","kind":"Asn","lastWriteUtc":1793361600,"lastRefreshUtc":null,"retired":false,"pick":{"asn":"15169","googleScope":null,"googleAddsGlobal":false,"azureTag":null,"awsService":null,"awsRegion":null,"handTicked":[],"handUnticked":[],"largeIncluded":[],"largeLeftOut":[],"largeRangeCheckEnabled":true,"ipv4MinPrefix":16,"ipv6MinPrefix":32}}]}""";

    private const string SampleJson =
        """{"version":2,"feeds":[{"key":"AS15169","kind":"Asn","lastWriteUtc":1793361600,"lastRefreshUtc":null,"retired":false,"pick":{"asn":"15169","googleScope":null,"googleAddsGlobal":false,"azureTag":null,"awsService":null,"awsRegion":null,"handTicked":[],"handUnticked":[],"largeIncluded":[],"largeLeftOut":[],"largeRangeCheckEnabled":true,"ipv4MinPrefix":16,"ipv6MinPrefix":32,"seenUnconfirmed":[]},"proof":{"settings":"types=fortigate;ipv4=1;ipv6=1;split=1;ipv6enabled=1","files":{"fortigate/AS15169_ipv4.txt":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","fortigate/AS15169_ipv6.txt":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}}]}""";

    internal static SavedFeedWriteProof SampleProof() =>
        new("types=fortigate;ipv4=1;ipv6=1;split=1;ipv6enabled=1", new Dictionary<string, string>
        {
            ["fortigate/AS15169_ipv6.txt"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ["fortigate/AS15169_ipv4.txt"] = new string('a', 64),
        });

    internal static SavedFeedPick AsnPick(string asn = "15169") =>
        new(asn, null, false, null, null, null, [], [], [], [], true, 16, 32);

    internal static SavedFeedPick GooglePick() =>
        new(null, "us-east1", true, null, null, null, ["34.0.0.0/24"], ["2600:1900::/28"], ["8.8.0.0/16"], ["9.9.0.0/16"],
            null, null, null);

    internal static SavedFeedPick AzurePick() =>
        new(null, null, false, "AzureCloud.uksouth", null, null, [], ["20.0.0.0/24"], [], [], null, null, null);

    internal static SavedFeedPick AwsPick(string? region = null) =>
        new(null, null, false, null, "AMAZON", region, [], [], [], [], null, null, null);

    internal static SavedFeedPick RollingPick() => new(null, null, false, null, null, null, [], [], [], [], null, null, null);

    private static SavedFeedList Written(SavedFeedList list, string key, FeedKind kind, SavedFeedPick pick, DateTimeOffset at,
                                         SavedFeedWriteProof? proof = null)
    {
        (SavedFeedsFailure failure, SavedFeedList next) = list.Apply(key, kind, new SavedFeedIntent.Written(pick, proof), at);
        Assert.Equal(SavedFeedsFailure.None, failure);
        return next;
    }

    private static SavedFeedList EveryKind()
    {
        SavedFeedList list = SavedFeedList.Empty;
        list = Written(list, "AS15169", FeedKind.Asn, AsnPick(), T0);
        list = Written(list, "GoogleCloud_us-east1_global", FeedKind.GoogleCloud, GooglePick(), T0);
        list = Written(list, "Azure_AzureCloud_uksouth", FeedKind.Azure, AzurePick(), T0);
        list = Written(list, "AWS_AMAZON_all", FeedKind.Aws, AwsPick(), T1);
        list = Written(list, "AWS_AMAZON_eu-west-1", FeedKind.Aws, AwsPick("eu-west-1"), T1);
        list = Written(list, "AbuseIPDB_rolling", FeedKind.AbuseIpDbRolling, RollingPick(), T1);
        (_, list) = list.Apply("Azure_AzureCloud_uksouth", FeedKind.Azure, new SavedFeedIntent.Retired(), T1);
        return list;
    }

    private static void AssertSameFeed(SavedFeed expected, SavedFeed actual)
    {
        Assert.Equal(expected with { Pick = null!, Proof = null }, actual with { Pick = null!, Proof = null });
        AssertSamePick(expected.Pick, actual.Pick);
        AssertSameProof(expected.Proof, actual.Proof);
    }

    private static void AssertSamePick(SavedFeedPick expected, SavedFeedPick actual)
    {
        Assert.Equal(expected.HandTicked, actual.HandTicked);
        Assert.Equal(expected.HandUnticked, actual.HandUnticked);
        Assert.Equal(expected.LargeIncluded, actual.LargeIncluded);
        Assert.Equal(expected.LargeLeftOut, actual.LargeLeftOut);
        Assert.Equal(expected.SeenUnconfirmed ?? [], actual.SeenUnconfirmed ?? []);
        Assert.Equal(expected with { HandTicked = [], HandUnticked = [], LargeIncluded = [], LargeLeftOut = [], SeenUnconfirmed = null },
                     actual with { HandTicked = [], HandUnticked = [], LargeIncluded = [], LargeLeftOut = [], SeenUnconfirmed = null },
                     new PickScalars());
    }

    internal static void AssertSameProof(SavedFeedWriteProof? expected, SavedFeedWriteProof? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null || actual is null)
            return;
        Assert.Equal(expected.SettingsFingerprint, actual.SettingsFingerprint);
        Assert.Equal(expected.FileSha256.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                     actual.FileSha256.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private sealed class PickScalars : IEqualityComparer<SavedFeedPick>
    {
        public bool Equals(SavedFeedPick? x, SavedFeedPick? y) =>
            x is not null && y is not null && x.Asn == y.Asn && x.GoogleScope == y.GoogleScope
            && x.GoogleAddsGlobal == y.GoogleAddsGlobal && x.AzureTag == y.AzureTag && x.AwsService == y.AwsService
            && x.AwsRegion == y.AwsRegion && x.LargeRangeCheckEnabled == y.LargeRangeCheckEnabled
            && x.IPv4MinPrefix == y.IPv4MinPrefix && x.IPv6MinPrefix == y.IPv6MinPrefix;

        public int GetHashCode(SavedFeedPick obj) => 0;
    }

    // ----- The JSON -----

    [Fact]
    public void One_ASN_feed_is_written_as_the_documented_json()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof());

        Assert.Equal(SampleJson, Encoding.UTF8.GetString(list.ToJson()));
    }

    [Fact]
    public void A_list_of_every_kind_round_trips_byte_for_byte()
    {
        SavedFeedList list = EveryKind();
        byte[] json = list.ToJson();

        SavedFeedList? read = SavedFeedList.TryRead(json);

        Assert.NotNull(read);
        Assert.True(read.IsAvailable);
        Assert.Equal(json, read.ToJson());
        Assert.Equal(list.Feeds.Count, read.Feeds.Count);
        for (int i = 0; i < list.Feeds.Count; i++)
            AssertSameFeed(list.Feeds[i], read.Feeds[i]);
        Assert.Equal(
            [FeedKind.AbuseIpDbRolling, FeedKind.Asn, FeedKind.Aws, FeedKind.Aws, FeedKind.Azure, FeedKind.GoogleCloud],
            read.Feeds.Select(feed => feed.Kind));
    }

    [Fact]
    public void A_refreshed_and_retired_feed_round_trips_byte_for_byte()
    {
        string json = SampleJson.Replace("\"lastRefreshUtc\":null,\"retired\":false",
                                         "\"lastRefreshUtc\":1793400000,\"retired\":true", StringComparison.Ordinal);

        SavedFeedList? read = SavedFeedList.TryRead(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(read);
        SavedFeed feed = Assert.Single(read.Feeds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_793_400_000), feed.LastRefreshUtc);
        Assert.True(feed.Retired);
        Assert.Equal(json, Encoding.UTF8.GetString(read.ToJson()));
    }

    // ----- Written -----

    [Fact]
    public void Written_adds_new_keys_in_order_ignoring_case()
    {
        SavedFeedList list = SavedFeedList.Empty;
        list = Written(list, "AWS_AMAZON_all", FeedKind.Aws, AwsPick(), T0);
        list = Written(list, "abc", FeedKind.Aws, AwsPick(), T0);
        list = Written(list, "AS15169", FeedKind.Asn, AsnPick(), T0);

        Assert.Equal(["abc", "AS15169", "AWS_AMAZON_all"], list.Feeds.Select(feed => feed.Key));
        SavedFeed added = list.Feeds[1];
        Assert.Equal(T0, added.LastWriteUtc);
        Assert.Null(added.LastRefreshUtc);
        Assert.False(added.Retired);
        Assert.Same(SavedFeedList.Empty, SavedFeedList.Empty);
        Assert.Empty(SavedFeedList.Empty.Feeds);
    }

    [Fact]
    public void Written_replaces_a_saved_key_keeping_its_refresh_and_clearing_retired()
    {
        string json = SampleJson.Replace("\"key\":\"AS15169\"", "\"key\":\"as15169\"", StringComparison.Ordinal)
                                .Replace("\"lastRefreshUtc\":null,\"retired\":false",
                                         "\"lastRefreshUtc\":1793400000,\"retired\":true", StringComparison.Ordinal);
        SavedFeedList list = SavedFeedList.TryRead(Encoding.UTF8.GetBytes(json))!;
        SavedFeedPick pick = AsnPick() with { LargeIncluded = ["8.8.0.0/16"], IPv4MinPrefix = 20 };

        SavedFeedList next = Written(list, "AS15169", FeedKind.Asn, pick, T1);

        SavedFeed feed = Assert.Single(next.Feeds);
        Assert.Equal("AS15169", feed.Key);
        Assert.Equal(T1, feed.LastWriteUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_793_400_000), feed.LastRefreshUtc);
        Assert.False(feed.Retired);
        AssertSamePick(pick, feed.Pick);
        Assert.Single(list.Feeds);
        Assert.True(list.Feeds[0].Retired);
    }

    [Fact]
    public void Written_stores_whole_seconds_and_copies_the_lists()
    {
        var ticked = new List<string> { "34.0.0.0/24" };
        SavedFeedPick pick = GooglePick() with { HandTicked = ticked };

        SavedFeedList list = Written(SavedFeedList.Empty, "G", FeedKind.GoogleCloud, pick, T0.AddMilliseconds(999));
        ticked.Add("35.0.0.0/24");

        Assert.Equal(T0, list.Feeds[0].LastWriteUtc);
        Assert.Equal(TimeSpan.Zero, list.Feeds[0].LastWriteUtc.Offset);
        Assert.Equal(["34.0.0.0/24"], list.Feeds[0].Pick.HandTicked);
    }

    [Fact]
    public void Written_with_nothing_new_is_the_same_list()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0);

        (SavedFeedsFailure failure, SavedFeedList next) =
            list.Apply("AS15169", FeedKind.Asn, new SavedFeedIntent.Written(AsnPick()), T0.AddMilliseconds(5));

        Assert.Equal(SavedFeedsFailure.None, failure);
        Assert.Same(list, next);
    }

    // ----- Retired and Remove -----

    [Fact]
    public void Retired_marks_only_a_saved_key_and_changes_nothing_else()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0);

        (SavedFeedsFailure failure, SavedFeedList next) =
            list.Apply("as15169", FeedKind.Asn, new SavedFeedIntent.Retired(), T1);

        Assert.Equal(SavedFeedsFailure.None, failure);
        SavedFeed feed = Assert.Single(next.Feeds);
        Assert.True(feed.Retired);
        Assert.Equal("AS15169", feed.Key);
        Assert.Equal(T0, feed.LastWriteUtc);
        AssertSamePick(AsnPick(), feed.Pick);

        (SavedFeedsFailure again, SavedFeedList same) = next.Apply("AS15169", FeedKind.Asn, new SavedFeedIntent.Retired(), T1);
        Assert.Equal(SavedFeedsFailure.None, again);
        Assert.Same(next, same);
    }

    [Fact]
    public void Retired_on_a_key_not_saved_is_not_found_and_saves_nothing()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0);

        (SavedFeedsFailure failure, SavedFeedList next) = list.Apply("AS1", FeedKind.Asn, new SavedFeedIntent.Retired(), T1);

        Assert.Equal(SavedFeedsFailure.NotFound, failure);
        Assert.Same(list, next);
        Assert.Single(next.Feeds);
    }

    [Fact]
    public void Remove_removes_a_saved_key_or_is_not_found()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0);
        list = Written(list, "AbuseIPDB_rolling", FeedKind.AbuseIpDbRolling, RollingPick(), T0);

        (SavedFeedsFailure failure, SavedFeedList next) = list.Remove("as15169");
        Assert.Equal(SavedFeedsFailure.None, failure);
        Assert.Equal(["AbuseIPDB_rolling"], next.Feeds.Select(feed => feed.Key));

        (SavedFeedsFailure missing, SavedFeedList same) = next.Remove("AS15169");
        Assert.Equal(SavedFeedsFailure.NotFound, missing);
        Assert.Same(next, same);
        Assert.Throws<ArgumentException>(() => next.Remove("a b"));
    }

    // ----- The caps -----

    [Fact]
    public void The_feed_cap_takes_the_last_feed_and_refuses_one_more()
    {
        SavedFeedList list = SavedFeedList.Empty;
        for (int i = 0; i < SavedFeedList.MaxFeeds; i++)
            list = Written(list, $"AS{i + 1}", FeedKind.Asn, AsnPick((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), T0);
        Assert.Equal(500, list.Feeds.Count);

        (SavedFeedsFailure failure, SavedFeedList next) =
            list.Apply("AS99999", FeedKind.Asn, new SavedFeedIntent.Written(AsnPick("99999")), T0);
        Assert.Equal(SavedFeedsFailure.TooMany, failure);
        Assert.Same(list, next);

        Assert.Equal(500, Written(list, "AS1", FeedKind.Asn, AsnPick(), T1).Feeds.Count);
    }

    [Fact]
    public void The_range_cap_takes_a_full_list_and_refuses_one_more()
    {
        List<string> full = Enumerable.Range(0, SavedFeedList.MaxRangesPerList)
                                      .Select(i => $"10.{i >> 8 & 0xFF}.{i & 0xFF}.0/24").ToList();
        Assert.Equal(65_536, full.Distinct().Count());

        SavedFeedList list = Written(SavedFeedList.Empty, "G", FeedKind.GoogleCloud, GooglePick() with { LargeLeftOut = full }, T0);
        Assert.Equal(65_536, list.Feeds[0].Pick.LargeLeftOut.Count);
        Assert.True(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick() with { LargeLeftOut = full }));

        List<string> over = [.. full, "11.0.0.0/24"];
        (SavedFeedsFailure failure, SavedFeedList next) =
            list.Apply("G", FeedKind.GoogleCloud, new SavedFeedIntent.Written(GooglePick() with { HandUnticked = over }), T1);
        Assert.Equal(SavedFeedsFailure.TooMany, failure);
        Assert.Same(list, next);
        Assert.False(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick() with { HandUnticked = over }));
    }

    [Fact]
    public void The_json_cap_takes_a_list_at_the_cap_and_refuses_one_byte_more()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0);
        var intent = new SavedFeedIntent.Written(GooglePick());
        int length = Written(list, "G", FeedKind.GoogleCloud, GooglePick(), T0).ToJson().Length;

        (SavedFeedsFailure atCap, SavedFeedList taken) = list.Apply("G", FeedKind.GoogleCloud, intent, T0, length);
        (SavedFeedsFailure over, SavedFeedList refused) = list.Apply("G", FeedKind.GoogleCloud, intent, T0, length - 1);

        Assert.Equal(SavedFeedsFailure.None, atCap);
        Assert.Equal(2, taken.Feeds.Count);
        Assert.Equal(SavedFeedsFailure.TooMany, over);
        Assert.Same(list, refused);
        Assert.Equal(16 * 1024 * 1024, SavedFeedList.MaxJsonBytes);
    }

    // ----- Unavailable -----

    [Fact]
    public void An_unavailable_list_refuses_every_change()
    {
        SavedFeedList list = SavedFeedList.Unavailable;

        Assert.False(list.IsAvailable);
        Assert.Empty(list.Feeds);
        (SavedFeedsFailure written, SavedFeedList a) = list.Apply("AS1", FeedKind.Asn, new SavedFeedIntent.Written(AsnPick("1")), T0);
        (SavedFeedsFailure retired, SavedFeedList b) = list.Apply("AS1", FeedKind.Asn, new SavedFeedIntent.Retired(), T0);
        (SavedFeedsFailure removed, SavedFeedList c) = list.Remove("AS1");

        Assert.Equal([SavedFeedsFailure.Unavailable, SavedFeedsFailure.Unavailable, SavedFeedsFailure.Unavailable],
                     [written, retired, removed]);
        Assert.Same(list, a);
        Assert.Same(list, b);
        Assert.Same(list, c);
        Assert.Throws<InvalidOperationException>(() => list.ToJson());
        Assert.True(SavedFeedList.Empty.IsAvailable);
    }

    // ----- Keys and picks -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("AS1\n")]
    [InlineData("\u00e4")]
    [InlineData("201")]
    public void An_invalid_key_throws(string? key)
    {
        if (key == "201")
            key = new string('a', 201);

        Assert.False(SavedFeedList.IsValidKey(key));
        Assert.Throws<ArgumentException>(() => SavedFeedList.Empty.Apply(key!, FeedKind.Asn, new SavedFeedIntent.Written(AsnPick()), T0));
        Assert.Throws<ArgumentException>(() => SavedFeedList.Unavailable.Apply(key!, FeedKind.Asn, new SavedFeedIntent.Retired(), T0));
        Assert.ThrowsAny<ArgumentException>(() => SavedFeedList.Empty.Remove(key!));
    }

    [Theory]
    [InlineData("AS15169")]
    [InlineData("GoogleCloud_us-east1_global")]
    [InlineData("a.b-c_D9")]
    [InlineData("200")]
    public void A_valid_key_is_taken(string key)
    {
        if (key == "200")
            key = new string('a', 200);

        Assert.True(SavedFeedList.IsValidKey(key));
        Assert.Single(Written(SavedFeedList.Empty, key, FeedKind.Asn, AsnPick(), T0).Feeds);
    }

    public static TheoryData<string> InvalidPicks => new(PickCases.Keys);

    private static readonly Dictionary<string, (FeedKind Kind, SavedFeedPick Pick)> PickCases = new(StringComparer.Ordinal)
    {
        ["asn: not digits"] = (FeedKind.Asn, AsnPick("AS1")),
        ["asn: empty"] = (FeedKind.Asn, AsnPick("")),
        ["asn: null"] = (FeedKind.Asn, AsnPick() with { Asn = null }),
        ["asn: eleven digits"] = (FeedKind.Asn, AsnPick("12345678901")),
        ["asn: a non-ASCII digit"] = (FeedKind.Asn, AsnPick("1\u0663")),
        ["asn: no large-range check"] = (FeedKind.Asn, AsnPick() with { LargeRangeCheckEnabled = null }),
        ["asn: no IPv4 threshold"] = (FeedKind.Asn, AsnPick() with { IPv4MinPrefix = null }),
        ["asn: IPv4 threshold 0"] = (FeedKind.Asn, AsnPick() with { IPv4MinPrefix = 0 }),
        ["asn: IPv4 threshold 33"] = (FeedKind.Asn, AsnPick() with { IPv4MinPrefix = 33 }),
        ["asn: no IPv6 threshold"] = (FeedKind.Asn, AsnPick() with { IPv6MinPrefix = null }),
        ["asn: IPv6 threshold 0"] = (FeedKind.Asn, AsnPick() with { IPv6MinPrefix = 0 }),
        ["asn: IPv6 threshold 129"] = (FeedKind.Asn, AsnPick() with { IPv6MinPrefix = 129 }),
        ["asn: a Google scope"] = (FeedKind.Asn, AsnPick() with { GoogleScope = "us-east1" }),
        ["asn: adds global"] = (FeedKind.Asn, AsnPick() with { GoogleAddsGlobal = true }),
        ["asn: an AWS region"] = (FeedKind.Asn, AsnPick() with { AwsRegion = "eu-west-1" }),
        ["asn: a hand tick"] = (FeedKind.Asn, AsnPick() with { HandTicked = ["8.8.8.0/24"] }),
        ["asn: a hand untick"] = (FeedKind.Asn, AsnPick() with { HandUnticked = ["8.8.8.0/24"] }),
        ["google: no scope"] = (FeedKind.GoogleCloud, GooglePick() with { GoogleScope = null }),
        ["google: an ASN"] = (FeedKind.GoogleCloud, GooglePick() with { Asn = "1" }),
        ["google: an Azure tag"] = (FeedKind.GoogleCloud, GooglePick() with { AzureTag = "AzureCloud" }),
        ["google: a threshold"] = (FeedKind.GoogleCloud, GooglePick() with { IPv4MinPrefix = 16 }),
        ["google: a large-range check"] = (FeedKind.GoogleCloud, GooglePick() with { LargeRangeCheckEnabled = true }),
        ["azure: no tag"] = (FeedKind.Azure, AzurePick() with { AzureTag = null }),
        ["azure: adds global"] = (FeedKind.Azure, AzurePick() with { GoogleAddsGlobal = true }),
        ["azure: an AWS service"] = (FeedKind.Azure, AzurePick() with { AwsService = "AMAZON" }),
        ["aws: no service"] = (FeedKind.Aws, AwsPick() with { AwsService = null }),
        ["aws: a Google scope"] = (FeedKind.Aws, AwsPick() with { GoogleScope = "global" }),
        ["aws: an IPv6 threshold"] = (FeedKind.Aws, AwsPick() with { IPv6MinPrefix = 32 }),
        ["rolling: an ASN"] = (FeedKind.AbuseIpDbRolling, RollingPick() with { Asn = "1" }),
        ["rolling: adds global"] = (FeedKind.AbuseIpDbRolling, RollingPick() with { GoogleAddsGlobal = true }),
        ["rolling: a large answer"] = (FeedKind.AbuseIpDbRolling, RollingPick() with { LargeIncluded = ["8.8.0.0/16"] }),
        ["rolling: a threshold"] = (FeedKind.AbuseIpDbRolling, RollingPick() with { IPv4MinPrefix = 16 }),
        ["text: empty"] = (FeedKind.Azure, AzurePick() with { AzureTag = "" }),
        ["text: 201 characters"] = (FeedKind.Azure, AzurePick() with { AzureTag = new string('a', 201) }),
        ["text: a control character"] = (FeedKind.Aws, AwsPick("eu-west-1\t")),
        ["text: a line separator"] = (FeedKind.GoogleCloud, GooglePick() with { GoogleScope = "us\u0085east1" }),
        ["list: null"] = (FeedKind.GoogleCloud, GooglePick() with { HandTicked = null! }),
        ["list: a null entry"] = (FeedKind.GoogleCloud, GooglePick() with { HandTicked = [null!] }),
        ["list: host bits set"] = (FeedKind.GoogleCloud, GooglePick() with { HandTicked = ["8.8.8.1/24"] }),
        ["list: a bare address"] = (FeedKind.GoogleCloud, GooglePick() with { HandTicked = ["8.8.8.8"] }),
        ["list: spaces"] = (FeedKind.GoogleCloud, GooglePick() with { LargeIncluded = [" 8.8.0.0/16"] }),
        ["list: IPv6 not canonical"] = (FeedKind.GoogleCloud, GooglePick() with { LargeLeftOut = ["2600:1900:0::/28"] }),
        ["list: a duplicate"] = (FeedKind.Azure, AzurePick() with { HandUnticked = ["20.0.0.0/24", "20.0.0.0/24"] }),
        ["list: ticked and unticked"] = (FeedKind.Azure, AzurePick() with { HandTicked = ["20.0.0.0/24"] }),
        ["list: included and left out"] = (FeedKind.Asn, AsnPick() with { LargeIncluded = ["8.8.0.0/16"], LargeLeftOut = ["8.8.0.0/16"] }),
        ["kind: unknown"] = ((FeedKind)99, RollingPick()),
    };

    [Theory]
    [MemberData(nameof(InvalidPicks))]
    public void An_invalid_pick_throws(string name)
    {
        (FeedKind kind, SavedFeedPick pick) = PickCases[name];

        Assert.False(SavedFeedList.IsValidPick(kind, pick));
        Assert.Throws<ArgumentException>(() => SavedFeedList.Empty.Apply("Feed", kind, new SavedFeedIntent.Written(pick), T0));
    }

    [Fact]
    public void The_example_picks_are_valid()
    {
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Asn, AsnPick() with { LargeIncluded = ["8.8.0.0/16", "2600::/16"] }));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Asn, AsnPick("4294967295") with { IPv4MinPrefix = 32, IPv6MinPrefix = 128 }));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Asn, AsnPick("1") with { IPv4MinPrefix = 1, IPv6MinPrefix = 1, LargeRangeCheckEnabled = false }));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick()));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Azure, AzurePick() with { AzureTag = new string('a', 200) }));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Aws, AwsPick()));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.Aws, AwsPick("eu-west-1")));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.AbuseIpDbRolling, RollingPick()));
    }

    // ----- TryRead -----

    private static byte[] Mutated(Action<JsonObject> change)
    {
        JsonObject root = JsonNode.Parse(SampleJson)!.AsObject();
        change(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static JsonObject Feed(JsonObject root) => root["feeds"]![0]!.AsObject();

    private static JsonObject Pick(JsonObject root) => Feed(root)["pick"]!.AsObject();

    private static JsonObject Proof(JsonObject root) => Feed(root)["proof"]!.AsObject();

    [Fact]
    public void The_sample_reads()
    {
        Assert.NotNull(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(SampleJson)));
        Assert.NotNull(SavedFeedList.TryRead(Mutated(_ => { })));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("feeds")]
    [InlineData("key")]
    [InlineData("kind")]
    [InlineData("lastWriteUtc")]
    [InlineData("lastRefreshUtc")]
    [InlineData("retired")]
    [InlineData("pick")]
    [InlineData("pick.asn")]
    [InlineData("pick.googleScope")]
    [InlineData("pick.googleAddsGlobal")]
    [InlineData("pick.azureTag")]
    [InlineData("pick.awsService")]
    [InlineData("pick.awsRegion")]
    [InlineData("pick.handTicked")]
    [InlineData("pick.handUnticked")]
    [InlineData("pick.largeIncluded")]
    [InlineData("pick.largeLeftOut")]
    [InlineData("pick.largeRangeCheckEnabled")]
    [InlineData("pick.ipv4MinPrefix")]
    [InlineData("pick.ipv6MinPrefix")]
    [InlineData("pick.seenUnconfirmed")]
    [InlineData("proof")]
    [InlineData("proof.settings")]
    [InlineData("proof.files")]
    public void A_missing_field_reads_as_nothing(string field)
    {
        byte[] json = Mutated(root =>
        {
            if (field is "version" or "feeds")
                root.Remove(field);
            else if (field.StartsWith("pick.", StringComparison.Ordinal))
                Pick(root).Remove(field[5..]);
            else if (field.StartsWith("proof.", StringComparison.Ordinal))
                Proof(root).Remove(field[6..]);
            else
                Feed(root).Remove(field);
        });

        Assert.Null(SavedFeedList.TryRead(json));
    }

    [Theory]
    [InlineData("version", "\"1\"")]
    [InlineData("version", "1.5")]
    [InlineData("version", "3")]
    [InlineData("version", "0")]
    [InlineData("feeds", "{}")]
    [InlineData("feeds", "null")]
    [InlineData("key", "1")]
    [InlineData("key", "null")]
    [InlineData("key", "\"a b\"")]
    [InlineData("key", "\"..\"")]
    [InlineData("kind", "0")]
    [InlineData("kind", "\"0\"")]
    [InlineData("kind", "\"asn\"")]
    [InlineData("kind", "\"Spamhaus\"")]
    [InlineData("kind", "\"GoogleCloud\"")]
    [InlineData("lastWriteUtc", "\"1793361600\"")]
    [InlineData("lastWriteUtc", "null")]
    [InlineData("lastWriteUtc", "1793361600.5")]
    [InlineData("lastWriteUtc", "999999999999999")]
    [InlineData("lastRefreshUtc", "false")]
    [InlineData("lastRefreshUtc", "-99999999999999")]
    [InlineData("retired", "0")]
    [InlineData("retired", "null")]
    [InlineData("pick", "null")]
    [InlineData("pick", "[]")]
    [InlineData("pick.asn", "15169")]
    [InlineData("pick.asn", "\"AS15169\"")]
    [InlineData("pick.googleScope", "\"us-east1\"")]
    [InlineData("pick.googleScope", "false")]
    [InlineData("pick.googleAddsGlobal", "null")]
    [InlineData("pick.googleAddsGlobal", "true")]
    [InlineData("pick.azureTag", "1")]
    [InlineData("pick.awsService", "[]")]
    [InlineData("pick.awsRegion", "\"\"")]
    [InlineData("pick.handTicked", "null")]
    [InlineData("pick.handTicked", "[\"8.8.8.0/24\"]")]
    [InlineData("pick.handUnticked", "[1]")]
    [InlineData("pick.largeIncluded", "[\"8.8.8.1/24\"]")]
    [InlineData("pick.largeIncluded", "[null]")]
    [InlineData("pick.largeLeftOut", "\"8.8.8.0/24\"")]
    [InlineData("pick.largeLeftOut", "[\"8.8.8.0/24\",\"8.8.8.0/24\"]")]
    [InlineData("pick.largeRangeCheckEnabled", "null")]
    [InlineData("pick.largeRangeCheckEnabled", "1")]
    [InlineData("pick.ipv4MinPrefix", "33")]
    [InlineData("pick.ipv4MinPrefix", "16.0")]
    [InlineData("pick.ipv4MinPrefix", "\"16\"")]
    [InlineData("pick.ipv6MinPrefix", "null")]
    [InlineData("pick.ipv6MinPrefix", "0")]
    [InlineData("pick.seenUnconfirmed", "null")]
    [InlineData("pick.seenUnconfirmed", "[\"8.8.8.0/24\"]")]
    [InlineData("proof", "[]")]
    [InlineData("proof", "\"x\"")]
    [InlineData("proof.settings", "null")]
    [InlineData("proof.settings", "\"caf\\u00e9\"")]
    [InlineData("proof.settings", "\"a\\nb\"")]
    [InlineData("proof.files", "null")]
    [InlineData("proof.files", "[]")]
    [InlineData("proof.files.fortigate/AS15169_ipv4.txt", "null")]
    [InlineData("proof.files.fortigate/AS15169_ipv4.txt", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    [InlineData("proof.files.fortigate/AS15169_ipv4.txt", "\"aaaa\"")]
    [InlineData("proof.files.fortigate/AS15169_ipv4.txt", "\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("proof.files.other/AS15169_ipv4.txt", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("proof.files.fortigate/../AS15169.txt", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("proof.files.fortigate/AS15169_ipv4.csv", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("proof.files.fortigate/a b.txt", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("proof.files.fortigate\\AS15169.txt", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    [InlineData("proof.files.fortigate/x/y.txt", "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public void A_wrong_or_invalid_value_reads_as_nothing(string field, string value)
    {
        byte[] json = Mutated(root =>
        {
            JsonNode? node = JsonNode.Parse(value);
            if (field is "version" or "feeds")
                root[field] = node;
            else if (field.StartsWith("pick.", StringComparison.Ordinal))
                Pick(root)[field[5..]] = node;
            else if (field.StartsWith("proof.files.", StringComparison.Ordinal))
                Proof(root)["files"]![field[12..]] = node;
            else if (field.StartsWith("proof.", StringComparison.Ordinal))
                Proof(root)[field[6..]] = node;
            else
                Feed(root)[field] = node;
        });

        Assert.Null(SavedFeedList.TryRead(json));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("feed")]
    [InlineData("pick")]
    [InlineData("proof")]
    public void An_unknown_property_reads_as_nothing(string where)
    {
        byte[] json = Mutated(root =>
        {
            JsonObject target = where switch { "root" => root, "feed" => Feed(root), "proof" => Proof(root), _ => Pick(root) };
            target["extra"] = 1;
        });

        Assert.Null(SavedFeedList.TryRead(json));
    }

    [Fact]
    public void A_repeated_property_reads_as_nothing()
    {
        string json = SampleJson.Replace("\"key\":\"AS15169\",", "\"key\":\"AS15169\",\"key\":\"AS15169\",", StringComparison.Ordinal);

        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"feeds\":[null]}")]
    [InlineData("{\"version\":1,\"feeds\":[]")]
    [InlineData("{\"version\":1,\"feeds\":[]}garbage")]
    public void Text_that_is_not_the_shape_reads_as_nothing(string text)
    {
        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void A_lone_surrogate_in_a_key_reads_as_nothing()
    {
        string json = SampleJson.Replace("\"key\":\"AS15169\"", "\"key\":\"AS\\ud800\"", StringComparison.Ordinal);

        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Keys_alike_ignoring_case_read_as_nothing()
    {
        byte[] json = Mutated(root =>
        {
            JsonNode copy = Feed(root).DeepClone();
            copy["key"] = "as15169";
            root["feeds"]!.AsArray().Add(copy);
        });

        Assert.Null(SavedFeedList.TryRead(json));
    }

    [Fact]
    public void Feeds_out_of_order_are_read_in_order()
    {
        byte[] json = Mutated(root =>
        {
            JsonNode copy = Feed(root).DeepClone();
            copy["key"] = "AS1";
            copy["pick"]!["asn"] = "1";
            root["feeds"]!.AsArray().Insert(0, copy);
            JsonNode last = Feed(root).DeepClone();
            last["key"] = "AS0";
            root["feeds"]!.AsArray().Add(last);
        });

        SavedFeedList? read = SavedFeedList.TryRead(json);

        Assert.NotNull(read);
        Assert.Equal(["AS0", "AS1", "AS15169"], read.Feeds.Select(feed => feed.Key));
    }

    [Fact]
    public void More_than_the_feed_cap_reads_as_nothing()
    {
        SavedFeedList list = SavedFeedList.Empty;
        for (int i = 0; i < SavedFeedList.MaxFeeds; i++)
            list = Written(list, $"F{i}", FeedKind.AbuseIpDbRolling, RollingPick(), T0);
        byte[] full = list.ToJson();
        Assert.NotNull(SavedFeedList.TryRead(full));

        JsonObject root = JsonNode.Parse(full)!.AsObject();
        JsonNode extra = root["feeds"]![0]!.DeepClone();
        extra["key"] = "Z_extra";
        root["feeds"]!.AsArray().Add(extra);

        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void Json_over_the_byte_cap_reads_as_nothing()
    {
        byte[] json = Encoding.UTF8.GetBytes(SampleJson);

        Assert.NotNull(SavedFeedList.TryRead(json, json.Length));
        Assert.Null(SavedFeedList.TryRead(json, json.Length - 1));
    }

    // ----- Format 2: the proof and the seen list -----

    [Fact]
    public void A_format_1_list_reads_with_no_proof_and_no_seen_ranges_and_saves_as_format_2()
    {
        SavedFeedList? read = SavedFeedList.TryRead(Encoding.UTF8.GetBytes(V1Json));

        Assert.NotNull(read);
        SavedFeed feed = Assert.Single(read.Feeds);
        Assert.Null(feed.Proof);
        Assert.Empty(feed.Pick.SeenUnconfirmed ?? []);
        string expected = SampleJson[..SampleJson.IndexOf(",\"proof\":", StringComparison.Ordinal)] + ",\"proof\":null}]}";
        Assert.Equal(expected, Encoding.UTF8.GetString(read.ToJson()));
        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(V1Json.Replace("\"ipv6MinPrefix\":32", "\"ipv6MinPrefix\":32,\"seenUnconfirmed\":[]", StringComparison.Ordinal))));
        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(V1Json.Replace("}}]}", "},\"proof\":null}]}", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_format_2_list_without_the_seen_list_or_proof_reads_as_nothing()
    {
        Assert.Null(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(V1Json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal))));
    }

    [Fact]
    public void A_proof_and_seen_ranges_round_trip_byte_for_byte_with_the_files_in_ordinal_order()
    {
        SavedFeedPick pick = GooglePick() with { SeenUnconfirmed = ["35.0.0.0/24", "34.0.0.0/24", "2600:1900::/28"] };
        SavedFeedList list = Written(SavedFeedList.Empty, "G", FeedKind.GoogleCloud, pick, T0, SampleProof());
        byte[] json = list.ToJson();
        string text = Encoding.UTF8.GetString(json);

        SavedFeedList? read = SavedFeedList.TryRead(json);

        Assert.NotNull(read);
        Assert.Equal(json, read.ToJson());
        AssertSameFeed(list.Feeds[0], read.Feeds[0]);
        Assert.Contains("\"seenUnconfirmed\":[\"35.0.0.0/24\",\"34.0.0.0/24\",\"2600:1900::/28\"]}", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("AS15169_ipv4", StringComparison.Ordinal) < text.IndexOf("AS15169_ipv6", StringComparison.Ordinal));
        Assert.NotNull(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(SampleJson)));
        Assert.Equal(SampleJson, Encoding.UTF8.GetString(SavedFeedList.TryRead(Encoding.UTF8.GetBytes(SampleJson))!.ToJson()));
    }

    [Fact]
    public void A_proof_of_no_files_or_more_than_16_reads_as_nothing_and_16_reads()
    {
        byte[] Files(int count) => Mutated(root =>
        {
            var files = new JsonObject();
            for (int i = 0; i < count; i++)
                files[$"modsecurity/F{i:D2}.txt"] = new string('c', 64);
            Proof(root)["files"] = files;
        });

        Assert.NotNull(SavedFeedList.TryRead(Files(16)));
        Assert.Null(SavedFeedList.TryRead(Files(17)));
        Assert.Null(SavedFeedList.TryRead(Files(0)));
        Assert.False(SavedFeedWriteProof.IsValid(new SavedFeedWriteProof("ok", new Dictionary<string, string>())));
    }

    [Fact]
    public void A_settings_fingerprint_over_200_characters_reads_as_nothing()
    {
        Assert.NotNull(SavedFeedList.TryRead(Mutated(root => Proof(root)["settings"] = new string('x', 200))));
        Assert.Null(SavedFeedList.TryRead(Mutated(root => Proof(root)["settings"] = new string('x', 201))));
    }

    [Fact]
    public void A_file_key_over_260_characters_reads_as_nothing()
    {
        string Key(int length) => "fortigate/" + new string('k', length - "fortigate/".Length - 4) + ".txt";
        Assert.NotNull(SavedFeedList.TryRead(Mutated(root => Proof(root)["files"]![Key(260)] = new string('d', 64))));
        Assert.Null(SavedFeedList.TryRead(Mutated(root => Proof(root)["files"]![Key(261)] = new string('d', 64))));
    }

    [Theory]
    [InlineData(FeedKind.Asn)]
    [InlineData(FeedKind.AbuseIpDbRolling)]
    public void A_seen_list_on_an_ASN_or_rolling_pick_is_invalid(FeedKind kind)
    {
        SavedFeedPick pick = (kind == FeedKind.Asn ? AsnPick() : RollingPick()) with { SeenUnconfirmed = ["8.8.8.0/24"] };

        Assert.False(SavedFeedList.IsValidPick(kind, pick));
        Assert.Throws<ArgumentException>(() => SavedFeedList.Empty.Apply("K", kind, new SavedFeedIntent.Written(pick), T0));
        Assert.Null(SavedFeedList.TryRead(Mutated(root => Pick(root)["seenUnconfirmed"] = new JsonArray("8.8.8.0/24"))));
    }

    [Theory]
    [InlineData("8.8.8.1/24")]
    [InlineData("not a range")]
    public void A_seen_range_that_isnt_canonical_is_invalid(string text)
    {
        Assert.False(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick() with { SeenUnconfirmed = [text] }));
        Assert.False(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick() with { SeenUnconfirmed = ["8.8.8.0/24", "8.8.8.0/24"] }));
        Assert.True(SavedFeedList.IsValidPick(FeedKind.GoogleCloud, GooglePick() with { SeenUnconfirmed = ["34.0.0.0/24", "9.9.0.0/16"] }));
    }

    [Fact]
    public void A_seen_list_over_the_cap_is_too_many()
    {
        SavedFeedPick pick = AwsPick() with
        {
            SeenUnconfirmed = Enumerable.Range(0, SavedFeedList.MaxRangesPerList + 1).Select(i => $"10.{i >> 16}.{(i >> 8) & 255}.{i & 255}/32").ToList(),
        };

        (SavedFeedsFailure failure, SavedFeedList next) = SavedFeedList.Empty.Apply("A", FeedKind.Aws, new SavedFeedIntent.Written(pick), T0);

        Assert.Equal(SavedFeedsFailure.TooMany, failure);
        Assert.Same(SavedFeedList.Empty, next);
    }

    [Fact]
    public void A_null_seen_list_is_stored_as_empty()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "A", FeedKind.Aws, AwsPick() with { SeenUnconfirmed = null }, T0);

        Assert.NotNull(list.Feeds[0].Pick.SeenUnconfirmed);
        Assert.Empty(list.Feeds[0].Pick.SeenUnconfirmed!);
    }

    [Fact]
    public void Written_stores_its_proof_and_a_write_without_one_clears_the_old_proof()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof());
        AssertSameProof(SampleProof(), list.Feeds[0].Proof);

        SavedFeedList next = Written(list, "AS15169", FeedKind.Asn, AsnPick(), T0);

        Assert.NotSame(list, next);
        Assert.Null(next.Feeds[0].Proof);
    }

    [Fact]
    public void A_same_second_rewrite_with_a_new_proof_or_seen_list_saves_it()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "G", FeedKind.GoogleCloud, GooglePick(), T0, SampleProof());
        SavedFeedWriteProof newer = SampleProof() with
        {
            FileSha256 = new Dictionary<string, string> { ["fortigate/AS15169_ipv4.txt"] = new string('e', 64) },
        };

        SavedFeedList next = Written(list, "G", FeedKind.GoogleCloud, GooglePick(), T0.AddMilliseconds(300), newer);
        Assert.NotSame(list, next);
        AssertSameProof(newer, next.Feeds[0].Proof);

        SavedFeedList seen = Written(next, "G", FeedKind.GoogleCloud, GooglePick() with { SeenUnconfirmed = ["1.1.1.0/24"] },
                                     T0.AddMilliseconds(600), newer);
        Assert.NotSame(next, seen);
        Assert.Equal(["1.1.1.0/24"], seen.Feeds[0].Pick.SeenUnconfirmed!);

        (_, SavedFeedList same) = seen.Apply("G", FeedKind.GoogleCloud, new SavedFeedIntent.Written(
            GooglePick() with { SeenUnconfirmed = ["1.1.1.0/24"] }, SampleProof() with { FileSha256 = new Dictionary<string, string>(newer.FileSha256) }), T0);
        Assert.Same(seen, same);
    }

    [Fact]
    public void Retired_clears_the_proof()
    {
        SavedFeedList list = Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof());

        (SavedFeedsFailure failure, SavedFeedList next) = list.Apply("AS15169", FeedKind.Asn, new SavedFeedIntent.Retired(), T1);

        Assert.Equal(SavedFeedsFailure.None, failure);
        Assert.True(next.Feeds[0].Retired);
        Assert.Null(next.Feeds[0].Proof);
    }

    [Fact]
    public void An_invalid_proof_is_refused_by_Apply()
    {
        var bad = new SavedFeedWriteProof("ok", new Dictionary<string, string> { ["fortigate/AS1.txt"] = "short" });

        Assert.Throws<ArgumentException>(() =>
            SavedFeedList.Empty.Apply("AS1", FeedKind.Asn, new SavedFeedIntent.Written(AsnPick("1"), bad), T0));
    }

    // ----- Stamps -----

    private static SavedFeedStamp Stamp(SavedFeed expected, SavedFeedPick? pick = null, SavedFeedWriteProof? proof = null,
                                        DateTimeOffset? at = null) =>
        new(expected, pick, proof, at ?? T1.AddMilliseconds(700));

    private static SavedFeedList TwoFeeds() =>
        Written(Written(SavedFeedList.Empty, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof()),
                "G", FeedKind.GoogleCloud, GooglePick(), T0);

    [Fact]
    public void A_stamp_saves_the_refresh_time_proof_and_pick_and_keeps_the_rest()
    {
        SavedFeedList list = TwoFeeds();
        SavedFeed asn = list.Feeds[0], google = list.Feeds[1];
        SavedFeedPick newPick = GooglePick() with { HandTicked = [], SeenUnconfirmed = ["1.1.1.0/24"] };

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            list.ApplyStamps([Stamp(asn, proof: null), Stamp(google, newPick, SampleProof())], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Saved, SavedFeedStampOutcome.Saved], outcomes);
        SavedFeed a = next.Feeds[0], g = next.Feeds[1];
        Assert.Equal(T1, a.LastRefreshUtc);
        Assert.Null(a.Proof);
        AssertSamePick(asn.Pick, a.Pick);
        Assert.Equal((asn.Key, asn.Kind, asn.LastWriteUtc, asn.Retired), (a.Key, a.Kind, a.LastWriteUtc, a.Retired));
        AssertSamePick(newPick, g.Pick);
        AssertSameProof(SampleProof(), g.Proof);
        Assert.Equal(T0, g.LastWriteUtc);
        Assert.Equal(TimeSpan.Zero, g.LastRefreshUtc!.Value.Offset);
    }

    [Fact]
    public void A_stamp_with_no_refresh_time_clears_the_proof_and_keeps_the_last_refresh_time()
    {
        SavedFeedList list = TwoFeeds();
        (_, SavedFeedList refreshed) = list.ApplyStamps([Stamp(list.Feeds[0], proof: SampleProof())], SavedFeedList.MaxJsonBytes);
        SavedFeed before = refreshed.Feeds[0];
        Assert.Equal(T1, before.LastRefreshUtc);

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            refreshed.ApplyStamps([new SavedFeedStamp(before, null, null, null)], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Saved], outcomes);
        Assert.Null(next.Feeds[0].Proof);
        Assert.Equal(T1, next.Feeds[0].LastRefreshUtc);
        Assert.Equal(before.LastWriteUtc, next.Feeds[0].LastWriteUtc);
    }

    [Fact]
    public void A_stamp_with_no_refresh_time_on_a_never_refreshed_feed_leaves_it_never_refreshed()
    {
        SavedFeedList list = TwoFeeds();

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            list.ApplyStamps([new SavedFeedStamp(list.Feeds[0], null, null, null)], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Saved], outcomes);
        Assert.Null(next.Feeds[0].Proof);
        Assert.Null(next.Feeds[0].LastRefreshUtc);
    }

    [Fact]
    public void A_stamp_that_changes_nothing_is_unchanged_and_the_list_is_the_same()
    {
        SavedFeedList list = TwoFeeds();
        (_, SavedFeedList stamped) = list.ApplyStamps([Stamp(list.Feeds[0], proof: SampleProof())], SavedFeedList.MaxJsonBytes);

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            stamped.ApplyStamps([Stamp(stamped.Feeds[0], proof: SampleProof(), at: T1)], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Unchanged], outcomes);
        Assert.Same(stamped, next);
    }

    [Fact]
    public void A_stamp_of_a_retired_feed_is_superseded_and_saves_nothing()
    {
        (_, SavedFeedList list) = TwoFeeds().Apply("G", FeedKind.GoogleCloud, new SavedFeedIntent.Retired(), T1);

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            list.ApplyStamps([Stamp(list.Feeds[1], proof: SampleProof())], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Superseded], outcomes);
        Assert.Same(list, next);
        Assert.Null(next.Feeds[1].Proof);
    }

    [Fact]
    public void A_stamp_of_a_removed_feed_is_removed_and_saves_nothing()
    {
        SavedFeedList list = TwoFeeds();
        (_, SavedFeedList removed) = list.Remove("g");

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            removed.ApplyStamps([Stamp(list.Feeds[1])], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Removed], outcomes);
        Assert.Same(removed, next);
        Assert.Single(next.Feeds);
    }

    public static TheoryData<string> Supersessions => ["re-added", "retired", "same-second proof", "same-second pick", "key case"];

    [Theory]
    [MemberData(nameof(Supersessions))]
    public void A_stamp_of_a_feed_changed_since_it_was_read_is_superseded(string change)
    {
        // The feed was refreshed once, so a Remove and a page write in the same second still differ from it
        SavedFeedList list = TwoFeeds();
        list = list.ApplyStamps([Stamp(list.Feeds[0], proof: SampleProof(), at: T0)], SavedFeedList.MaxJsonBytes).Next;
        SavedFeed expected = list.Feeds[0];
        SavedFeedList changed = change switch
        {
            "re-added" => Written(list.Remove("AS15169").Next, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof()),
            "retired" => list.Apply("AS15169", FeedKind.Asn, new SavedFeedIntent.Retired(), T0).Next,
            "same-second proof" => Written(list, "AS15169", FeedKind.Asn, AsnPick(), T0, SampleProof() with
            {
                FileSha256 = new Dictionary<string, string> { ["fortigate/AS15169.txt"] = new string('f', 64) },
            }),
            "same-second pick" => Written(list, "AS15169", FeedKind.Asn, AsnPick() with { LargeLeftOut = ["8.0.0.0/8"] }, T0,
                                          SampleProof()),
            _ => Written(list.Remove("AS15169").Next, "as15169", FeedKind.Asn, AsnPick(), T0, SampleProof()),
        };
        Assert.NotSame(list, changed);

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            changed.ApplyStamps([Stamp(expected, proof: SampleProof())], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Superseded], outcomes);
        Assert.Same(changed, next);
    }

    [Fact]
    public void A_stamp_on_an_unavailable_list_is_unavailable()
    {
        SavedFeed feed = TwoFeeds().Feeds[0];

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            SavedFeedList.Unavailable.ApplyStamps([Stamp(feed), Stamp(TwoFeeds().Feeds[1])], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Unavailable, SavedFeedStampOutcome.Unavailable], outcomes);
        Assert.Same(SavedFeedList.Unavailable, next);
    }

    [Fact]
    public void A_new_pick_over_a_list_cap_is_too_many_and_the_others_still_save()
    {
        SavedFeedList list = TwoFeeds();
        SavedFeedPick over = GooglePick() with
        {
            SeenUnconfirmed = Enumerable.Range(0, SavedFeedList.MaxRangesPerList + 1).Select(i => $"10.{i >> 16}.{(i >> 8) & 255}.{i & 255}/32").ToList(),
        };

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) =
            list.ApplyStamps([Stamp(list.Feeds[0]), Stamp(list.Feeds[1], over)], SavedFeedList.MaxJsonBytes);

        Assert.Equal([SavedFeedStampOutcome.Saved, SavedFeedStampOutcome.TooMany], outcomes);
        AssertSamePick(GooglePick(), next.Feeds[1].Pick);
        Assert.Null(next.Feeds[1].LastRefreshUtc);
        Assert.Equal(T1, next.Feeds[0].LastRefreshUtc);
    }

    [Fact]
    public void Over_the_byte_cap_the_stamp_that_grew_most_is_dropped_first()
    {
        SavedFeedList list = TwoFeeds();
        SavedFeedPick small = AsnPick() with { LargeLeftOut = ["8.0.0.0/8"] };
        SavedFeedPick large = GooglePick() with { SeenUnconfirmed = ["1.1.1.0/24", "1.1.2.0/24", "1.1.3.0/24", "1.1.4.0/24"] };
        SavedFeedStamp[] stamps = [Stamp(list.Feeds[0], small), Stamp(list.Feeds[1], large)];
        int both = list.ApplyStamps(stamps, SavedFeedList.MaxJsonBytes).Next.ToJson().Length;

        (IReadOnlyList<SavedFeedStampOutcome> outcomes, SavedFeedList next) = list.ApplyStamps(stamps, both - 1);

        Assert.Equal([SavedFeedStampOutcome.Saved, SavedFeedStampOutcome.TooMany], outcomes);
        AssertSamePick(small, next.Feeds[0].Pick);
        AssertSamePick(GooglePick(), next.Feeds[1].Pick);
        Assert.True(next.ToJson().Length <= both - 1);

        // A cap no list of these feeds fits: every applied stamp is dropped and the list stays as it was
        (IReadOnlyList<SavedFeedStampOutcome> none, SavedFeedList same) = list.ApplyStamps(stamps, 100);
        Assert.Equal([SavedFeedStampOutcome.TooMany, SavedFeedStampOutcome.TooMany], none);
        Assert.Same(list, same);
    }

    [Fact]
    public void Two_stamps_of_one_key_or_an_invalid_new_pick_throw()
    {
        SavedFeedList list = TwoFeeds();
        SavedFeed asn = list.Feeds[0];

        Assert.Throws<ArgumentException>(() => list.ApplyStamps([Stamp(asn), Stamp(asn with { Key = "as15169" })], SavedFeedList.MaxJsonBytes));
        Assert.Throws<ArgumentException>(() => list.ApplyStamps([Stamp(asn, GooglePick())], SavedFeedList.MaxJsonBytes));
    }
}
