using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The rule text lands in a server's configuration, so nothing outside the allowlists is written.</summary>
public sealed class ModSecurityRulesTests
{
    private const string Folder = "/etc/modsecurity/lists";
    private const string Prefix = "SecRule REMOTE_ADDR \"@ipMatchFromFile ";

    private static ModSecurityRuleRequest Request(ModSecurityAction action = ModSecurityAction.Deny,
                                                  string location = Folder, string label = "Test feed",
                                                  string[]? files = null, int[]? ids = null)
    {
        files ??= ["a.txt"];
        return new(ids ?? Enumerable.Range(10_000, files.Length).ToArray(), action, location, files, label);
    }

    private static string Msg(string line)
    {
        Assert.EndsWith("'\"", line);
        int start = line.IndexOf("msg:'", StringComparison.Ordinal) + 5;
        return line[start..^2];
    }

    private static void AssertFailed(ModSecurityRuleSet set, ModSecurityRuleFailure expected)
    {
        Assert.Equal(expected, set.Failure);
        Assert.Empty(set.Rules);
        Assert.Equal("", set.Text);
    }

    [Fact]
    public void Deny_on_a_folder_gives_the_whole_line()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(action: ModSecurityAction.Deny,
            label: "AWS EC2 all regions", files: ["AWS_EC2_all_ipv4.txt"]));

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/AWS_EC2_all_ipv4.txt\" "
            + "\"id:10000,phase:1,deny,status:403,log,t:none,msg:'AWS EC2 all regions (AWS_EC2_all_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    [Fact]
    public void Log_only_on_a_folder_gives_the_whole_line()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(action: ModSecurityAction.LogOnly,
            label: "AWS EC2 all regions", files: ["AWS_EC2_all_ipv4.txt"]));

        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/AWS_EC2_all_ipv4.txt\" "
            + "\"id:10000,phase:1,pass,log,t:none,msg:'AWS EC2 all regions (AWS_EC2_all_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    [Fact]
    public void Deny_on_a_url_keeps_the_query_after_the_file_name()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(ids: [42], action: ModSecurityAction.Deny,
            location: "https://acct.blob.core.windows.net/feeds/?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D",
            label: "Abuse", files: ["AbuseIPDB_rolling_ipv4.txt"]));

        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile https://acct.blob.core.windows.net/feeds/AbuseIPDB_rolling_ipv4.txt"
            + "?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D\" "
            + "\"id:42,phase:1,deny,status:403,log,t:none,msg:'Abuse (AbuseIPDB_rolling_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    [Fact]
    public void Log_only_on_a_url_keeps_the_query_after_the_file_name()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(ids: [42], action: ModSecurityAction.LogOnly,
            location: "https://acct.blob.core.windows.net/feeds/?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D",
            label: "Abuse", files: ["AbuseIPDB_rolling_ipv4.txt"]));

        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile https://acct.blob.core.windows.net/feeds/AbuseIPDB_rolling_ipv4.txt"
            + "?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D\" "
            + "\"id:42,phase:1,pass,log,t:none,msg:'Abuse (AbuseIPDB_rolling_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    // deny with 444 gives Apache a 500 page and drop alone gives nginx a 403 page, so Close is drop with 444
    [Fact]
    public void Close_on_a_folder_gives_the_whole_line()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(action: ModSecurityAction.Close,
            label: "AWS EC2 all regions", files: ["AWS_EC2_all_ipv4.txt"]));

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/AWS_EC2_all_ipv4.txt\" "
            + "\"id:10000,phase:1,drop,status:444,log,t:none,msg:'AWS EC2 all regions (AWS_EC2_all_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    [Fact]
    public void Close_on_a_url_keeps_the_query_after_the_file_name()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(ids: [42], action: ModSecurityAction.Close,
            location: "https://acct.blob.core.windows.net/feeds/?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D",
            label: "Abuse", files: ["AbuseIPDB_rolling_ipv4.txt"]));

        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile https://acct.blob.core.windows.net/feeds/AbuseIPDB_rolling_ipv4.txt"
            + "?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D\" "
            + "\"id:42,phase:1,drop,status:444,log,t:none,msg:'Abuse (AbuseIPDB_rolling_ipv4.txt)'\"",
            Assert.Single(set.Rules));
    }

    [Fact]
    public void Exactly_one_space_follows_the_operator()
    {
        string line = Assert.Single(ModSecurityRules.Build(Request()).Rules);

        Assert.Contains("@ipMatchFromFile /", line);
        Assert.DoesNotContain("@ipMatchFromFile  ", line);
    }

    [Fact]
    public void An_undefined_action_builds_the_log_only_text()
    {
        string line = Assert.Single(ModSecurityRules.Build(Request(action: (ModSecurityAction)7)).Rules);

        Assert.Equal(
            "SecRule REMOTE_ADDR \"@ipMatchFromFile /etc/modsecurity/lists/a.txt\" "
            + "\"id:10000,phase:1,pass,log,t:none,msg:'Test feed (a.txt)'\"",
            line);
    }

    [Fact]
    public void Two_family_files_give_two_rules_with_their_ids_in_order()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(ids: [500, 501], label: "X",
            files: ["X_ipv4.txt", "X_ipv6.txt"]));

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Equal(2, set.Rules.Count);
        Assert.Contains("/etc/modsecurity/lists/X_ipv4.txt\" \"id:500,", set.Rules[0]);
        Assert.Contains("/etc/modsecurity/lists/X_ipv6.txt\" \"id:501,", set.Rules[1]);
        Assert.Equal(set.Rules[0] + "\n" + set.Rules[1] + "\n", set.Text);
        Assert.DoesNotContain("\r", set.Text);
        Assert.DoesNotContain("\n\n", set.Text);
        Assert.DoesNotContain("#", set.Text);
        Assert.All(set.Rules, line => Assert.StartsWith(Prefix, line));
        Assert.All(set.Rules, line => Assert.DoesNotContain("\n", line));
    }

    [Fact]
    public void A_repeated_file_name_gets_a_rule_and_id_each()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(ids: [7, 8], files: ["a.txt", "a.txt"]));

        Assert.Equal(2, set.Rules.Count);
        Assert.Contains("\"id:7,", set.Rules[0]);
        Assert.Contains("\"id:8,", set.Rules[1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99_999)]
    public void Ids_at_the_ends_of_the_range_build(int id)
    {
        string line = Assert.Single(ModSecurityRules.Build(Request(ids: [id])).Rules);

        Assert.Contains($"\"id:{id},", line);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Ids_outside_the_range_are_refused(int id)
    {
        AssertFailed(ModSecurityRules.Build(Request(ids: [id])), ModSecurityRuleFailure.RuleIdOutOfRange);
    }

    [Fact]
    public void Ids_are_written_without_group_separators()
    {
        string line = Assert.Single(ModSecurityRules.Build(Request(ids: [99_999])).Rules);

        Assert.Contains("\"id:99999,", line);
    }

    [Fact]
    public void A_null_request_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ModSecurityRules.Build(null!));
    }

    [Fact]
    public void Null_or_empty_files_give_no_files()
    {
        AssertFailed(ModSecurityRules.Build(new ModSecurityRuleRequest([1], ModSecurityAction.Deny, Folder, null!,
            "x")), ModSecurityRuleFailure.NoFiles);
        AssertFailed(ModSecurityRules.Build(new ModSecurityRuleRequest([1], ModSecurityAction.Deny, Folder, [], "x")),
            ModSecurityRuleFailure.NoFiles);
    }

    [Fact]
    public void A_null_file_name_is_invalid()
    {
        AssertFailed(ModSecurityRules.Build(new ModSecurityRuleRequest([1, 2], ModSecurityAction.Deny, Folder,
            ["a.txt", null!], "x")), ModSecurityRuleFailure.FileNameInvalid);
    }

    [Fact]
    public void A_null_location_gives_no_location()
    {
        AssertFailed(ModSecurityRules.Build(new ModSecurityRuleRequest([1], ModSecurityAction.Deny, null!,
            ["a.txt"], "x")), ModSecurityRuleFailure.NoLocation);
    }

    [Fact]
    public void Every_location_failure_reaches_build()
    {
        AssertFailed(ModSecurityRules.Build(Request(location: "")), ModSecurityRuleFailure.NoLocation);
        AssertFailed(ModSecurityRules.Build(Request(location: "http://x/y")), ModSecurityRuleFailure.NotHttps);
        AssertFailed(ModSecurityRules.Build(Request(location: "srv/lists")), ModSecurityRuleFailure.LocationInvalid);
    }

    [Fact]
    public void Empty_files_with_an_empty_location_give_no_files()
    {
        AssertFailed(ModSecurityRules.Build(new ModSecurityRuleRequest([0], ModSecurityAction.Deny, "", [], "x")),
            ModSecurityRuleFailure.NoFiles);
    }

    [Fact]
    public void A_bad_file_name_with_a_bad_id_and_location_is_an_invalid_file_name()
    {
        AssertFailed(ModSecurityRules.Build(Request(ids: [0], location: "", files: ["a b"])),
            ModSecurityRuleFailure.FileNameInvalid);
    }

    [Fact]
    public void A_bad_id_with_no_location_is_out_of_range()
    {
        AssertFailed(ModSecurityRules.Build(Request(ids: [0], location: "")),
            ModSecurityRuleFailure.RuleIdOutOfRange);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("_x")]
    [InlineData("-x")]
    [InlineData("AbuseIPDB_rolling_ipv4.txt")]
    [InlineData("a..b")]
    [InlineData("x.")]
    public void Valid_file_names_build(string file)
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(Request(files: [file]));

        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Contains($"/etc/modsecurity/lists/{file}\" ", Assert.Single(set.Rules));
    }

    [Fact]
    public void A_file_name_of_200_characters_builds()
    {
        Assert.Equal(ModSecurityRuleFailure.None,
            ModSecurityRules.Build(Request(files: [new string('a', 200)])).Failure);
    }

    [Fact]
    public void A_file_name_of_201_characters_is_refused()
    {
        AssertFailed(ModSecurityRules.Build(Request(files: [new string('a', 201)])),
            ModSecurityRuleFailure.FileNameInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("a\n")]
    [InlineData("a\r")]
    [InlineData("\u00e9.txt")]
    [InlineData("a$b")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a%b")]
    [InlineData("a:b")]
    [InlineData("a~b")]
    [InlineData("a#b")]
    [InlineData("a{b")]
    [InlineData("a}b")]
    [InlineData("a@b")]
    [InlineData("a;b")]
    [InlineData("a*b")]
    [InlineData("a,b")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\ud83d\ude00b")]
    public void Invalid_file_names_are_refused(string file)
    {
        AssertFailed(ModSecurityRules.Build(Request(files: [file])), ModSecurityRuleFailure.FileNameInvalid);
    }

    [Fact]
    public void A_label_has_every_refused_character_replaced_and_runs_collapsed()
    {
        string label = "a'b\"c\\d%e$f{g,h;i#j\nk\tl\u00e9m";

        string line = Assert.Single(ModSecurityRules.Build(Request(label: label, files: ["f.txt"])).Rules);

        Assert.Equal("a b c d e f g h i j k l m (f.txt)", Msg(line));
    }

    [Fact]
    public void A_label_keeps_its_allowed_characters_and_is_trimmed()
    {
        string line = Assert.Single(ModSecurityRules.Build(
            Request(label: "  ''Az09 ._:/()+-  ;; x  ", files: ["f.txt"])).Rules);

        Assert.Equal("Az09 ._:/()+- x (f.txt)", Msg(line));
    }

    [Fact]
    public void A_surrogate_pair_in_a_label_becomes_one_space()
    {
        string line = Assert.Single(ModSecurityRules.Build(Request(label: "a\ud83d\ude00b", files: ["f.txt"])).Rules);

        Assert.Equal("a b (f.txt)", Msg(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("'\"\\%$")]
    [InlineData(null)]
    public void An_empty_label_gives_the_file_name_alone(string? label)
    {
        string line = Assert.Single(ModSecurityRules.Build(Request(label: label!, files: ["f.txt"])).Rules);

        Assert.Equal("f.txt", Msg(line));
    }

    [Fact]
    public void A_long_label_is_cut_so_the_msg_is_120_characters()
    {
        string line = Assert.Single(ModSecurityRules.Build(
            Request(label: new string('x', 300), files: ["a.txt"])).Rules);

        string msg = Msg(line);
        Assert.Equal(120, msg.Length);
        Assert.Equal(new string('x', 112) + " (a.txt)", msg);
    }

    [Fact]
    public void A_cut_label_ending_in_a_space_is_trimmed_before_the_file_name()
    {
        string label = new string('x', 111) + " " + new string('y', 188);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: label, files: ["a.txt"])).Rules));

        Assert.Equal(new string('x', 111) + " (a.txt)", msg);
        Assert.DoesNotContain("  (", msg);
    }

    [Fact]
    public void A_label_that_fits_exactly_is_kept_whole()
    {
        string label = new string('x', 112);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: label, files: ["a.txt"])).Rules));

        Assert.Equal(label + " (a.txt)", msg);
    }

    [Fact]
    public void A_file_name_of_200_characters_with_a_label_gives_the_file_name_cut_to_120()
    {
        string file = new string('f', 200);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: "Label", files: [file])).Rules));

        Assert.Equal(new string('f', 120), msg);
    }

    [Fact]
    public void A_file_name_of_200_characters_without_a_label_gives_the_file_name_cut_to_120()
    {
        string file = new string('f', 200);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: "", files: [file])).Rules));

        Assert.Equal(new string('f', 120), msg);
    }

    [Fact]
    public void A_file_name_leaving_one_character_of_room_keeps_one_label_character()
    {
        string file = new string('f', 116);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: "Label", files: [file])).Rules));

        Assert.Equal("L (" + file + ")", msg);
        Assert.Equal(120, msg.Length);
    }

    [Fact]
    public void A_file_name_leaving_no_room_gives_the_file_name_alone()
    {
        string file = new string('f', 117);

        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: "Label", files: [file])).Rules));

        Assert.Equal(file, msg);
    }

    [Theory]
    [InlineData("It's \"quoted\" \\ 100% $HOME %{tx.x}")]
    [InlineData("'")]
    [InlineData("%{REMOTE_ADDR}")]
    public void No_msg_holds_a_quote_backslash_percent_or_dollar(string label)
    {
        string msg = Msg(Assert.Single(ModSecurityRules.Build(Request(label: label, files: ["f.txt"])).Rules));

        Assert.NotEmpty(msg);
        Assert.True(msg.Length <= 120);
        Assert.DoesNotContain("'", msg);
        Assert.DoesNotContain("\"", msg);
        Assert.DoesNotContain("\\", msg);
        Assert.DoesNotContain("%", msg);
        Assert.DoesNotContain("$", msg);
    }
}
