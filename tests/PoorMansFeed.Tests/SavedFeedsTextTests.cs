using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedsTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 41, 59, TimeSpan.Zero);
    private const string Folder = "/exports";

    internal static SavedFeedPick Pick(string? asn = null, string? googleScope = null, bool googleGlobal = false,
                                       string? azureTag = null, string? awsService = null, string? awsRegion = null) =>
        new(asn, googleScope, googleGlobal, azureTag, awsService, awsRegion, [], [], [], [], null, null, null);

    internal static SavedFeed Feed(string key, FeedKind kind, SavedFeedPick? pick = null, bool retired = false,
                                   DateTimeOffset? refreshed = null) =>
        new(key, kind, pick ?? Pick(), At, refreshed, retired);

    private static HistoryRow Row(bool added = true, FeedRemovalReason reason = FeedRemovalReason.NotTold,
                                  bool ipv6 = false, FeedType? copy = null, DateOnly? dataset = null,
                                  DateTimeOffset? list = null, FeedChangeCause cause = FeedChangeCause.PageWrite) =>
        new(At, "AS64500", FeedKind.Asn, cause, ipv6, copy, added, "203.0.113.0/24", reason, FeedFileState.Read,
            dataset, list);

    private static string File(string folder, string name) => Path.Combine(Folder, folder, name);

    // ---- names, kinds and pages ----

    [Fact]
    public void Each_kind_has_its_name_from_the_pick()
    {
        Assert.Equal("AS64500", AppText.SavedFeedName(Feed("AS64500", FeedKind.Asn, Pick(asn: "AS64500"))));
        Assert.Equal("Google Cloud us-east1", AppText.SavedFeedName(
            Feed("GoogleCloud_us-east1", FeedKind.GoogleCloud, Pick(googleScope: "us-east1"))));
        Assert.Equal("Google Cloud us-east1 + global", AppText.SavedFeedName(
            Feed("GoogleCloud_us-east1_global", FeedKind.GoogleCloud,
                 Pick(googleScope: "us-east1", googleGlobal: true))));
        Assert.Equal("Azure AzureCloud.westeurope", AppText.SavedFeedName(
            Feed("Azure_AzureCloud_westeurope", FeedKind.Azure, Pick(azureTag: "AzureCloud.westeurope"))));
        Assert.Equal("AWS EC2 eu-west-1", AppText.SavedFeedName(
            Feed("AWS_EC2_eu-west-1", FeedKind.Aws, Pick(awsService: "EC2", awsRegion: "eu-west-1"))));
        Assert.Equal("AWS EC2 all regions", AppText.SavedFeedName(
            Feed("AWS_EC2_all", FeedKind.Aws, Pick(awsService: "EC2"))));
        Assert.Equal("AbuseIPDB rolling", AppText.SavedFeedName(Feed("AbuseIPDB_rolling", FeedKind.AbuseIpDbRolling)));
    }

    [Theory]
    [InlineData(FeedKind.GoogleCloud, null)]
    [InlineData(FeedKind.GoogleCloud, "")]
    [InlineData(FeedKind.Azure, null)]
    [InlineData(FeedKind.Azure, "")]
    [InlineData(FeedKind.Aws, null)]
    [InlineData(FeedKind.Aws, "")]
    public void A_name_whose_field_is_missing_is_the_key(FeedKind kind, string? field)
    {
        SavedFeedPick pick = kind switch
        {
            FeedKind.GoogleCloud => Pick(googleScope: field, googleGlobal: true),
            FeedKind.Azure => Pick(azureTag: field),
            _ => Pick(awsService: field, awsRegion: "eu-west-1"),
        };

        Assert.Equal("the_key", AppText.SavedFeedName(Feed("the_key", kind, pick)));
    }

    [Fact]
    public void An_empty_AWS_region_is_the_key_but_a_null_one_is_every_region()
    {
        Assert.Equal("AWS_EC2_x",
                     AppText.SavedFeedName(Feed("AWS_EC2_x", FeedKind.Aws, Pick(awsService: "EC2", awsRegion: ""))));
        Assert.Equal("AWS EC2 all regions",
                     AppText.SavedFeedName(Feed("AWS_EC2_all", FeedKind.Aws, Pick(awsService: "EC2"))));
    }

    [Fact]
    public void Kinds_and_pages_are_named()
    {
        Assert.Equal(["ASN", "Google Cloud", "Azure", "AWS", "AbuseIPDB"],
                     Enum.GetValues<FeedKind>().Select(AppText.SavedFeedKind));
        Assert.Equal(["IPInfo Lookup", "Google Cloud Lookup", "Azure Lookup", "AWS Lookup",
                      "the AbuseIPDB Blacklist page"],
                     Enum.GetValues<FeedKind>().Select(AppText.SavedFeedPage));
    }

    // ---- the page's fixed lines ----

    [Fact]
    public void The_fixed_lines_are_as_worded()
    {
        Assert.Equal("Saved feeds can't be used, because the app data folder couldn't be created",
                     AppText.SavedFeedsUnavailable);
        Assert.Equal("The change history can't be used, because the app data folder couldn't be created",
                     AppText.HistoryUnavailable);
        Assert.Equal("The saved feeds couldn't be read. Please contact the Infra Team.", AppText.SavedFeedsReadFailed);
        Assert.Equal("No feed is saved yet. A feed is saved each time it is written from its page.",
                     AppText.SavedFeedsEmpty);
        Assert.Equal("1 saved feed", AppText.SavedFeedsSummary(1));
        Assert.Equal("1,200 saved feeds", AppText.SavedFeedsSummary(1200));
        Assert.Equal("Written 2026-10-30 09:41 UTC", AppText.SavedFeedLastWrite(At));
        Assert.Equal("Not refreshed yet", AppText.SavedFeedLastRefresh(null));
        Assert.Equal("Refreshed 2026-10-30 09:41 UTC", AppText.SavedFeedLastRefresh(At));
        Assert.Equal("Retired", AppText.SavedFeedRetiredState);
        Assert.Equal("Retire AS64500", AppText.RetireSavedFeedName("AS64500"));
        Assert.Equal("Remove AS64500", AppText.RemoveSavedFeedName("AS64500"));
        Assert.Equal("All feeds", AppText.HistoryAllFeeds);
        Assert.Equal("Any time", AppText.HistoryAnyTime);
        Assert.Equal("Last 24 hours", AppText.HistoryLast24Hours);
        Assert.Equal("Last 7 days", AppText.HistoryLast7Days);
        Assert.Equal("Last 30 days", AppText.HistoryLast30Days);
        Assert.Equal("Enter an address such as 203.0.113.7 or a network such as 203.0.113.0/24.",
                     AppText.HistorySearchInvalid);
        Assert.Equal("The change history couldn't be read. Please try again or contact the Infra Team.",
                     AppText.HistoryReadFailed);
    }

    [Fact]
    public void The_confirmations_and_busy_lines_are_as_worded()
    {
        Assert.Equal("Retire the AS64500 feed?", AppText.RetireSavedFeedTitle("AS64500"));
        Assert.Equal("Retire feed", AppText.RetireSavedFeedButton);
        Assert.Equal("Each AS64500 feed file in the export folder's fortigate and modsecurity folders, whatever is "
                     + "ticked, is rewritten with no addresses, so a FortiGate or ModSecurity server matches none of "
                     + "them once it reads the file again. The files stay so until the feed is written again from AWS "
                     + "Lookup. Its change history is kept.", AppText.RetireSavedFeedBody("AS64500", FeedKind.Aws));
        Assert.Equal("Retiring the AS64500 feed\u2026", AppText.RetiringSavedFeed("AS64500"));
        Assert.Equal("Remove the AS64500 feed from saved feeds?", AppText.RemoveSavedFeedTitle("AS64500"));
        Assert.Equal("Remove", AppText.RemoveSavedFeedButton);
        Assert.Equal("Its files stay as they are and its change history is kept. Writing it again from IPInfo Lookup "
                     + "saves it again.", AppText.RemoveSavedFeedBody(FeedKind.Asn));
        Assert.Equal("Clear the change history?", AppText.ClearHistoryTitle);
        Assert.Equal("Every recorded change is deleted. The saved feeds and their files are kept. "
                     + "This can't be undone.",
                     AppText.ClearHistoryBody);
        Assert.Equal("Clear history", AppText.ClearHistoryButton);
        Assert.Equal("The feed wasn't retired \u2014 press Retire feed again when the current task ends",
                     AppText.RetireSavedFeedBusy);
        Assert.Equal("The feed wasn't removed \u2014 press Remove again when the current task ends",
                     AppText.RemoveSavedFeedBusy);
        Assert.Equal("The history wasn't cleared \u2014 press Clear history again when the current task ends",
                     AppText.ClearHistoryBusy);
    }

    // ---- the result lines ----

    [Fact]
    public void Each_retire_failure_has_its_line()
    {
        string fortigate = File("fortigate", "AS64500_ipv4.txt");
        string modsecurity = File("modsecurity", "AS64500_ipv4.txt");
        var none = new RetireResult(RetireFailure.None, Folder, [fortigate, modsecurity], null, [], null);
        Assert.Equal("Feed retired: fortigate" + Path.DirectorySeparatorChar + "AS64500_ipv4.txt and modsecurity"
                     + Path.DirectorySeparatorChar + "AS64500_ipv4.txt in /exports",
                     AppText.SavedFeedRetireLine(none, "AS64500"));

        var unrecorded = none with { Record = new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Failed) };
        Assert.StartsWith("This change wasn't recorded in the history. Feed retired: ",
                          AppText.SavedFeedRetireLine(unrecorded, "AS64500"));

        Assert.Equal("Nothing was retired \u2014 there's no AS64500 feed file in the fortigate or modsecurity folder "
                     + "of /exports",
                     AppText.SavedFeedRetireLine(new(RetireFailure.NothingToRetire, Folder, [], null, [], null),
                                                 "AS64500"));
        Assert.Equal("Nothing was retired \u2014 the AS64500 feed isn't saved any more",
                     AppText.SavedFeedRetireLine(new(RetireFailure.NotSaved, "", [], null, [], null), "AS64500"));
        Assert.Equal(AppText.Generic,
                     AppText.SavedFeedRetireLine(new(RetireFailure.NotRetirable, "", [], null, [], null), "AS64500"));
        Assert.Equal(AppText.Generic,
                     AppText.SavedFeedRetireLine(new((RetireFailure)99, "", [], null, [], null), "AS64500"));

        Assert.Equal("The feed couldn't be retired. Please try again or contact the Infra Team.",
                     AppText.SavedFeedRetireLine(new(RetireFailure.WriteFailed, "", [], null, [], null), "AS64500"));
        var partway = new RetireResult(RetireFailure.WriteFailed, Folder, [fortigate], modsecurity, [modsecurity],
                                       new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Recorded));
        string sep = Path.DirectorySeparatorChar.ToString();
        Assert.Equal("modsecurity" + sep + "AS64500_ipv4.txt couldn't be emptied. fortigate" + sep
                     + "AS64500_ipv4.txt was emptied before the failure. The old modsecurity" + sep
                     + "AS64500_ipv4.txt was left as it was. The "
                     + "saved feed couldn't be updated. Please try again or contact the Infra Team.",
                     AppText.SavedFeedRetireLine(partway, "AS64500"));
    }

    [Fact]
    public void Each_remove_and_clear_outcome_has_its_line()
    {
        Assert.Equal("AS64500 removed from saved feeds",
                     AppText.SavedFeedRemoveLine(SavedFeedsFailure.None, "AS64500"));
        Assert.Equal("Nothing was removed \u2014 the AS64500 feed isn't saved any more",
                     AppText.SavedFeedRemoveLine(SavedFeedsFailure.NotFound, "AS64500"));
        Assert.Equal("The saved feeds couldn't be read, so nothing was removed. Please try again or contact the "
                     + "Infra Team.",
                     AppText.SavedFeedRemoveLine(SavedFeedsFailure.Unavailable, "AS64500"));
        SavedFeedsFailure[] others = [SavedFeedsFailure.TooMany, SavedFeedsFailure.StorageError, (SavedFeedsFailure)99];
        foreach (SavedFeedsFailure failure in others)
        {
            Assert.Equal("The AS64500 feed couldn't be removed. Please try again or contact the Infra Team.",
                         AppText.SavedFeedRemoveLine(failure, "AS64500"));
        }

        Assert.Equal("Change history cleared", AppText.HistoryClearLine(HistoryState.Missing));
        foreach (HistoryState state in Enum.GetValues<HistoryState>().Where(state => state != HistoryState.Missing))
        {
            Assert.Equal("The change history couldn't be cleared. Please try again or contact the Infra Team.",
                         AppText.HistoryClearLine(state));
        }
    }

    // ---- the history's lines ----

    [Fact]
    public void The_history_row_texts_are_as_worded()
    {
        Assert.Equal("2026-10-30 09:41 UTC", AppText.HistoryWhen(At));
        Assert.Equal("Added", AppText.HistoryAdded);
        Assert.Equal("Removed", AppText.HistoryRemoved);
        Assert.Equal(["Written from its page", "Retired", "Refresh all"],
                     Enum.GetValues<FeedChangeCause>().Select(AppText.HistoryCause));
        Assert.Equal(["", "Exception", "No longer in the source", "No longer in the list", "Left out at the write",
                      "Its family is switched off"], Enum.GetValues<FeedRemovalReason>().Select(AppText.HistoryReason));
        Assert.Equal("IPv4", AppText.HistoryFamily(false, null));
        Assert.Equal("IPv6", AppText.HistoryFamily(true, null));
        Assert.Equal("IPv4, Fortigate copy only", AppText.HistoryFamily(false, FeedType.Fortigate));
        Assert.Equal("IPv6, ModSecurity copy only", AppText.HistoryFamily(true, FeedType.ModSecurity));
        Assert.Equal("", AppText.HistoryCheckedAgainst(null, null));
        Assert.Equal("Dataset of 3 October 2026", AppText.HistoryCheckedAgainst(new DateOnly(2026, 10, 3), null));
        Assert.Equal("List downloaded 2026-10-30 09:41 UTC", AppText.HistoryCheckedAgainst(null, At));
        Assert.Equal("Dataset of 3 October 2026, List downloaded 2026-10-30 09:41 UTC",
                     AppText.HistoryCheckedAgainst(new DateOnly(2026, 10, 3), At));
    }

    [Fact]
    public void The_history_summary_names_what_is_shown()
    {
        Assert.Equal("", AppText.HistorySummary(0, 0, false));
        Assert.Equal("", AppText.HistorySummary(0, 5, false));
        Assert.Equal("1 change", AppText.HistorySummary(1, 1, false));
        Assert.Equal("12 changes", AppText.HistorySummary(12, 12, false));
        Assert.Equal("Showing 300 of 1,200 changes", AppText.HistorySummary(300, 1200, false));
        Assert.Equal("Showing 300 of more than 100,000 changes", AppText.HistorySummary(300, 100_000, true));
    }

    [Fact]
    public void Each_history_state_has_its_empty_line()
    {
        Assert.Equal("No change is recorded yet.", AppText.HistoryEmpty(HistoryState.Missing, false));
        Assert.Equal("No change is recorded yet.", AppText.HistoryEmpty(HistoryState.Usable, false));
        Assert.Equal("No recorded change matches.", AppText.HistoryEmpty(HistoryState.Missing, true));
        Assert.Equal("No recorded change matches.", AppText.HistoryEmpty(HistoryState.Usable, true));
        foreach (bool filtered in new[] { false, true })
        {
            Assert.Equal("The change history was made by a newer version of PoorMansFeed, so it isn't shown.",
                         AppText.HistoryEmpty(HistoryState.Newer, filtered));
            Assert.Equal("The change history can't be read. Clear history starts a new one.",
                         AppText.HistoryEmpty(HistoryState.Unreadable, filtered));
            Assert.Equal(AppText.HistoryReadFailed, AppText.HistoryEmpty(HistoryState.StorageError, filtered));
        }
    }

    // ---- the rows ----

    [Fact]
    public void A_saved_feed_row_carries_its_texts()
    {
        SavedFeedRow row = SavedFeedRow.From(Feed("AWS_EC2_all", FeedKind.Aws, Pick(awsService: "EC2")));

        Assert.Equal("AWS_EC2_all", row.Key);
        Assert.Equal(FeedKind.Aws, row.Kind);
        Assert.Equal("AWS EC2 all regions", row.NameText);
        Assert.Equal("AWS", row.KindText);
        Assert.Equal("Written 2026-10-30 09:41 UTC", row.LastWriteText);
        Assert.Equal("Not refreshed yet", row.LastRefreshText);
        Assert.Equal("", row.StateText);
        Assert.False(row.IsRetired);
        Assert.Equal("Retire AWS EC2 all regions", row.RetireName);
        Assert.Equal("Remove AWS EC2 all regions", row.RemoveName);
        Assert.Equal("AWS EC2 all regions, AWS, Written 2026-10-30 09:41 UTC, Not refreshed yet", row.AccessibleText);

        SavedFeedRow retired = SavedFeedRow.From(Feed("AS64500", FeedKind.Asn, retired: true, refreshed: At));
        Assert.True(retired.IsRetired);
        Assert.Equal("Retired", retired.StateText);
        Assert.Equal("AS64500, ASN, Written 2026-10-30 09:41 UTC, Refreshed 2026-10-30 09:41 UTC, Retired",
                     retired.AccessibleText);
    }

    [Fact]
    public void A_history_row_carries_its_texts()
    {
        HistoryChangeRow added =
            HistoryChangeRow.From(Row(added: true, reason: FeedRemovalReason.Exception), "Feed name");
        Assert.Equal("2026-10-30 09:41 UTC", added.WhenText);
        Assert.Equal("Feed name", added.FeedText);
        Assert.Equal("203.0.113.0/24", added.EntryText);
        Assert.Equal("Added", added.ChangeText);
        Assert.Equal("Written from its page", added.CauseText);
        Assert.Equal("", added.ReasonText);
        Assert.Equal("IPv4", added.FamilyText);
        Assert.Equal("", added.CheckedAgainstText);
        Assert.Equal("2026-10-30 09:41 UTC, Feed name, 203.0.113.0/24, Added, Written from its page, IPv4",
                     added.AccessibleText);

        HistoryChangeRow removed = HistoryChangeRow.From(
            Row(added: false, reason: FeedRemovalReason.SourceDropped, ipv6: true, copy: FeedType.ModSecurity,
                dataset: new DateOnly(2026, 10, 3), cause: FeedChangeCause.RefreshAll), "AS64500");
        Assert.Equal("Removed", removed.ChangeText);
        Assert.Equal("Refresh all", removed.CauseText);
        Assert.Equal("No longer in the source", removed.ReasonText);
        Assert.Equal("IPv6, ModSecurity copy only", removed.FamilyText);
        Assert.Equal("Dataset of 3 October 2026", removed.CheckedAgainstText);
        Assert.Equal("2026-10-30 09:41 UTC, AS64500, 203.0.113.0/24, Removed, Refresh all, No longer in the source, "
                     + "IPv6, ModSecurity copy only, Dataset of 3 October 2026", removed.AccessibleText);

        Assert.Equal("", HistoryChangeRow.From(Row(added: false, reason: FeedRemovalReason.NotTold), "x").ReasonText);
    }

    // ---- the page and the close ----

    [Fact]
    public void The_page_its_filters_and_its_columns_have_their_texts()
    {
        Assert.Equal("Saved feeds", AppText.SavedFeedsTitle);
        Assert.Equal("The feeds written from each page", AppText.SavedFeedsCaption);
        Assert.Equal("Feeds", AppText.SavedFeedsListTitle);
        Assert.Equal("Change history", AppText.HistoryTitle);
        Assert.Equal("Changes", AppText.HistoryListTitle);
        Assert.Equal("Every address or network each feed write added or removed", AppText.HistoryCaption);
        Assert.Equal("Address or network", AppText.HistorySearchWatermark);
        Assert.Equal("Search", AppText.HistorySearchButton);
        Assert.Equal("Feed shown in the history", AppText.HistoryFeedBoxName);
        Assert.Equal("Period shown in the history", AppText.HistoryPeriodBoxName);
        Assert.Equal("Time", AppText.HistoryTimeHeader);
        Assert.Equal("Feed", AppText.HistoryFeedHeader);
        Assert.Equal("Entry", AppText.HistoryEntryHeader);
        Assert.Equal("Change", AppText.HistoryChangeHeader);
        Assert.Equal("Cause", AppText.HistoryCauseHeader);
        Assert.Equal("Reason", AppText.HistoryReasonHeader);
    }

    [Fact]
    public void The_close_questions_have_their_texts()
    {
        Assert.Equal("Finish the saved feeds work?", AppText.CloseSavedFeedsWorkTitle);
        Assert.Equal("Saved feeds is still retiring or removing a feed, or Change history is still clearing the "
                     + "history. Closing " + GraphiteUi.AppInfo.Name + " waits for it to finish and then closes the application.",
                     AppText.CloseSavedFeedsWorkBody);
        Assert.Equal("Finish the feed write?", AppText.CloseFeedWriteTitle);
        Assert.Equal("A feed is still being written. Closing " + GraphiteUi.AppInfo.Name
                     + " waits for the write to finish, so no feed file is left half written, and then closes the "
                     + "application.", AppText.CloseFeedWriteBody);
        Assert.Equal("Wait and close", AppText.CloseFeedWriteButton);
    }

    [Fact]
    public void The_help_section_is_as_worded()
    {
        Assert.Equal("8. Saved feeds", AppText.HelpSavedFeedsHeading);
        Assert.Equal("Each feed you write from its page is saved: an ASN feed from IPInfo Lookup, a cloud feed from "
                     + "Google Cloud, Azure or AWS Lookup, and the AbuseIPDB feed. Saved feeds, in the list on the left, "
                     + "lists them. Retire feed rewrites a feed's files with no addresses in both folders, whatever is "
                     + "ticked, until the feed is written again from its page. Remove forgets a saved feed and leaves "
                     + "its files as they are. Change history, under it in the list, lists every address or network "
                     + "each write added or removed, what started it and why an entry was removed. Search it by address "
                     + "or network, narrow it by feed and period, or delete it with Clear history. Refresh all, on "
                     + "the Saved feeds page, brings every saved feed up to date: it updates the IPinfo dataset when a "
                     + "new one is out, checks the cloud sources and the AbuseIPDB blacklist, then writes each feed "
                     + "whose entries changed. With Refresh all while the app is open ticked in Settings, it also runs "
                     + "on its own at the interval you choose. On a Mac, the keychain can ask once for access after a "
                     + "new version; choose Always Allow, or a refresh on its own waits for your answer.",
                     AppText.HelpSavedFeedsText);
    }
    // ---- the next timed refresh ----

    private const string Dot = "\u00A0\u00B7\u00A0";
    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_due_time_today_is_named_by_its_time_after_the_feed_count()
    {
        Assert.Equal("3 saved feeds" + Dot + "next refresh 14:05 UTC",
                     AppText.SavedFeedsSummary(3, Noon, Noon.AddHours(2).AddMinutes(5), isWaiting: false));
        Assert.Equal("1 saved feed" + Dot + "next refresh 23:59 UTC",
                     AppText.SavedFeedsSummary(1, Noon, new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero), false));
    }

    [Fact]
    public void The_due_time_tomorrow_says_tomorrow_by_the_UTC_date_not_by_24_hours()
    {
        Assert.Equal("next refresh tomorrow 09:00 UTC",
                     AppText.RefreshDueClause(Noon, new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), false));
        // Under 24 hours ahead, yet on the next UTC date
        Assert.Equal("next refresh tomorrow 00:00 UTC",
                     AppText.RefreshDueClause(new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero),
                                              new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), false));
        Assert.Equal("next refresh tomorrow 23:59 UTC",
                     AppText.RefreshDueClause(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
                                              new DateTimeOffset(2026, 11, 2, 23, 59, 0, TimeSpan.Zero), false));
        // An offset of the times given doesn't move the day
        Assert.Equal("next refresh 23:30 UTC",
                     AppText.RefreshDueClause(new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.FromHours(13)),
                                              new DateTimeOffset(2026, 11, 1, 18, 30, 0, TimeSpan.FromHours(-5)), false));
    }

    [Fact]
    public void A_due_time_is_rounded_up_to_its_minute_and_an_exact_minute_stays()
    {
        Assert.Equal("next refresh 14:06 UTC", AppText.RefreshDueClause(Noon, Noon.AddHours(2).AddMinutes(5).AddSeconds(1), false));
        Assert.Equal("next refresh 14:06 UTC", AppText.RefreshDueClause(Noon, Noon.AddHours(2).AddMinutes(5).AddTicks(1), false));
        Assert.Equal("next refresh 14:05 UTC", AppText.RefreshDueClause(Noon, Noon.AddHours(2).AddMinutes(5), false));
        // Rounding up across midnight names the next day
        Assert.Equal("next refresh tomorrow 00:00 UTC",
                     AppText.RefreshDueClause(Noon, new DateTimeOffset(2026, 11, 1, 23, 59, 30, TimeSpan.Zero), false));
    }

    [Fact]
    public void A_due_time_neither_today_nor_tomorrow_has_its_full_date()
    {
        Assert.Equal("next refresh 2026-11-03 00:00 UTC",
                     AppText.RefreshDueClause(Noon, new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), false));
        Assert.Equal("next refresh 2026-12-25 08:01 UTC",
                     AppText.RefreshDueClause(Noon, new DateTimeOffset(2026, 12, 25, 8, 0, 1, TimeSpan.Zero), false));
    }

    [Fact]
    public void A_due_time_already_past_reads_as_today_and_a_waiting_run_has_its_own_clause()
    {
        Assert.Equal("next refresh 11:59 UTC", AppText.RefreshDueClause(Noon, Noon.AddSeconds(-90), false));
        Assert.Equal("next refresh 23:59 UTC",
                     AppText.RefreshDueClause(new DateTimeOffset(2026, 11, 2, 0, 0, 20, TimeSpan.Zero),
                                              new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero), false));
        Assert.Equal("refresh waiting for other work", AppText.RefreshDueClause(Noon, Noon.AddMinutes(-3), true));
        Assert.Equal("2 saved feeds" + Dot + "refresh waiting for other work",
                     AppText.SavedFeedsSummary(2, Noon, Noon.AddMinutes(-3), isWaiting: true));
    }

    [Fact]
    public void With_no_due_time_there_is_no_clause()
    {
        Assert.Null(AppText.RefreshDueClause(Noon, null, false));
        Assert.Null(AppText.RefreshDueClause(Noon, null, true));
        Assert.Equal("2 saved feeds", AppText.SavedFeedsSummary(2, Noon, null, isWaiting: false));
        Assert.Equal("2 saved feeds", AppText.SavedFeedsSummary(2, Noon, null, isWaiting: true));
    }
}
