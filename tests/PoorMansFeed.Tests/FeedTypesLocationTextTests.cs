using System.Globalization;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The ModSecurity location's texts: each refusal's line, and the SAS expiry's wording and nearness.</summary>
public sealed class FeedTypesLocationTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly ModSecurityRuleFailure[] NoLine =
    [
        ModSecurityRuleFailure.None,
        ModSecurityRuleFailure.NoLocation,
        ModSecurityRuleFailure.NoFiles,
        ModSecurityRuleFailure.FileNameInvalid,
        ModSecurityRuleFailure.RuleIdOutOfRange,
        ModSecurityRuleFailure.RuleIdRepeated,
    ];

    private static readonly ModSecurityRuleFailure[] LocationRefusals =
    [
        ModSecurityRuleFailure.NotHttps,
        ModSecurityRuleFailure.LocationInvalid,
        ModSecurityRuleFailure.SasCanWrite,
        ModSecurityRuleFailure.SasRightsUnknown,
        ModSecurityRuleFailure.SasScopeTooWide,
        ModSecurityRuleFailure.SasExpiryUnknown,
    ];

    // The helper's fallback gives an unlisted value a line, so only these lists can make a new value fail
    [Fact]
    public void EveryFailureIsEitherALocationRefusalWithALineOrHasNone()
    {
        foreach (ModSecurityRuleFailure failure in Enum.GetValues<ModSecurityRuleFailure>())
        {
            Assert.True(NoLine.Contains(failure) != LocationRefusals.Contains(failure),
                $"{failure} must be in exactly one list: a location refusal, or a failure with no line.");
            string? line = AppText.ModSecurityLocationProblem(failure);
            if (NoLine.Contains(failure))
            {
                Assert.Null(line);
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(line), failure.ToString());
            }
        }
    }

    [Fact]
    public void EachLocationRefusalGivesItsOwnLine()
    {
        Assert.Equal("A link must start with https://, because ModSecurity v2 refuses http:// and v3 reads it as a file "
                     + "path. A folder on the server starts with /.",
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.NotHttps));
        Assert.Equal(AppText.ModSecurityLocationInvalid,
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.LocationInvalid));
        Assert.Equal("This SAS can write. Make a new read-only SAS (sp=r or rl): editing sp doesn't lower what its "
                     + "signature allows.",
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasCanWrite));
        Assert.Equal("This link has a signature (sig) whose rights can't be read from the link. Use a read-only SAS "
                     + "with its rights in the link (sp=r or rl).",
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasRightsUnknown));
        Assert.Equal("This SAS covers a whole account or a single file. Make a read-only SAS for the container or "
                     + "directory that holds the lists (sr=c or d).",
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasScopeTooWide));
        Assert.Equal("The link has no expiry the app can read. Make a read-only SAS with one expiry (se) in the link, "
                     + "and one key expiry (ske) too for a user delegation SAS. Write the + of a time offset as %2B.",
            AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasExpiryUnknown));
    }

    [Fact]
    public void TheInvalidLineNamesTheLengthLimitFromItsConstant()
    {
        string limit = ModSecurityLimits.MaxLocationLength.ToString("N0", CultureInfo.InvariantCulture);
        Assert.Equal("2,048", limit);
        Assert.Equal("This location can't be used. A folder starts with /, is at most 1,024 characters and holds only "
                     + "letters a to z in either case, digits and . _ - /, with no // or . or .. part. A link "
                     + "starts with https:// and is at most " + limit + " characters. Besides those letters and "
                     + "digits it holds only - . _ ~ / : ? & = + and commas, and any other character is written "
                     + "with % escapes, such as %24web for $web. Before the ? it has no & and no . or .. part, and "
                     + "none of %26, %3F, %2F or %5C. A SAS token pasted after the link needs a ? in front of it.",
            AppText.ModSecurityLocationInvalid);
    }

    // The line lists what a path may hold bare; ? starts the query and % an escape, so each is tested on its own
    [Fact]
    public void TheInvalidLineListsEveryCharacterAPathMayHoldBare()
    {
        const string link = "https://acct.blob.core.windows.net/feeds/a";
        for (char c = '!'; c <= '~'; c++)
        {
            if (c is '?' or '%')
            {
                continue;
            }

            bool inLink = char.IsAsciiLetterOrDigit(c) || "-._~/:=+,".Contains(c);
            Assert.True(
                (inLink ? ModSecurityRuleFailure.None : ModSecurityRuleFailure.LocationInvalid)
                == ModSecurityRules.CheckLocation(link + c + "b/lists"), $"link with {c}");
            bool inFolder = char.IsAsciiLetterOrDigit(c) || "._-/".Contains(c);
            Assert.True(
                (inFolder ? ModSecurityRuleFailure.None : ModSecurityRuleFailure.LocationInvalid)
                == ModSecurityRules.CheckLocation("/srv/a" + c + "b/lists"), $"folder with {c}");
        }

        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(link + "\u00e9/lists"));
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(link + "%C3%A9/lists"));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation("/srv/caf\u00e9"));
        foreach (string letter in new[] { "\u03b1", "\u0434" })
        {
            Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation("/srv/a" + letter));
            Assert.Equal(ModSecurityRuleFailure.LocationInvalid,
                ModSecurityRules.CheckLocation(link + letter + "/lists"));
        }

        foreach (string escape in new[] { "%26", "%3F", "%3f", "%2F", "%5C" })
        {
            Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(link + escape + "b/lists"));
        }

        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(link + "b/lists?x=1&y=2"));
    }

    // The folder's limit is private to the rules, so the line's 1,024 is tied to what they do
    [Fact]
    public void TheInvalidLineNamesTheLimitsAndCharactersTheRulesHold()
    {
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation("/" + new string('a', 1_023)));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation("/" + new string('a', 1_024)));
        const string link = "https://acct.blob.core.windows.net/";
        string longest = link + new string('a', ModSecurityLimits.MaxLocationLength - link.Length);
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(longest));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(longest + "a"));
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(link + "Az09-._~/:,+x=y/lists"));
        Assert.Equal(ModSecurityRuleFailure.None, ModSecurityRules.CheckLocation(link + "%24web/lists"));
        Assert.Equal(ModSecurityRuleFailure.LocationInvalid, ModSecurityRules.CheckLocation(link + "$web/lists"));
    }

    // ----- the note for a location as typed -----

    private static string Sas(string expiries) =>
        "https://acct.blob.core.windows.net/feeds?sv=2026-01-01&sr=c&sp=rl&" + expiries + "&sig=SAMPLEONLY%2Bnot%3Dreal";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("/etc/modsecurity/lists")]
    [InlineData("https://h.example/lists")]
    public void ALocationWithNoExpiryGivesAnEmptyNoteThatIsNotRefused(string? location)
    {
        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(location, Now);

        Assert.Equal(new ModSecurityLocationNote("", null, IsRefused: false), note);
        Assert.False(AppText.ModSecurityLocationIsRefused(location));
    }

    [Theory]
    [InlineData("http://h.example/lists", ModSecurityRuleFailure.NotHttps)]
    [InlineData("lists", ModSecurityRuleFailure.LocationInvalid)]
    [InlineData("https://acct.blob.core.windows.net/feeds?sr=c&sp=rw&se=2026-12-01&sig=x", ModSecurityRuleFailure.SasCanWrite)]
    [InlineData("https://acct.blob.core.windows.net/feeds?sr=c&se=2026-12-01&sig=x", ModSecurityRuleFailure.SasRightsUnknown)]
    [InlineData("https://acct.blob.core.windows.net/feeds?sr=b&sp=r&se=2026-12-01&sig=x", ModSecurityRuleFailure.SasScopeTooWide)]
    [InlineData("https://acct.blob.core.windows.net/feeds?sr=c&sp=r&sig=x", ModSecurityRuleFailure.SasExpiryUnknown)]
    public void ARefusedLocationGivesItsOwnLineAndNoNearness(string location, ModSecurityRuleFailure failure)
    {
        Assert.Equal(failure, ModSecurityRules.CheckLocation(location));

        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(" " + location + "\r\n", Now);

        Assert.Equal(new ModSecurityLocationNote(AppText.ModSecurityLocationProblem(failure)!, null, IsRefused: true), note);
        Assert.True(AppText.ModSecurityLocationIsRefused(location));
    }

    [Theory]
    [InlineData("se=2026-11-22T08:00:00Z", "Later", "The SAS in this link expires on 22 November 2026 at 08:00 UTC.")]
    [InlineData("se=2026-10-30", "Soon", "The SAS in this link expires at the start of 30 October 2026 (UTC), in less")]
    [InlineData("se=2026-10-23T08:00:00Z", "Passed", "The SAS in this link expired on 23 October 2026 at 08:00 UTC.")]
    public void AnExpiryGivesItsLineAndNearness(string expiries, string nearness, string opening)
    {
        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(Sas(expiries) + "\n", Now);

        Assert.StartsWith(opening, note.Text);
        Assert.Equal(Enum.Parse<SasExpiryNearness>(nearness), note.Nearness);
        Assert.False(note.IsRefused);
    }

    // A user delegation SAS fails at its key's expiry when that comes first, so the note shows the earlier
    [Fact]
    public void AUserDelegationSasShowsTheEarlierOfItsExpiryAndItsKeys()
    {
        ModSecurityLocationNote keyFirst = AppText.ModSecurityLocationNoteFor(
            Sas("skoid=o&ske=2026-10-30T12:00:00Z&se=2027-10-23T00:00:00Z"), Now);
        ModSecurityLocationNote linkFirst = AppText.ModSecurityLocationNoteFor(
            Sas("skoid=o&ske=2026-10-30&se=2026-10-25T06:30:00Z"), Now);

        Assert.StartsWith("The SAS in this link expires on 30 October 2026 at 12:00 UTC, in less", keyFirst.Text);
        Assert.Equal(SasExpiryNearness.Soon, keyFirst.Nearness);
        Assert.StartsWith("The SAS in this link expires on 25 October 2026 at 06:30 UTC,", linkFirst.Text);
    }

    [Theory]
    [InlineData("skoid=o&se=2027-01-01")]
    [InlineData("ske=2026-12-01&ske=2026-12-02&se=2027-01-01")]
    [InlineData("se=2027-01-01T00:00:00+08:00")]
    public void AnExpiryTheRulesCantReadIsRefusedWithTheExpiryLine(string expiries)
    {
        ModSecurityLocationNote note = AppText.ModSecurityLocationNoteFor(Sas(expiries), Now);

        Assert.True(note.IsRefused);
        Assert.Equal(AppText.ModSecurityLocationProblem(ModSecurityRuleFailure.SasExpiryUnknown), note.Text);
        Assert.Null(note.Nearness);
    }

    [Fact]
    public void ANullExpiryGivesNoTextAndNoNearness()
    {
        Assert.Null(AppText.ModSecuritySasExpiry(null, null, Now));
        Assert.Null(AppText.ModSecuritySasExpiryNearness(null, Now));
    }

    [Theory]
    [InlineData(0, "Passed", "The SAS in this link expired ")]
    [InlineData(-1, "Passed", "The SAS in this link expired ")]
    [InlineData(1, "Soon", "The SAS in this link expires ")]
    [InlineData(14 * 86_400 - 1, "Soon", "The SAS in this link expires ")]
    [InlineData(14 * 86_400, "Later", "The SAS in this link expires ")]
    [InlineData(30 * 86_400, "Later", "The SAS in this link expires ")]
    public void TheNearnessAndWordingFollowTheFourteenDayLine(int secondsFromNow, string nearness, string opening)
    {
        DateTimeOffset expiry = Now.AddSeconds(secondsFromNow);
        var expected = Enum.Parse<SasExpiryNearness>(nearness);

        Assert.Equal(expected, AppText.ModSecuritySasExpiryNearness(expiry, Now));
        string text = AppText.ModSecuritySasExpiry(expiry, true, Now)!;
        Assert.StartsWith(opening, text);
        Assert.Equal(expected == SasExpiryNearness.Soon, text.Contains("less than", StringComparison.Ordinal));
        Assert.Equal(expected == SasExpiryNearness.Passed, text.Contains("Make a new one:", StringComparison.Ordinal));
    }

    [Fact]
    public void EachNearnessHasItsWholeText()
    {
        var expiry = new DateTimeOffset(2026, 11, 1, 14, 30, 0, TimeSpan.Zero);
        const string When = "on 1 November 2026 at 14:30 UTC";

        Assert.Equal("The SAS in this link expires " + When + ".",
            AppText.ModSecuritySasExpiry(expiry, true, expiry.AddDays(-30)));
        Assert.Equal("The SAS in this link expires " + When + ", in less than 14 days. Make a new one before then. "
                     + "Once it expires, the server can't fetch the lists: it won't start, and a reload is refused, "
                     + "keeping the lists it had.",
            AppText.ModSecuritySasExpiry(expiry, true, expiry.AddDays(-3)));
        Assert.Equal("The SAS in this link expired " + When + ". Make a new one: the server can't fetch the lists, so "
                     + "it won't start, and a reload is refused, keeping the lists it had.",
            AppText.ModSecuritySasExpiry(expiry, true, expiry.AddDays(1)));
    }

    [Fact]
    public void TheSoonTextNamesTheWarningDaysFromTheConstant()
    {
        string text = AppText.ModSecuritySasExpiry(Now.AddDays(3), true, Now)!;
        Assert.Contains("less than " + ModSecurityLimits.SasExpiryWarningDays + " days", text);
        Assert.Contains("less than 14 days", text);
    }

    [Theory]
    [InlineData("2026-11-01T14:30:59Z", true, "on 1 November 2026 at 14:30 UTC")]
    [InlineData("2026-11-01T00:00:00Z", true, "on 1 November 2026 at 00:00 UTC")]
    [InlineData("2026-11-01T00:00:00Z", false, "at the start of 1 November 2026 (UTC)")]
    [InlineData("2026-11-01T20:00:00Z", true, "on 1 November 2026 at 20:00 UTC")]
    [InlineData("2026-11-01T22:00:00-02:00", true, "on 2 November 2026 at 00:00 UTC")]
    public void TheExpiryIsWrittenInUtcToTheMinute(string expiry, bool hasTime, string when)
    {
        DateTimeOffset at = DateTimeOffset.Parse(expiry, CultureInfo.InvariantCulture);

        Assert.Equal("The SAS in this link expires " + when + ".",
            AppText.ModSecuritySasExpiry(at, hasTime, Now.AddYears(-1)));
    }

    // A time the link gives is shown even when the caller doesn't know whether it has one
    [Fact]
    public void AnUnknownHasTimeIsWrittenWithTheTime()
    {
        var at = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal("The SAS in this link expires on 1 November 2026 at 20:00 UTC.",
            AppText.ModSecuritySasExpiry(at, null, Now.AddYears(-1)));
    }

    [Fact]
    public void TheActionAndStartFreshTextsAreSet()
    {
        Assert.Equal("Rule action", AppText.ModSecurityActionLabel);
        Assert.Equal("Log only", AppText.ModSecurityLogOnly);
        Assert.Equal("Deny with status 403", AppText.ModSecurityDeny);
        Assert.Equal("Where your server reads the lists", AppText.ModSecurityLocationLabel);
        Assert.Equal("/etc/modsecurity/lists or https://\u2026", AppText.ModSecurityLocationPlaceholder);
        Assert.Equal("After Start fresh, raise the first rule id on the Settings page above every rule id already in "
                     + "your server's configuration.", AppText.ModSecurityStartFreshRuleIds);
        Assert.StartsWith("The whole location, query included, is kept and shown as plain text,",
            AppText.ModSecurityLocationWarning);
        Assert.Contains(" A SAS signed with a user delegation key stops working when its key expires (ske), within 7 "
                        + "days, whatever expiry (se) the link gives, so the expiry shown is the earlier of the two. ",
            AppText.ModSecurityLocationWarning);
        Assert.EndsWith("editing sp, sr, ss or srt doesn't lower what its signature allows.",
            AppText.ModSecurityLocationWarning);
    }
}
