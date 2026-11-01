using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;
using static PoorMansFeed.Tests.SavedFeedsTextTests;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeSavedFeedsService _feeds = new();
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly FakeTimeProvider _time = new(Now);

    private static readonly SavedFeed Asn = Feed("AS64500", FeedKind.Asn, Pick(asn: "AS64500"));
    private static readonly SavedFeed Aws = Feed("AWS_EC2_all", FeedKind.Aws, Pick(awsService: "EC2"));

    public SavedFeedsViewModelTests() => _gate.SetStarted(true);

    private SavedFeedsViewModel Create() => new(_feeds, _ui, _gate, _time);

    private static IEnumerable<IAsyncRelayCommand> Commands(SavedFeedsViewModel vm) =>
        [vm.SearchCommand, vm.ClearHistoryCommand];

    private static void AssertCommands(SavedFeedsViewModel vm, SavedFeedRow row, bool enabled)
    {
        Assert.All(Commands(vm), command => Assert.Equal(enabled, command.CanExecute(null)));
        Assert.Equal(enabled, vm.RetireCommand.CanExecute(row));
        Assert.Equal(enabled, vm.RemoveCommand.CanExecute(row));
    }

    private static HistoryRow Change(string key, string entry = "203.0.113.0/24", bool added = true,
                                     FeedRemovalReason reason = FeedRemovalReason.NotTold) =>
        new(Now, key, FeedKind.Asn, FeedChangeCause.PageWrite, false, null, added, entry, reason, FeedFileState.Read,
            null, null);

    private static HistoryRead Read(IReadOnlyList<HistoryRow> rows, IReadOnlyList<string>? keys = null,
                                    int? total = null, bool capped = false, HistoryState state = HistoryState.Usable) =>
        new(state, rows, total ?? rows.Count, keys ?? rows.Select(row => row.FeedKey).Distinct().Order().ToList(),
            capped);

    private async Task<SavedFeedsViewModel> ActiveAsync(params SavedFeed[] saved)
    {
        _feeds.Saved = new SavedFeedsRead(true, saved);
        SavedFeedsViewModel vm = Create();
        await vm.ActivateAsync();
        return vm;
    }

    private static SavedFeedRow RowFor(SavedFeed feed) => SavedFeedRow.From(feed);

    // ---- before the start ----

    [Fact]
    public async Task Nothing_is_read_before_the_start_and_an_early_activation_reads_once_when_it_starts()
    {
        var gate = new WorkGate();
        _feeds.Saved = new SavedFeedsRead(true, [Asn]);
        using var vm = new SavedFeedsViewModel(_feeds, _ui, gate, _time);

        await vm.ActivateAsync();
        await vm.ActivateAsync();
        Assert.Equal(0, _feeds.SavedReads);
        Assert.Empty(_feeds.Queries);
        AssertCommands(vm, RowFor(Asn), enabled: false);

        gate.SetStarted(true);
        Assert.Equal(1, _feeds.SavedReads);
        Assert.Single(_feeds.Queries);
        Assert.Single(vm.Feeds);

        gate.SetBusy(true);
        gate.SetBusy(false);
        Assert.Equal(1, _feeds.SavedReads);
        Assert.Single(_feeds.Queries);
    }

    [Fact]
    public void Changing_a_filter_before_the_start_sends_nothing()
    {
        var gate = new WorkGate();
        using var vm = new SavedFeedsViewModel(_feeds, _ui, gate, _time);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        vm.SelectedFeed = new HistoryFeedChoice("AS64500", "AS64500");

        Assert.Empty(_feeds.Queries);
    }

    [Fact]
    public async Task The_commands_need_the_app_started_and_ignore_updates_and_holds()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        SavedFeedRow row = vm.Feeds[0];
        AssertCommands(vm, row, enabled: true);
        Assert.False(vm.RetireCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));

        _gate.SetBusy(true);
        AssertCommands(vm, row, enabled: true);
        _gate.SetBusy(false);

        using (_gate.Hold())
            AssertCommands(vm, row, enabled: true);

        _gate.SetStarted(false);
        AssertCommands(vm, row, enabled: false);
        _gate.SetStarted(true);
        AssertCommands(vm, row, enabled: true);
    }

    [Fact]
    public async Task The_commands_are_off_while_a_press_is_out_and_after_dispose()
    {
        SavedFeedsViewModel vm = await ActiveAsync(Asn);
        SavedFeedRow row = vm.Feeds[0];
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;

        Task press = vm.RemoveCommand.ExecuteAsync(row);
        Assert.True(vm.IsWorking);
        AssertCommands(vm, row, enabled: false);

        _ui.ConfirmResult = false;
        dialog.SetResult();
        await press;
        Assert.False(vm.IsWorking);
        AssertCommands(vm, row, enabled: true);

        vm.Dispose();
        AssertCommands(vm, row, enabled: false);
    }

    // ---- unavailable ----

    [Fact]
    public async Task A_null_service_is_unavailable_and_reads_nothing()
    {
        using var vm = new SavedFeedsViewModel(null, _ui, _gate, _time);

        Assert.False(vm.IsAvailable);
        Assert.Equal(AppText.SavedFeedsUnavailable, vm.FeedsEmptyText);
        Assert.Equal(AppText.HistoryUnavailable, vm.HistoryEmptyText);
        AssertCommands(vm, RowFor(Asn), enabled: false);

        await vm.ActivateAsync();
        vm.SelectedPeriod = vm.PeriodChoices[2];
        Assert.Empty(vm.Feeds);
        Assert.Equal(AppText.SavedFeedsUnavailable, vm.FeedsEmptyText);
        Assert.Equal(AppText.HistoryUnavailable, vm.HistoryEmptyText);
    }

    [Fact]
    public void The_constructor_needs_its_parts()
    {
        Assert.Throws<ArgumentNullException>(() => new SavedFeedsViewModel(_feeds, null!, _gate, _time));
        Assert.Throws<ArgumentNullException>(() => new SavedFeedsViewModel(_feeds, _ui, null!, _time));
        Assert.Throws<ArgumentNullException>(() => new SavedFeedsViewModel(_feeds, _ui, _gate, null!));
    }

    [Fact]
    public async Task A_read_that_isnt_available_shows_its_line_and_no_rows()
    {
        _feeds.Saved = new SavedFeedsRead(false, [Asn]);
        using SavedFeedsViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Empty(vm.Feeds);
        Assert.False(vm.HasFeeds);
        Assert.Equal(AppText.SavedFeedsReadFailed, vm.FeedsEmptyText);
        Assert.Equal("", vm.FeedsSummaryText);
    }

    // ---- the AS name ----

    private static readonly SavedFeed NamedAsn = Feed("AS64500", FeedKind.Asn, Pick(asn: "64500"));

    [Fact]
    public async Task An_asn_feed_shows_the_datasets_as_name_beside_its_number_and_nowhere_else()
    {
        _feeds.AsNames[64500] = "EXAMPLE-NET Example\nNetworks";
        using SavedFeedsViewModel vm = await ActiveAsync(Aws, NamedAsn);

        SavedFeedRow asn = vm.Feeds[1];
        Assert.Equal("AS64500", asn.NameText);
        Assert.Equal("EXAMPLE-NET Example Networks", asn.AsNameText);
        Assert.Equal("AS64500" + AppText.Joiner + "EXAMPLE-NET Example Networks", asn.DisplayNameText);
        Assert.StartsWith("AS64500, EXAMPLE-NET Example Networks, ASN, ", asn.AccessibleText);
        Assert.Equal(AppText.RetireSavedFeedName("AS64500"), asn.RetireName);
        Assert.Equal(AppText.RemoveSavedFeedName("AS64500"), asn.RemoveName);
        Assert.Equal(vm.Feeds[0].NameText, vm.Feeds[0].DisplayNameText);
        Assert.Equal([64500u], Assert.Single(_feeds.AsNameReads));

        await vm.RemoveCommand.ExecuteAsync(asn);

        Assert.Equal(AppText.RemoveSavedFeedTitle("AS64500"), Assert.Single(_ui.Confirms).Title);
    }

    [Fact]
    public async Task An_asn_with_no_name_or_a_failed_read_shows_the_number_alone()
    {
        using (SavedFeedsViewModel vm = await ActiveAsync(NamedAsn))
        {
            Assert.Equal("AS64500", vm.Feeds[0].DisplayNameText);
            Assert.Equal("", vm.Feeds[0].AsNameText);
        }

        _feeds.AsNamesException = new IOException();
        using SavedFeedsViewModel failed = await ActiveAsync(NamedAsn);
        Assert.Equal("AS64500", failed.Feeds[0].DisplayNameText);
    }

    [Fact]
    public async Task A_read_overtaken_while_it_waits_for_names_leaves_the_rows_to_the_later_read()
    {
        _feeds.AsNames[64500] = "OLD-NAME";
        using SavedFeedsViewModel vm = await ActiveAsync(NamedAsn, Aws);
        var held = new TaskCompletionSource();
        _feeds.AsNameGates.Enqueue(held);
        await vm.ActivateAsync();
        Task overtaken = vm.PendingAsNames!;

        _feeds.AsNames[64500] = "NEW-NAME";
        _feeds.Saved = new SavedFeedsRead(true, [NamedAsn]);
        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[1]);
        Assert.Equal("AS64500" + AppText.Joiner + "NEW-NAME", Assert.Single(vm.Feeds).DisplayNameText);

        held.SetResult();
        await overtaken;

        Assert.Equal("AS64500" + AppText.Joiner + "NEW-NAME", Assert.Single(vm.Feeds).DisplayNameText);
    }

    [Fact]
    public async Task The_rows_and_the_history_show_while_the_names_are_still_out()
    {
        _feeds.AsNames[64500] = "EXAMPLE-NET";
        var held = new TaskCompletionSource();
        _feeds.AsNameGates.Enqueue(held);
        _feeds.Saved = new SavedFeedsRead(true, [NamedAsn]);
        using SavedFeedsViewModel vm = Create();

        await vm.ActivateAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("AS64500", Assert.Single(vm.Feeds).DisplayNameText);
        Assert.Single(_feeds.Queries);
        held.SetResult();
        await vm.PendingAsNames!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("AS64500" + AppText.Joiner + "EXAMPLE-NET", Assert.Single(vm.Feeds).DisplayNameText);
    }

    [Fact]
    public async Task No_asn_feed_asks_for_no_name()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Aws);

        Assert.Empty(_feeds.AsNameReads);
    }

    // ---- activation ----

    [Fact]
    public async Task Activation_shows_the_feeds_in_order_and_sends_the_unfiltered_query()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Aws, Asn);

        Assert.Equal(["AWS EC2 all regions", "AS64500"], vm.Feeds.Select(row => row.NameText));
        Assert.Equal(RowFor(Aws).AccessibleText, vm.Feeds[0].AccessibleText);
        Assert.True(vm.HasFeeds);
        Assert.Equal(AppText.SavedFeedsSummary(2), vm.FeedsSummaryText);
        Assert.Equal(AppText.SavedFeedsEmpty, vm.FeedsEmptyText);
        (HistoryQuery query, int limit) = Assert.Single(_feeds.Queries);
        Assert.Equal(new HistoryQuery(null, null, null), query);
        Assert.Equal(SavedFeedsViewModel.HistoryRowLimit, limit);
        Assert.Equal(300, SavedFeedsViewModel.HistoryRowLimit);
    }

    [Fact]
    public async Task No_feed_gives_the_empty_line_and_no_summary()
    {
        using SavedFeedsViewModel vm = await ActiveAsync();

        Assert.False(vm.HasFeeds);
        Assert.Equal("", vm.FeedsSummaryText);
        Assert.Equal(AppText.SavedFeedsEmpty, vm.FeedsEmptyText);
    }

    [Fact]
    public async Task A_second_activation_joins_the_read_still_out()
    {
        var answer = new TaskCompletionSource<HistoryRead>();
        _feeds.HistoryGates.Enqueue(answer);
        using SavedFeedsViewModel vm = Create();

        Task first = vm.ActivateAsync();
        Task second = vm.ActivateAsync();
        Assert.Same(first, second);
        answer.SetResult(Read([]));
        await first;

        Assert.Single(_feeds.Queries);
        Assert.Equal(1, _feeds.SavedReads);
    }

    [Fact]
    public async Task Activation_reads_nothing_while_a_press_is_out()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;
        Task press = vm.ClearHistoryCommand.ExecuteAsync(null);

        await vm.ActivateAsync();
        vm.SelectedPeriod = vm.PeriodChoices[1];
        Assert.Equal(1, _feeds.SavedReads);
        Assert.Single(_feeds.Queries);

        _ui.ConfirmResult = false;
        dialog.SetResult();
        await press;
    }

    // ---- the query ----

    [Fact]
    public async Task Choosing_a_feed_sends_its_key()
    {
        _feeds.History = Read([Change("AS64500")]);
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);

        vm.SelectedFeed = vm.FeedChoices[1];
        await (vm.PendingQuery ?? Task.CompletedTask);

        Assert.Equal("AS64500", _feeds.Queries[^1].Query.FeedKey);
        Assert.Equal(SavedFeedsViewModel.HistoryRowLimit, _feeds.Queries[^1].Limit);
    }

    [Fact]
    public async Task Each_period_sends_the_clock_now_less_its_span_when_the_query_runs()
    {
        using SavedFeedsViewModel vm = await ActiveAsync();
        Assert.Equal(["Any time", "Last 24 hours", "Last 7 days", "Last 30 days"],
                     vm.PeriodChoices.Select(c => c.Text));
        Assert.Same(vm.PeriodChoices[0], vm.SelectedPeriod);
        Assert.Null(vm.PeriodChoices[0].Span);

        _time.Advance(TimeSpan.FromHours(5));
        DateTimeOffset now = Now.AddHours(5);
        TimeSpan[] spans = [TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromDays(30)];
        for (int i = 1; i <= 3; i++)
        {
            vm.SelectedPeriod = vm.PeriodChoices[i];
            Assert.Equal(spans[i - 1], vm.PeriodChoices[i].Span);
            Assert.Equal(now - spans[i - 1], _feeds.Queries[^1].Query.SinceUtc);
        }

        vm.SelectedPeriod = vm.PeriodChoices[0];
        Assert.Null(_feeds.Queries[^1].Query.SinceUtc);
        Assert.Equal(5, _feeds.Queries.Count);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData(" 203.0.113.0/24 ", "203.0.113.0/24")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    public async Task Search_sends_the_match_the_parser_gives(string text, string expected)
    {
        using SavedFeedsViewModel vm = await ActiveAsync();

        vm.SearchText = text;
        Assert.Single(_feeds.Queries);
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(HistoryMatch.TryParse(text, out HistoryMatch? match));
        Assert.Equal(match.Text, _feeds.Queries[^1].Query.Match?.Text);
        Assert.Equal(expected, _feeds.Queries[^1].Query.Match?.Text);
        Assert.Equal("", vm.SearchProblemText);
    }

    [Fact]
    public async Task Invalid_search_text_sends_nothing_and_a_later_valid_search_clears_the_line()
    {
        using SavedFeedsViewModel vm = await ActiveAsync();

        vm.SearchText = "010.0.0.1";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Single(_feeds.Queries);
        Assert.Equal(AppText.HistorySearchInvalid, vm.SearchProblemText);

        vm.SearchText = "203.0.113.7";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, _feeds.Queries.Count);
        Assert.Equal("", vm.SearchProblemText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_box_sends_a_null_match(string text)
    {
        using SavedFeedsViewModel vm = await ActiveAsync();
        vm.SearchText = "203.0.113.7";
        await vm.SearchCommand.ExecuteAsync(null);

        vm.SearchText = text;
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Null(_feeds.Queries[^1].Query.Match);
        Assert.Equal(3, _feeds.Queries.Count);
    }

    // ---- stale answers ----

    [Fact]
    public async Task An_older_answer_arriving_last_leaves_the_newer_rows()
    {
        using SavedFeedsViewModel vm = await ActiveAsync();
        var first = new TaskCompletionSource<HistoryRead>();
        var second = new TaskCompletionSource<HistoryRead>();
        _feeds.HistoryGates.Enqueue(first);
        _feeds.HistoryGates.Enqueue(second);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        Task older = vm.PendingQuery!;
        vm.SelectedPeriod = vm.PeriodChoices[2];
        Task newer = vm.PendingQuery!;

        second.SetResult(Read([Change("AS2", "198.51.100.0/24")]));
        await newer;
        first.SetResult(Read([Change("AS1", "192.0.2.0/24")]));
        await older;

        Assert.Equal(["198.51.100.0/24"], vm.HistoryRows.Select(row => row.EntryText));
    }

    [Fact]
    public async Task A_throw_after_a_newer_answer_changes_nothing()
    {
        using SavedFeedsViewModel vm = await ActiveAsync();
        var first = new TaskCompletionSource<HistoryRead>();
        _feeds.HistoryGates.Enqueue(first);
        _feeds.History = Read([Change("AS2")]);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        Task older = vm.PendingQuery!;
        vm.SelectedPeriod = vm.PeriodChoices[2];
        await vm.PendingQuery!;
        string empty = vm.HistoryEmptyText;

        first.SetException(new InvalidOperationException("late"));
        await older;

        Assert.Single(vm.HistoryRows);
        Assert.Equal(empty, vm.HistoryEmptyText);
        Assert.NotEqual(AppText.HistoryReadFailed, vm.HistoryEmptyText);
    }

    [Fact]
    public async Task A_throw_while_newest_shows_the_read_failure()
    {
        _feeds.History = Read([Change("AS2")]);
        using SavedFeedsViewModel vm = await ActiveAsync();
        Assert.Single(vm.HistoryRows);

        _feeds.HistoryException = new InvalidOperationException("broken");
        vm.SelectedPeriod = vm.PeriodChoices[1];
        await vm.PendingQuery!;

        Assert.Equal(AppText.HistoryReadFailed, vm.HistoryEmptyText);
        Assert.Empty(vm.HistoryRows);
        Assert.False(vm.HasHistoryRows);
        Assert.Equal("", vm.HistorySummaryText);
    }

    [Fact]
    public async Task Dispose_cancels_a_read_still_out_quietly()
    {
        var answer = new TaskCompletionSource<HistoryRead>();
        _feeds.HistoryGates.Enqueue(answer);
        SavedFeedsViewModel vm = Create();
        Task activation = vm.ActivateAsync();
        string empty = vm.HistoryEmptyText;

        vm.Dispose();
        await activation;
        answer.TrySetResult(Read([Change("AS1")]));

        Assert.Empty(vm.HistoryRows);
        Assert.Equal(empty, vm.HistoryEmptyText);
    }

    [Fact]
    public async Task An_answer_that_comes_after_dispose_despite_the_cancel_fills_nothing()
    {
        var answer = new TaskCompletionSource<HistoryRead>();
        _feeds.HistoryGates.Enqueue(answer);
        _feeds.HistoryIgnoresCancel = true;
        SavedFeedsViewModel vm = Create();
        Task activation = vm.ActivateAsync();

        vm.Dispose();
        answer.SetResult(Read([Change("AS1")]));
        await activation;

        Assert.Empty(vm.HistoryRows);
    }

    [Fact]
    public async Task Dispose_while_the_confirmation_shows_calls_nothing_and_shows_nothing()
    {
        SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        vm.Dispose();
        dialog.SetResult();
        await press;

        Assert.Empty(_feeds.Retires);
        Assert.Equal("", vm.ActionText);
    }

    // ---- rows and choices ----

    [Fact]
    public async Task History_rows_keep_the_order_and_name_a_saved_key_by_its_feed()
    {
        _feeds.History = Read([Change("AWS_EC2_all", "198.51.100.0/24"),
                               Change("AS64999", "192.0.2.0/24", added: false, reason: FeedRemovalReason.NotTold),
                               Change("AWS_EC2_all", "203.0.113.0/24", added: false,
                                      reason: FeedRemovalReason.Exception)],
                              keys: ["AS64999", "AWS_EC2_all"]);
        using SavedFeedsViewModel vm = await ActiveAsync(Aws);

        Assert.Equal(["198.51.100.0/24", "192.0.2.0/24", "203.0.113.0/24"],
                     vm.HistoryRows.Select(row => row.EntryText));
        Assert.Equal(["AWS EC2 all regions", "AS64999", "AWS EC2 all regions"],
                     vm.HistoryRows.Select(row => row.FeedText));
        Assert.Equal(["", "", "Exception"], vm.HistoryRows.Select(row => row.ReasonText));
        Assert.True(vm.HasHistoryRows);
        Assert.Equal(AppText.HistorySummary(3, 3, false), vm.HistorySummaryText);

        Assert.Equal([(null, "All feeds"), ("AS64999", "AS64999"), ("AWS_EC2_all", "AWS EC2 all regions")],
                     vm.FeedChoices.Select(choice => (choice.Key, choice.Text)));
        Assert.Null(vm.SelectedFeed?.Key);
    }

    [Fact]
    public async Task A_refill_keeps_a_key_still_there_and_sends_no_query()
    {
        _feeds.History = Read([Change("AS1"), Change("AS2")]);
        using SavedFeedsViewModel vm = await ActiveAsync();

        vm.SelectedFeed = vm.FeedChoices.Single(choice => choice.Key == "AS2");
        await vm.PendingQuery!;
        Assert.Equal(2, _feeds.Queries.Count);
        Assert.Equal("AS2", vm.SelectedFeed?.Key);

        _feeds.History = Read([Change("AS2")], keys: ["AS2", "AS3"]);
        vm.SearchText = "";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(3, _feeds.Queries.Count);
        Assert.Equal("AS2", vm.SelectedFeed?.Key);
        Assert.Equal([null, "AS2", "AS3"], vm.FeedChoices.Select(choice => choice.Key));
    }

    [Fact]
    public async Task A_refill_falls_back_to_all_feeds_when_the_key_is_gone()
    {
        _feeds.History = Read([Change("AS1"), Change("AS2")]);
        using SavedFeedsViewModel vm = await ActiveAsync();
        vm.SelectedFeed = vm.FeedChoices.Single(choice => choice.Key == "AS2");
        await vm.PendingQuery!;

        _feeds.History = Read([], keys: ["AS1"]);
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PendingQuery!;

        Assert.Null(vm.SelectedFeed?.Key);
        Assert.Same(vm.FeedChoices[0], vm.SelectedFeed);
        Assert.Equal(4, _feeds.Queries.Count);
        Assert.Null(_feeds.Queries[^1].Query.FeedKey);
    }

    [Fact]
    public async Task A_fallback_answered_at_once_shows_the_all_feeds_rows()
    {
        _feeds.History = Read([Change("AS1"), Change("AS2")]);
        using SavedFeedsViewModel vm = await ActiveAsync();
        vm.SelectedFeed = vm.FeedChoices.Single(choice => choice.Key == "AS2");
        await vm.PendingQuery!;

        var gone = new TaskCompletionSource<HistoryRead>();
        gone.SetResult(Read([], keys: ["AS1"]));
        var all = new TaskCompletionSource<HistoryRead>();
        all.SetResult(Read([Change("AS1", "198.51.100.0/24")]));
        _feeds.HistoryGates.Enqueue(gone);
        _feeds.HistoryGates.Enqueue(all);
        await vm.SearchCommand.ExecuteAsync(null);
        await vm.PendingQuery!;

        Assert.Equal(["198.51.100.0/24"], vm.HistoryRows.Select(row => row.EntryText));
        Assert.True(vm.HasHistoryRows);
        Assert.Null(_feeds.Queries[^1].Query.FeedKey);
    }

    [Fact]
    public async Task After_clear_history_a_chosen_feed_falls_back_and_says_nothing_is_recorded()
    {
        _feeds.History = Read([Change("AS64500")]);
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        vm.SelectedFeed = vm.FeedChoices[1];
        await vm.PendingQuery!;

        _feeds.History = Read([], keys: [], state: HistoryState.Missing);
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        await vm.PendingQuery!;

        Assert.Same(vm.FeedChoices[0], vm.SelectedFeed);
        Assert.Null(_feeds.Queries[^1].Query.FeedKey);
        Assert.Equal(AppText.HistoryEmpty(HistoryState.Missing, filtered: false), vm.HistoryEmptyText);
    }

    [Fact]
    public async Task A_filter_change_while_the_press_reads_again_is_queried_when_the_press_ends()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var reread = new TaskCompletionSource<HistoryRead>();
        _feeds.OnCall = () => _feeds.HistoryGates.Enqueue(reread);
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        reread.SetResult(Read([]));
        await press;
        await vm.PendingQuery!;

        Assert.Equal(Now - TimeSpan.FromHours(24), _feeds.Queries[^1].Query.SinceUtc);
    }

    [Fact]
    public async Task A_filter_change_while_the_call_is_out_is_read_once_by_the_press()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _feeds.RetireGate = new TaskCompletionSource();
        TaskCompletionSource call = _feeds.RetireGate;
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        call.SetResult();
        await press;

        Assert.Equal(2, _feeds.Queries.Count);
        Assert.Equal(Now - TimeSpan.FromHours(24), _feeds.Queries[^1].Query.SinceUtc);
    }

    [Fact]
    public async Task A_filter_change_during_a_retire_the_close_stopped_reads_nothing()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _feeds.RetireGate = new TaskCompletionSource();
        TaskCompletionSource call = _feeds.RetireGate;
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        Task stop = vm.StopForCloseAsync();
        call.SetResult();
        await stop;
        await press;

        Assert.Single(_feeds.Queries);
    }

    [Fact]
    public async Task A_filter_change_during_a_declined_press_is_queried_when_it_ends()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;
        _ui.ConfirmResult = false;
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        vm.SelectedPeriod = vm.PeriodChoices[2];
        Assert.Single(_feeds.Queries);
        dialog.SetResult();
        await press;
        await vm.PendingQuery!;

        Assert.Equal(2, _feeds.Queries.Count);
        Assert.Equal(Now - TimeSpan.FromDays(7), _feeds.Queries[^1].Query.SinceUtc);
    }

    [Theory]
    [InlineData(HistoryState.Missing)]
    [InlineData(HistoryState.Usable)]
    [InlineData(HistoryState.Newer)]
    [InlineData(HistoryState.Unreadable)]
    [InlineData(HistoryState.StorageError)]
    public async Task Each_state_with_no_rows_gives_its_empty_line(HistoryState state)
    {
        _feeds.History = HistoryRead.Empty(state);
        using SavedFeedsViewModel vm = await ActiveAsync();
        Assert.Equal(AppText.HistoryEmpty(state, filtered: false), vm.HistoryEmptyText);
        Assert.False(vm.HasHistoryRows);
        Assert.Equal("", vm.HistorySummaryText);

        vm.SelectedPeriod = vm.PeriodChoices[1];
        Assert.Equal(AppText.HistoryEmpty(state, filtered: true), vm.HistoryEmptyText);
        vm.SelectedPeriod = vm.PeriodChoices[0];
        Assert.Equal(AppText.HistoryEmpty(state, filtered: false), vm.HistoryEmptyText);

        vm.SearchText = "203.0.113.7";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(AppText.HistoryEmpty(state, filtered: true), vm.HistoryEmptyText);
    }

    [Fact]
    public async Task A_chosen_feed_counts_as_a_filter()
    {
        _feeds.History = Read([], keys: ["AS1"]);
        using SavedFeedsViewModel vm = await ActiveAsync();

        vm.SelectedFeed = vm.FeedChoices[1];
        await vm.PendingQuery!;

        Assert.Equal(AppText.HistoryEmpty(HistoryState.Usable, filtered: true), vm.HistoryEmptyText);
    }

    [Fact]
    public async Task The_summary_follows_all_shown_fewer_shown_and_capped()
    {
        _feeds.History = Read([Change("AS1"), Change("AS1")], total: 2);
        using SavedFeedsViewModel vm = await ActiveAsync();
        Assert.Equal("2 changes", vm.HistorySummaryText);

        _feeds.History = Read([Change("AS1"), Change("AS1")], total: 500);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Showing 2 of 500 changes", vm.HistorySummaryText);

        _feeds.History = Read([Change("AS1"), Change("AS1")], total: 100_000, capped: true);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Showing 2 of more than 100,000 changes", vm.HistorySummaryText);
    }

    [Fact]
    public async Task The_summary_says_up_to_when_the_size_cap_trimmed_changes()
    {
        var trimmed = new DateTimeOffset(2026, 10, 3, 23, 59, 59, TimeSpan.Zero);
        _feeds.History = Read([Change("AS1"), Change("AS1")], total: 500) with { CapTrimmedBeforeUtc = trimmed };
        using SavedFeedsViewModel vm = await ActiveAsync();
        Assert.Equal("Showing 2 of 500 changes \u2014 changes up to 3 October 2026 were trimmed to keep the history under 256 MB",
                     vm.HistorySummaryText);
        Assert.Equal("1 change \u2014 changes up to 3 October 2026 were trimmed to keep the history under 256 MB",
                     AppText.HistorySummary(1, 1, false, trimmed));
        Assert.Equal("Showing 1 of more than 100,000 changes", AppText.HistorySummary(1, 100_000, true));

        _feeds.History = Read([], total: 0) with { CapTrimmedBeforeUtc = trimmed };
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("", vm.HistorySummaryText);
    }

    // ---- Retire ----

    [Fact]
    public async Task Retire_confirms_with_the_rows_name_and_kind_then_calls_with_its_own_pair_and_reads_again()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn, Aws);
        SavedFeedRow row = vm.Feeds[1];
        bool heldInCall = true;
        _feeds.OnCall = () => heldInCall = _gate.IsHeld;
        _ui.OnDialogOpen = () => Assert.True(_gate.IsHeld);

        await vm.RetireCommand.ExecuteAsync(row);

        (string title, string message, string button) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.RetireSavedFeedTitle("AWS EC2 all regions"), title);
        Assert.Equal(AppText.RetireSavedFeedBody("AWS EC2 all regions", FeedKind.Aws), message);
        Assert.Equal(AppText.RetireSavedFeedButton, button);
        Assert.Equal([(FeedKind.Aws, "AWS_EC2_all")], _feeds.Retires);
        Assert.False(heldInCall);
        Assert.Equal(AppText.SavedFeedRetireLine(_feeds.RetireResult, "AWS EC2 all regions"), vm.ActionText);
        Assert.False(vm.IsActionWarning);
        Assert.Equal(2, _feeds.SavedReads);
        Assert.Equal(2, _feeds.Queries.Count);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Retire_reads_the_current_query_again()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        vm.SelectedPeriod = vm.PeriodChoices[1];
        _feeds.Saved = new SavedFeedsRead(true, [Asn with { Retired = true }]);

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.Equal(Now - TimeSpan.FromHours(24), _feeds.Queries[^1].Query.SinceUtc);
        Assert.True(vm.Feeds[0].IsRetired);
        Assert.True(vm.RetireCommand.CanExecute(vm.Feeds[0]));
    }

    [Theory]
    [InlineData(RetireFailure.None, false)]
    [InlineData(RetireFailure.NothingToRetire, true)]
    [InlineData(RetireFailure.NotRetirable, true)]
    [InlineData(RetireFailure.NotSaved, true)]
    [InlineData(RetireFailure.WriteFailed, true)]
    public async Task Each_retire_failure_shows_its_line_and_tone(RetireFailure failure, bool warning)
    {
        _feeds.RetireResult = new RetireResult(failure, "/exports", [], null, [], null);
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.Equal(AppText.SavedFeedRetireLine(_feeds.RetireResult, "AS64500"), vm.ActionText);
        Assert.Equal(warning, vm.IsActionWarning);
    }

    [Fact]
    public async Task Retire_while_the_gate_is_held_shows_the_busy_line_and_no_dialog()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        using (_gate.Hold())
            await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.Empty(_ui.Confirms);
        Assert.Empty(_feeds.Retires);
        Assert.Equal(AppText.RetireSavedFeedBusy, vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Fact]
    public async Task A_retire_dialog_that_throws_shows_the_generic_warning()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _ui.ConfirmException = new InvalidOperationException("no window");

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.Empty(_feeds.Retires);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.True(vm.IsActionWarning);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task A_declined_retire_calls_nothing_and_shows_nothing()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _ui.ConfirmResult = false;

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.Empty(_feeds.Retires);
        Assert.Equal("", vm.ActionText);
        Assert.Equal(1, _feeds.SavedReads);
    }

    [Fact]
    public async Task Asks_before_close_only_while_the_retire_is_with_the_service()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;
        _feeds.RetireGate = new TaskCompletionSource();

        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.False(vm.AsksBeforeClose);
        Assert.True(_gate.IsHeld);

        dialog.SetResult();
        await Task.Yield();
        Assert.False(_gate.IsHeld);
        Assert.True(vm.AsksBeforeClose);
        Assert.Equal(AppText.RetiringSavedFeed("AS64500"), vm.ActionText);
        Assert.False(vm.IsActionWarning);

        _feeds.RetireGate.SetResult();
        await press;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task A_retire_that_throws_shows_the_write_failed_line()
    {
        _feeds.RetireException = new InvalidOperationException("broken");
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        var failed = new RetireResult(RetireFailure.WriteFailed, "", [], null, [], null);
        Assert.Equal(AppText.SavedFeedRetireLine(failed, "AS64500"),
                     vm.ActionText);
        Assert.True(vm.IsActionWarning);
        Assert.Equal(2, _feeds.SavedReads);
    }

    // ---- Remove and Clear history ----

    [Theory]
    [InlineData(SavedFeedsFailure.None, false)]
    [InlineData(SavedFeedsFailure.NotFound, true)]
    [InlineData(SavedFeedsFailure.TooMany, true)]
    [InlineData(SavedFeedsFailure.Unavailable, true)]
    [InlineData(SavedFeedsFailure.StorageError, true)]
    public async Task Remove_confirms_calls_shows_its_line_and_reads_again(SavedFeedsFailure failure, bool warning)
    {
        _feeds.RemoveResult = failure;
        using SavedFeedsViewModel vm = await ActiveAsync(Asn, Aws);
        bool heldInCall = true;
        _feeds.OnCall = () => heldInCall = _gate.IsHeld;
        _feeds.Saved = new SavedFeedsRead(true, [Asn]);

        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[1]);

        (string title, string message, string button) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.RemoveSavedFeedTitle("AWS EC2 all regions"), title);
        Assert.Equal(AppText.RemoveSavedFeedBody(FeedKind.Aws), message);
        Assert.Equal(AppText.RemoveSavedFeedButton, button);
        Assert.Equal(["AWS_EC2_all"], _feeds.Removes);
        Assert.False(heldInCall);
        Assert.Equal(AppText.SavedFeedRemoveLine(failure, "AWS EC2 all regions"), vm.ActionText);
        Assert.Equal(warning, vm.IsActionWarning);
        Assert.Single(vm.Feeds);
        Assert.Equal(2, _feeds.Queries.Count);
    }

    [Fact]
    public async Task Remove_busy_throw_and_decline()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        using (_gate.Hold())
            await vm.RemoveCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.Empty(_ui.Confirms);
        Assert.Equal(AppText.RemoveSavedFeedBusy, vm.ActionText);
        Assert.True(vm.IsActionWarning);

        _ui.ConfirmException = new InvalidOperationException("no window");
        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.True(vm.IsActionWarning);

        _ui.ConfirmResult = false;
        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.Empty(_feeds.Removes);

        _ui.ConfirmResult = true;
        _feeds.RemoveException = new InvalidOperationException("broken");
        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.Equal(AppText.SavedFeedRemoveLine(SavedFeedsFailure.StorageError, "AS64500"), vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    [Theory]
    [InlineData(HistoryState.Missing, false)]
    [InlineData(HistoryState.Usable, true)]
    [InlineData(HistoryState.Newer, true)]
    [InlineData(HistoryState.Unreadable, true)]
    [InlineData(HistoryState.StorageError, true)]
    public async Task Clear_history_confirms_calls_shows_its_line_and_reads_again(HistoryState state, bool warning)
    {
        _feeds.ClearResult = state;
        _feeds.History = Read([Change("AS1")]);
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        bool heldInCall = true;
        _feeds.OnCall = () => heldInCall = _gate.IsHeld;
        _feeds.History = HistoryRead.Empty(HistoryState.Missing);

        await vm.ClearHistoryCommand.ExecuteAsync(null);

        Assert.Equal([(AppText.ClearHistoryTitle, AppText.ClearHistoryBody, AppText.ClearHistoryButton)], _ui.Confirms);
        Assert.Equal(1, _feeds.Clears);
        Assert.False(heldInCall);
        Assert.Equal(AppText.HistoryClearLine(state), vm.ActionText);
        Assert.Equal(warning, vm.IsActionWarning);
        Assert.Empty(vm.HistoryRows);
        Assert.Equal(2, _feeds.SavedReads);
        Assert.Equal(2, _feeds.Queries.Count);
    }

    [Fact]
    public async Task Clear_history_busy_throw_and_decline()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        using (_gate.Hold())
            await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Empty(_ui.Confirms);
        Assert.Equal(AppText.ClearHistoryBusy, vm.ActionText);
        Assert.True(vm.IsActionWarning);

        _ui.ConfirmException = new InvalidOperationException("no window");
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(AppText.Generic, vm.ActionText);

        _ui.ConfirmResult = false;
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(0, _feeds.Clears);

        _ui.ConfirmResult = true;
        _feeds.ClearException = new InvalidOperationException("broken");
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(AppText.HistoryClearLine(HistoryState.StorageError), vm.ActionText);
        Assert.True(vm.IsActionWarning);
    }

    // ---- the close ----

    [Fact]
    public async Task Close_holds_disable_the_commands_until_the_last_is_released()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        SavedFeedRow row = vm.Feeds[0];

        IDisposable first = vm.HoldRunsForClose();
        IDisposable second = vm.HoldRunsForClose();
        AssertCommands(vm, row, enabled: false);
        first.Dispose();
        first.Dispose();
        AssertCommands(vm, row, enabled: false);
        second.Dispose();
        AssertCommands(vm, row, enabled: true);
    }

    [Fact]
    public async Task The_stop_waits_for_the_retire_cancels_nothing_and_the_page_then_shows_and_reads_nothing()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _feeds.RetireGate = new TaskCompletionSource();
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.True(vm.AsksBeforeClose);
        string line = vm.ActionText;

        Task stop = vm.StopForCloseAsync();
        Assert.False(stop.IsCompleted);
        Assert.False(_feeds.RetireTokens[0].IsCancellationRequested);

        _feeds.RetireGate.SetResult();
        await stop;
        await press;

        Assert.False(_feeds.RetireTokens[0].IsCancellationRequested);
        Assert.Equal(line, vm.ActionText);
        Assert.Equal(1, _feeds.SavedReads);
        Assert.Single(_feeds.Queries);
    }

    [Fact]
    public async Task The_stop_returns_at_once_with_nothing_out()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        Assert.True(vm.StopForCloseAsync().IsCompleted);
    }

    [Fact]
    public async Task Dispose_twice_is_safe_and_unsubscribes_the_gate()
    {
        var gate = new WorkGate();
        var vm = new SavedFeedsViewModel(_feeds, _ui, gate, _time);
        await vm.ActivateAsync();

        vm.Dispose();
        vm.Dispose();
        gate.SetStarted(true);

        Assert.Equal(0, _feeds.SavedReads);
        Assert.Empty(_feeds.Queries);
        Assert.False(vm.AsksBeforeClose);
        await vm.ActivateAsync();
        Assert.Equal(0, _feeds.SavedReads);
    }

    [Fact]
    public void A_gate_change_after_dispose_refreshes_no_command()
    {
        var vm = new SavedFeedsViewModel(_feeds, _ui, _gate, _time);
        vm.Dispose();
        int changes = 0;
        vm.SearchCommand.CanExecuteChanged += (_, _) => changes++;

        _gate.SetBusy(true);
        _gate.SetBusy(false);

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task An_answer_longer_than_the_limit_shows_only_the_limit()
    {
        _feeds.History = Read(Enumerable.Range(0, SavedFeedsViewModel.HistoryRowLimit + 1)
            .Select(i => Change("AS64500", "203.0.113." + (i % 256))).ToList(), total: 400);

        SavedFeedsViewModel vm = await ActiveAsync(Asn);

        Assert.Equal(SavedFeedsViewModel.HistoryRowLimit, vm.HistoryRows.Count);
    }

    [Fact]
    public async Task A_retire_out_at_dispose_ends_quietly()
    {
        SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _feeds.RetireGate = new TaskCompletionSource();
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);
        string line = vm.ActionText;

        vm.Dispose();
        await press;

        Assert.Equal(line, vm.ActionText);
        Assert.Equal(1, _feeds.SavedReads);
    }

    // ---- the window's needs ----

    [Fact]
    public async Task HasActionText_follows_the_action_line_and_raises_its_change()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasActionText);

        await vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);

        Assert.NotEqual("", vm.ActionText);
        Assert.True(vm.HasActionText);
        Assert.Contains(nameof(SavedFeedsViewModel.HasActionText), raised);

        raised.Clear();
        await vm.RemoveCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.Contains(nameof(SavedFeedsViewModel.HasActionText), raised);
        Assert.Equal(vm.ActionText.Length > 0, vm.HasActionText);
    }

    [Fact]
    public async Task AskBeforeCloseAsync_answers_yes_at_once_while_no_call_is_out()
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _ui.ConfirmResult = false;

        Assert.True(await vm.AskBeforeCloseAsync());
        Assert.Empty(_ui.Confirms);

        // A confirmation open is not a call out
        var dialog = new TaskCompletionSource();
        _ui.ConfirmGate = dialog;
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.True(await vm.AskBeforeCloseAsync());
        Assert.Single(_ui.Confirms);
        dialog.SetResult();
        await press;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AskBeforeCloseAsync_asks_the_page_question_while_a_call_is_out(bool answer)
    {
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        _feeds.RetireGate = new TaskCompletionSource();
        Task press = vm.RetireCommand.ExecuteAsync(vm.Feeds[0]);
        Assert.True(vm.AsksBeforeClose);
        _ui.Confirms.Clear();
        _ui.ConfirmResult = answer;

        Assert.Equal(answer, await vm.AskBeforeCloseAsync());

        var (title, message, button) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.CloseSavedFeedsWorkTitle, title);
        Assert.Equal(AppText.CloseSavedFeedsWorkBody, message);
        Assert.Equal(AppText.CloseFeedWriteButton, button);
        Assert.False(_feeds.RetireTokens[0].IsCancellationRequested);
        _feeds.RetireGate.SetResult();
        await press;
    }

    [Fact]
    public async Task A_null_from_the_view_keeps_the_feed_choice_and_sends_no_query()
    {
        _feeds.History = Read([Change("AS64500")]);
        using SavedFeedsViewModel vm = await ActiveAsync(Asn);
        HistoryFeedChoice chosen = vm.FeedChoices[1];
        vm.SelectedFeed = chosen;
        await vm.PendingQuery!;
        int queries = _feeds.Queries.Count;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedFeed = null;

        Assert.Same(chosen, vm.SelectedFeed);
        Assert.Equal(queries, _feeds.Queries.Count);
        Assert.DoesNotContain(nameof(SavedFeedsViewModel.SelectedFeed), raised);
    }
    // ---- the next timed refresh ----

    private const string Dot = "\u00A0\u00B7\u00A0";

    [Fact]
    public async Task The_summary_follows_the_schedule_in_each_form()
    {
        var schedule = new FakeRefreshSchedule();
        _feeds.Saved = new SavedFeedsRead(true, [Asn, Aws]);
        using var vm = new SavedFeedsViewModel(_feeds, _ui, _gate, _time, null, schedule);
        await vm.ActivateAsync();
        Assert.Equal("2 saved feeds", vm.FeedsSummaryText);

        schedule.Set(Now.AddHours(2).AddMinutes(4).AddSeconds(30));
        Assert.Equal("2 saved feeds" + Dot + "next refresh 14:05 UTC", vm.FeedsSummaryText);

        schedule.Set(Now.AddHours(13));
        Assert.Equal("2 saved feeds" + Dot + "next refresh tomorrow 01:00 UTC", vm.FeedsSummaryText);

        schedule.Set(Now.AddDays(3));
        Assert.Equal("2 saved feeds" + Dot + "next refresh 2026-11-03 12:00 UTC", vm.FeedsSummaryText);

        schedule.Set(Now.AddMinutes(-2), isWaiting: true);
        Assert.Equal("2 saved feeds" + Dot + "refresh waiting for other work", vm.FeedsSummaryText);

        schedule.Set(null);
        Assert.Equal("2 saved feeds", vm.FeedsSummaryText);
    }

    [Fact]
    public async Task The_day_is_the_view_models_clock_so_tomorrow_turns_into_today_at_midnight()
    {
        var schedule = new FakeRefreshSchedule();
        _feeds.Saved = new SavedFeedsRead(true, [Asn]);
        using var vm = new SavedFeedsViewModel(_feeds, _ui, _gate, _time, null, schedule);
        schedule.Set(Now.AddHours(14));
        await vm.ActivateAsync();
        Assert.Equal("1 saved feed" + Dot + "next refresh tomorrow 02:00 UTC", vm.FeedsSummaryText);

        _time.Advance(TimeSpan.FromHours(12));
        schedule.Set(Now.AddHours(14));
        Assert.Equal("1 saved feed" + Dot + "next refresh 02:00 UTC", vm.FeedsSummaryText);
    }

    [Fact]
    public async Task With_no_saved_feed_the_summary_stays_empty_whatever_the_schedule()
    {
        var schedule = new FakeRefreshSchedule();
        _feeds.Saved = new SavedFeedsRead(true, []);
        using var vm = new SavedFeedsViewModel(_feeds, _ui, _gate, _time, null, schedule);
        await vm.ActivateAsync();

        schedule.Set(Now.AddHours(1));
        Assert.Equal("", vm.FeedsSummaryText);
        schedule.Set(Now, isWaiting: true);
        Assert.Equal("", vm.FeedsSummaryText);
    }

    [Fact]
    public async Task Dispose_stops_following_the_schedule()
    {
        var schedule = new FakeRefreshSchedule();
        _feeds.Saved = new SavedFeedsRead(true, [Asn]);
        var vm = new SavedFeedsViewModel(_feeds, _ui, _gate, _time, null, schedule);
        await vm.ActivateAsync();
        Assert.Equal(1, schedule.Listeners);

        vm.Dispose();
        Assert.Equal(0, schedule.Listeners);
        schedule.Set(Now.AddHours(1));
        Assert.Equal("1 saved feed", vm.FeedsSummaryText);
    }
}
