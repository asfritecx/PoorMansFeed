using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>Kept ids aren't consecutive, so a request gives one id per file, in the files' order.</summary>
public sealed class ModSecurityRulesGivenIdTests
{
    private const string Folder = "/etc/modsecurity/lists";

    private static ModSecurityRuleSet Build(int[]? ids, string[]? files = null, string location = Folder) =>
        ModSecurityRules.Build(new ModSecurityRuleRequest(ids!, ModSecurityAction.Deny, location,
            files ?? ["a_ipv4.txt", "a_ipv6.txt"], "X"));

    private static void AssertFailed(ModSecurityRuleSet set, ModSecurityRuleFailure expected)
    {
        Assert.Equal(expected, set.Failure);
        Assert.Empty(set.Rules);
        Assert.Equal("", set.Text);
    }

    [Fact]
    public void Given_ids_are_used_in_file_order()
    {
        ModSecurityRuleSet set = Build([20_001, 15]);

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/a_ipv4.txt\" "
            + "\"id:20001,phase:1,deny,status:403,log,t:none,msg:'X (a_ipv4.txt)'\"\n"
            + "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/a_ipv6.txt\" "
            + "\"id:15,phase:1,deny,status:403,log,t:none,msg:'X (a_ipv6.txt)'\"\n",
            set.Text);
    }

    [Fact]
    public void Given_ids_at_the_ends_of_the_range_build()
    {
        ModSecurityRuleSet set = Build([99_999, 1]);

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Contains("\"id:99999,", set.Rules[0]);
        Assert.Contains("\"id:1,", set.Rules[1]);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 100_000)]
    [InlineData(-1, 5)]
    [InlineData(int.MinValue, 5)]
    [InlineData(5, int.MaxValue)]
    public void A_given_id_out_of_range_is_refused(int first, int second)
    {
        AssertFailed(Build([first, second]), ModSecurityRuleFailure.RuleIdOutOfRange);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1 })]
    [InlineData(new[] { 1, 2, 3 })]
    public void A_count_that_differs_from_the_files_is_out_of_range(int[] ids)
    {
        AssertFailed(Build(ids), ModSecurityRuleFailure.RuleIdOutOfRange);
    }

    [Fact]
    public void Two_equal_ids_are_repeated()
    {
        AssertFailed(Build([7, 7]), ModSecurityRuleFailure.RuleIdRepeated);
        AssertFailed(Build([1, 2, 1], ["a.txt", "b.txt", "c.txt"]), ModSecurityRuleFailure.RuleIdRepeated);
    }

    [Fact]
    public void Out_of_range_comes_before_repeated()
    {
        AssertFailed(Build([0, 0]), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build([7, 7, 100_000], ["a.txt", "b.txt", "c.txt"]), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build([7, 7], ["a.txt", "b.txt", "c.txt"]), ModSecurityRuleFailure.RuleIdOutOfRange);
    }

    [Fact]
    public void Given_ids_are_checked_after_the_files_and_before_the_location()
    {
        AssertFailed(Build([], []), ModSecurityRuleFailure.NoFiles);
        AssertFailed(Build([0], ["a b"]), ModSecurityRuleFailure.FileNameInvalid);
        AssertFailed(Build([7, 7], location: ""), ModSecurityRuleFailure.RuleIdRepeated);
        AssertFailed(Build([0, 1], location: "srv"), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build([1, 2], location: "https://h.example/d?sp=w&sig=x"), ModSecurityRuleFailure.SasCanWrite);
    }

    [Fact]
    public void A_repeated_file_name_with_two_ids_builds()
    {
        ModSecurityRuleSet set = Build([3, 4], ["a.txt", "a.txt"]);

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Equal(2, set.Rules.Count);
    }

    [Fact]
    public void No_ids_are_out_of_range_where_the_other_id_checks_run()
    {
        AssertFailed(Build(null), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build(null, ["a.txt"]), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build(null, location: ""), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build(null, location: "https://h.example/d?sp=w&sig=x"), ModSecurityRuleFailure.RuleIdOutOfRange);
        AssertFailed(Build(null, []), ModSecurityRuleFailure.NoFiles);
        AssertFailed(Build(null, ["a b"]), ModSecurityRuleFailure.FileNameInvalid);
    }
}
