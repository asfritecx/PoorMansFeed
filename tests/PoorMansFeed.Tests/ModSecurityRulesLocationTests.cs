using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>A location is a server folder or an https prefix, and anything outside its allowlist is refused.</summary>
public sealed class ModSecurityRulesLocationTests
{
    private static string? RuleLocation(string location, string file = "a.txt")
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, location, [file], "x"));
        if (set.Failure != ModSecurityRuleFailure.None)
        {
            return null;
        }

        string line = Assert.Single(set.Rules);
        const string head = "SecRule REMOTE_ADDR \"@ipMatchFromFile ";
        Assert.StartsWith(head, line);
        return line[head.Length..line.IndexOf("\" \"id:", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void An_empty_location_gives_no_location(string? location)
    {
        Assert.Equal(ModSecurityRuleFailure.NoLocation, ModSecurityRules.CheckLocation(location!));
    }

    [Theory]
    [InlineData("http://x/y")]
    [InlineData("HTTP://x/y")]
    [InlineData("Http://x")]
    [InlineData("http://")]
    public void Plain_http_is_not_https(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.NotHttps, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("HTTPS://x")]
    [InlineData("Https://x")]
    [InlineData("ftp://x")]
    [InlineData("srv/lists")]
    [InlineData("C:\\lists")]
    [InlineData(" /srv")]
    [InlineData("/srv ")]
    [InlineData("~/lists")]
    public void Other_locations_are_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//srv")]
    [InlineData("/srv//lists")]
    [InlineData("/srv/lists//")]
    [InlineData("/srv/../lists")]
    [InlineData("/srv/..")]
    [InlineData("/..")]
    [InlineData("/../srv")]
    [InlineData("/srv/./lists")]
    [InlineData("/srv/.")]
    [InlineData("/.")]
    [InlineData("/./srv")]
    [InlineData("/srv/./")]
    [InlineData("/srv/../")]
    [InlineData("/srv/%2e%2e/x")]
    [InlineData("/srv/%2e/x")]
    public void Bad_folders_are_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("/srv/lists", "/srv/lists/a.txt")]
    [InlineData("/srv/lists/", "/srv/lists/a.txt")]
    [InlineData("/a/..b", "/a/..b/a.txt")]
    [InlineData("/a/b..", "/a/b../a.txt")]
    [InlineData("/a/.b", "/a/.b/a.txt")]
    [InlineData("/a/b.", "/a/b./a.txt")]
    [InlineData("/a/...", "/a/.../a.txt")]
    [InlineData("/a", "/a/a.txt")]
    [InlineData("/etc/modsecurity/lists/", "/etc/modsecurity/lists/a.txt")]
    public void Good_folders_give_the_file_inside_them(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Fact]
    public void The_example_folder_gives_the_example_path()
    {
        Assert.Equal("/etc/modsecurity/lists/AWS_EC2_all_ipv4.txt",
            RuleLocation("/etc/modsecurity/lists/", "AWS_EC2_all_ipv4.txt"));
    }

    [Fact]
    public void A_folder_of_1024_characters_is_allowed_and_1025_is_not()
    {
        string ok = "/" + new string('a', 1023);
        string over = "/" + new string('a', 1024);

        Assert.Equal(1024, ok.Length);
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(ok));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(over));
    }

    [Fact]
    public void A_url_of_2048_characters_is_allowed_and_2049_is_not()
    {
        const string head = "https://h.example/";
        string ok = head + new string('a', 2048 - head.Length);
        string over = head + new string('a', 2049 - head.Length);

        Assert.Equal(2048, ok.Length);
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(ok));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(over));
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("https:///x")]
    [InlineData("https://?q")]
    [InlineData("https://:8443/x")]
    [InlineData("https://:8443")]
    public void A_url_with_no_host_is_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("https://h:8443/x", "https://h:8443/x/a.txt")]
    [InlineData("https://lists.example.com/feeds", "https://lists.example.com/feeds/a.txt")]
    [InlineData("https://acct.blob.core.windows.net/feeds/?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D",
                "https://acct.blob.core.windows.net/feeds/a.txt?sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D")]
    [InlineData("https://h.example", "https://h.example/a.txt")]
    [InlineData("https://h.example//", "https://h.example/a.txt")]
    [InlineData("https://h.example/d?", "https://h.example/d/a.txt")]
    [InlineData("https://h.example?x=1", "https://h.example/a.txt?x=1")]
    [InlineData("https://h.example/d?a=1?b=2", "https://h.example/d/a.txt?a=1?b=2")]
    [InlineData("https://h.example/a~b:c?d&e=f+g,h%2Fi%2fj", "https://h.example/a~b:c/a.txt?d&e=f+g,h%2Fi%2fj")]
    public void Good_urls_give_the_file_under_them_with_the_query_after(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData("https://h.example/.")]
    [InlineData("https://h.example/..")]
    [InlineData("https://h.example/./")]
    [InlineData("https://h.example/../")]
    [InlineData("https://h.example/./d")]
    [InlineData("https://h.example/../d")]
    [InlineData("https://h.example/d/./x")]
    [InlineData("https://h.example/d/../x")]
    [InlineData("https://h.example/d/..?sig=x")]
    [InlineData("https://h.example:8443/d/.?sig=x")]
    [InlineData("https://h.example/%2e/x")]
    [InlineData("https://h.example/%2E/x")]
    [InlineData("https://h.example/%2e%2e/x")]
    [InlineData("https://h.example/%2E%2E/x")]
    [InlineData("https://h.example/%2e%2E/x")]
    [InlineData("https://h.example/.%2e/x")]
    [InlineData("https://h.example/%2E./x")]
    [InlineData("https://h.example/d/%2e%2e")]
    [InlineData("https://h.example/d/%2e?sig=x")]
    public void A_url_with_a_dot_segment_in_its_path_is_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("https://h.example/d/.b", "https://h.example/d/.b/a.txt")]
    [InlineData("https://h.example/d/b.", "https://h.example/d/b./a.txt")]
    [InlineData("https://h.example/d/...", "https://h.example/d/.../a.txt")]
    [InlineData("https://h.example/d/%2e%2e%2e", "https://h.example/d/%2e%2e%2e/a.txt")]
    [InlineData("https://h.example/d/x%2e", "https://h.example/d/x%2e/a.txt")]
    [InlineData("https://h.example/d/%2ex", "https://h.example/d/%2ex/a.txt")]
    public void A_url_dot_that_is_not_a_whole_segment_is_kept(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData("https://h.example/d%2fx")]
    [InlineData("https://h.example/d%2Fx")]
    [InlineData("https://h.example/d%5cx")]
    [InlineData("https://h.example/d%5Cx")]
    [InlineData("https://h.example/d/%2f..")]
    [InlineData("https://h.example/%5c..%5cx")]
    [InlineData("https://h.example/d%2f")]
    [InlineData("https://h.example/d%2F?x=1")]
    [InlineData("https://h.example/d%5C?sv=2026-01-01&sr=c&sp=rl&sig=SAMPLEONLY%2Bnot%3Dreal")]
    [InlineData("https://h.example%2fx/d")]
    [InlineData("https://h.example:8443%5c/d")]
    [InlineData("https://h.example%2F")]
    public void An_encoded_slash_or_backslash_before_the_query_is_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    // The portal's and az's SAS tokens come without their ?, so the natural paste puts the token in the path
    [Theory]
    [InlineData("https://acct.blob.core.windows.net/feedssp=racwdl&st=2026-10-23T00:00:00Z&se=2027-10-23T00:00:00Z"
                + "&spr=https&sv=2025-01-05&sr=c&sig=SAMPLEONLY%3D")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sp=racwdl&se=2027-10-23&sr=c&sig=SAMPLEONLY%3D")]
    [InlineData("https://acct.blob.core.windows.net/feedssp=rl&sr=c&se=2027-10-23&sig=SAMPLEONLY")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sig=SAMPLEONLY&sp=rl&sr=c&se=2027-10-23")]
    [InlineData("https://acct.blob.core.windows.net/feeds%3Fsp=racwdl&ss=b&srt=sco&se=2027-10-23&sig=SAMPLEONLY")]
    [InlineData("https://acct.blob.core.windows.net/feeds%3fsp=rl")]
    [InlineData("https://acct.blob.core.windows.net/feeds%3F?sp=rl&sr=c&se=2027-10-23&sig=SAMPLEONLY")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sp=rl&sr=c?se=2027-10-23&sig=SAMPLEONLY")]
    [InlineData("https://h.example/d&x")]
    [InlineData("https://h.example/d&")]
    [InlineData("https://h.example/&/d")]
    [InlineData("https://h.example/d%26x")]
    [InlineData("https://h.example/d%26?x=1")]
    [InlineData("https://h.example&x/d")]
    [InlineData("https://h.example%3F/d")]
    [InlineData("https://h.example:8443%26/d")]
    [InlineData("https://h.example&")]
    public void An_ampersand_or_encoded_question_mark_before_the_query_is_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
        Assert.Null(ModSecurityRules.SasExpiry(location));
    }

    [Theory]
    [InlineData("https://h.example/year=2026/month=10", "https://h.example/year=2026/month=10/a.txt")]
    [InlineData("https://h.example/k=v?sp=rl&sr=d&sdd=2&se=2026-11-30&sig=x",
                "https://h.example/k=v/a.txt?sp=rl&sr=d&sdd=2&se=2026-11-30&sig=x")]
    [InlineData("https://h.example/d%3D", "https://h.example/d%3D/a.txt")]
    public void An_equals_sign_before_the_query_is_kept(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    // A token whose ? was typed after its sig puts the signature in the path; the path is judged before any SAS check,
    // so a query that would fail one still gives LocationInvalid
    [Theory]
    [InlineData("https://acct.blob.core.windows.net/feeds/sig=SAMPLEONLY?sp=racwdl&sr=c&se=2027-10-23")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sig=SAMPLEONLY")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sig=SAMPLEONLY/d?sp=rl&sr=c&se=2027-10-23&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds/sig=SAMPLEONLY/d?sp=racwdl&sr=c&se=2027-10-23&sig=x")]
    [InlineData("https://h.example/sig=1?SP=r&sig=x")]
    [InlineData("https://sig=x.example?sp=racwdl&sr=c&se=2027-10-23&sig=x")]
    [InlineData("https://h.example/x-sig=1/")]
    [InlineData("https://h.example/%73ig=1/")]
    [InlineData("https://h.example/%73%49%67=1")]
    [InlineData("https://h.example/SIG=1")]
    [InlineData("https://h.example/Sig=1")]
    [InlineData("https://h.example/sig_x=1")]
    [InlineData("https://h.example/sig1=1")]
    [InlineData("https://h.example/sig+x=1")]
    [InlineData("https://h.example/a.sig=1")]
    [InlineData("https://h.example/sig=")]
    [InlineData("https://h.example/sig==1")]
    [InlineData("https://h.example/sig%3D1")]
    [InlineData("https://h.example/sig%3d1")]
    [InlineData("https://h.example/d/sig=1?x=1")]
    [InlineData("https://h.example/sig=1/d")]
    [InlineData("https://sig=x.example/d")]
    [InlineData("https://h.example:8443/sig=x")]
    public void A_path_segment_named_like_a_signature_is_invalid(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
        Assert.Null(ModSecurityRules.SasExpiry(location));
    }

    [Theory]
    [InlineData("https://h.example/year=2026/", "https://h.example/year=2026/a.txt")]
    [InlineData("https://h.example/signal=1/", "https://h.example/signal=1/a.txt")]
    [InlineData("https://h.example/sig/", "https://h.example/sig/a.txt")]
    [InlineData("https://h.example/sig-x/d", "https://h.example/sig-x/d/a.txt")]
    [InlineData("https://h.example/xsig=1", "https://h.example/xsig=1/a.txt")]
    [InlineData("https://h.example/sigs=1", "https://h.example/sigs=1/a.txt")]
    [InlineData("https://h.example/signature=1", "https://h.example/signature=1/a.txt")]
    [InlineData("https://h.example/a=sig", "https://h.example/a=sig/a.txt")]
    [InlineData("https://h.example/a=sig=1", "https://h.example/a=sig=1/a.txt")]
    [InlineData("https://h.example/x%3Dsig=1", "https://h.example/x%3Dsig=1/a.txt")]
    [InlineData("https://h.example/%2573ig=1", "https://h.example/%2573ig=1/a.txt")]
    [InlineData("https://h.example/sig%253D1", "https://h.example/sig%253D1/a.txt")]
    [InlineData("https://h.example/d?x=1/sig=2", "https://h.example/d/a.txt?x=1/sig=2")]
    [InlineData("https://h.example/d?sig=x&sp=rl&sr=c&se=2026-11-30",
                "https://h.example/d/a.txt?sig=x&sp=rl&sr=c&se=2026-11-30")]
    public void A_path_segment_not_named_like_a_signature_is_kept(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData("https://h.example/d%252f", "https://h.example/d%252f/a.txt")]
    [InlineData("https://h.example/d%255c", "https://h.example/d%255c/a.txt")]
    [InlineData("https://h.example/d%2e%5b", "https://h.example/d%2e%5b/a.txt")]
    [InlineData("https://h.example/d%253f", "https://h.example/d%253f/a.txt")]
    [InlineData("https://h.example/d%2526", "https://h.example/d%2526/a.txt")]
    [InlineData("https://h.example/d%22f", "https://h.example/d%22f/a.txt")]
    [InlineData("https://h.example/d?p=%2f%5C", "https://h.example/d/a.txt?p=%2f%5C")]
    [InlineData("https://h.example/d?sv=2026-01-01&sr=c&sp=rl&se=2026-11-30&sig=SAMPLEONLY%2Fnot%5Creal",
                "https://h.example/d/a.txt?sv=2026-01-01&sr=c&sp=rl&se=2026-11-30&sig=SAMPLEONLY%2Fnot%5Creal")]
    [InlineData("https://h.example/d?sr=c&sp=rl&se=2026-11-30T14:05:09%2B08:00&sig=SAMPLEONLY",
                "https://h.example/d/a.txt?sr=c&sp=rl&se=2026-11-30T14:05:09%2B08:00&sig=SAMPLEONLY")]
    [InlineData("https://h.example/d?sr=c&sp=rl&se=2026-11-30T14%3A05%3A09.5%2D08%3A00&sig=SAMPLEONLY",
                "https://h.example/d/a.txt?sr=c&sp=rl&se=2026-11-30T14%3A05%3A09.5%2D08%3A00&sig=SAMPLEONLY")]
    public void Other_escapes_and_separators_in_the_query_are_kept(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData("https://h.example/d?p=../x", "https://h.example/d/a.txt?p=../x")]
    [InlineData("https://h.example/d?/./x", "https://h.example/d/a.txt?/./x")]
    [InlineData("https://h.example/d?p=%2e%2e/%2E", "https://h.example/d/a.txt?p=%2e%2e/%2E")]
    [InlineData("https://h.example?..", "https://h.example/a.txt?..")]
    public void Dot_segments_in_the_query_are_not_checked(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData("https://../d", "https://../d/a.txt")]
    [InlineData("https://./d", "https://./d/a.txt")]
    [InlineData("https://%2e%2e/d", "https://%2e%2e/d/a.txt")]
    [InlineData("https://..:8443/d", "https://..:8443/d/a.txt")]
    [InlineData("https://..?x=1", "https://../a.txt?x=1")]
    public void Dots_in_the_host_are_not_checked(string location, string expected)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
        Assert.Equal(expected, RuleLocation(location));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("\\")]
    [InlineData("$")]
    [InlineData("#")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("@")]
    [InlineData(";")]
    [InlineData("*")]
    [InlineData("!")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    [InlineData("`")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("\u00e9")]
    [InlineData("\ud83d\ude00")]
    [InlineData("~")]
    [InlineData(",")]
    [InlineData(":")]
    [InlineData("%")]
    [InlineData("%2F")]
    [InlineData("?")]
    [InlineData("&")]
    [InlineData("=")]
    [InlineData("+")]
    public void A_folder_refuses_every_character_outside_its_allowlist(string inserted)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid,
            ModSecurityRules.CheckLocation("/srv/li" + inserted + "sts"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("\\")]
    [InlineData("$")]
    [InlineData("#")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("@")]
    [InlineData(";")]
    [InlineData("*")]
    [InlineData("!")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    [InlineData("`")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("\u00e9")]
    [InlineData("\ud83d\ude00")]
    [InlineData("%")]
    [InlineData("%4")]
    [InlineData("%G1")]
    [InlineData("%{")]
    [InlineData("%{x}")]
    public void A_url_refuses_every_character_outside_its_allowlist(string inserted)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid,
            ModSecurityRules.CheckLocation("https://h.example/li" + inserted + "sts"));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid,
            ModSecurityRules.CheckLocation("https://h.example/d?q=" + inserted));
    }

    [Theory]
    [InlineData("/srv/lists\n")]
    [InlineData("https://h.example/d\n")]
    [InlineData("https://h.example/d?q=1\n")]
    [InlineData("/srv/lists\r\n")]
    public void A_trailing_newline_is_refused(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%4")]
    public void A_url_ending_in_a_cut_escape_is_refused(string tail)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid,
            ModSecurityRules.CheckLocation("https://h.example/d" + tail));
    }

    [Fact]
    public void A_url_accepts_every_character_of_its_allowlist()
    {
        const string location = "https://Az09-._~.example:443/Az09-._~/:x?a=1&b=2+3,4%2F%2f%aB";

        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));
    }

    [Theory]
    [InlineData("/srv/lists")]
    [InlineData("/srv/lists/")]
    [InlineData("/")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("https://h.example/%2e%2e/x")]
    [InlineData("https://h.example/d?p=../x")]
    [InlineData("https://h.example/d?x=1")]
    [InlineData("https://")]
    [InlineData("https://h.example/%G1")]
    [InlineData("http://h.example")]
    [InlineData("HTTPS://h.example")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("srv")]
    [InlineData("/srv/lists\n")]
    [InlineData("https://h.example/d%2fx")]
    [InlineData("https://h.example/d?sp=rw&sr=c&sig=x")]
    [InlineData("https://h.example/d?sr=c&sig=x")]
    [InlineData("https://h.example/d?SP=r&sr=c&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=b&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30T14Z&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30T14:05:09.5%2B08:00&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30&sig%20=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30&sig=x&")]
    [InlineData("https://h.example/d?sig2=x&sp=w")]
    [InlineData("https://h.example/d?X-Amz-Signature=x&sp=w")]
    [InlineData("https://h.example/dsp=rw&sr=c&sig=x")]
    [InlineData("https://h.example/d%3fsp=rw")]
    [InlineData("https://h.example/year=2026")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2026-11-30T14:05:09+08:00&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2027-10-23&skoid=x&ske=2026-10-30T00:00:00Z&sig=x")]
    [InlineData("https://h.example/d?sp=r&sr=c&se=2027-10-23&skoid=x&sig=x")]
    [InlineData("https://h.example/sig=x?sp=rw&sr=c&se=2027-10-23")]
    [InlineData("https://h.example/signal=1")]
    public void Build_and_check_location_agree(string location)
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(new ModSecurityRuleRequest([10_000, 10_001],
            ModSecurityAction.LogOnly, location, ["a_ipv4.txt", "a_ipv6.txt"], "x"));

        Assert.Equal(ModSecurityRules.CheckLocation(location), set.Failure);
    }
}
