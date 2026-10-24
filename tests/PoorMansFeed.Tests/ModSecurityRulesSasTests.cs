using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>A SAS in a server's configuration may only read and list one container or directory.</summary>
public sealed class ModSecurityRulesSasTests
{
    private const string Base = "https://acct.blob.core.windows.net/feeds?";

    private static void AssertRefused(string query, ModSecurityRuleFailure expected)
    {
        Assert.Equal(expected, ModSecurityRules.CheckLocation(Base + query));

        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, Base + query, ["a.txt"], "x"));
        Assert.Equal(expected, set.Failure);
        Assert.Empty(set.Rules);
        Assert.Equal("", set.Text);
    }

    [Theory]
    [InlineData("sv=2024-11-04&sr=c&sp=rl&se=2026-11-30&sig=AbC%2Bd%3D")]
    [InlineData("sv=2026-01-01&sr=c&sp=rl&se=2026-11-30T14:05:09Z&sig=SAMPLEONLY%2Bnot%3Dreal")]
    [InlineData("sr=d&sp=r&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&sr=d&sdd=1&se=2026-11-30&sig=x")]
    [InlineData("sr=c&sp=l&se=2026-11-30&sig=x")]
    [InlineData("sr=c&sp=lr&se=2026-11-30&sig=x")]
    [InlineData("sr=c&sp=rrl&se=2026-11-30&sig=x")]
    [InlineData("sig=x&sp=rl&sr=c&se=2026-11-30")]
    [InlineData("se=2026-11-30&sig=x&sp=rl&sr=c")]
    [InlineData("sp=%72%6C&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=%72%6c&sr=%63&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sp=l&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&sr=d&se=2026-11-30&sig=x")]
    [InlineData("si=policy&sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&sv=2024-11-04&spr=https&st=2026-01-01&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&se=2026-11-30T14:05:09%2B08:00&sig=x")]
    [InlineData("sp=r&sr=c&se=2026-11-30T14:05:09.1234567%2B08:00&sig=x")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&sig=y")]
    public void A_read_only_sas_for_one_container_or_directory_is_kept(string query)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(Base + query));

        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, Base + query, ["a.txt"], "x"));
        Assert.Equal(ModSecurityRuleFailure.None, set.Failure);
        Assert.Contains("@ipMatchFromFile https://acct.blob.core.windows.net/feeds/a.txt?" + query + "\"",
            Assert.Single(set.Rules));
    }

    // Written in another case or escaped: refused now because a SAS's names must all be plain lowercase letters
    [Theory]
    [InlineData("SP=rl&sr=c&sig=x")]
    [InlineData("Sp=r&sr=c&sig=x")]
    [InlineData("sp=r&sr=c&SIG=x")]
    [InlineData("sp=r&sr=c&sIg=x")]
    [InlineData("SIG=x")]
    [InlineData("%73p=r&sr=c&sig=x")]
    [InlineData("s%70=r&sr=c&sig=x")]
    [InlineData("%73%70=rl&sr=c&sig=x")]
    [InlineData("S%50=r&sr=c&sig=x")]
    [InlineData("sp=r&sr=c&%73%69%67=x")]
    [InlineData("sp=r&sr=c&%53%49%47=x")]
    [InlineData("%53%49%47=x")]
    [InlineData("sp=r&SR=c&sig=x")]
    [InlineData("sp=r&s%72=c&sig=x")]
    [InlineData("sp=r&sr=c&SI=policy&sig=x")]
    [InlineData("sp=r&sr=c&SE=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&%73e=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&SS=b&sig=x")]
    [InlineData("sp=r&sr=c&%73s=b&sig=x")]
    [InlineData("sp=r&sr=c&Srt=o&sig=x")]
    [InlineData("sp=r&sp=r&SP=w&sr=c&sig=x")]
    [InlineData("sp=rw&SR=c&sig=x")]
    [InlineData("sp=rw&sr=b&SIG=x")]
    [InlineData("SV=2024-11-04&SPR=https&ST=2026-01-01&sp=r&sr=c&se=2026-11-30&sig=x")]
    public void A_sas_name_not_written_in_lowercase_is_rights_unknown(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasRightsUnknown);
    }

    [Theory]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&sdd2=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&s_d=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&s-d=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&s.d=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&x+y=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&x?y=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&x%00=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&%73dd=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&%20")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&=1")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig=x&")]
    [InlineData("&sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=r&&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&%20sp=racwd&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&sp%20=w&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&sp+=w&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&sr=c&%20sr=b&se=2026-11-30&sig=x")]
    [InlineData("sp=rl&sr=c&se=2026-11-30&%20se=2020-01-01&sig=x")]
    public void A_sas_name_that_isnt_only_lowercase_letters_is_rights_unknown(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasRightsUnknown);
    }

    // Each starts the checks, so its sp=w would be refused; the garbled name itself is what refuses it first
    [Theory]
    [InlineData("x?sig=x&sp=w")]
    [InlineData("??sig=x&sp=w")]
    [InlineData("sig%20=x&sp=w")]
    [InlineData("%20sig=x&sp=w")]
    [InlineData("sig+=x&sp=w")]
    [InlineData("+sig=x&sp=w")]
    [InlineData("sig%00=x&sp=w")]
    [InlineData("sig2=1&sp=w")]
    [InlineData("2sig=1&sp=w")]
    [InlineData("sig%3D=x&sp=w")]
    [InlineData("sig.x=1&sp=w")]
    [InlineData("x-sig=1&sp=w")]
    [InlineData("x_sig=1&sp=w")]
    [InlineData("%E9sig=x&sp=w")]
    [InlineData("s%69g=x&sp=w")]
    [InlineData("%53Ig=x&sp=w")]
    [InlineData("sp=r&sr=c&se=2026-11-30&sig%20=x")]
    [InlineData("sp=r&sr=c&se=2026-11-30&Sig=x")]
    public void A_garbled_sig_still_starts_the_checks(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasRightsUnknown);
    }

    [Theory]
    [InlineData("sv=2024-11-04&sr=c&sig=x")]
    [InlineData("sig=x")]
    [InlineData("sig")]
    [InlineData("si=policy&sig=x")]
    [InlineData("si=policy&sv=2024-11-04&sr=c&sig=AbC%2Bd%3D")]
    [InlineData("s%70x=r&sr=c&sig=x")]
    [InlineData("sp%3D=r&sr=c&sig=x")]
    [InlineData("spr=https&sr=c&sig=x")]
    [InlineData("spx=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sr=c&se=2026-11-30&sig=x")]
    [InlineData("ss=b&srt=sco&sig=x")]
    [InlineData("sr=b&sig=x")]
    public void A_sas_with_no_sp_is_rights_unknown(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasRightsUnknown);
    }

    [Theory]
    [InlineData("sv=2024-11-04&sr=c&sp=racwdl&sig=AbC%2Bd%3D")]
    [InlineData("sp=rw&sr=c&sig=x")]
    [InlineData("sp=w&sr=c&sig=x")]
    [InlineData("sp=rld&sr=c&sig=x")]
    [InlineData("sp=R&sr=c&sig=x")]
    [InlineData("sp=rL&sr=c&sig=x")]
    [InlineData("sp=&sr=c&sig=x")]
    [InlineData("sp&sr=c&sig=x")]
    [InlineData("sp=r+l&sr=c&sig=x")]
    [InlineData("sp=r%20&sr=c&sig=x")]
    [InlineData("sp=r%00&sr=c&sig=x")]
    [InlineData("sp=%77&sr=c&sig=x")]
    [InlineData("sp=%52&sr=c&sig=x")]
    [InlineData("sp=r&sp=w&sr=c&sig=x")]
    [InlineData("sp=rl&sp=&sr=c&sig=x")]
    [InlineData("sp=rw&sr=c&sig")]
    [InlineData("sp=rw&sr=c&sig=")]
    [InlineData("sp=rw&sig=x")]
    [InlineData("sp=rw&ss=b&srt=o&sig=x")]
    [InlineData("sp=w&sr=b&sig=x")]
    [InlineData("sp=rw&sr=c&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sp=w&sr=c&se=2026-11-30&sig=x")]
    public void A_sas_that_can_do_more_than_read_and_list_is_refused(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasCanWrite);
    }

    [Theory]
    [InlineData("sp=rl&sig=x")]
    [InlineData("sp=r&sr=b&sig=x")]
    [InlineData("sp=r&sr=bv&sig=x")]
    [InlineData("sp=r&sr=s&sig=x")]
    [InlineData("sp=r&sr=f&sig=x")]
    [InlineData("sp=r&sr=&sig=x")]
    [InlineData("sp=r&sr&sig=x")]
    [InlineData("sp=r&sr=C&sig=x")]
    [InlineData("sp=r&sr=D&sig=x")]
    [InlineData("sp=r&sr=cd&sig=x")]
    [InlineData("sp=r&sr=c%20&sig=x")]
    [InlineData("sp=r&sr=%62&sig=x")]
    [InlineData("sp=r&sr=c&sr=b&sig=x")]
    [InlineData("sp=r&sr=c&ss=b&sig=x")]
    [InlineData("sp=r&sr=c&ss=&sig=x")]
    [InlineData("sp=r&sr=d&srt=o&sig=x")]
    [InlineData("sp=r&sr=c&srt&sig=x")]
    [InlineData("sp=r&ss=b&srt=sco&sig=x")]
    [InlineData("sv=2024-11-04&ss=b&srt=sco&sp=rl&se=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=b&se=2026-11-30&sig=x")]
    public void A_sas_wider_than_one_container_or_directory_is_refused(string query)
    {
        AssertRefused(query, ModSecurityRuleFailure.SasScopeTooWide);
    }

    [Theory]
    [InlineData("sp=racwdl")]
    [InlineData("si=policy")]
    [InlineData("sv=2024-11-04&sr=c")]
    [InlineData("ss=b&srt=sco&sp=rl")]
    [InlineData("sr=b&sp=w")]
    [InlineData("SP=w&SR=b")]
    [InlineData("%73p=w&SR=b")]
    [InlineData("se=2020-01-01&sp=w")]
    [InlineData("&&sp=w&")]
    [InlineData("signature=x&sp=w")]
    [InlineData("xsig=1&sp=w")]
    [InlineData("sigx=1&sp=w")]
    [InlineData("sigsig=1&sp=w")]
    [InlineData("x=sig&sp=w")]
    [InlineData("s%C3%AFg=x&sp=w")]
    [InlineData("s+ig=x&sp=w")]
    [InlineData("s%20ig=x&sp=w")]
    [InlineData("%2573ig=x&sp=w")]
    [InlineData("si%67n=x&sp=w")]
    [InlineData("X-Amz-Signature=x&sp=w")]
    [InlineData("X-Amz-Credential=x&X-Amz-Signature=y")]
    public void A_query_with_no_sig_is_not_checked(string query)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(Base + query));
    }

    [Theory]
    [InlineData("https://h.example/../d?sp=w&sig=x")]
    [InlineData("https://h.example/%2e/d?sig=x")]
    [InlineData("https://h.example/d%2f?sp=w&sig=x")]
    [InlineData("https://h.example/d%5C?SIG=x")]
    [InlineData("https://h.example/d?sp=w&sig=x%G1")]
    [InlineData("https://h.example/d?sp=w&sig=x\n")]
    [InlineData("https:///d?sp=w&sig=x")]
    public void A_location_of_the_wrong_shape_stays_invalid_whatever_its_sas(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(location));
    }

    [Fact]
    public void A_folder_never_has_a_sas()
    {
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation("/srv?sp=w&sig=x"));
    }

    [Fact]
    public void A_bad_file_name_comes_before_a_sas_that_can_write()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, Base + "sp=w&sig=x", ["a b"], "x"));

        Assert.Equal(ModSecurityRuleFailure.FileNameInvalid, set.Failure);
    }

    [Fact]
    public void An_id_out_of_range_comes_before_a_sas_that_can_write()
    {
        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([0], ModSecurityAction.Deny, Base + "sp=w&sig=x", ["a.txt"], "x"));

        Assert.Equal(ModSecurityRuleFailure.RuleIdOutOfRange, set.Failure);
    }
}
