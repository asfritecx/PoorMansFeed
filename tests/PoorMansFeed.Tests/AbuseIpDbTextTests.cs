using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

/// <summary>
/// The AbuseIPDB wording built in code: the score line, the daily-limit line, and the secret dialog's two sets of texts.
/// Times are shown in UTC whatever the machine's zone.
/// </summary>
public sealed class AbuseIpDbTextTests
{
    private static AbuseCheck Check(int score, int reports, int users, DateTimeOffset? last) =>
        new(score, reports, users, last, IsWhitelisted: false, IsTor: false, CountryCode: "NL");

    [Fact]
    public void AScoreWithReportsNamesTheReportsUsersAndLastDate()
    {
        var last = new DateTimeOffset(2026, 10, 18, 9, 15, 0, TimeSpan.Zero);

        Assert.Equal("AbuseIPDB score 87 of 100: 412 reports from 96 users, last on 2026-10-18.",
            AppText.AbuseScore(Check(87, 412, 96, last)));
    }

    [Fact]
    public void OneReportFromOneUserIsSingular()
    {
        var last = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("AbuseIPDB score 5 of 100: 1 report from 1 user, last on 2026-10-01.",
            AppText.AbuseScore(Check(5, 1, 1, last)));
    }

    [Fact]
    public void AScoreWithReportsAndNoDateLeavesTheDateOut()
    {
        Assert.Equal("AbuseIPDB score 40 of 100: 12 reports from 3 users.", AppText.AbuseScore(Check(40, 12, 3, null)));
    }

    [Fact]
    public void AScoreWithNoReportsSaysSoForTheCheckedDays()
    {
        Assert.Equal("AbuseIPDB score 0 of 100: no reports in the last 30 days.", AppText.AbuseScore(Check(0, 0, 0, null)));
    }

    [Fact]
    public void LargeCountsHaveAThousandsSeparator()
    {
        var last = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal("AbuseIPDB score 100 of 100: 1,234 reports from 1,001 users, last on 2026-10-18.",
            AppText.AbuseScore(Check(100, 1234, 1001, last)));
    }

    [Theory]
    [InlineData("2026-10-18T20:30:00Z")]
    [InlineData("2026-10-19T03:00:00+08:00")]
    [InlineData("2026-10-18T23:59:59Z")]
    public void TheLastReportDateIsTheUtcDate(string reportedAt)
    {
        DateTimeOffset last = DateTimeOffset.Parse(reportedAt, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("AbuseIPDB score 87 of 100: 2 reports from 2 users, last on 2026-10-18.",
            AppText.AbuseScore(Check(87, 2, 2, last)));
    }

    [Fact]
    public void TheDailyLimitGivesTheUtcTimeToTheMinute()
    {
        var retryAt = new DateTimeOffset(2026, 10, 20, 8, 5, 42, TimeSpan.FromHours(8));

        // Rounded up, so the time named is never before the limit lifts
        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again after 2026-10-20 00:06 UTC.",
            AppText.AbuseRateLimited(retryAt));
    }

    [Fact]
    public void TheDailyLimitKeepsAnExactMinute()
    {
        var retryAt = new DateTimeOffset(2026, 10, 20, 8, 5, 0, TimeSpan.FromHours(8));

        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again after 2026-10-20 00:05 UTC.",
            AppText.AbuseRateLimited(retryAt));
    }

    [Fact]
    public void TheDailyLimitRoundsUpIntoTheNextDay()
    {
        var retryAt = new DateTimeOffset(2026, 10, 20, 23, 59, 30, TimeSpan.Zero);

        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again after 2026-10-21 00:00 UTC.",
            AppText.AbuseRateLimited(retryAt));
    }

    [Fact]
    public void TheDailyLimitAtTheLastRepresentableTimeIsShownAsItIs()
    {
        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again after 9999-12-31 23:59 UTC.",
            AppText.AbuseRateLimited(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void TheDailyLimitWithoutATimeSaysLater()
    {
        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again later.", AppText.AbuseRateLimited(null));
    }

    [Fact]
    public void TheCheckTooltipNamesTheCheckedDays()
    {
        Assert.Equal("Send the address to AbuseIPDB for its score over the last 30 days", AppText.CheckAbuseTooltip);
    }

    [Theory]
    [InlineData(TokenPromptReason.FirstRun, AppText.TokenFirstTitle, AppText.TokenBody)]
    [InlineData(TokenPromptReason.Reset, AppText.TokenResetTitle, AppText.TokenResetBody)]
    public void TheIpinfoPromptIsTodaysTokenTexts(TokenPromptReason reason, string title, string body)
    {
        Assert.Equal(new SecretPrompt(title, body, "API token", "Show token", "Save token"), SecretPrompt.ForIpInfo(reason));
    }

    [Theory]
    [InlineData(AbuseKeyAsk.Missing, AppText.AbuseKeyFirstTitle, AppText.AbuseKeyBody)]
    [InlineData(AbuseKeyAsk.Rejected, AppText.AbuseKeyReplaceTitle, AppText.AbuseKeyReplaceBody)]
    [InlineData(AbuseKeyAsk.Replace, AppText.AbuseKeySetTitle, AppText.AbuseKeySetBody)]
    public void TheAbuseIpDbPromptIsTheKeyTexts(AbuseKeyAsk why, string title, string body)
    {
        Assert.Equal(new SecretPrompt(title, body, "AbuseIPDB key", "Show key", "Save key"), SecretPrompt.ForAbuseIpDb(why));
    }
}
