using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>A SAS's expiry is read in the forms Microsoft documents, as UTC; a SAS without one is refused.</summary>
public sealed class ModSecurityRulesExpiryTests
{
    private const string Base = "https://acct.blob.core.windows.net/feeds?";

    private static string Sas(string expiry) =>
        Base + "sv=2026-01-01&sr=c&sp=rl&" + expiry + "&sig=SAMPLEONLY%2Bnot%3Dreal";

    private static void AssertRead(string location, DateTimeOffset expected, bool hasTime)
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(location));

        DateTimeOffset? read = ModSecurityRules.SasExpiry(location);
        Assert.Equal(expected, read);
        Assert.Equal(TimeSpan.Zero, read!.Value.Offset);
        Assert.Equal(hasTime, ModSecurityRules.SasExpiryHasTime(location));
    }

    private static void AssertExpiryUnknown(string location)
    {
        Assert.Equal(ModSecurityRuleFailure.SasExpiryUnknown, ModSecurityRules.CheckLocation(location));

        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, location, ["a.txt"], "x"));
        Assert.Equal(ModSecurityRuleFailure.SasExpiryUnknown, set.Failure);
        Assert.Empty(set.Rules);
        Assert.Equal("", set.Text);
        Assert.Null(ModSecurityRules.SasExpiry(location));
        Assert.Null(ModSecurityRules.SasExpiryHasTime(location));
    }

    [Theory]
    [InlineData("se=2026-11-30", 0, 0, 0, false)]
    [InlineData("se=%32%30%32%36-11-30", 0, 0, 0, false)]
    [InlineData("se=2026-11-30T14:05Z", 14, 5, 0, true)]
    [InlineData("se=2026-11-30T14:05:09Z", 14, 5, 9, true)]
    [InlineData("se=2026-11-30T14%3A05%3A09Z", 14, 5, 9, true)]
    [InlineData("se=2026-11-30T14%3a05Z", 14, 5, 0, true)]
    [InlineData("se=2026-11-30T14:05:09%5A", 14, 5, 9, true)]
    [InlineData("se=2026-11-30T00:00Z", 0, 0, 0, true)]
    [InlineData("se=2026-11-30T00:00:00Z", 0, 0, 0, true)]
    [InlineData("se=2026-11-30T23:59:59Z", 23, 59, 59, true)]
    public void The_expiry_is_read_in_each_of_azures_forms_as_utc(string expiry, int hour, int minute, int second,
        bool hasTime)
    {
        AssertRead(Sas(expiry), new DateTimeOffset(2026, 11, 30, hour, minute, second, TimeSpan.Zero), hasTime);
    }

    [Theory]
    [InlineData(".5", 5_000_000)]
    [InlineData(".05", 500_000)]
    [InlineData(".12", 1_200_000)]
    [InlineData(".123", 1_230_000)]
    [InlineData(".1234", 1_234_000)]
    [InlineData(".12345", 1_234_500)]
    [InlineData(".123456", 1_234_560)]
    [InlineData(".1234567", 1_234_567)]
    [InlineData(".0000001", 1)]
    [InlineData(".9999999", 9_999_999)]
    [InlineData(".0", 0)]
    [InlineData(".0000000", 0)]
    [InlineData("%2E5", 5_000_000)]
    public void Fractional_seconds_of_one_to_seven_digits_are_read_as_ticks(string fraction, long ticks)
    {
        AssertRead(Sas("se=2026-11-30T14:05:09" + fraction + "Z"),
            new DateTimeOffset(2026, 11, 30, 14, 5, 9, TimeSpan.Zero).AddTicks(ticks), true);
    }

    [Theory]
    [InlineData("2026-11-30T14:05:09%2B08:00", 2026, 11, 30, 6, 5, 9)]
    [InlineData("2026-11-30T14:05:09%2b08%3a00", 2026, 11, 30, 6, 5, 9)]
    [InlineData("2026-11-30T14:05:09-05:00", 2026, 11, 30, 19, 5, 9)]
    [InlineData("2026-11-30T14:05%2B05:30", 2026, 11, 30, 8, 35, 0)]
    [InlineData("2026-11-30T14:05:09%2B00:00", 2026, 11, 30, 14, 5, 9)]
    [InlineData("2026-11-30T14:05:09-00:00", 2026, 11, 30, 14, 5, 9)]
    [InlineData("2026-11-30T00:30:00%2B01:00", 2026, 11, 29, 23, 30, 0)]
    [InlineData("2026-12-31T23:30:00-01:00", 2027, 1, 1, 0, 30, 0)]
    [InlineData("2024-03-01T00:00%2B00:01", 2024, 2, 29, 23, 59, 0)]
    [InlineData("2026-11-30T14:05:09%2B23:59", 2026, 11, 29, 14, 6, 9)]
    [InlineData("2026-11-30T14:05:09-23:59", 2026, 12, 1, 14, 4, 9)]
    public void An_offset_is_the_local_times_lead_on_utc(string expiry, int year, int month, int day, int hour,
        int minute, int second)
    {
        AssertRead(Sas("se=" + expiry), new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero),
            true);
    }

    [Fact]
    public void A_fraction_and_an_offset_are_read_together()
    {
        AssertRead(Sas("se=2026-11-30T14:05:09.5-08:00"),
            new DateTimeOffset(2026, 11, 30, 22, 5, 9, TimeSpan.Zero).AddTicks(5_000_000), true);
    }

    [Fact]
    public void The_expiry_is_read_up_to_both_ends_of_the_calendar()
    {
        AssertRead(Sas("se=0001-01-01"), DateTimeOffset.MinValue, false);
        AssertRead(Sas("se=0001-01-01T01:00%2B01:00"), DateTimeOffset.MinValue, true);
        AssertRead(Sas("se=9999-12-31"), new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero), false);
        AssertRead(Sas("se=9999-12-31T23:59:59.9999999Z"), DateTimeOffset.MaxValue, true);
        AssertRead(Sas("se=9999-12-31T22:59:59.9999999-01:00"), DateTimeOffset.MaxValue, true);
        AssertRead(Sas("se=2024-02-29"), new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), false);
    }

    [Theory]
    [InlineData("se=0001-01-01T00:59:59.9999999%2B01:00")]
    [InlineData("se=0001-01-01T00:00%2B00:01")]
    [InlineData("se=9999-12-31T23:00-01:00")]
    [InlineData("se=9999-12-31T23:59:59.9999999-00:01")]
    public void An_expiry_past_either_end_of_the_calendar_is_unknown(string expiry)
    {
        AssertExpiryUnknown(Base + "sp=r&sr=c&" + expiry + "&sig=x");
    }

    [Theory]
    [InlineData("se=2026-11-30&sig=x", 2026, 11, 30)]
    [InlineData("sig=x&se=0001-01-01", 1, 1, 1)]
    [InlineData("se=9999-12-31&sig=x", 9999, 12, 31)]
    public void The_expiry_may_come_anywhere_in_the_query(string query, int year, int month, int day)
    {
        AssertRead(Base + "sp=r&sr=c&" + query, new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero), false);
    }

    [Fact]
    public void A_directory_sas_has_its_expiry_read_too()
    {
        AssertRead(Base + "sp=rl&sr=d&sdd=1&se=2026-11-30&sig=x",
            new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero), false);
    }

    [Theory]
    [InlineData("se=")]
    [InlineData("se")]
    [InlineData("se=2026-11-31")]
    [InlineData("se=2026-02-29")]
    [InlineData("se=2026-00-10")]
    [InlineData("se=2026-13-01")]
    [InlineData("se=2026-11-00")]
    [InlineData("se=0000-01-01")]
    [InlineData("se=2026-1-30")]
    [InlineData("se=26-11-30")]
    [InlineData("se=02026-11-30")]
    [InlineData("se=2026-11-30Z")]
    [InlineData("se=2026-11-30%2B08:00")]
    [InlineData("se=2026-11-30T14Z")]
    [InlineData("se=2026-11-30T14%2B08:00")]
    [InlineData("se=2026-11-30T14:05")]
    [InlineData("se=2026-11-30T14:05:09")]
    [InlineData("se=2026-11-30T14:05:09.5")]
    [InlineData("se=2026-11-30T14:05:09z")]
    [InlineData("se=2026-11-30t14:05:09Z")]
    [InlineData("se=2026-11-30%2014:05:09Z")]
    [InlineData("se=2026-11-30T24:00Z")]
    [InlineData("se=2026-11-30T14:60Z")]
    [InlineData("se=2026-11-30T14:05:60Z")]
    [InlineData("se=2026-11-30T1405Z")]
    [InlineData("se=2026-11-30T14:05:09.Z")]
    [InlineData("se=2026-11-30T14:05:09.12345678Z")]
    [InlineData("se=2026-11-30T14:05:09,5Z")]
    [InlineData("se=2026-11-30T14:05.5Z")]
    [InlineData("se=2026-11-30T14:05:09%2B24:00")]
    [InlineData("se=2026-11-30T14:05:09%2B08:60")]
    [InlineData("se=2026-11-30T14:05:09%2B0800")]
    [InlineData("se=2026-11-30T14:05:09%2B08")]
    [InlineData("se=2026-11-30T14:05:09%2008:00")]
    [InlineData("se=2026-11-30T14:05:09Z%2B08:00")]
    [InlineData("se=2026-11-30T14:05:09%2B08:00Z")]
    [InlineData("se=2026-11-30%20")]
    [InlineData("se=%202026-11-30")]
    [InlineData("se=2026-11-30%0A")]
    [InlineData("se=2026-11-30T14:05:09Z%0A")]
    [InlineData("se=2026-11-30T14:05:09ZZ")]
    [InlineData("se=%EF%BC%92026-11-30")]
    [InlineData("se=1764511509")]
    [InlineData("se=2026-11-30&se=2026-11-30")]
    [InlineData("se=2026-11-30&se=2026-12-01")]
    [InlineData("se=2026-11-30&se=")]
    public void An_expiry_that_cant_be_read_or_isnt_one_is_expiry_unknown(string expiry)
    {
        AssertExpiryUnknown(Base + "sp=r&sr=c&" + expiry + "&sig=x");
    }

    [Theory]
    [InlineData("se=2026-11-30T14:05:09+08:00")]
    [InlineData("se=2026-11-30T14:05+05:30")]
    [InlineData("se=2026-11-30T14:05:09+00:00")]
    [InlineData("se=2026-11-30T14:05:09.5+08:00")]
    [InlineData("se=2026-11-30T14%3A05%3A09+08%3A00")]
    [InlineData("se=2026-11-30T14:05:09Z&ske=2026-10-30T00:00:00+00:00")]
    public void A_raw_plus_in_an_expiry_is_expiry_unknown(string expiry)
    {
        AssertExpiryUnknown(Base + "sp=r&sr=c&" + expiry + "&sig=x");
    }

    [Theory]
    [InlineData("sp=r&sr=c&se=2026-11-30T14:05:09Z&sig=SAMPLE+ONLY", 2026, 11, 30, 14, 5, 9)]
    [InlineData("sv=a+b&sp=r&sr=c&se=2026-11-30T14:05:09Z&sig=x", 2026, 11, 30, 14, 5, 9)]
    [InlineData("sp=r&sr=c&st=2026-10-23T00:00:00+08:00&se=2026-11-30T14:05:09Z&sig=x", 2026, 11, 30, 14, 5, 9)]
    [InlineData("skoid=a+b&skt=2026-10-23T00:00:00+08:00&ske=2026-10-30T00:00:00Z"
        + "&sp=r&sr=c&se=2026-11-30T14:05:09Z&sig=x", 2026, 10, 30, 0, 0, 0)]
    public void A_raw_plus_outside_an_expiry_is_kept(string query, int year, int month, int day, int hour, int minute,
        int second)
    {
        string location = Base + query;
        AssertRead(location, new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero), true);

        ModSecurityRuleSet set = ModSecurityRules.Build(
            new ModSecurityRuleRequest([10_000], ModSecurityAction.Deny, location, ["a.txt"], "x"));
        Assert.Contains("@ipMatchFromFile https://acct.blob.core.windows.net/feeds/a.txt?" + query + "\"",
            Assert.Single(set.Rules));
    }

    [Theory]
    [InlineData("se=2027-10-23T00:00:00Z&ske=2026-10-30T00:00:00Z", 2026, 10, 30, 0, 0, true)]
    [InlineData("ske=2026-10-30T00:00:00Z&se=2027-10-23T00:00:00Z", 2026, 10, 30, 0, 0, true)]
    [InlineData("se=2027-10-23&ske=2026-10-30T12:30:00Z", 2026, 10, 30, 12, 30, true)]
    [InlineData("se=2026-10-25&ske=2026-10-30T00:00:00Z", 2026, 10, 25, 0, 0, false)]
    [InlineData("se=2027-10-23T00:00:00Z&ske=2026-10-30", 2026, 10, 30, 0, 0, false)]
    [InlineData("se=2026-10-30T00:00:00Z&ske=2026-10-30", 2026, 10, 30, 0, 0, true)]
    [InlineData("se=2026-10-30&ske=2026-10-30T00:00:00Z", 2026, 10, 30, 0, 0, false)]
    [InlineData("se=2026-10-30T08:00:00Z&ske=2026-10-30T14:00:00%2B08:00", 2026, 10, 30, 6, 0, true)]
    [InlineData("se=2026-10-30T05:00:00Z&ske=2026-10-30T14:00:00%2B08:00", 2026, 10, 30, 5, 0, true)]
    [InlineData("se=2027-10-23T00:00:00Z&ske=2026-10-30T00%3A00%3A00Z", 2026, 10, 30, 0, 0, true)]
    public void A_delegation_sas_expires_with_its_key_when_that_comes_first(string query, int year, int month, int day,
        int hour, int minute, bool hasTime)
    {
        AssertRead(Base + "skoid=x&sktid=x&skt=2026-10-23T00:00:00Z&sks=b&skv=2025-01-05&sr=c&sp=rl&" + query
                   + "&sig=SAMPLEONLY", new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero), hasTime);
    }

    [Fact]
    public void The_earlier_of_the_two_wins_by_a_single_tick()
    {
        DateTimeOffset key = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        AssertRead(Sas("se=2026-10-30T00:00:00.0000001Z&ske=2026-10-30T00:00:00Z"), key, true);
        AssertRead(Sas("se=2026-10-30T00:00:00Z&ske=2026-10-30T00:00:00.0000001Z"), key, true);
        AssertRead(Sas("ske=2026-10-30T00:00:00.0000001Z&se=2026-10-30"), key, false);
    }

    [Theory]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z")]
    [InlineData("skoid=&se=2027-10-23T00:00:00Z")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=2026-10-30T14Z")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=soon")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=2026-10-30T00:00:00%2B24:00")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=2026-10-30T00:00:00+08:00")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=2026-10-30&ske=2026-10-30")]
    [InlineData("skoid=x&se=2027-10-23T00:00:00Z&ske=2026-10-30&ske=2026-11-30")]
    [InlineData("se=2027-10-23T00:00:00Z&ske=soon")]
    [InlineData("se=2027-10-23T00:00:00Z&ske=2026-10-30&ske=2026-10-31")]
    [InlineData("skoid=x&ske=2026-10-30T00:00:00Z")]
    public void A_delegation_sas_without_one_readable_key_expiry_is_expiry_unknown(string query)
    {
        AssertExpiryUnknown(Base + "sr=c&sp=rl&" + query + "&sig=SAMPLEONLY");
    }

    [Fact]
    public void An_ske_counts_without_skoid_and_the_other_key_names_dont()
    {
        AssertRead(Sas("se=2027-10-23&ske=2026-10-30T00:00:00Z"),
            new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero), true);
        AssertRead(Sas("se=2027-10-23&skt=2026-10-23T00:00:00Z&sktid=x"),
            new DateTimeOffset(2027, 10, 23, 0, 0, 0, TimeSpan.Zero), false);
    }

    [Theory]
    [InlineData("sp=r&sr=c&sig=x")]
    [InlineData("sp=rl&sr=d&sdd=1&sig=x")]
    [InlineData("si=policy&sp=r&sr=c&sig=x")]
    [InlineData("sv=2024-11-04&sr=c&sp=rl&st=2026-01-01&sig=AbC%2Bd%3D")]
    [InlineData("sp=r&sr=c&ses=2026-11-30&sig=x")]
    [InlineData("sp=r&sr=c&sig=x&e=2026-11-30")]
    public void A_sas_with_no_expiry_is_expiry_unknown(string query)
    {
        AssertExpiryUnknown(Base + query);
    }

    [Theory]
    [InlineData("sp=rw&sr=c&sig=x", ModSecurityRuleFailure.SasCanWrite)]
    [InlineData("sp=r&sr=b&sig=x", ModSecurityRuleFailure.SasScopeTooWide)]
    [InlineData("sp=r&ss=b&sig=x", ModSecurityRuleFailure.SasScopeTooWide)]
    [InlineData("sr=c&sig=x", ModSecurityRuleFailure.SasRightsUnknown)]
    [InlineData("SP=r&sr=c&sig=x", ModSecurityRuleFailure.SasRightsUnknown)]
    [InlineData("sp=r&sr=c&sig=x&se=2026-11-30T14Z&x1=y", ModSecurityRuleFailure.SasRightsUnknown)]
    [InlineData("sp=rw&sr=c&skoid=x&sig=x", ModSecurityRuleFailure.SasCanWrite)]
    [InlineData("sp=r&sr=b&se=2026-11-30T14:05:09+08:00&sig=x", ModSecurityRuleFailure.SasScopeTooWide)]
    [InlineData("sp=r&ss=b&skoid=x&se=2026-11-30&sig=x", ModSecurityRuleFailure.SasScopeTooWide)]
    public void The_expiry_is_checked_after_the_names_rights_and_scope(string query, ModSecurityRuleFailure expected)
    {
        Assert.Equal(expected, ModSecurityRules.CheckLocation(Base + query));
    }

    [Theory]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?se=2026-11-30")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=rw&sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=b&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&SE=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&%73e=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30&SIG=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30&sig%20=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30&sig=x&")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30T14Z&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/fe%2feds?sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds/..?sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("http://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sv=2024-11-04&se=2026-11-30&sp=rl")]
    [InlineData("https://acct.blob.core.windows.net/feeds")]
    [InlineData("/srv/lists")]
    [InlineData("/srv?se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feedssp=r&sr=c&se=2026-11-30&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30T14:05:09+08:00&sig=x")]
    [InlineData("https://acct.blob.core.windows.net/feeds?sp=r&sr=c&se=2026-11-30&skoid=x&sig=x")]
    [InlineData("")]
    [InlineData(null)]
    public void A_location_with_no_usable_sas_has_no_expiry(string? location)
    {
        Assert.Null(ModSecurityRules.SasExpiry(location!));
        Assert.Null(ModSecurityRules.SasExpiryHasTime(location!));
    }

    // Every pick of up to four pieces, so the two members can't drift apart on a query no row names
    [Fact]
    public void Has_time_is_null_exactly_when_the_expiry_is_and_an_accepted_sas_always_has_one()
    {
        (string Text, bool Sig)[] pieces =
        [
            ("sp=r", false), ("sp=rw", false), ("SP=r", false), ("sr=c", false), ("sr=b", false), ("ss=b", false),
            ("se=2026-11-30", false), ("se=2026-11-30T14:05Z", false), ("se=2026-11-30T14Z", false),
            ("se=2026-11-30T14:05:09.5%2B08:00", false), ("se=", false), ("", false), ("si=policy", false),
            ("sig=x", true), ("SIG=x", true), ("sig%20=x", true), ("xsig=x", false), ("sp=rl&sr=c", false),
            ("skoid=x", false), ("ske=2026-10-30T00:00:00Z", false), ("se=2026-11-30T14:05:09+08:00", false),
        ];

        int accepted = 0, read = 0;
        foreach ((string Text, bool Sig)[] pick in Picks(pieces, 4))
        {
            string location = Base + string.Join("&", pick.Select(piece => piece.Text));
            DateTimeOffset? expiry = ModSecurityRules.SasExpiry(location);
            bool? hasTime = ModSecurityRules.SasExpiryHasTime(location);
            ModSecurityRuleFailure failure = ModSecurityRules.CheckLocation(location);

            Assert.Equal(expiry is null, hasTime is null);
            Assert.True(expiry is null || failure == ModSecurityRuleFailure.None, location);
            if (failure == ModSecurityRuleFailure.None && pick.Any(piece => piece.Sig))
            {
                Assert.True(expiry is not null, location);
                accepted++;
            }

            read += expiry is null ? 0 : 1;
        }

        Assert.True(accepted > 0 && read == accepted);
    }

    private static IEnumerable<T[]> Picks<T>(T[] items, int most)
    {
        IEnumerable<T[]> level = [[]];
        for (int length = 1; length <= most; length++)
        {
            level = level.SelectMany(pick => items.Select(item => (T[])[.. pick, item])).ToList();
            foreach (T[] pick in level)
            {
                yield return pick;
            }
        }
    }

    [Fact]
    public void The_expiry_warning_starts_fourteen_days_ahead()
    {
        Assert.Equal(14, ModSecurityLimits.SasExpiryWarningDays);
    }
}
