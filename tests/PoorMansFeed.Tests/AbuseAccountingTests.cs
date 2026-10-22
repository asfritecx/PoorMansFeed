using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

public sealed class AbuseAccountingTests
{
    private static readonly DateTimeOffset First = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly October1 = new(2026, 10, 1);
    private static readonly DateOnly October2 = new(2026, 10, 2);
    private static readonly AbuseRateLimit NoRemaining = new(9, null, null);

    [Fact]
    public void Counting_starts_a_new_day_at_1_and_adds_one_on_the_same_day()
    {
        AbuseAccounting once = AbuseAccounting.Empty.After(NoRemaining, First, countDownload: true);
        AbuseAccounting twice = once.After(NoRemaining, First.AddHours(1), countDownload: true);
        AbuseAccounting nextDay = twice.After(NoRemaining, new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), countDownload: true);

        Assert.Equal(new AbuseAccounting(null, October1, 1), once);
        Assert.Equal(new AbuseAccounting(null, October1, 2), twice);
        Assert.Equal(new AbuseAccounting(null, October2, 1), nextDay);
    }

    [Fact]
    public void The_day_is_the_UTC_date_of_an_answer_given_with_an_offset()
    {
        // 07:00 at +08:00 on 2 October is 23:00 on 1 October in UTC; 20:00 at -05:00 on 1 October is 01:00 on 2 October
        AbuseAccounting east = AbuseAccounting.Empty.After(NoRemaining, new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.FromHours(8)), true);
        AbuseAccounting west = AbuseAccounting.Empty.After(NoRemaining, new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.FromHours(-5)), true);

        Assert.Equal(October1, east.DownloadsDay);
        Assert.Equal(October2, west.DownloadsDay);
    }

    [Fact]
    public void The_count_stops_at_int_MaxValue()
    {
        var nearlyFull = new AbuseAccounting(null, October1, int.MaxValue - 1);

        AbuseAccounting full = nearlyFull.After(NoRemaining, First, countDownload: true);
        AbuseAccounting stillFull = full.After(NoRemaining, First, countDownload: true);

        Assert.Equal(int.MaxValue, full.DownloadsOnDay);
        Assert.Equal(int.MaxValue, stillFull.DownloadsOnDay);
        Assert.Equal(October1, stillFull.DownloadsDay);
    }

    [Fact]
    public void Without_counting_the_day_and_its_count_stay_and_the_limit_is_still_replaced()
    {
        var before = new AbuseAccounting(new AbuseRateLimit(5, 2, null), October1, 3);

        AbuseAccounting after = before.After(new AbuseRateLimit(5, 0, First.AddHours(14)), First.AddDays(1), countDownload: false);

        Assert.Equal(new AbuseAccounting(new AbuseRateLimit(5, 0, First.AddHours(14)), October1, 3), after);
        Assert.Equal(new AbuseAccounting(null, null, 0), AbuseAccounting.Empty.After(NoRemaining, First, countDownload: false));
    }

    [Fact]
    public void The_limit_is_replaced_only_when_Remaining_is_sent()
    {
        DateTimeOffset reset = First.AddHours(14);
        var stored = new AbuseAccounting(new AbuseRateLimit(5, 4, reset), October1, 1);

        AbuseAccounting kept = stored.After(new AbuseRateLimit(9, null, First.AddHours(20)), First, countDownload: false);
        AbuseAccounting replaced = stored.After(new AbuseRateLimit(null, 3, null), First, countDownload: false);

        Assert.Equal(new AbuseRateLimit(5, 4, reset), kept.RateLimit);
        Assert.Equal(new AbuseRateLimit(null, 3, null), replaced.RateLimit);
    }

    [Fact]
    public void The_record_is_unchanged_in_place()
    {
        var before = new AbuseAccounting(new AbuseRateLimit(5, 4, null), October1, 2);
        var copy = before with { };

        AbuseAccounting after = before.After(new AbuseRateLimit(5, 3, null), First, countDownload: true);

        Assert.Equal(copy, before);
        Assert.NotSame(before, after);
        Assert.Equal(new AbuseAccounting(new AbuseRateLimit(5, 3, null), October1, 3), after);
        Assert.Equal(new AbuseAccounting(null, null, 0), AbuseAccounting.Empty);
    }

    [Fact]
    public void A_null_rate_limit_throws()
    {
        Assert.Throws<ArgumentNullException>(() => AbuseAccounting.Empty.After(null!, First, countDownload: true));
    }
}
