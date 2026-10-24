using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

/// <summary>
/// Every number comes from <see cref="AbuseIpDbLimits"/> or the result, and every time is the UTC minute, whatever
/// the machine's zone: a past time rounded down, a future one up.
/// </summary>
public sealed class AbuseIpDbPageTextTests
{
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 20, 8, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // A feed file is named with its type's folder, joined by this platform's separator
    private static readonly string V4 = Path.Combine("fortigate", "AbuseIPDB_rolling_ipv4.txt");
    private static readonly string V6 = Path.Combine("fortigate", "AbuseIPDB_rolling_ipv6.txt");

    private static AbuseListStatus Status(int ipv4, int ipv6, DateTimeOffset? last, AbuseRateLimit? limit, int today) =>
        new(AbuseListState.Usable, ipv4, ipv6, last, last, limit, today, RefusedUntil: null);

    private static AbuseDownloadResult Download(int listed, int skipped, int added, int lapsed, int expired) =>
        new(AbuseIpDbFailure.None, NewGeneration: true, listed, skipped, added, lapsed, expired, Downloaded, RetryAt: null);

    private static AbuseFeedWriteResult Feed(int ipv4, int ipv6, int leftOut, int removals, params string[] emptied) =>
        new(AbuseFeedWriteFailure.None, ["/e/fortigate/AbuseIPDB_rolling_ipv4.txt"], ipv4, ipv6, leftOut, removals, emptied);

    private static AbuseNetworksResult Networks(int count, int noAsn, int notInIpinfo, int leftOut, bool datasetHasIPv6,
                                                bool includesIPv6 = true)
    {
        var groups = Enumerable.Range(1, count)
            .Select(i => new AbuseNetworkGroup((uint)i, "Name", "example.com", 1, []))
            .ToList();
        var dataset = new DatasetInfo(new DateOnly(2026, 10, 19), 4_170_047, "abc", Downloaded, datasetHasIPv6);
        return new AbuseNetworksResult(AbuseNetworksFailure.None, groups, noAsn, [], notInIpinfo, 900, 100,
            includesIPv6, 30, Downloaded, dataset, leftOut);
    }

    // The whole line is the head, then the detail when there is one
    private static void AssertFactsJoin(AbuseListStatus status, int keepDays)
    {
        string head = AppText.AbuseFactsHead(status, Now);
        string detail = AppText.AbuseFactsDetail(status, keepDays, Now);
        Assert.Equal(detail.Length == 0 ? head : head + AppText.Joiner + detail, AppText.AbuseFacts(status, keepDays, Now));
    }

    [Fact]
    public void TheFactsRowNamesTheDownloadTheRemainingDownloadsAndTheCounts()
    {
        var status = Status(9_981, 19, Downloaded, new AbuseRateLimit(5, 4, null), 1);

        Assert.Equal("Downloaded 2026-10-20 08:00 UTC", AppText.AbuseFactsHead(status, Now));
        Assert.Equal("4 of 5 downloads left today" + AppText.Joiner + "9,981 IPv4 and 19 IPv6 addresses in the last 30 days",
            AppText.AbuseFactsDetail(status, 30, Now));
        Assert.Equal("Downloaded 2026-10-20 08:00 UTC" + AppText.Joiner + "4 of 5 downloads left today"
            + AppText.Joiner + "9,981 IPv4 and 19 IPv6 addresses in the last 30 days", AppText.AbuseFacts(status, 30, Now));
        AssertFactsJoin(status, 30);
    }

    [Fact]
    public void TheFactsRowIsSingularForOneAddressOneDayAndOneDownload()
    {
        var status = Status(0, 1, Downloaded, new AbuseRateLimit(1, 1, null), 0);

        Assert.Equal("1 of 1 download left today" + AppText.Joiner + "0 IPv4 and 1 IPv6 address in the last 1 day",
            AppText.AbuseFactsDetail(status, 1, Now));
        Assert.Equal("Downloaded 2026-10-20 08:00 UTC" + AppText.Joiner + "1 of 1 download left today"
            + AppText.Joiner + "0 IPv4 and 1 IPv6 address in the last 1 day", AppText.AbuseFacts(status, 1, Now));
        AssertFactsJoin(status, 1);
    }

    [Fact]
    public void CountingTheDownloadsUsedTheDetailLeadsWithThem()
    {
        var status = Status(10, 0, Downloaded, null, 2);

        Assert.Equal("Downloaded 2026-10-20 08:00 UTC", AppText.AbuseFactsHead(status, Now));
        Assert.Equal("2 of " + AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + " downloads used today" + AppText.Joiner
            + "10 IPv4 and 0 IPv6 addresses in the last 30 days", AppText.AbuseFactsDetail(status, 30, Now));
        AssertFactsJoin(status, 30);
    }

    [Fact]
    public void WithNoRemainingCountTheFactsRowCountsTheDownloadsUsedOfTheFreeLimit()
    {
        var status = Status(10, 0, Downloaded, null, 2);

        Assert.Contains(AppText.Joiner + "2 of " + AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + " downloads used today"
            + AppText.Joiner, AppText.AbuseFacts(status, 30, Now));
        Assert.StartsWith("3 of 5 downloads used today" + AppText.Joiner,
            AppText.AbuseFactsDetail(Status(10, 0, Downloaded, new AbuseRateLimit(5, null, null), 3), 30, Now));
    }

    [Fact]
    public void AMissingListSaysItWasNotDownloadedYet()
    {
        var cleared = new AbuseListStatus(AbuseListState.Missing, 0, 0, null, null, new AbuseRateLimit(5, 3, null), 2, null);

        Assert.Equal("Not downloaded yet" + AppText.Joiner + "3 of 5 downloads left today", AppText.AbuseFacts(cleared, 30, Now));
        Assert.Equal("Not downloaded yet", AppText.AbuseFactsHead(cleared, Now));
        Assert.Equal("3 of 5 downloads left today", AppText.AbuseFactsDetail(cleared, 30, Now));
        AssertFactsJoin(cleared, 30);

        // Only failed attempts since: the list file is there but has had no download
        var attempted = new AbuseListStatus(AbuseListState.Usable, 0, 0, null, null, null, 0, null);
        Assert.Equal("Not downloaded yet", AppText.AbuseFactsHead(attempted, Now));
        Assert.Equal("0 of " + AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + " downloads used today",
            AppText.AbuseFactsDetail(attempted, 30, Now));
        AssertFactsJoin(attempted, 30);
    }

    [Theory]
    [InlineData(AbuseListState.Newer)]
    [InlineData(AbuseListState.Unreadable)]
    [InlineData(AbuseListState.StorageError)]
    public void AListThatCantBeUsedShowsOnlyTheDownloads(AbuseListState state)
    {
        var status = new AbuseListStatus(state, 0, 0, null, null, new AbuseRateLimit(5, 3, null), 2, null);

        Assert.Equal("3 of 5 downloads left today", AppText.AbuseFacts(status, 30, Now));
        Assert.Equal("3 of 5 downloads left today", AppText.AbuseFactsHead(status, Now));
        Assert.Equal("", AppText.AbuseFactsDetail(status, 30, Now));
        AssertFactsJoin(status, 30);
    }

    [Fact]
    public void TheDownloadTimeIsTheUtcMinuteRoundedDown()
    {
        var offset = new DateTimeOffset(2026, 10, 20, 16, 0, 30, TimeSpan.FromHours(8));
        var exact = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

        Assert.StartsWith("Downloaded 2026-10-20 08:00 UTC" + AppText.Joiner,
            AppText.AbuseFacts(Status(1, 0, offset, null, 0), 30, Now));
        Assert.StartsWith("Downloaded 2026-10-20 08:00 UTC" + AppText.Joiner,
            AppText.AbuseFacts(Status(1, 0, exact, null, 0), 30, Now));
        Assert.Equal("The next download can be sent after 2026-10-20 08:01 UTC.", AppText.AbuseNextDownloadAfter(Downloaded));
        Assert.Equal("No downloads are left today. Nothing was sent. Please try again after 2026-10-20 08:01 UTC.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.LimitReached, Downloaded));
    }

    [Fact]
    public void AStoredLimitCountsOnlyWhileItsResetIsCurrent()
    {
        string Left(DateTimeOffset? resetAt) =>
            AppText.AbuseFactsDetail(Status(10, 0, Downloaded, new AbuseRateLimit(5, 0, resetAt), 5), 30, Now);

        Assert.StartsWith("0 of 5 downloads left today" + AppText.Joiner, Left(Now.AddHours(1)));
        Assert.StartsWith("5 of 5 downloads left today" + AppText.Joiner, Left(Now.AddHours(-1)));
        Assert.StartsWith("5 of 5 downloads left today" + AppText.Joiner, Left(Now.AddHours(30)));
        Assert.StartsWith("0 of 5 downloads left today" + AppText.Joiner, Left(null));
        Assert.StartsWith("5 of 5 downloads left today" + AppText.Joiner, Left(Now));
        Assert.StartsWith("0 of 5 downloads left today" + AppText.Joiner, Left(Now.AddHours(AbuseIpDbLimits.MaxResetAheadHours)));
    }

    [Fact]
    public void AStaleStoredLimitWithNoTotalCountsTheDownloadsUsedToday()
    {
        var status = Status(10, 0, Downloaded, new AbuseRateLimit(null, 0, Now.AddHours(-1)), 2);

        Assert.StartsWith("2 of " + AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + " downloads used today" + AppText.Joiner,
            AppText.AbuseFactsDetail(status, 30, Now));
    }

    [Fact]
    public void TheKeepDaysLabelNamesTheDaysOfListingsInTheFeed()
    {
        Assert.Equal("Days of listings in the feed", AppText.AbuseIpDbKeepDaysLabel);
    }

    [Theory]
    [InlineData("2026-10-20T08:00:30+08:00", "2026-10-20 00:01 UTC.")]
    [InlineData("2026-10-20T07:59:00+08:00", "2026-10-19 23:59 UTC.")]
    [InlineData("2026-10-19T19:30:01-05:00", "2026-10-20 00:31 UTC.")]
    public void TheNextDownloadTimeIsTheUtcMinuteRoundedUp(string at, string expectedEnd)
    {
        DateTimeOffset time = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("The next download can be sent after " + expectedEnd, AppText.AbuseNextDownloadAfter(time));
    }

    [Fact]
    public void TheStaleWarningSaysTheFeedHoldsWhatWasListedThen()
    {
        Assert.Equal("The last download is more than 30 days old, so the feed holds what was listed in the 30 days before it. "
            + "Download the blacklist to bring it up to date.", AppText.AbuseListStale(30));
    }

    [Fact]
    public void TheListStateLineCoversTheUnusableStatesOnly()
    {
        Assert.Equal("The AbuseIPDB list was made by a newer version of PoorMansFeed, so it can't be used.",
            AppText.AbuseListStateLine(AbuseListState.Newer));
        Assert.Equal("The AbuseIPDB list couldn't be read. Clear the list to start again.",
            AppText.AbuseListStateLine(AbuseListState.Unreadable));
        Assert.Equal("The AbuseIPDB list couldn't be read now. Please try again or contact the Infra Team.",
            AppText.AbuseListStateLine(AbuseListState.StorageError));
        Assert.Equal("", AppText.AbuseListStateLine(AbuseListState.Usable));
        Assert.Equal("", AppText.AbuseListStateLine(AbuseListState.Missing));
    }

    [Fact]
    public void ADownloadNamesWhatWasListedAddedAndDroppedOut()
    {
        Assert.Equal("Blacklist downloaded: 10,000 addresses, 120 new, 80 dropped out of the last 30 days",
            AppText.AbuseDownloaded(Download(10_000, 0, 120, 80, 0), 30));
        Assert.Equal("Blacklist downloaded: 1 address, 1 new, 0 dropped out of the last 1 day",
            AppText.AbuseDownloaded(Download(1, 0, 1, 0, 0), 1));
    }

    [Fact]
    public void ADownloadNamesTheExpiredAndTheSkippedOnlyWhenThereAreAny()
    {
        Assert.Equal("Blacklist downloaded: 10,000 addresses, 120 new, 80 dropped out of the last 30 days, "
            + "7 removed after 365 days unlisted" + AppText.Dash + "3 entries were left out",
            AppText.AbuseDownloaded(Download(10_000, 3, 120, 80, 7), 30));
        Assert.Equal("Blacklist downloaded: 5 addresses, 0 new, 0 dropped out of the last 30 days"
            + AppText.Dash + "1 entry was left out", AppText.AbuseDownloaded(Download(5, 1, 0, 0, 0), 30));
    }

    [Fact]
    public void AClockBehindTheLastDownloadShowsOnlyTheLastDownload()
    {
        AbuseListStatus status = Status(10, 2, Downloaded, new AbuseRateLimit(5, 3, null), 2) with
        {
            IPv4Count = 0, IPv6Count = 0, ClockBehind = true,
        };

        Assert.Equal("Downloaded 2026-10-20 08:00 UTC", AppText.AbuseFactsHead(status, Now));
        Assert.Equal("", AppText.AbuseFactsDetail(status, 30, Now));
        Assert.Equal("Downloaded 2026-10-20 08:00 UTC", AppText.AbuseFacts(status, 30, Now));
        AssertFactsJoin(status, 30);
    }

    [Fact]
    public void TheClockBehindLinesSayWhatToDo()
    {
        Assert.Equal("This computer's clock is more than a day behind the last download, so the addresses aren't counted. "
            + "Correct the clock.", AppText.AbuseClockBehind);
        Assert.Equal("Nothing was sent, because this computer's clock is more than a day behind the last download. "
            + "Correct the clock, then try again.", AppText.AbuseDownloadFailure(AbuseIpDbFailure.ClockBehind, null));
        Assert.Equal("The networks weren't grouped, because this computer's clock is more than a day behind the last "
            + "download. Correct the clock.", AppText.AbuseNetworksFailureLine(AbuseNetworksFailure.ClockWrong));
    }

    [Fact]
    public void EveryDownloadFailureHasItsOwnLine()
    {
        AbuseIpDbFailure[] failures = Enum.GetValues<AbuseIpDbFailure>()
            .Where(f => f is not (AbuseIpDbFailure.None or AbuseIpDbFailure.NotPublic)).ToArray();

        var lines = failures.Select(f => AppText.AbuseDownloadFailure(f, null)).ToList();

        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.DoesNotContain(AppText.Generic, lines);
        Assert.Equal(AppText.Generic, AppText.AbuseDownloadFailure(AbuseIpDbFailure.NotPublic, null));
    }

    [Fact]
    public void TheDownloadFailuresReuseTheCheckLinesWhereTheyMeanTheSame()
    {
        Assert.Equal(AppText.AbuseKeyRejected, AppText.AbuseDownloadFailure(AbuseIpDbFailure.KeyRejected, null));
        Assert.Equal(AppText.AbuseKeyUnavailable, AppText.AbuseDownloadFailure(AbuseIpDbFailure.KeyUnavailable, null));
        Assert.Equal(AppText.AbuseOffline, AppText.AbuseDownloadFailure(AbuseIpDbFailure.Offline, null));
        Assert.Equal(AppText.AbuseKeyUnreadable, AppText.AbuseDownloadFailure(AbuseIpDbFailure.NoKey, null));
        Assert.Equal("Nothing was downloaded" + AppText.Dash + "AbuseIPDB needs an API key", AppText.AbuseDownloadKeyCancelled);
        Assert.Equal("Nothing was downloaded" + AppText.Dash + "AbuseIPDB didn't accept the saved key",
            AppText.AbuseDownloadKeyReplaceCancelled);

        var at = new DateTimeOffset(2026, 10, 21, 0, 0, 10, TimeSpan.Zero);
        Assert.Equal(AppText.AbuseRateLimited(at), AppText.AbuseDownloadFailure(AbuseIpDbFailure.RateLimited, at));
    }

    [Fact]
    public void TheLimitLineNamesTheUtcMinuteOrSaysLater()
    {
        var at = new DateTimeOffset(2026, 10, 21, 8, 0, 10, TimeSpan.FromHours(8));

        Assert.Equal("No downloads are left today. Nothing was sent. Please try again after 2026-10-21 00:01 UTC.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.LimitReached, at));
        Assert.Equal("No downloads are left today. Nothing was sent. Please try again later.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.LimitReached, null));
    }

    [Fact]
    public void TheDownloadsThatStillCountSaySo()
    {
        Assert.Equal("AbuseIPDB's answer couldn't be read, so nothing was added. It still counts as one of today's downloads.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.BadResponse, null));
        Assert.Equal("The blacklist wasn't added, because this computer's clock is more than a day from AbuseIPDB's. "
            + "Correct the clock, then try again. The download still counts.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.ClockWrong, null));
    }

    [Fact]
    public void AListThatFailsAfterACountedDownloadSaysItCounts()
    {
        const string counts = " The blacklist was downloaded, so it still counts as one of today's downloads.";
        foreach (AbuseIpDbFailure failure in new[] { AbuseIpDbFailure.ListNewer, AbuseIpDbFailure.ListUnreadable,
                     AbuseIpDbFailure.StorageError })
        {
            Assert.Equal(AppText.AbuseDownloadFailure(failure, null) + counts,
                AppText.AbuseDownloadFailure(failure, null, counted: true));
            Assert.DoesNotContain("still counts", AppText.AbuseDownloadFailure(failure, null));
        }

        // These lines already say so, once
        foreach (AbuseIpDbFailure failure in new[] { AbuseIpDbFailure.BadResponse, AbuseIpDbFailure.ClockWrong })
            Assert.Equal(AppText.AbuseDownloadFailure(failure, null), AppText.AbuseDownloadFailure(failure, null, counted: true));
    }

    [Fact]
    public void OnlyTheRefusalsBeforeTheSendSayNothingWasSent()
    {
        foreach (AbuseIpDbFailure failure in new[] { AbuseIpDbFailure.DiskNearlyFull, AbuseIpDbFailure.LimitReached,
                     AbuseIpDbFailure.ClockBehind })
        {
            Assert.Contains("Nothing was sent", AppText.AbuseDownloadFailure(failure, null));
        }

        // A list that is newer, unreadable or can't be saved can also come back after the download was sent and counted
        foreach (AbuseIpDbFailure failure in new[] { AbuseIpDbFailure.ListNewer, AbuseIpDbFailure.ListUnreadable,
                     AbuseIpDbFailure.StorageError })
        {
            Assert.DoesNotContain("Nothing was sent", AppText.AbuseDownloadFailure(failure, null));
        }

        Assert.Equal("There isn't enough free space for the blacklist. Free some space, then try again. Nothing was sent.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.DiskNearlyFull, null));
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Newer),
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.ListNewer, null));
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable),
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.ListUnreadable, null));
        Assert.Equal("The AbuseIPDB list couldn't be read or saved. Please try again or contact the Infra Team.",
            AppText.AbuseDownloadFailure(AbuseIpDbFailure.StorageError, null));
    }

    [Fact]
    public void AWrittenFeedNamesTheCountsAndTheFolder()
    {
        Assert.Equal("Feed written: 9,981 IPv4 and 19 IPv6 addresses to the fortigate folder of /e",
            AppText.AbuseFeedWritten(Feed(9_981, 19, 0, 0), "/e"));
        Assert.Equal("Feed written: 1 IPv4 and 1 IPv6 address to the fortigate folder of /e",
            AppText.AbuseFeedWritten(Feed(1, 1, 0, 0), "/e"));
    }

    [Fact]
    public void AWrittenFeedNamesTheExceptionsTheLeftOutAndTheEmptiedFilesOnlyWhenThereAreAny()
    {
        string line = AppText.AbuseFeedWritten(Feed(131_072, 0, 2_000, 1, "/e/fortigate/AbuseIPDB_rolling_ipv6.txt"), "/e");

        Assert.Equal("Feed written: 131,072 IPv4 and 0 IPv6 addresses to the fortigate folder of /e"
            + AppText.Dash + "1 address removed by exceptions"
            + AppText.Dash + "2,000 older addresses left out over the limit of 131,072 a file"
            + AppText.Dash + $"{V6} was emptied", line);
        Assert.Equal("Feed written: 5 IPv4 and 0 IPv6 addresses to the fortigate folder of /e" + AppText.Dash + "1 older address left out over the limit of 131,072 a file",
            AppText.AbuseFeedWritten(Feed(5, 0, 1, 0), "/e"));
    }

    [Fact]
    public void EveryFeedWriteFailureHasItsOwnLine()
    {
        var lines = Enum.GetValues<AbuseFeedWriteFailure>()
            .Where(f => f is not AbuseFeedWriteFailure.None)
            .Select(f => AppText.AbuseFeedWriteFailureLine(Failed(f), 30, ipv6Enabled: true)).ToList();

        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.DoesNotContain(AppText.Generic, lines);
        Assert.Equal("Nothing was written" + AppText.Dash + "download the blacklist first",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NoDownload), 30, ipv6Enabled: true));
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable),
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.ListUnreadable), 30, ipv6Enabled: true));
    }

    private static AbuseFeedWriteResult Failed(AbuseFeedWriteFailure failure, int removals = 0, params string[] files) =>
        new(failure, files, 0, 0, 0, removals, []);

    [Fact]
    public void NothingToWriteSaysWhetherExceptionsCoveredTheAddressesAndWhichFamily()
    {
        Assert.Equal("Nothing was written" + AppText.Dash + "exceptions cover every address listed in the last 30 days",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NothingToWrite, removals: 3), 30, ipv6Enabled: true));
        Assert.Equal("Nothing was written" + AppText.Dash + "exceptions cover every IPv4 address listed in the last 1 day",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NothingToWrite, removals: 1), 1, ipv6Enabled: false));
        Assert.Equal("Nothing was written" + AppText.Dash + "no address was listed in the last 30 days",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NothingToWrite), 30, ipv6Enabled: true));
        Assert.Equal("Nothing was written" + AppText.Dash + "no IPv4 address was listed in the last 30 days",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NothingToWrite), 30, ipv6Enabled: false));
    }

    [Fact]
    public void TheFeedWriteReusesTheExceptionsAndExportFolderLines()
    {
        Assert.Equal(AppText.ExceptionsUnreadable,
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.ExceptionsUnreadable), 30, ipv6Enabled: true));
        Assert.Equal(AppText.ExportFolderUnavailable,
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.FolderUnavailable), 30, ipv6Enabled: true));
    }

    [Fact]
    public void AFailedWriteNamesTheFilesWrittenBeforeIt()
    {
        Assert.Equal("The feed couldn't be written. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed), 30, ipv6Enabled: true));
        Assert.Equal($"The feed couldn't be written. {V4} was written before the failure. "
            + "Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed, 0,
                "/e/fortigate/AbuseIPDB_rolling_ipv4.txt"), 30, ipv6Enabled: true));
        Assert.Equal($"The feed couldn't be written. {V4} and {V6} were written "
            + "before the failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed, 0,
                "/e/fortigate/AbuseIPDB_rolling_ipv4.txt", "/e/fortigate/AbuseIPDB_rolling_ipv6.txt"), 30, ipv6Enabled: true));
    }

    [Fact]
    public void AFailedWriteNamesTheFilesEmptiedBeforeItAsEmptied()
    {
        const string ipv4 = "/e/fortigate/AbuseIPDB_rolling_ipv4.txt";
        const string ipv6 = "/e/fortigate/AbuseIPDB_rolling_ipv6.txt";

        Assert.Equal($"The feed couldn't be written. {V4} was emptied before the failure. "
            + "Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4], [ipv4]), 30, ipv6Enabled: true));
        Assert.Equal($"The feed couldn't be written. {V4} was written before the failure. "
            + $"{V6} was emptied before the failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4, ipv6], [ipv6]), 30, ipv6Enabled: true));
        Assert.Equal($"The feed couldn't be written. {V4} and {V6} were written "
            + "before the failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4, ipv6], []), 30, ipv6Enabled: true));
    }

    private static AbuseFeedWriteResult FailedWrite(string[] files, string[] emptied) =>
        new(AbuseFeedWriteFailure.WriteFailed, files, 0, 0, 0, 0, emptied);

    // A write that failed on one file names it first; the unsaved note follows only files done before the failure
    [Fact]
    public void AFailedWriteNamesTheFailedFileFirst()
    {
        string folder = Path.Combine(Path.GetTempPath(), "e", "fortigate");
        string ipv4 = Path.Combine(folder, "AbuseIPDB_rolling_ipv4.txt");
        string ipv6 = Path.Combine(folder, "AbuseIPDB_rolling_ipv6.txt");

        Assert.Equal($"{V6} couldn't be written. {V4} was written before the "
            + "failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4], []) with { FailedFile = ipv6 }, 30, ipv6Enabled: true));
        Assert.Equal($"{V6} couldn't be written. {V4} was emptied before the "
            + "failure. Unsaved changes on the Settings page weren't used. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4], [ipv4]) with { FailedFile = ipv6 }, 30, ipv6Enabled: true,
                unsaved: true));
        Assert.Equal($"{V4} couldn't be written. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([], []) with { FailedFile = ipv4 }, 30, ipv6Enabled: true,
                unsaved: true));
        Assert.Equal($"The feed couldn't be written. {V4} was written before the failure. "
            + "Unsaved changes on the Settings page weren't used. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4], []), 30, ipv6Enabled: true, unsaved: true));
        Assert.Equal("Nothing was written" + AppText.Dash + "download the blacklist first",
            AppText.AbuseFeedWriteFailureLine(Failed(AbuseFeedWriteFailure.NoDownload), 30, ipv6Enabled: true, unsaved: true));
    }

    [Fact]
    public void AFailedEmptyNamesTheFailedFileFirst()
    {
        string folder = Path.Combine(Path.GetTempPath(), "e", "fortigate");
        string ipv4 = Path.Combine(folder, "AbuseIPDB_rolling_ipv4.txt");
        string ipv6 = Path.Combine(folder, "AbuseIPDB_rolling_ipv6.txt");
        AbuseFeedWriteResult failed = Failed(AbuseFeedWriteFailure.WriteFailed) with { FailedFile = ipv4 };

        Assert.Equal($"{V4} couldn't be emptied. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(failed, folder));
        Assert.Equal($"{V6} couldn't be emptied. {V4} was emptied before the "
            + "failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(FailedWrite([ipv4], [ipv4]) with { FailedFile = ipv6 }, folder));
    }

    [Fact]
    public void AFailedWriteOrEmptyNamesTheOldFilesItLeft()
    {
        string folder = Path.Combine(Path.GetTempPath(), "e", "fortigate");
        string ipv4 = Path.Combine(folder, "AbuseIPDB_rolling_ipv4.txt");
        string ipv6 = Path.Combine(folder, "AbuseIPDB_rolling_ipv6.txt");

        Assert.Equal($"{V4} couldn't be written. The old {V4} and "
            + $"{V6} were left as they were. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([], []) with { FailedFile = ipv4, OldFilesLeft = [ipv4, ipv6] }, 30,
                ipv6Enabled: true, unsaved: true));
        // After the files done before it, and before the unsaved note
        Assert.Equal($"{V6} couldn't be written. {V4} was written before the "
            + $"failure. The old {V6} was left as it was. Unsaved changes on the Settings page weren't "
            + "used. Please try again or contact the Infra Team.",
            AppText.AbuseFeedWriteFailureLine(FailedWrite([ipv4], []) with { FailedFile = ipv6, OldFilesLeft = [ipv6] }, 30,
                ipv6Enabled: true, unsaved: true));
        Assert.Equal($"{V4} couldn't be emptied. The old {V6} was left as it "
            + "was. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed) with { FailedFile = ipv4, OldFilesLeft = [ipv6] },
                folder));
        Assert.Equal($"{V6} couldn't be emptied. {V4} was emptied before the "
            + $"failure. The old {V6} was left as it was. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(FailedWrite([ipv4], [ipv4]) with { FailedFile = ipv6, OldFilesLeft = [ipv6] }, folder));
    }

    [Fact]
    public void AFailedEmptyNamesTheFilesEmptiedBeforeIt()
    {
        Assert.Equal("The feed couldn't be emptied. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed), "/e"));
        Assert.Equal($"The feed couldn't be emptied. {V4} was emptied before the failure. "
            + "Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed, 0,
                "/e/fortigate/AbuseIPDB_rolling_ipv4.txt"), "/e"));
        Assert.Equal($"The feed couldn't be emptied. {V4} and {V6} were emptied "
            + "before the failure. Please try again or contact the Infra Team.",
            AppText.AbuseFeedEmptyFailureLine(Failed(AbuseFeedWriteFailure.WriteFailed, 0,
                "/e/fortigate/AbuseIPDB_rolling_ipv4.txt", "/e/fortigate/AbuseIPDB_rolling_ipv6.txt"), "/e"));
    }

    [Fact]
    public void EmptyFeedNamesTheFilesOrTheFolderThatHadNone()
    {
        var emptied = new AbuseFeedWriteResult(AbuseFeedWriteFailure.None,
            ["/e/fortigate/AbuseIPDB_rolling_ipv4.txt", "/e/fortigate/AbuseIPDB_rolling_ipv6.txt"], 0, 0, 0, 0,
            ["/e/fortigate/AbuseIPDB_rolling_ipv4.txt", "/e/fortigate/AbuseIPDB_rolling_ipv6.txt"]);

        Assert.Equal($"Feed emptied: {V4} and {V6} in /e",
            AppText.AbuseFeedEmptied(emptied, "/e"));
        Assert.Equal("Nothing was emptied" + AppText.Dash
            + "there's no AbuseIPDB feed file in the fortigate or modsecurity folder of /e",
            AppText.AbuseFeedEmptyFailureLine(Failed(AbuseFeedWriteFailure.NothingToWrite), "/e"));
    }

    [Fact]
    public void TheWriteFeedTooltipNamesTheIPv6EmptyingOnlyWhenIPv6IsOff()
    {
        string on = AppText.WriteAbuseFeedTooltip(ipv6Enabled: true);
        string off = AppText.WriteAbuseFeedTooltip(ipv6Enabled: false);

        Assert.DoesNotContain("IPv6", on);
        Assert.Contains("With IPv6 off, an existing IPv6 feed file is emptied.", off);
        Assert.Contains("FortiOS 7.4.4", on);
        Assert.Contains("262,144", off);
        const string start = "Write a threat feed of the addresses listed in the days before the last download, "
            + "as many days as Settings sets.";
        Assert.StartsWith(start + " From FortiOS 7.4.4", on);
        Assert.StartsWith(start + " With IPv6 off, an existing IPv6 feed file is emptied. From FortiOS 7.4.4", off);
    }

    // The summary leads with the report note, so the note shows whenever the line trims
    private const string Note = "A report, so exceptions aren't applied: ";

    [Fact]
    public void TheNetworksSummaryLeadsWithTheNoteInBothForms()
    {
        string capped = AppText.AbuseNetworksSummary(Networks(400, 2, 100, 5, datasetHasIPv6: false), 300, ipv6Enabled: true);
        string whole = AppText.AbuseNetworksSummary(Networks(12, 2, 100, 5, datasetHasIPv6: false), 12, ipv6Enabled: true);

        Assert.Equal(Note + "showing 300 of 400 networks for 1,000 addresses" + AppText.Joiner
            + "2 addresses with no ASN in IPinfo" + AppText.Joiner
            + "100 addresses not in IPinfo (the dataset holds no IPv6 networks)" + AppText.Joiner
            + "5 older addresses not looked up", capped);
        Assert.Equal(Note + "12 networks for 1,000 addresses" + AppText.Joiner
            + "2 addresses with no ASN in IPinfo" + AppText.Joiner
            + "100 addresses not in IPinfo (the dataset holds no IPv6 networks)" + AppText.Joiner
            + "5 older addresses not looked up", whole);
    }

    [Fact]
    public void TheNetworksSummaryCountsTheNetworksAndBuckets()
    {
        Assert.Equal(Note + "12 networks for 1,000 addresses" + AppText.Joiner + "40 addresses with no ASN in IPinfo"
            + AppText.Joiner + "1 address not in IPinfo",
            AppText.AbuseNetworksSummary(Networks(12, 40, 1, 0, datasetHasIPv6: true), 12, ipv6Enabled: true));
        Assert.StartsWith(Note + "1 network for 1,000 addresses" + AppText.Joiner + "1 address with no ASN in IPinfo",
            AppText.AbuseNetworksSummary(Networks(1, 1, 0, 0, datasetHasIPv6: true), 1, ipv6Enabled: true));
    }

    [Fact]
    public void TheNetworksSummaryNamesTheCapAndTheLeftOutOnlyWhenThereAreAny()
    {
        string line = AppText.AbuseNetworksSummary(Networks(400, 0, 0, 5, datasetHasIPv6: true), 300, ipv6Enabled: true);

        Assert.StartsWith(Note + "showing 300 of 400 networks for 1,000 addresses", line);
        Assert.Contains(AppText.Joiner + "5 older addresses not looked up", line);
        Assert.DoesNotContain("not looked up",
            AppText.AbuseNetworksSummary(Networks(400, 0, 0, 0, datasetHasIPv6: true), 300, ipv6Enabled: true));
    }

    [Fact]
    public void TheNotInIpinfoBucketSaysWhenTheDatasetHoldsNoIPv6()
    {
        string ipv4Only = AppText.AbuseNetworksSummary(Networks(2, 0, 100, 0, datasetHasIPv6: false), 2, ipv6Enabled: true);
        string withIPv6 = AppText.AbuseNetworksSummary(Networks(2, 0, 100, 0, datasetHasIPv6: true), 2, ipv6Enabled: true);
        // Each condition alone: the grouping didn't look IPv6 up, or IPv6 is off now
        string notLookedUp = AppText.AbuseNetworksSummary(
            Networks(2, 0, 100, 0, datasetHasIPv6: false, includesIPv6: false), 2, ipv6Enabled: true);
        string ipv6Off = AppText.AbuseNetworksSummary(
            Networks(2, 0, 100, 0, datasetHasIPv6: false, includesIPv6: true), 2, ipv6Enabled: false);

        Assert.Contains("100 addresses not in IPinfo (the dataset holds no IPv6 networks)", ipv4Only);
        Assert.EndsWith("100 addresses not in IPinfo", withIPv6);
        Assert.DoesNotContain("holds no IPv6", notLookedUp);
        Assert.DoesNotContain("holds no IPv6", ipv6Off);
    }

    [Fact]
    public void EveryNetworksFailureHasItsOwnLine()
    {
        var lines = Enum.GetValues<AbuseNetworksFailure>()
            .Where(f => f != AbuseNetworksFailure.None)
            .Select(AppText.AbuseNetworksFailureLine).ToList();

        Assert.Equal(lines.Count, lines.Distinct().Count());
        Assert.DoesNotContain(AppText.Generic, lines);
        Assert.Equal("Update the IPinfo dataset to group the addresses by network.",
            AppText.AbuseNetworksFailureLine(AbuseNetworksFailure.NoDataset));
    }

    [Fact]
    public void TheCountriesCellShowsTheTopThreeAndHowManyMore()
    {
        AbuseCountryCount[] countries = [new("NL", 3), new("SG", 120), new("US", 40), new("DE", 3), new("FR", 1), new("JP", 1)];

        Assert.Equal("SG 120, US 40, DE 3, +3", AppText.AbuseNetworkCountries(countries));
        Assert.Equal("SG 1,200", AppText.AbuseNetworkCountries([new("SG", 1_200)]));
        Assert.Equal(AppText.Missing, AppText.AbuseNetworkCountries([]));
    }

    [Fact]
    public void GroupingShowsItsProgressWhenThereIsAny()
    {
        Assert.Equal("Grouping the addresses by network\u2026", AppText.GroupingNetworks(null));
        Assert.Equal("Grouping the addresses by network\u2026", AppText.GroupingNetworks(new GroupingProgress(0, 0)));
        Assert.Equal("Grouping 1,000 of 4,000 addresses by network\u2026", AppText.GroupingNetworks(new GroupingProgress(1_000, 4_000)));
        Assert.Equal("Grouping 0 of 1 address by network\u2026", AppText.GroupingNetworks(new GroupingProgress(0, 1)));
    }

    [Fact]
    public void TheRowButtonNamesTheAsnAndTheLookupPage()
    {
        Assert.Equal("Look up AS15169 on IPInfo Lookup", AppText.LookUpNetworkName(15169));
        Assert.Equal("Look up AS4294967295 on IPInfo Lookup", AppText.LookUpNetworkName(uint.MaxValue));
    }

    [Fact]
    public void TheConfirmationsFollowTheVerbObjectRule()
    {
        Assert.Equal("Clear the AbuseIPDB list?", AppText.ClearAbuseListTitle);
        Assert.Equal("Clear list", AppText.ClearAbuseListButton);
        Assert.Equal("Every downloaded address is removed from this computer. The download count, the AbuseIPDB key and the "
            + "feed files are kept; choose Empty feed to empty the files. This cannot be undone.", AppText.ClearAbuseListBody);
        Assert.Equal("Empty the AbuseIPDB feed?", AppText.EmptyAbuseFeedTitle);
        Assert.Equal("Empty feed", AppText.EmptyAbuseFeedButton);
        Assert.Equal("Each AbuseIPDB feed file in the export folder's fortigate and modsecurity folders, whatever is ticked, "
            + "is rewritten with no addresses, so a FortiGate or ModSecurity server matches none of them once it reads the "
            + "file again. The AbuseIPDB list is kept, and the next Write feed fills the files again.",
            AppText.EmptyAbuseFeedBody);
    }

    [Fact]
    public void TheKeepDaysCaptionNamesTheHorizonFromTheConstant()
    {
        Assert.Equal(365, AbuseIpDbLimits.MaxKeepDays);
        Assert.Equal("An address stays in the AbuseIPDB feed while it was listed within this many days of the last download. "
            + "Each address is kept in the list for 365 days after it was last listed, so raising the number later brings "
            + "back addresses it had left out.", AppText.AbuseIpDbKeepDaysCaption);
    }

    [Fact]
    public void TheHelpSectionAndTheCaptionNameTheCutoffAndThePool()
    {
        Assert.Equal("5. AbuseIPDB", AppText.HelpAbuseHeading);
        string help = AppText.HelpAbuseText();
        Assert.Contains("FortiOS 7.4.4", help);
        Assert.Contains("Empty feed", help);
        Assert.Contains(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + " downloads a day", help);
        Assert.Contains(" downloads a day. New addresses lists the addresses the last download added. "
            + "Right-click an offending network to open its ASN on ipinfo.io, or a new address to open it on ipinfo.io "
            + "or AbuseIPDB's website. "
            + "Write feed writes a threat feed of the addresses listed in the days "
            + "before the last download, as many days as Settings sets, leaving out the networks under Exceptions. "
            + "With IPv6 off, the download asks for IPv4 addresses only, and an existing IPv6 feed file is emptied.", help);
        Assert.Contains("Empty feed rewrites the feed files with no addresses, so a FortiGate or ModSecurity server that "
            + "reads them blocks none.", help);
        Assert.Contains("of the last download", AppText.AbuseIpDbKeepDaysCaption);
    }

    [Fact]
    public void TheNewAddressesTextsNameTheReportTheDownloadAndTheCounts()
    {
        Assert.Equal("New addresses", AppText.NewAddresses);
        Assert.Equal("Show the addresses the last download added", AppText.NewAddressesTooltip);
        Assert.Equal("New addresses", AppText.NewAddressesTitle);

        // A past time, so cut to its minute, in UTC whatever the zone it came in
        var asOf = new DateTimeOffset(2026, 10, 20, 16, 0, 59, TimeSpan.FromHours(8));
        Assert.Equal("A report, so exceptions aren't applied. Added to the list by the download of 2026-10-20 08:00 UTC."
            + " Each line gives the address, its AbuseIPDB score out of 100 and the day of its last report.",
            AppText.NewAddressesMessage(asOf));

        Assert.Equal("1 new address", AppText.NewAddressesCount(1, 1));
        Assert.Equal("300 new addresses", AppText.NewAddressesCount(300, 300));
        Assert.Equal("Showing 300 of 1,234 new addresses", AppText.NewAddressesCount(300, 1_234));

        Assert.Equal("0", AppText.NewAddressScore(0));
        Assert.Equal("100", AppText.NewAddressScore(100));
        Assert.Equal("2026-10-19", AppText.NewAddressLastReported(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("Unknown", AppText.NewAddressLastReported(null));

        Assert.Equal("Copied 1 address", AppText.NewAddressesCopied(1));
        Assert.Equal("Copied 1,234 addresses", AppText.NewAddressesCopied(1_234));
    }

    [Fact]
    public void TheNewAddressesFailureLinesReuseTheListLinesAndNameAnUnknownSet()
    {
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Newer),
            AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.ListNewer));
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable),
            AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.ListUnreadable));
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.StorageError),
            AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.StorageError));
        Assert.Equal("The new addresses aren't known for the last download.",
            AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.NoDownload));
        Assert.Equal("The new addresses aren't known for the last download.",
            AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.NotKnown));
        Assert.Equal(AppText.Generic, AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.None));
        Assert.Equal("The new addresses couldn't be shown. Please try again or contact the Infra Team.",
            AppText.AbuseNewAddressesNotShown);
        Assert.Equal("The new addresses weren't shown \u2014 press New addresses again when the current task ends",
            AppText.AbuseNewAddressesBusy);
        Assert.Equal("The list wasn't cleared \u2014 press Clear list again when the current task ends",
            AppText.AbuseClearListBusy);
        Assert.Equal("The feed wasn't emptied \u2014 press Empty feed again when the current task ends",
            AppText.AbuseEmptyFeedBusy);
    }

    [Fact]
    public void TheInProgressLinesEndInAnEllipsis()
    {
        foreach (string line in new[] { AppText.ReadingAbuseList, AppText.DownloadingBlacklist, AppText.ClearingAbuseList,
                     AppText.WritingAbuseFeed, AppText.EmptyingAbuseFeed })
        {
            Assert.EndsWith("\u2026", line);
        }
    }
}
