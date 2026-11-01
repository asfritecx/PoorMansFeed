using System.ComponentModel;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.History;
using static PoorMansFeed.Tests.SavedFeedsTextTests;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedsRefreshTests : IDisposable
{
    private const string Dot = "\u00A0\u00B7\u00A0";
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Refreshed = new(2026, 10, 31, 11, 30, 0, TimeSpan.Zero);

    private readonly FakeSavedFeedsService _feeds = new();
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly FakeFeedRefresher _refresher = new();
    private readonly RefreshAllRunner _runner;

    public SavedFeedsRefreshTests()
    {
        _gate.SetStarted(true);
        _runner = new RefreshAllRunner(_refresher, _gate, _ui)
        {
            ProgressFactory = handler => new ImmediateProgress<RefreshProgress>(handler),
        };
    }

    public void Dispose() => _runner.Dispose();

    private SavedFeedsViewModel Create(bool noRunner = false, IWorkGate? gate = null) =>
        new(_feeds, _ui, gate ?? _gate, new FakeTimeProvider(Now), noRunner ? null : _runner);

    private static FeedRefreshResult Result(string key, FeedRefreshOutcome outcome,
                                            FeedRefreshReason reason = FeedRefreshReason.None, int added = 0,
                                            int removed = 0, int newUnconfirmed = 0, int newLarge = 0,
                                            string? failedFile = null, FeedKind kind = FeedKind.Asn,
                                            bool? written = null, FeedRecordResult? record = null,
                                            IReadOnlyList<string>? oldFilesLeft = null) =>
        new(key, kind, outcome, reason, written ?? outcome == FeedRefreshOutcome.Changed, added, removed, newUnconfirmed,
            newLarge, failedFile, oldFilesLeft ?? [], record);

    private static readonly FeedRecordResult HistoryNotRecorded = new(FeedSaveOutcome.Saved, HistoryOutcome.Failed);

    private static RefreshRunResult Run(IReadOnlyList<FeedRefreshResult> feeds, bool cancelled = false,
                                        bool saveFailed = false, UpdateResult? dataset = null,
                                        IReadOnlyList<SourceRefresh>? sources = null, AbuseRefreshDownload? abuse = null,
                                        bool available = true) =>
        new(available, dataset, sources ?? [], abuse, feeds, cancelled, saveFailed);

    private static FeedRefreshResult[] Outcomes(int changed = 0, int unchanged = 0, int failed = 0, int skipped = 0) =>
    [
        .. Enumerable.Range(0, changed).Select(i => Result("C" + i, FeedRefreshOutcome.Changed, added: 1)),
        .. Enumerable.Range(0, unchanged).Select(i => Result("U" + i, FeedRefreshOutcome.Unchanged)),
        .. Enumerable.Range(0, failed).Select(i => Result("F" + i, FeedRefreshOutcome.Failed, FeedRefreshReason.Failed)),
        .. Enumerable.Range(0, skipped).Select(i => Result("S" + i, FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired)),
    ];

    private async Task FinishAsync(SavedFeedsViewModel vm, RefreshRunResult result)
    {
        _refresher.Finish(result);
        await _runner.PendingRun!;
        await vm.PendingRefreshRead!;
    }

    private async Task<(string Line, bool Warning)> SummaryOfAsync(RefreshRunResult result)
    {
        using SavedFeedsViewModel vm = Create();
        vm.RefreshAllCommand.Execute(null);
        Assert.True(_runner.IsRunning);
        await FinishAsync(vm, result);
        return (vm.ActionText, vm.IsActionWarning);
    }

    // ---- the button ----

    [Fact]
    public void Idle_the_button_reads_Refresh_all_and_can_be_pressed()
    {
        using SavedFeedsViewModel vm = Create();

        Assert.Equal("Refresh all", vm.RefreshAllText);
        Assert.Equal("Update the dataset and the cloud and AbuseIPDB sources, then write every saved feed again",
            vm.RefreshAllTip);
        Assert.True(vm.RefreshAllCommand.CanExecute(null));
    }

    [Fact]
    public void Running_the_button_reads_Stop_refresh_and_after_a_cancel_it_is_off()
    {
        using SavedFeedsViewModel vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(1, _refresher.Calls);
        Assert.Equal("Stop refresh", vm.RefreshAllText);
        Assert.Equal("Stop Refresh all and keep the feeds it has already refreshed", vm.RefreshAllTip);
        Assert.Contains(nameof(SavedFeedsViewModel.RefreshAllText), raised);
        Assert.Contains(nameof(SavedFeedsViewModel.RefreshAllTip), raised);
        Assert.True(vm.RefreshAllCommand.CanExecute(null));

        vm.RefreshAllCommand.Execute(null);

        Assert.True(_runner.CancelRequested);
        Assert.True(_refresher.Token.IsCancellationRequested);
        Assert.False(vm.RefreshAllCommand.CanExecute(null));
        Assert.Equal(1, _refresher.Calls);
    }

    [Fact]
    public void With_no_runner_before_start_and_after_dispose_the_button_is_off()
    {
        using SavedFeedsViewModel none = Create(noRunner: true);
        Assert.False(none.RefreshAllCommand.CanExecute(null));
        Assert.Equal("Refresh all", none.RefreshAllText);

        using SavedFeedsViewModel early = Create(gate: new WorkGate());
        Assert.False(early.RefreshAllCommand.CanExecute(null));

        SavedFeedsViewModel disposed = Create();
        disposed.Dispose();
        Assert.False(disposed.RefreshAllCommand.CanExecute(null));
    }

    [Fact]
    public void A_close_hold_turns_the_button_off_until_released()
    {
        using SavedFeedsViewModel vm = Create();

        IDisposable hold = vm.HoldRunsForClose();
        Assert.False(vm.RefreshAllCommand.CanExecute(null));
        hold.Dispose();
        Assert.True(vm.RefreshAllCommand.CanExecute(null));
    }

    [Fact]
    public void A_press_while_a_page_call_is_out_starts_nothing_and_says_so()
    {
        using SavedFeedsViewModel vm = Create();
        using IDisposable work = _gate.EnterPageWork();

        Assert.True(vm.RefreshAllCommand.CanExecute(null));
        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(0, _refresher.Calls);
        Assert.False(_runner.IsRunning);
        Assert.Equal("Refresh all didn't start \u2014 press Refresh all again when the current task ends", vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Fact]
    public void A_press_while_the_gate_is_held_starts_nothing_and_says_so()
    {
        using SavedFeedsViewModel vm = Create();
        using IDisposable hold = _gate.Hold();

        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(0, _refresher.Calls);
        Assert.Equal("Refresh all didn't start \u2014 press Refresh all again when the current task ends", vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Fact]
    public void A_press_the_runner_refuses_as_unavailable_sets_no_line()
    {
        using SavedFeedsViewModel vm = Create();
        _runner.Dispose();

        vm.RefreshAllCommand.Execute(null);

        Assert.Equal(0, _refresher.Calls);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasActionText);
    }

    // ---- the progress line ----

    [Theory]
    [InlineData(RefreshStep.Dataset, "Updating the IPinfo dataset\u2026")]
    [InlineData(RefreshStep.Sources, "Checking the cloud sources\u2026")]
    [InlineData(RefreshStep.AbuseIpDb, "Checking the AbuseIPDB blacklist\u2026")]
    [InlineData(RefreshStep.Feeds, "Refreshing saved feeds: 2 of 1,500\u2026")]
    [InlineData(RefreshStep.Saving, "Saving the refreshed feeds\u2026")]
    public void Each_step_shows_its_progress_line(RefreshStep step, string expected)
    {
        using SavedFeedsViewModel vm = Create();
        vm.RefreshAllCommand.Execute(null);
        Assert.Equal("Starting Refresh all\u2026", vm.ActionText);
        Assert.False(vm.IsActionWarning);

        _refresher.Progress!.Report(new RefreshProgress(step, 2, 1500, null, null));

        Assert.Equal(expected, vm.ActionText);
        Assert.False(vm.IsActionWarning);
    }

    [Fact]
    public void A_cancel_shows_the_stopping_line_over_later_progress()
    {
        using SavedFeedsViewModel vm = Create();
        vm.RefreshAllCommand.Execute(null);

        vm.RefreshAllCommand.Execute(null);
        Assert.Equal("Stopping the refresh\u2026", vm.ActionText);
        _refresher.Progress!.Report(new RefreshProgress(RefreshStep.Feeds, 1, 2, null, null));

        Assert.Equal("Stopping the refresh\u2026", vm.ActionText);
        Assert.False(vm.IsActionWarning);
    }

    [Fact]
    public async Task A_run_that_throws_ends_on_the_generic_line()
    {
        using SavedFeedsViewModel vm = Create();
        _refresher.Failure = new InvalidOperationException("boom");

        vm.RefreshAllCommand.Execute(null);
        await _runner.PendingRun!;

        Assert.False(_runner.IsRunning);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.True(vm.IsActionWarning);
        Assert.Equal("Refresh all", vm.RefreshAllText);
    }

    // ---- the summary ----

    [Fact]
    public async Task The_summary_counts_each_outcome_and_leaves_out_the_zeros()
    {
        Assert.Equal(("Refreshed 4 saved feeds: 1 changed, 2 unchanged, 1 failed", true),
            await SummaryOfAsync(Run(Outcomes(changed: 1, unchanged: 2, failed: 1))));
        Assert.Equal(("Refreshed 2 saved feeds: 2 unchanged", false),
            await SummaryOfAsync(Run(Outcomes(unchanged: 2))));
        Assert.Equal(("Refreshed 1 saved feed: 1 skipped", false),
            await SummaryOfAsync(Run(Outcomes(skipped: 1))));
    }

    [Fact]
    public async Task A_stopped_run_leads_with_the_stop_and_gives_counts_only()
    {
        Assert.Equal(("Refresh all stopped \u2014 1 changed, 3 skipped", false),
            await SummaryOfAsync(Run(Outcomes(changed: 1, skipped: 3), cancelled: true)));
        Assert.Equal(("Refresh all stopped", false), await SummaryOfAsync(FakeFeedRefresher.Cancelled()));
    }

    [Fact]
    public async Task Unreadable_saved_feeds_and_no_saved_feeds_have_lines_of_their_own()
    {
        Assert.Equal(("The saved feeds couldn't be read, so nothing was refreshed.", true),
            await SummaryOfAsync(Run([], available: false)));
        Assert.Equal(("There are no saved feeds to refresh.", false), await SummaryOfAsync(Run([])));
    }

    private async Task<(string Line, bool Warning)> TimedSummaryOfAsync(RefreshRunResult result)
    {
        using SavedFeedsViewModel vm = Create();
        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart(RefreshTrigger.Timer));
        await FinishAsync(vm, result);
        return (vm.ActionText, vm.IsActionWarning);
    }

    [Fact]
    public async Task A_timed_runs_summary_says_it_was_on_schedule_and_a_pressed_runs_does_not()
    {
        Assert.Equal(("On schedule: Refreshed 2 saved feeds: 1 changed, 1 failed", true),
            await TimedSummaryOfAsync(Run(Outcomes(changed: 1, failed: 1))));
        Assert.Equal(("On schedule: Refresh all stopped", false), await TimedSummaryOfAsync(FakeFeedRefresher.Cancelled()));
        Assert.Equal(("Refreshed 2 saved feeds: 1 changed, 1 failed", true),
            await SummaryOfAsync(Run(Outcomes(changed: 1, failed: 1))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_timed_run_that_found_no_saved_feed_puts_back_the_line_it_found(bool available)
    {
        using SavedFeedsViewModel vm = Create();
        vm.RefreshAllCommand.Execute(null);
        await FinishAsync(vm, Run(Outcomes(unchanged: 1)));
        Assert.Equal("Refreshed 1 saved feed: 1 unchanged", vm.ActionText);

        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart(RefreshTrigger.Timer));
        Assert.Equal("Starting Refresh all\u2026", vm.ActionText);
        await FinishAsync(vm, Run([], available: available));

        Assert.Equal("Refreshed 1 saved feed: 1 unchanged", vm.ActionText);
        Assert.False(vm.IsActionWarning);
        Assert.Equal("Refresh all", vm.RefreshAllText);
    }

    [Fact]
    public async Task A_timed_run_that_found_no_saved_feed_puts_back_a_warning_line_as_a_warning()
    {
        using SavedFeedsViewModel vm = Create();
        vm.RefreshAllCommand.Execute(null);
        await FinishAsync(vm, Run(Outcomes(changed: 1, failed: 1)));
        Assert.Equal(("Refreshed 2 saved feeds: 1 changed, 1 failed", true), (vm.ActionText, vm.IsActionWarning));

        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart(RefreshTrigger.Timer));
        Assert.False(vm.IsActionWarning);
        await FinishAsync(vm, Run([]));

        Assert.Equal("Refreshed 2 saved feeds: 1 changed, 1 failed", vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Fact]
    public async Task A_timed_run_that_throws_ends_on_the_generic_line()
    {
        using SavedFeedsViewModel vm = Create();
        _refresher.Failure = new InvalidOperationException("boom");

        _runner.TryStart(RefreshTrigger.Timer);
        await _runner.PendingRun!;

        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Fact]
    public async Task A_failed_save_comes_before_every_other_problem()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 changed. The saved feeds couldn't be saved, so the next refresh writes "
                + "these feeds again.", true),
            await SummaryOfAsync(Run(Outcomes(changed: 1), saveFailed: true,
                dataset: new UpdateResult(UpdateFailure.Offline, false, null),
                sources: [new SourceRefresh(FeedKind.Aws, SourceRefreshState.Saved)],
                abuse: new AbuseRefreshDownload(AbuseIpDbFailure.Offline, null))));
    }

    [Fact]
    public async Task A_failed_dataset_update_comes_before_a_source_and_the_blacklist()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged. The dataset couldn't be updated, so the feeds used the one "
                + "already there.", true),
            await SummaryOfAsync(Run(Outcomes(unchanged: 1), dataset: new UpdateResult(UpdateFailure.Offline, false, null),
                sources: [new SourceRefresh(FeedKind.Aws, SourceRefreshState.Saved)],
                abuse: new AbuseRefreshDownload(AbuseIpDbFailure.Offline, null))));
    }

    [Fact]
    public async Task A_dataset_kept_back_for_the_user_says_so_before_any_other_dataset_line()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged. The dataset wasn't downloaded, to keep the last IPinfo "
                + "downloads of the day for Update dataset, so the feeds used the one already there.", true),
            await SummaryOfAsync(Run([Result("A", FeedRefreshOutcome.Unchanged, kind: FeedKind.Azure)],
                dataset: new UpdateResult(UpdateFailure.KeptForUser, false, null),
                sources: [new SourceRefresh(FeedKind.Aws, SourceRefreshState.Saved)])));
    }

    [Fact]
    public async Task A_dataset_kept_back_for_the_user_isnt_named_when_no_feed_used_the_dataset()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged", false),
            await SummaryOfAsync(Run([Result("R", FeedRefreshOutcome.Unchanged, kind: FeedKind.AbuseIpDbRolling)],
                dataset: new UpdateResult(UpdateFailure.KeptForUser, false, null))));
    }

    [Fact]
    public async Task The_first_source_that_used_its_saved_ranges_is_named()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged. Azure couldn't be checked, so its saved ranges were used.",
                true),
            await SummaryOfAsync(Run([Result("A", FeedRefreshOutcome.Unchanged, kind: FeedKind.Azure)],
                dataset: new UpdateResult(UpdateFailure.None, true, null),
                sources:
                [
                    new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Fresh),
                    new SourceRefresh(FeedKind.Azure, SourceRefreshState.Saved),
                    new SourceRefresh(FeedKind.Aws, SourceRefreshState.Saved),
                ],
                abuse: new AbuseRefreshDownload(AbuseIpDbFailure.Offline, null))));
    }

    [Fact]
    public async Task A_failed_blacklist_download_is_named_but_one_kept_for_the_user_is_not()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged. The AbuseIPDB blacklist couldn't be downloaded, so the "
                + "rolling feed used the list already there.", true),
            await SummaryOfAsync(Run(Rolling, abuse: new AbuseRefreshDownload(AbuseIpDbFailure.Offline, null))));
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged", false),
            await SummaryOfAsync(Run(Rolling, abuse: new AbuseRefreshDownload(AbuseIpDbFailure.KeptForUser, null))));
        Assert.Equal(("Refreshed 1 saved feed: 1 unchanged", false),
            await SummaryOfAsync(Run(Rolling, abuse: new AbuseRefreshDownload(AbuseIpDbFailure.None, null))));
    }

    private static readonly FeedRefreshResult[] Rolling =
        [Result(AbuseFeedKey, FeedRefreshOutcome.Unchanged, kind: FeedKind.AbuseIpDbRolling)];

    private const string AbuseFeedKey = "AbuseIPDB_rolling";

    // ---- the summary's clauses only when true ----

    [Fact]
    public async Task A_stopped_run_names_only_a_step_that_a_written_feed_used()
    {
        Assert.Equal(("Refresh all stopped \u2014 2 skipped", false),
            await SummaryOfAsync(Run(Outcomes(skipped: 2), cancelled: true,
                dataset: new UpdateResult(UpdateFailure.Offline, false, null),
                sources: [new SourceRefresh(FeedKind.Aws, SourceRefreshState.Saved)],
                abuse: new AbuseRefreshDownload(AbuseIpDbFailure.NoKey, null))));
        Assert.Equal(("Refresh all stopped \u2014 1 changed, 1 skipped. The dataset couldn't be updated, so the feeds "
                + "used the one already there.", true),
            await SummaryOfAsync(Run(Outcomes(changed: 1, skipped: 1), cancelled: true,
                dataset: new UpdateResult(UpdateFailure.Offline, false, null))));
    }

    [Fact]
    public async Task A_rolling_feed_that_failed_gets_no_blacklist_clause()
    {
        Assert.Equal(("Refreshed 2 saved feeds: 1 unchanged, 1 failed", true),
            await SummaryOfAsync(Run(
                [
                    Result("AS1", FeedRefreshOutcome.Unchanged),
                    Result(AbuseFeedKey, FeedRefreshOutcome.Failed, FeedRefreshReason.ListUnavailable,
                           kind: FeedKind.AbuseIpDbRolling),
                ],
                abuse: new AbuseRefreshDownload(AbuseIpDbFailure.Offline, null))));
    }

    [Fact]
    public async Task A_cloud_feed_gets_the_dataset_clause_and_the_rolling_feed_alone_does_not()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 changed. The dataset couldn't be updated, so the feeds used the one "
                + "already there.", true),
            await SummaryOfAsync(Run([Result("AWS_EC2", FeedRefreshOutcome.Changed, added: 1, kind: FeedKind.Aws)],
                dataset: new UpdateResult(UpdateFailure.Offline, false, null))));
        Assert.Equal(("Refreshed 1 saved feed: 1 changed", false),
            await SummaryOfAsync(Run([Result(AbuseFeedKey, FeedRefreshOutcome.Changed, added: 1,
                                             kind: FeedKind.AbuseIpDbRolling)],
                dataset: new UpdateResult(UpdateFailure.Offline, false, null))));
    }

    [Fact]
    public async Task The_cloud_clause_names_the_provider_whose_feed_was_written()
    {
        Assert.Equal(("Refreshed 2 saved feeds: 1 unchanged, 1 failed. Azure couldn't be checked, so its saved ranges "
                + "were used.", true),
            await SummaryOfAsync(Run(
                [
                    Result("G", FeedRefreshOutcome.Failed, FeedRefreshReason.PickGone, kind: FeedKind.GoogleCloud),
                    Result("A", FeedRefreshOutcome.Unchanged, kind: FeedKind.Azure),
                ],
                sources:
                [
                    new SourceRefresh(FeedKind.GoogleCloud, SourceRefreshState.Saved),
                    new SourceRefresh(FeedKind.Azure, SourceRefreshState.Saved),
                ])));
    }

    [Fact]
    public async Task A_failed_save_shows_on_a_stopped_run()
    {
        Assert.Equal(("Refresh all stopped \u2014 1 changed. The saved feeds couldn't be saved, so the next refresh "
                + "writes these feeds again.", true),
            await SummaryOfAsync(Run(Outcomes(changed: 1), cancelled: true, saveFailed: true)));
    }

    // ---- the history clause ----

    [Fact]
    public async Task Changes_not_recorded_are_counted()
    {
        Assert.Equal(("Refreshed 1 saved feed: 1 changed. 1 feed's changes weren't recorded in the history.", true),
            await SummaryOfAsync(Run([Result("AS1", FeedRefreshOutcome.Changed, added: 1, record: HistoryNotRecorded)])));
        Assert.Equal(("Refreshed 3 saved feeds: 2 changed, 1 unchanged. 2 feeds' changes weren't recorded in the history.", true),
            await SummaryOfAsync(Run(
            [
                Result("AS1", FeedRefreshOutcome.Changed, added: 1, record: HistoryNotRecorded),
                Result("AS2", FeedRefreshOutcome.Changed, added: 1, record: HistoryNotRecorded),
                Result("AS3", FeedRefreshOutcome.Unchanged, record: new FeedRecordResult(FeedSaveOutcome.Saved,
                    HistoryOutcome.Recorded)),
            ])));
    }

    [Fact]
    public async Task The_history_clause_comes_after_the_save_and_before_the_dataset()
    {
        FeedRefreshResult[] feeds = [Result("AS1", FeedRefreshOutcome.Changed, added: 1, record: HistoryNotRecorded)];
        var dataset = new UpdateResult(UpdateFailure.Offline, false, null);

        Assert.Equal(("Refreshed 1 saved feed: 1 changed. 1 feed's changes weren't recorded in the history.", true),
            await SummaryOfAsync(Run(feeds, dataset: dataset)));
        Assert.Equal(("Refreshed 1 saved feed: 1 changed. The saved feeds couldn't be saved, so the next refresh writes "
                + "these feeds again.", true),
            await SummaryOfAsync(Run(feeds, saveFailed: true, dataset: dataset)));
    }

    [Fact]
    public async Task The_history_clause_shows_on_a_stopped_run()
    {
        Assert.Equal(("Refresh all stopped \u2014 1 changed. 1 feed's changes weren't recorded in the history.", true),
            await SummaryOfAsync(Run([Result("AS1", FeedRefreshOutcome.Changed, added: 1, record: HistoryNotRecorded)],
                cancelled: true)));
    }

    // ---- what a row's write left ----

    [Fact]
    public void The_tip_ends_with_what_the_write_left()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn, refreshed: Refreshed);
        string file = Path.Combine("exports", "fortigate", "AS1_ipv4.txt");
        SavedFeedRow row = SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Changed, added: 2, record:
            new FeedRecordResult(FeedSaveOutcome.StorageError, HistoryOutcome.Failed), oldFilesLeft: [file]));

        Assert.Equal("Changed +2 \u22120" + Dot + AppText.SavedFeedRefreshedAt(Refreshed) + ". The saved feed couldn't "
            + "be updated. This change wasn't recorded in the history. The old fortigate" + Path.DirectorySeparatorChar
            + "AS1_ipv4.txt was left as it was.", row.LastRefreshTip);
        Assert.True(row.IsRefreshWarning);
    }

    [Fact]
    public void A_changed_row_cautions_for_a_record_failure_or_old_files_left()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn);
        Assert.True(SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Changed, added: 1,
            record: HistoryNotRecorded)).IsRefreshWarning);
        Assert.True(SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Changed, added: 1,
            record: new FeedRecordResult(FeedSaveOutcome.TooMany, HistoryOutcome.Recorded))).IsRefreshWarning);
        Assert.True(SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Changed, added: 1,
            oldFilesLeft: [Path.Combine("exports", "fortigate", "AS1.txt")])).IsRefreshWarning);
        Assert.False(SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Changed, added: 1,
            record: new FeedRecordResult(FeedSaveOutcome.Saved, HistoryOutcome.Recorded))).IsRefreshWarning);
    }

    [Fact]
    public void A_failure_says_what_was_written_first()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn);
        Assert.Equal("Couldn't be refreshed: its files were written, but the saved feed couldn't be updated",
            SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Failed, FeedRefreshReason.NotSaved, written: true))
                .LastRefreshTip);
        Assert.Equal("Couldn't be refreshed: a file couldn't be written" + Dot + "+3 \u22121 written first",
            SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed, added: 3,
                removed: 1, written: true)).LastRefreshTip);
    }

    [Fact]
    public void A_list_unavailable_says_there_is_no_list_to_use()
    {
        Assert.Equal("Couldn't be refreshed: there's no AbuseIPDB list it could use",
            SavedFeedRow.From(Feed(AbuseFeedKey, FeedKind.AbuseIpDbRolling), Result(AbuseFeedKey,
                FeedRefreshOutcome.Failed, FeedRefreshReason.ListUnavailable, kind: FeedKind.AbuseIpDbRolling))
                .LastRefreshTip);
    }

    [Fact]
    public void The_stop_tip_keeps_what_was_refreshed() =>
        Assert.Equal("Stop Refresh all and keep the feeds it has already refreshed", AppText.StopRefreshTip);

    // ---- a result only while it is true ----

    private async Task<SavedFeedsViewModel> AfterARunAsync(SavedFeed feed)
    {
        _feeds.Saved = new SavedFeedsRead(true, [feed]);
        SavedFeedsViewModel vm = Create();
        await vm.ActivateAsync();
        vm.RefreshAllCommand.Execute(null);
        await FinishAsync(vm, Run([Result(feed.Key, FeedRefreshOutcome.Changed, added: 4)]));
        return vm;
    }

    [Fact]
    public async Task A_row_keeps_its_result_while_the_feed_is_as_the_run_left_it()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn, refreshed: Refreshed);
        using SavedFeedsViewModel vm = await AfterARunAsync(feed);
        Assert.Equal("Changed +4 \u22120", vm.Feeds[0].LastRefreshText);
        Assert.Equal("Changed +4 \u22120" + Dot + "Last refreshed 2026-10-31 11:30 UTC", vm.Feeds[0].LastRefreshTip);

        await vm.ActivateAsync();

        Assert.Equal("Changed +4 \u22120", vm.Feeds[0].LastRefreshText);
    }

    [Fact]
    public async Task A_later_write_or_retire_shows_the_plain_cell()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn, refreshed: Refreshed);
        using SavedFeedsViewModel vm = await AfterARunAsync(feed);

        _feeds.Saved = new SavedFeedsRead(true, [feed with { LastWriteUtc = feed.LastWriteUtc.AddMinutes(5) }]);
        await vm.ActivateAsync();
        Assert.Equal(AppText.SavedFeedLastRefresh(Refreshed), vm.Feeds[0].LastRefreshText);

        _feeds.Saved = new SavedFeedsRead(true, [feed with { Retired = true }]);
        await vm.ActivateAsync();
        Assert.Equal(AppText.SavedFeedLastRefresh(Refreshed), vm.Feeds[0].LastRefreshText);

        _feeds.Saved = new SavedFeedsRead(true, [feed]);
        await vm.ActivateAsync();
        Assert.Equal("Changed +4 \u22120", vm.Feeds[0].LastRefreshText);
    }

    [Fact]
    public async Task A_new_run_forgets_the_last_runs_rows()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn, refreshed: Refreshed);
        using SavedFeedsViewModel vm = await AfterARunAsync(feed);

        vm.RefreshAllCommand.Execute(null);
        Assert.True(_runner.IsRunning);
        await vm.ActivateAsync();

        Assert.Equal(AppText.SavedFeedLastRefresh(Refreshed), vm.Feeds[0].LastRefreshText);
    }

    // ---- the rows ----

    [Fact]
    public async Task After_a_run_each_row_shows_its_own_result_joined_ignoring_case()
    {
        _feeds.Saved = new SavedFeedsRead(true,
        [
            Feed("AS1", FeedKind.Asn, refreshed: Refreshed), Feed("AS2", FeedKind.Asn), Feed("AS3", FeedKind.Asn),
            Feed("AS4", FeedKind.Asn, refreshed: Refreshed), Feed("AS5", FeedKind.Asn),
        ]);
        using SavedFeedsViewModel vm = Create();
        await vm.ActivateAsync();
        Assert.All(vm.Feeds, row => Assert.Equal(row.LastRefreshText, row.LastRefreshTip));
        vm.RefreshAllCommand.Execute(null);

        await FinishAsync(vm, Run(
        [
            Result("as1", FeedRefreshOutcome.Changed, added: 1200, removed: 1, newUnconfirmed: 1, newLarge: 1),
            Result("AS2", FeedRefreshOutcome.Unchanged),
            Result("as3", FeedRefreshOutcome.Skipped, FeedRefreshReason.Retired),
            Result("AS4", FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed, failedFile: "/exports/fortigate/AS4_ipv4.txt"),
        ]));

        string at = AppText.SavedFeedRefreshedAt(Refreshed);
        SavedFeedRow changed = vm.Feeds[0];
        Assert.Equal("Changed +1,200 \u22121" + Dot + "2 new left out", changed.LastRefreshText);
        Assert.Equal(changed.LastRefreshText + Dot + at, changed.LastRefreshTip);
        Assert.True(changed.IsRefreshWarning);
        Assert.Contains(changed.LastRefreshTip, changed.AccessibleText);

        SavedFeedRow unchanged = vm.Feeds[1];
        Assert.Equal(("Unchanged", "Unchanged", false),
            (unchanged.LastRefreshText, unchanged.LastRefreshTip, unchanged.IsRefreshWarning));

        SavedFeedRow skipped = vm.Feeds[2];
        Assert.Equal(("Skipped", "Skipped: it's retired", false),
            (skipped.LastRefreshText, skipped.LastRefreshTip, skipped.IsRefreshWarning));

        SavedFeedRow failed = vm.Feeds[3];
        Assert.Equal("Couldn't be refreshed", failed.LastRefreshText);
        Assert.Equal("Couldn't be refreshed: fortigate" + Path.DirectorySeparatorChar + "AS4_ipv4.txt couldn't be written"
            + Dot + at, failed.LastRefreshTip);
        Assert.True(failed.IsRefreshWarning);
        Assert.Contains(failed.LastRefreshTip, failed.AccessibleText);

        SavedFeedRow none = vm.Feeds[4];
        Assert.Equal(("Not refreshed yet", "Not refreshed yet", false),
            (none.LastRefreshText, none.LastRefreshTip, none.IsRefreshWarning));
        Assert.Equal(SavedFeedRow.From(Feed("AS5", FeedKind.Asn)).AccessibleText, none.AccessibleText);
    }

    [Fact]
    public void Every_reason_has_words_of_its_own()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn);
        List<string> tips = Enum.GetValues<FeedRefreshReason>()
            .Where(reason => reason is not FeedRefreshReason.None)
            .Select(reason => SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Failed, reason)).LastRefreshTip)
            .ToList();

        Assert.All(tips, tip => Assert.StartsWith("Couldn't be refreshed: ", tip));
        Assert.Equal(tips.Count, tips.Distinct().Count());
        Assert.Equal("Couldn't be refreshed: a file couldn't be written",
            SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Failed, FeedRefreshReason.WriteFailed)).LastRefreshTip);
        Assert.Equal("Couldn't be refreshed: something went wrong",
            SavedFeedRow.From(feed, Result("AS1", FeedRefreshOutcome.Failed, FeedRefreshReason.None)).LastRefreshTip);
    }

    [Fact]
    public void A_row_with_no_result_is_as_before()
    {
        SavedFeed feed = Feed("AS1", FeedKind.Asn, refreshed: Refreshed);
        SavedFeedRow row = SavedFeedRow.From(feed, null);

        Assert.Equal(AppText.SavedFeedLastRefresh(Refreshed), row.LastRefreshText);
        Assert.Equal(row.LastRefreshText, row.LastRefreshTip);
        Assert.False(row.IsRefreshWarning);
        Assert.Equal(SavedFeedRow.From(feed).AccessibleText, row.AccessibleText);
    }

    [Fact]
    public void A_disposed_page_no_longer_hears_the_runner()
    {
        SavedFeedsViewModel vm = Create();
        vm.Dispose();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(RefreshStartRefusal.None, _runner.TryStart());
        _refresher.Progress!.Report(new RefreshProgress(RefreshStep.Feeds, 1, 2, null, null));

        Assert.Empty(raised);
        Assert.Equal("", vm.ActionText);
    }
}
