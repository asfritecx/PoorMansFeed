using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class IpInfoDownloadsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static IpInfoDownloads With(params DateTimeOffset[] requests) => IpInfoDownloads.FromStored(requests, null);

    private static IpInfoDownloads Counted(int count) =>
        With([.. Enumerable.Range(0, count).Select(i => Now - TimeSpan.FromMinutes(10 * (i + 1)))]);

    // ----- The window -----

    [Fact]
    public void A_request_counts_after_24_hours_less_a_second_and_not_at_24_hours()
    {
        Assert.Equal(1, With(Now - Day + Second).CountedAt(Now));
        Assert.Equal(0, With(Now - Day).CountedAt(Now));
    }

    [Fact]
    public void A_request_ahead_of_now_counts_up_to_24_hours_ahead_and_not_past_it()
    {
        Assert.Equal(1, With(Now + Day).CountedAt(Now));
        Assert.Equal(0, With(Now + Day + Second).CountedAt(Now));
    }

    [Fact]
    public void A_clock_set_back_still_counts_the_requests_it_put_ahead_of_now()
    {
        IpInfoDownloads downloads = Counted(0).AfterRequest(Now).AfterRequest(Now + TimeSpan.FromMinutes(1));
        DateTimeOffset setBack = Now - TimeSpan.FromHours(5);

        Assert.Equal(2, downloads.CountedAt(setBack));
        Assert.Equal(3, downloads.AfterRequest(setBack).CountedAt(setBack));
    }

    [Fact]
    public void A_request_drops_the_requests_no_longer_counted_at_either_end()
    {
        IpInfoDownloads downloads = With(Now - Day, Now - Day + Second, Now + Day, Now + Day + Second).AfterRequest(Now);

        Assert.Equal([Now - Day + Second, Now, Now + Day], downloads.Requests);
    }

    [Fact]
    public void At_most_64_requests_are_kept_the_newest()
    {
        IpInfoDownloads downloads = With([.. Enumerable.Range(0, 70).Select(i => Now - TimeSpan.FromMinutes(70 - i))]);
        Assert.Equal(IpInfoDownloads.MaxKept, downloads.Requests.Count);
        Assert.Equal(Now - TimeSpan.FromMinutes(64), downloads.Requests[0]);

        IpInfoDownloads after = downloads.AfterRequest(Now);
        Assert.Equal(64, after.Requests.Count);
        Assert.Equal(Now - TimeSpan.FromMinutes(63), after.Requests[0]);
        Assert.Equal(Now, after.Requests[^1]);
    }

    [Fact]
    public void Stored_requests_are_read_oldest_first()
    {
        Assert.Equal([Now - Second, Now], With(Now, Now - Second).Requests);
    }

    // ----- The policy -----

    [Theory]
    [InlineData(0, UpdateFailure.None)]
    [InlineData(6, UpdateFailure.None)]
    [InlineData(7, UpdateFailure.KeptForUser)]
    [InlineData(10, UpdateFailure.KeptForUser)]
    public void Refresh_all_keeps_three_downloads_for_the_user(int counted, UpdateFailure expected)
    {
        Assert.Equal(expected, Counted(counted).Judge(Now, IpInfoUpdatePolicy.RefreshAll));
    }

    [Fact]
    public void A_policy_that_keeps_none_allows_the_tenth_request_and_not_the_eleventh()
    {
        var keepsNone = new IpInfoUpdatePolicy(0);
        Assert.Equal(UpdateFailure.None, Counted(9).Judge(Now, keepsNone));
        Assert.Equal(UpdateFailure.KeptForUser, Counted(10).Judge(Now, keepsNone));
    }

    [Fact]
    public void A_stored_refusal_refuses_under_a_policy_until_it_passes()
    {
        IpInfoDownloads refused = IpInfoDownloads.FromStored([], Now + TimeSpan.FromHours(2));

        Assert.Equal(UpdateFailure.RateLimited, refused.Judge(Now, IpInfoUpdatePolicy.RefreshAll));
        Assert.Equal(UpdateFailure.None, refused.Judge(Now + TimeSpan.FromHours(2), IpInfoUpdatePolicy.RefreshAll));
    }

    // ----- The refusal -----

    [Fact]
    public void A_refusal_reads_until_it_passes_and_not_when_more_than_24_hours_ahead()
    {
        Assert.Equal(Now + Second, IpInfoDownloads.FromStored([], Now + Second).RefusedAt(Now));
        Assert.Null(IpInfoDownloads.FromStored([], Now).RefusedAt(Now));
        Assert.Equal(Now + Day, IpInfoDownloads.FromStored([], Now + Day).RefusedAt(Now));
        Assert.Null(IpInfoDownloads.FromStored([], Now + Day + Second).RefusedAt(Now));
        Assert.Equal(UpdateFailure.None,
                     IpInfoDownloads.FromStored([], Now + Day + Second).Judge(Now, IpInfoUpdatePolicy.RefreshAll));
    }

    [Fact]
    public void A_refusal_with_retry_after_holds_that_long()
    {
        Assert.Equal(Now + TimeSpan.FromHours(3), Counted(2).AfterRefusal(Now, TimeSpan.FromHours(3)).RefusedUntil);
    }

    [Fact]
    public void A_refusal_with_retry_after_is_never_shorter_than_an_hour_nor_longer_than_24_hours()
    {
        Assert.Equal(Now + TimeSpan.FromHours(1), Counted(2).AfterRefusal(Now, TimeSpan.FromMinutes(5)).RefusedUntil);
        Assert.Equal(Now + TimeSpan.FromHours(1), Counted(2).AfterRefusal(Now, TimeSpan.FromMinutes(-5)).RefusedUntil);
        Assert.Equal(Now + Day, Counted(2).AfterRefusal(Now, TimeSpan.FromDays(3)).RefusedUntil);
    }

    [Fact]
    public void A_refusal_without_retry_after_holds_until_the_oldest_counted_request_is_24_hours_old()
    {
        IpInfoDownloads downloads = With(Now - Day, Now - TimeSpan.FromHours(20), Now - TimeSpan.FromHours(2), Now);

        Assert.Equal(Now + TimeSpan.FromHours(4), downloads.AfterRefusal(Now, null).RefusedUntil);
    }

    [Fact]
    public void A_refusal_without_retry_after_holds_at_least_an_hour()
    {
        IpInfoDownloads downloads = With(Now - Day + TimeSpan.FromMinutes(10), Now - TimeSpan.FromHours(1));

        Assert.Equal(Now + TimeSpan.FromHours(1), downloads.AfterRefusal(Now, null).RefusedUntil);
    }

    [Fact]
    public void A_refusal_with_nothing_counted_holds_24_hours()
    {
        Assert.Equal(Now + Day, With(Now - Day).AfterRefusal(Now, null).RefusedUntil);
    }

    [Fact]
    public void A_refusal_after_the_clock_was_set_back_holds_no_more_than_24_hours()
    {
        IpInfoDownloads downloads = With(Now + TimeSpan.FromHours(10));

        Assert.Equal(Now + Day, downloads.AfterRefusal(Now, null).RefusedUntil);
    }

    [Fact]
    public void A_refusal_keeps_the_counted_requests_and_a_request_keeps_a_refusal_that_holds()
    {
        IpInfoDownloads refused = With(Now - Day, Now - Second).AfterRefusal(Now, TimeSpan.FromHours(2));
        Assert.Equal([Now - Second], refused.Requests);

        Assert.Equal(Now + TimeSpan.FromHours(2), refused.AfterRequest(Now).RefusedUntil);
        Assert.Null(refused.AfterRequest(Now + TimeSpan.FromHours(2)).RefusedUntil);
    }

    [Fact]
    public void Two_records_with_the_same_times_are_equal()
    {
        Assert.Equal(IpInfoDownloads.Empty, IpInfoDownloads.FromStored([], null));
        Assert.Equal(With(Now, Now - Second), With(Now - Second, Now));
        Assert.NotEqual(With(Now), With(Now - Second));
    }
}
