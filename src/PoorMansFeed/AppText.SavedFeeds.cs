using System.Globalization;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;
using GraphiteUi;

namespace PoorMansFeed;

public static partial class AppText
{
    public const string SavedFeedsTitle = "Saved feeds";
    public const string SavedFeedsCaption = "The feeds written from each page";
    public const string SavedFeedsListTitle = "Feeds";
    public const string HistoryTitle = "Change history";
    public const string HistoryListTitle = "Changes";
    public const string HistoryCaption = "Every address or network each feed write added or removed";
    public const string HistorySearchWatermark = "Address or network";
    public const string HistorySearchButton = "Search";
    public const string HistoryFeedBoxName = "Feed shown in the history";
    public const string HistoryPeriodBoxName = "Period shown in the history";
    public const string HistoryTimeHeader = "Time";
    public const string HistoryFeedHeader = "Feed";
    public const string HistoryEntryHeader = "Entry";
    public const string HistoryChangeHeader = "Change";
    public const string HistoryCauseHeader = "Cause";
    public const string HistoryReasonHeader = "Reason";

    // The close
    public const string CloseSavedFeedsWorkTitle = "Finish the saved feeds work?";

    public const string CloseSavedFeedsWorkBody =
        "Saved feeds is still retiring or removing a feed, or Change history is still clearing the history. Closing "
        + AppInfo.Name + " waits for it to finish and then closes the application.";

    public const string CloseFeedWriteTitle = "Finish the feed write?";

    public const string CloseFeedWriteBody =
        "A feed is still being written. Closing " + AppInfo.Name + " waits for the write to finish, so no feed file is "
        + "left half written, and then closes the application.";

    public const string CloseFeedWriteButton = "Wait and close";

    public const string CloseRefreshTitle = "Stop the refresh?";

    public const string CloseRefreshBody =
        "Refresh all is still running. Closing " + AppInfo.Name + " stops it: a dataset update or a download under way "
        + "is cancelled, no feed file is left half written and the feeds already refreshed are saved. Then the "
        + "application closes.";

    public const string CloseRefreshButton = "Stop and close";

    public const string SavedFeedsUnavailable =
        "Saved feeds can't be used, because the app data folder couldn't be created";

    public const string HistoryUnavailable =
        "The change history can't be used, because the app data folder couldn't be created";

    // Read once at launch, so showing the page again gives the same answer for the session
    public const string SavedFeedsReadFailed = "The saved feeds couldn't be read. Please contact the Infra Team.";

    public const string SavedFeedsEmpty =
        "No feed is saved yet. A feed is saved each time it is written from its page.";

    public static string SavedFeedsSummary(int count) => Count(count, "saved feed", "saved feeds");

    /// <summary>The feed count, then when the timer runs Refresh all next, if it is on.</summary>
    public static string SavedFeedsSummary(int count, DateTimeOffset now, DateTimeOffset? nextDueUtc, bool isWaiting) =>
        RefreshDueClause(now, nextDueUtc, isWaiting) is { } due ? SavedFeedsSummary(count) + Joiner + due : SavedFeedsSummary(count);

    /// <summary>Null with no due time to name; rounded up to its minute, so never before the run can start.</summary>
    public static string? RefreshDueClause(DateTimeOffset now, DateTimeOffset? nextDueUtc, bool isWaiting)
    {
        if (nextDueUtc is not { } due)
            return null;
        if (isWaiting)
            return "refresh waiting for other work";

        // A due time already past is waiting for the tick that starts the run; the day is the UTC date's
        DateTime named = UpToMinute(due.UtcDateTime);
        DateTime today = now.UtcDateTime.Date;
        string time = named.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
        if (due <= now || named.Date == today)
            return "next refresh " + time;
        if (named.Date == today.AddDays(1))
            return "next refresh tomorrow " + time;
        return "next refresh " + named.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>The pick's words, as the Azure key is lossy; the key when a field the name needs is missing.</summary>
    public static string SavedFeedName(SavedFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        SavedFeedPick pick = feed.Pick;
        string? name = feed.Kind switch
        {
            FeedKind.GoogleCloud when Given(pick.GoogleScope) =>
                "Google Cloud " + pick.GoogleScope + (pick.GoogleAddsGlobal ? " + global" : ""),
            FeedKind.Azure when Given(pick.AzureTag) => "Azure " + pick.AzureTag,
            FeedKind.Aws when Given(pick.AwsService) && pick.AwsRegion is not "" =>
                "AWS " + pick.AwsService + " " + (pick.AwsRegion ?? "all regions"),
            FeedKind.AbuseIpDbRolling => "AbuseIPDB rolling",
            _ => null,
        };
        return name ?? feed.Key;

        static bool Given(string? value) => !string.IsNullOrEmpty(value);
    }

    /// <summary>The dataset's AS name as it is, on one line: a control or line separator character reads as a space.</summary>
    public static string SavedFeedAsName(string asName)
    {
        ArgumentNullException.ThrowIfNull(asName);
        var line = new System.Text.StringBuilder(asName.Length);
        foreach (char c in asName)
            line.Append(char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        return line.ToString().Trim();
    }

    public static string SavedFeedKind(FeedKind kind) => kind switch
    {
        FeedKind.Asn => "ASN",
        FeedKind.GoogleCloud => "Google Cloud",
        FeedKind.Azure => "Azure",
        FeedKind.Aws => "AWS",
        FeedKind.AbuseIpDbRolling => "AbuseIPDB",
        _ => "",
    };

    public static string SavedFeedPage(FeedKind kind) => kind switch
    {
        FeedKind.Asn => "IPInfo Lookup",
        FeedKind.GoogleCloud => "Google Cloud Lookup",
        FeedKind.Azure => "Azure Lookup",
        FeedKind.Aws => "AWS Lookup",
        FeedKind.AbuseIpDbRolling => "the AbuseIPDB Blacklist page",
        _ => "",
    };

    public static string SavedFeedLastWrite(DateTimeOffset at) => "Written " + UtcMinuteDown(at);

    public static string SavedFeedLastRefresh(DateTimeOffset? at) =>
        at is { } refreshed ? "Refreshed " + UtcMinuteDown(refreshed) : "Not refreshed yet";

    public static string SavedFeedRefreshedAt(DateTimeOffset at) => "Last refreshed " + UtcMinuteDown(at);

    public const string SavedFeedRetiredState = "Retired";

    public static string RetireSavedFeedName(string name) => "Retire " + name;

    public static string RemoveSavedFeedName(string name) => "Remove " + name;

    // Retire
    public static string RetireSavedFeedTitle(string name) => "Retire the " + name + " feed?";

    public const string RetireSavedFeedButton = "Retire feed";

    public static string RetireSavedFeedBody(string name, FeedKind kind) =>
        "Each " + name + " feed file in the export folder's fortigate and modsecurity folders, whatever is ticked, is "
        + "rewritten with no addresses, so a FortiGate or ModSecurity server matches none of them once it reads the "
        + "file again. The files stay so until the feed is written again from " + SavedFeedPage(kind)
        + ". Its change history is kept.";

    public static string RetiringSavedFeed(string name) => "Retiring the " + name + " feed" + Ellipsis;

    /// <summary>Only None is not a warning.</summary>
    public static string SavedFeedRetireLine(RetireResult result, string name)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Failure switch
        {
            RetireFailure.None =>
                FeedRecordLead(result.Record) + "Feed retired: " + FileNames(result.Files) + " in " + result.Folder,
            RetireFailure.NothingToRetire => "Nothing was retired" + Dash + "there's no " + name
                + " feed file in the fortigate or modsecurity folder of " + result.Folder,
            RetireFailure.NotSaved => "Nothing was retired" + Dash + "the " + name + " feed isn't saved any more",
            RetireFailure.WriteFailed =>
                (result.FailedFile is { } failed ? FailedFileLead(failed, "emptied") : "The feed couldn't be retired.")
                + DoneBeforeFailure(result.Files, "emptied") + OldFilesLeftSentence(result.OldFilesLeft)
                + FeedRecordSentence(result.Record) + TryAgain,
            _ => Generic,
        };
    }

    // Remove
    public static string RemoveSavedFeedTitle(string name) => "Remove the " + name + " feed from saved feeds?";

    public const string RemoveSavedFeedButton = "Remove";

    public static string RemoveSavedFeedBody(FeedKind kind) =>
        "Its files stay as they are and its change history is kept. Writing it again from " + SavedFeedPage(kind)
        + " saves it again.";

    /// <summary>Only None is not a warning.</summary>
    public static string SavedFeedRemoveLine(SavedFeedsFailure failure, string name) => failure switch
    {
        SavedFeedsFailure.None => name + " removed from saved feeds",
        SavedFeedsFailure.NotFound => "Nothing was removed" + Dash + "the " + name + " feed isn't saved any more",
        SavedFeedsFailure.Unavailable => "The saved feeds couldn't be read, so nothing was removed." + TryAgain,
        _ => "The " + name + " feed couldn't be removed." + TryAgain,
    };

    // Clear history
    public const string ClearHistoryTitle = "Clear the change history?";

    public const string ClearHistoryBody =
        "Every recorded change is deleted. The saved feeds and their files are kept. This can't be undone.";

    public const string ClearHistoryButton = "Clear history";

    /// <summary>Missing, the one success, is what a clear that leaves no database gives.</summary>
    public static string HistoryClearLine(HistoryState state) =>
        state == HistoryState.Missing ? "Change history cleared" : "The change history couldn't be cleared." + TryAgain;

    public const string RetireSavedFeedBusy =
        "The feed wasn't retired" + Dash + "press Retire feed again when the current task ends";

    public const string RemoveSavedFeedBusy =
        "The feed wasn't removed" + Dash + "press Remove again when the current task ends";

    public const string ClearHistoryBusy =
        "The history wasn't cleared" + Dash + "press Clear history again when the current task ends";

    // The history's filters
    public const string HistoryAllFeeds = "All feeds";
    public const string HistoryAnyTime = "Any time";
    public const string HistoryLast24Hours = "Last 24 hours";
    public const string HistoryLast7Days = "Last 7 days";
    public const string HistoryLast30Days = "Last 30 days";

    public const string HistorySearchInvalid =
        "Enter an address such as 203.0.113.7 or a network such as 203.0.113.0/24.";

    // The history's rows
    public static string HistoryWhen(DateTimeOffset at) => UtcMinuteDown(at);

    public const string HistoryAdded = "Added";
    public const string HistoryRemoved = "Removed";

    public static string HistoryCause(FeedChangeCause cause) => cause switch
    {
        FeedChangeCause.PageWrite => "Written from its page",
        FeedChangeCause.Retire => "Retired",
        FeedChangeCause.RefreshAll => "Refresh all",
        _ => "",
    };

    public static string HistoryReason(FeedRemovalReason reason) => reason switch
    {
        FeedRemovalReason.Exception => "Exception",
        FeedRemovalReason.SourceDropped => "No longer in the source",
        FeedRemovalReason.LeftTheList => "No longer in the list",
        FeedRemovalReason.LeftOut => "Left out at the write",
        FeedRemovalReason.FamilyOff => "Its family is switched off",
        _ => "",
    };

    public static string HistoryFamily(bool isIPv6, FeedType? copy) =>
        (isIPv6 ? "IPv6" : "IPv4") + (copy is { } type ? ", " + FeedTypeName(type) + " copy only" : "");

    // The names the feed type labels and the rules dialog already use
    private static string FeedTypeName(FeedType type) => type switch
    {
        FeedType.Fortigate => "Fortigate",
        FeedType.ModSecurity => "ModSecurity",
        _ => "",
    };

    public static string HistoryCheckedAgainst(DateOnly? datasetDate, DateTimeOffset? listDownloadedUtc)
    {
        var parts = new List<string>(2);
        if (datasetDate is { } date)
            parts.Add("Dataset of " + date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
        if (listDownloadedUtc is { } downloaded)
            parts.Add("List downloaded " + UtcMinuteDown(downloaded));
        return string.Join(", ", parts);
    }

    /// <summary>Empty with no rows, as the empty line shows; a size-cap trim is named after the count.</summary>
    public static string HistorySummary(int shown, int total, bool capped, DateTimeOffset? capTrimmedBeforeUtc = null)
    {
        if (shown == 0)
            return "";
        string count = capped ? "Showing " + Number(shown) + " of more than " + Number(total) + " changes"
            : shown < total ? "Showing " + Number(shown) + " of " + Number(total) + " changes"
            : Count(total, "change", "changes");
        return capTrimmedBeforeUtc is DateTimeOffset trimmed
            ? count + Dash + "changes up to " + trimmed.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
              + " were trimmed to keep the history under " + Number(HistoryLimits.CapMegabytes) + " MB"
            : count;
    }

    /// <summary>Shown only with no rows; filtered is true when the feed, the period or the match is set.</summary>
    public static string HistoryEmpty(HistoryState state, bool filtered) => state switch
    {
        HistoryState.Missing or HistoryState.Usable =>
            filtered ? "No recorded change matches." : "No change is recorded yet.",
        HistoryState.Newer => "The change history was made by a newer version of " + GraphiteUi.AppInfo.Name + ", so it isn't shown.",
        HistoryState.Unreadable => "The change history can't be read. Clear history starts a new one.",
        _ => HistoryReadFailed,
    };

    public const string HistoryReadFailed =
        "The change history couldn't be read. Please try again or contact the Infra Team.";

    // Refresh all
    public const string RefreshAllButton = "Refresh all";

    public const string RefreshAllTip =
        "Update the dataset and the cloud and AbuseIPDB sources, then write every saved feed again";

    public const string StopRefreshButton = "Stop refresh";
    public const string StopRefreshTip = "Stop Refresh all and keep the feeds it has already refreshed";

    public const string RefreshAllBusy =
        "Refresh all didn't start" + Dash + "press Refresh all again when the current task ends";

    /// <summary>The line while a run is out; a cancel asked for wins over the latest report.</summary>
    public static string RefreshAllProgressLine(bool cancelRequested, RefreshProgress? progress)
    {
        if (cancelRequested)
            return "Stopping the refresh" + Ellipsis;
        return progress is not { } report ? "Starting Refresh all" + Ellipsis : report.Step switch
        {
            RefreshStep.Dataset => "Updating the IPinfo dataset" + Ellipsis,
            RefreshStep.Sources => "Checking the cloud sources" + Ellipsis,
            RefreshStep.AbuseIpDb => "Checking the AbuseIPDB blacklist" + Ellipsis,
            RefreshStep.Feeds => "Refreshing saved feeds: " + Number(report.Done) + " of " + Number(report.Total) + Ellipsis,
            RefreshStep.Saving => "Saving the refreshed feeds" + Ellipsis,
            _ => "Starting Refresh all" + Ellipsis,
        };
    }

    public static string RefreshAllSummary(RefreshRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.SavedFeedsAvailable)
            return "The saved feeds couldn't be read, so nothing was refreshed.";
        if (run.Feeds.Count == 0 && !run.Cancelled)
            return "There are no saved feeds to refresh.";

        string counts = string.Join(", ", new[]
            {
                (Count(run, FeedRefreshOutcome.Changed), "changed"),
                (Count(run, FeedRefreshOutcome.Unchanged), "unchanged"),
                (Count(run, FeedRefreshOutcome.Failed), "failed"),
                (Count(run, FeedRefreshOutcome.Skipped), "skipped"),
            }
            .Where(part => part.Item1 > 0)
            .Select(part => Number(part.Item1) + " " + part.Item2));
        string lead = run.Cancelled
            ? "Refresh all stopped" + (counts.Length > 0 ? Dash + counts : "")
            : "Refreshed " + SavedFeedsSummary(run.Feeds.Count) + ": " + counts;
        return RefreshAllProblem(run) is { } problem ? lead + ". " + problem : lead;

        static int Count(RefreshRunResult run, FeedRefreshOutcome outcome) =>
            run.Feeds.Count(feed => feed.Outcome == outcome);
    }

    /// <summary>The summary of a run the timer started.</summary>
    public static string RefreshAllOnSchedule(string summary) => "On schedule: " + summary;

    /// <summary>A failed feed, a problem sentence or saved feeds that couldn't be read.</summary>
    public static bool RefreshAllSummaryIsWarning(RefreshRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!run.SavedFeedsAvailable)
            return true;
        if (run.Feeds.Count == 0 && !run.Cancelled)
            return false;
        return run.Feeds.Any(feed => feed.Outcome == FeedRefreshOutcome.Failed) || RefreshAllProblem(run) is not null;
    }

    // At most one, the first that applies. A step's clause needs a feed it served that was written, stopped run or not;
    // a blacklist kept back for the user's own downloads is no problem
    private static string? RefreshAllProblem(RefreshRunResult run)
    {
        if (run.SaveFailed)
            return "The saved feeds couldn't be saved, so the next refresh writes these feeds again.";
        int notRecorded = run.Feeds.Count(feed => HistoryFailed(feed.Record));
        if (notRecorded > 0)
            return Count(notRecorded, "feed's changes weren't", "feeds' changes weren't") + " recorded in the history.";
        // The cloud checks judge ranges against the dataset too
        bool datasetFeedsWritten = WroteKind(run, FeedKind.Asn) || WroteKind(run, FeedKind.GoogleCloud)
                                   || WroteKind(run, FeedKind.Azure) || WroteKind(run, FeedKind.Aws);
        if (run.Dataset is { Failure: UpdateFailure.KeptForUser } && datasetFeedsWritten)
            return "The dataset wasn't downloaded, to keep the last IPinfo downloads of the day for Update dataset, so the "
                   + "feeds used the one already there.";
        if (run.Dataset is { Failure: not UpdateFailure.None } && datasetFeedsWritten)
            return "The dataset couldn't be updated, so the feeds used the one already there.";
        if (run.Sources.FirstOrDefault(source => source.State == SourceRefreshState.Saved && WroteKind(run, source.Provider))
            is { } saved)
            return SavedFeedKind(saved.Provider) + " couldn't be checked, so its saved ranges were used.";
        if (run.AbuseIpDb is { Failure: not (AbuseIpDbFailure.None or AbuseIpDbFailure.KeptForUser) }
            && WroteKind(run, FeedKind.AbuseIpDbRolling))
            return "The AbuseIPDB blacklist couldn't be downloaded, so the rolling feed used the list already there.";
        return null;

        static bool WroteKind(RefreshRunResult run, FeedKind kind) => run.Feeds.Any(feed =>
            feed.Kind == kind && feed.Outcome is FeedRefreshOutcome.Changed or FeedRefreshOutcome.Unchanged);
    }

    /// <summary>A feed's cell after a run: the outcome, then how many new ranges it left out unanswered.</summary>
    public static string SavedFeedRefreshCell(FeedRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string outcome = result.Outcome switch
        {
            FeedRefreshOutcome.Changed => "Changed +" + Number(result.Added) + " \u2212" + Number(result.Removed),
            FeedRefreshOutcome.Unchanged => "Unchanged",
            FeedRefreshOutcome.Skipped => "Skipped",
            _ => "Couldn't be refreshed",
        };
        int leftOut = result.NewUnconfirmed + result.NewLarge;
        return leftOut > 0 ? outcome + Joiner + Number(leftOut) + " new left out" : outcome;
    }

    /// <summary>The cell, why it was skipped or failed and when, then the sentences for what its write left.</summary>
    public static string SavedFeedRefreshTip(FeedRefreshResult result, DateTimeOffset? lastRefreshUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        string tip = SavedFeedRefreshCell(result);
        if (result.Outcome is FeedRefreshOutcome.Skipped or FeedRefreshOutcome.Failed)
            tip += ": " + RefreshReason(result);
        if (result.Outcome == FeedRefreshOutcome.Failed && result.Added + result.Removed > 0)
            tip += Joiner + "+" + Number(result.Added) + " \u2212" + Number(result.Removed) + " written first";
        if (lastRefreshUtc is { } at)
            tip += Joiner + SavedFeedRefreshedAt(at);
        string left = FeedRecordSentence(result.Record) + OldFilesLeftSentence(result.OldFilesLeft);
        return left.Length > 0 ? tip + "." + left : tip;
    }

    /// <summary>A row's caution: a failure, new ranges left out, or a record or old files the write left.</summary>
    public static bool SavedFeedRefreshIsWarning(FeedRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome == FeedRefreshOutcome.Failed || result.NewUnconfirmed + result.NewLarge > 0
            || SaveFailed(result.Record) || HistoryFailed(result.Record) || result.OldFilesLeft.Count > 0;
    }

    private static string RefreshReason(FeedRefreshResult result) => result.Reason switch
    {
        FeedRefreshReason.Retired => "it's retired",
        FeedRefreshReason.Removed => "it was removed",
        FeedRefreshReason.Superseded => "it was written again meanwhile",
        FeedRefreshReason.Cancelled => "the refresh stopped first",
        FeedRefreshReason.FeedsOff => "its feeds are switched off",
        FeedRefreshReason.SaveRefused => "its last save was refused, so the older pick wasn't written",
        FeedRefreshReason.NotSaved => result.Written
            ? "its files were written, but the saved feed couldn't be updated"
            : "its result couldn't be saved",
        FeedRefreshReason.SavedFeedsUnavailable => "the saved feeds couldn't be read",
        FeedRefreshReason.NoDataset => "there's no IPinfo dataset",
        FeedRefreshReason.SourceGone => "its AS isn't in the dataset any more",
        FeedRefreshReason.PickGone => "its pick isn't in the source any more",
        FeedRefreshReason.SourceUnavailable => "its source couldn't be read",
        FeedRefreshReason.NoFeedType => "no feed type is ticked in Settings",
        FeedRefreshReason.ExceptionsUnreadable => "the exceptions couldn't be read",
        FeedRefreshReason.SettingsChanged => "the settings changed during the write",
        FeedRefreshReason.FolderUnavailable => "the export folder can't be used",
        FeedRefreshReason.WriteFailed => result.FailedFile is { Length: > 0 } failed
            ? FeedFileName(failed) + " couldn't be written"
            : "a file couldn't be written",
        FeedRefreshReason.ListUnavailable => "there's no AbuseIPDB list it could use",
        FeedRefreshReason.ClockWrong => "the clock is behind the last download",
        _ => "something went wrong",
    };

    // Help
    public const string HelpSavedFeedsHeading = "8. " + SavedFeedsTitle;

    public const string HelpSavedFeedsText =
        "Each feed you write from its page is saved: an ASN feed from IPInfo Lookup, a cloud feed from Google Cloud, "
        + "Azure or AWS Lookup, and the AbuseIPDB feed. Saved feeds, in the list on the left, lists them. Retire feed "
        + "rewrites a feed's files with no addresses in both folders, whatever is ticked, until the feed is written "
        + "again from its page. Remove forgets a saved feed and leaves its files as they are. Change history, under it in "
        + "the list, lists every address or network each write added or removed, what started it and why an entry was "
        + "removed. Search it by address or network, narrow it by feed and period, or delete it with Clear history. "
        + "Refresh all, on the Saved feeds page, brings every saved feed up to date: it updates the IPinfo dataset when "
        + "a new one is out, checks the cloud sources and the AbuseIPDB blacklist, then writes each feed whose entries "
        + "changed. With Refresh all while the app is open ticked in Settings, it also runs on its own at the interval "
        + "you choose. On a Mac, the keychain can ask once for access after a new version; choose Always Allow, or a "
        + "refresh on its own waits for your answer.";
}
