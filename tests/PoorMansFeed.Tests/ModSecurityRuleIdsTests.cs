using System.Text;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

public sealed class ModSecurityRuleIdsTests
{
    private const int Max = ModSecurityLimits.RuleIdMax;

    // ----- Assign -----

    [Fact]
    public void An_empty_map_gives_the_first_new_name_the_first_id_and_the_next_one_above_it()
    {
        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = ModSecurityRuleIds.Empty.Assign(["AS1_ipv4.txt", "AS1_ipv6.txt"], 10_000);

        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.Equal([10_000, 10_001], result.RuleIds);
        Assert.Equal(2, next.Count);
        Assert.True(next.TryGet("AS1_ipv6.txt", out int id));
        Assert.Equal(10_001, id);
        Assert.Equal(0, ModSecurityRuleIds.Empty.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public void Kept_names_keep_their_ids_whatever_the_first_id_is_now(int firstId)
    {
        ModSecurityRuleIds map = Assigned(["a.txt", "b.txt"], 10_000);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["b.txt", "a.txt"], firstId);

        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.Equal([10_001, 10_000], result.RuleIds);
        Assert.Same(map, next);
    }

    [Fact]
    public void A_mixed_list_gives_kept_and_new_ids_in_its_order()
    {
        ModSecurityRuleIds map = Assigned(["a.txt", "b.txt"], 10_000);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["c.txt", "a.txt", "d.txt", "b.txt"], 10_000);

        Assert.Equal([10_002, 10_000, 10_003, 10_001], result.RuleIds);
        Assert.Equal(4, next.Count);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void A_first_id_above_the_highest_kept_starts_there()
    {
        ModSecurityRuleIds map = Assigned(["a.txt"], 100);

        (ModSecurityRuleIdsResult result, _) = map.Assign(["b.txt", "c.txt"], 500);

        Assert.Equal([500, 501], result.RuleIds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void A_first_id_at_or_below_the_highest_kept_continues_above_it(int firstId)
    {
        ModSecurityRuleIds map = Assigned(["a.txt", "b.txt"], 100);

        (ModSecurityRuleIdsResult result, _) = map.Assign(["c.txt"], firstId);

        Assert.Equal([102], result.RuleIds);
    }

    [Fact]
    public void A_repeated_name_gets_the_same_id_each_time()
    {
        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = ModSecurityRuleIds.Empty.Assign(["a.txt", "b.txt", "a.txt"], 7);

        Assert.Equal([7, 8, 7], result.RuleIds);
        Assert.Equal(2, next.Count);
    }

    [Fact]
    public void An_empty_list_gives_no_ids_and_the_same_map()
    {
        ModSecurityRuleIds map = Assigned(["a.txt"], 7);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign([], 7);

        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
    }

    [Fact]
    public void The_last_id_is_assignable_and_the_next_new_name_is_range_full()
    {
        (ModSecurityRuleIdsResult first, ModSecurityRuleIds map) = ModSecurityRuleIds.Empty.Assign(["a.txt"], Max - 1);
        Assert.Equal([Max - 1], first.RuleIds);
        (ModSecurityRuleIdsResult last, map) = map.Assign(["b.txt"], 1);
        Assert.Equal([Max], last.RuleIds);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["c.txt"], 1);

        Assert.Equal(ModSecurityRuleIdsFailure.RangeFull, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
    }

    [Fact]
    public void Once_the_highest_kept_id_is_the_last_a_lower_first_id_is_still_range_full_and_kept_names_still_get_theirs()
    {
        ModSecurityRuleIds map = Assigned(["a.txt", "b.txt"], Max - 1);

        Assert.Equal(ModSecurityRuleIdsFailure.RangeFull, map.Assign(["c.txt"], 1).Result.Failure);
        Assert.Equal(ModSecurityRuleIdsFailure.RangeFull, map.Assign(["a.txt", "c.txt"], 1).Result.Failure);

        (ModSecurityRuleIdsResult kept, ModSecurityRuleIds next) = map.Assign(["b.txt", "a.txt"], 1);
        Assert.Equal([Max, Max - 1], kept.RuleIds);
        Assert.Same(map, next);
    }

    [Fact]
    public void Two_new_names_where_the_second_is_range_full_add_nothing()
    {
        ModSecurityRuleIds map = Assigned(["a.txt"], Max - 2);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["b.txt", "c.txt", "d.txt"], 1);

        Assert.Equal(ModSecurityRuleIdsFailure.RangeFull, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
        Assert.False(map.TryGet("b.txt", out _));
    }

    [Fact]
    public void A_full_map_refuses_a_new_name_as_too_many_and_keeps_giving_kept_ids()
    {
        ModSecurityRuleIds map = Assigned(Names(ModSecurityRuleIds.MaxEntries), 1);
        Assert.Equal(ModSecurityRuleIds.MaxEntries, map.Count);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["new.txt"], 1);

        Assert.Equal(ModSecurityRuleIdsFailure.TooMany, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
        Assert.Equal([1], map.Assign(["f0.txt"], 1).Result.RuleIds);
    }

    [Fact]
    public void Two_new_names_where_the_second_is_too_many_add_nothing()
    {
        ModSecurityRuleIds map = Assigned(Names(ModSecurityRuleIds.MaxEntries - 1), 1);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["x.txt", "y.txt"], 1);

        Assert.Equal(ModSecurityRuleIdsFailure.TooMany, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
        Assert.Equal(ModSecurityRuleIds.MaxEntries - 1, map.Count);
    }

    public static TheoryData<string?> BadNames =>
    [
        null!, "", ".hidden.txt", "a/b.txt", "a\\b.txt", "a b.txt", "a\n", "caf\u00e9.txt", "a:b", new string('a', 201),
    ];

    [Theory]
    [MemberData(nameof(BadNames))]
    public void A_bad_name_is_refused_and_nothing_is_added_even_after_a_new_name(string? bad)
    {
        ModSecurityRuleIds map = Assigned(["kept.txt"], 10);

        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(["new.txt", "kept.txt", bad!], 10);

        Assert.Equal(ModSecurityRuleIdsFailure.FileNameInvalid, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(map, next);
    }

    [Fact]
    public void A_name_of_two_hundred_characters_is_kept()
    {
        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = ModSecurityRuleIds.Empty.Assign([new string('a', 200)], 10);

        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.True(next.TryGet(new string('a', 200), out int id));
        Assert.Equal(10, id);
    }

    [Fact]
    public void Names_that_differ_only_by_case_are_two_names()
    {
        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = ModSecurityRuleIds.Empty.Assign(["AS1.txt", "as1.txt"], 10);

        Assert.Equal([10, 11], result.RuleIds);
        Assert.Equal(2, next.Count);
    }

    [Fact]
    public void An_unavailable_map_gives_unavailable_whatever_the_names()
    {
        ModSecurityRuleIds map = ModSecurityRuleIds.Unavailable;
        Assert.False(map.IsAvailable);
        Assert.Equal(0, map.Count);
        Assert.True(ModSecurityRuleIds.Empty.IsAvailable);

        string?[][] lists = [["a.txt"], [], [null], ["../x"]];
        foreach (string?[] names in lists)
        {
            (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = map.Assign(names!, 10);
            Assert.Equal(ModSecurityRuleIdsFailure.Unavailable, result.Failure);
            Assert.Empty(result.RuleIds);
            Assert.Same(map, next);
        }

        Assert.Throws<InvalidOperationException>(() => map.ToJson());
        Assert.Throws<ArgumentNullException>(() => map.Assign(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Assign(["a.txt"], 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Max + 1)]
    public void A_null_list_or_a_first_id_out_of_range_throws(int firstId)
    {
        Assert.Throws<ArgumentNullException>(() => ModSecurityRuleIds.Empty.Assign(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModSecurityRuleIds.Empty.Assign(["a.txt"], firstId));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModSecurityRuleIds.Empty.Assign([null!], firstId));
    }

    // ----- The JSON -----

    [Fact]
    public void The_JSON_has_the_version_first_and_the_ids_ascending()
    {
        ModSecurityRuleIds map = Assigned(["b.txt"], 20);
        map = map.Assign(["a.txt"], 5).Next;

        Assert.Equal("""{"version":1,"ruleIds":[{"file":"b.txt","id":20},{"file":"a.txt","id":21}]}""",
                     Encoding.UTF8.GetString(map.ToJson()));
        Assert.Equal("""{"version":1,"ruleIds":[]}""", Encoding.UTF8.GetString(ModSecurityRuleIds.Empty.ToJson()));
    }

    [Fact]
    public void ToJson_then_TryRead_gives_the_same_map()
    {
        ModSecurityRuleIds map = Assigned(["AS15169_ipv4.txt", "AS15169_ipv6.txt", "x"], 10_000);

        ModSecurityRuleIds? read = ModSecurityRuleIds.TryRead(map.ToJson());

        Assert.NotNull(read);
        Assert.True(read.IsAvailable);
        Assert.Equal(3, read.Count);
        Assert.Equal(map.ToJson(), read.ToJson());
        Assert.Equal([10_001, 10_003], read.Assign(["AS15169_ipv6.txt", "new"], 1).Result.RuleIds);
    }

    [Fact]
    public void TryRead_takes_elements_in_any_order()
    {
        ModSecurityRuleIds? read = ModSecurityRuleIds.TryRead(
            """{"version":1,"ruleIds":[{"file":"b","id":9},{"id":3,"file":"a"}]}"""u8);

        Assert.NotNull(read);
        Assert.Equal("""{"version":1,"ruleIds":[{"file":"a","id":3},{"file":"b","id":9}]}""", Encoding.UTF8.GetString(read.ToJson()));
    }

    public static TheoryData<string> RefusedJson =>
    [
        "",
        "not json",
        "[]",
        "1",
        """{"ruleIds":[]}""",
        """{"version":0,"ruleIds":[]}""",
        """{"version":2,"ruleIds":[]}""",
        """{"version":1.0,"ruleIds":[]}""",
        """{"version":1e0,"ruleIds":[]}""",
        """{"version":"1","ruleIds":[]}""",
        """{"version":true,"ruleIds":[]}""",
        """{"version":1}""",
        """{"version":1,"ruleIds":{}}""",
        """{"version":1,"ruleIds":null}""",
        """{"version":1,"ruleIds":[],"extra":0}""",
        """{"version":1,"version":1,"ruleIds":[]}""",
        """{"version":1,"ruleIds":[],"ruleIds":[]}""",
        """{"version":1,"ruleIds":[1]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":1,"note":""}]}""",
        """{"version":1,"ruleIds":[{"file":"a"}]}""",
        """{"version":1,"ruleIds":[{"id":1}]}""",
        """{"version":1,"ruleIds":[{"file":"a","file":"b","id":1}]}""",
        """{"version":1,"ruleIds":[{"file":".a","id":1}]}""",
        """{"version":1,"ruleIds":[{"file":"a/b","id":1}]}""",
        """{"version":1,"ruleIds":[{"file":null,"id":1}]}""",
        """{"version":1,"ruleIds":[{"file":1,"id":1}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":0}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":100000}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":1.5}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":"1"}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":1},{"file":"a","id":2}]}""",
        """{"version":1,"ruleIds":[{"file":"a","id":1},{"file":"b","id":1}]}""",
        """{"version":1,"ruleIds":[]} trailing""",
    ];

    [Theory]
    [MemberData(nameof(RefusedJson))]
    public void TryRead_refuses_what_this_build_cant_fully_read(string json)
    {
        Assert.Null(ModSecurityRuleIds.TryRead(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TryRead_refuses_strings_that_arent_valid_text()
    {
        Assert.Null(ModSecurityRuleIds.TryRead("""{"version":1,"ruleIds":[{"file":"\uD800","id":1}]}"""u8));
        Assert.Null(ModSecurityRuleIds.TryRead("""{"version":1,"\uD800":[]}"""u8));
        byte[] badUtf8 = [.. Encoding.UTF8.GetBytes("{\"version\":1,\"ruleIds\":[{\"file\":\""), 0xC3, 0x28,
                          .. Encoding.UTF8.GetBytes("\",\"id\":1}]}")];
        Assert.Null(ModSecurityRuleIds.TryRead(badUtf8));
    }

    [Fact]
    public void TryRead_takes_the_cap_and_refuses_one_more()
    {
        Assert.Equal(ModSecurityRuleIds.MaxEntries, ModSecurityRuleIds.TryRead(Json(ModSecurityRuleIds.MaxEntries))!.Count);
        Assert.Null(ModSecurityRuleIds.TryRead(Json(ModSecurityRuleIds.MaxEntries + 1)));
    }

    // ----- Helpers -----

    private static ModSecurityRuleIds Assigned(IReadOnlyList<string> names, int firstId)
    {
        (ModSecurityRuleIdsResult result, ModSecurityRuleIds next) = ModSecurityRuleIds.Empty.Assign(names, firstId);
        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        return next;
    }

    private static string[] Names(int count) => [.. Enumerable.Range(0, count).Select(i => $"f{i}.txt")];

    private static byte[] Json(int count) => Encoding.UTF8.GetBytes(
        "{\"version\":1,\"ruleIds\":[" + string.Join(',', Enumerable.Range(0, count).Select(i => $"{{\"file\":\"f{i}\",\"id\":{i + 1}}}")) + "]}");
}
