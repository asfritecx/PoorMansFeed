using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class UserSettingsTests
{
    private const string SasLocation =
        "https://acct.blob.core.windows.net/lists?sp=rl&sr=c&se=2027-01-01&sv=2024-11-04&sig=SECRETSIG";

    [Fact]
    public void ToString_hides_a_location_and_prints_the_other_settings()
    {
        string text = new UserSettings { ModSecurityLocation = SasLocation, AbuseIpDbKeepDays = 45 }.ToString();

        Assert.DoesNotContain("SECRETSIG", text);
        Assert.DoesNotContain("sp=rl", text);
        Assert.DoesNotContain("acct.blob", text);
        Assert.StartsWith("UserSettings { ", text);
        Assert.EndsWith(" }", text);
        Assert.Contains("AbuseIpDbKeepDays = 45", text);
        Assert.Contains("ModSecurityLocation = (hidden)", text);
    }

    [Fact]
    public void ToString_prints_an_empty_location_as_empty()
    {
        string text = new UserSettings().ToString();

        Assert.Matches(@"ModSecurityLocation = (,| \})", text);
        Assert.DoesNotContain("(hidden)", text);
    }
}
