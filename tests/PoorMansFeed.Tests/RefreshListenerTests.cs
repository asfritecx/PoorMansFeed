using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

// What a page hears when Refresh all, or another page, changes something under it
public sealed class RefreshListenerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DatasetInfo FirstDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", Now, true);
    private static readonly DatasetInfo NewDataset = new(new DateOnly(2026, 10, 30), 1234999, "def", Now, true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-refresh-listen", Guid.NewGuid().ToString("N"));
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly FakeUi _ui = new();
    private readonly FakeAbuseIpDbService _abuse = new();
    private readonly WorkGate _gate = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeFeedRefresher _refresher = new();

    public RefreshListenerTests()
    {
        Directory.CreateDirectory(_folder);
        _ipinfo.DataFolder = Path.Combine(_folder, "data");
        _ipinfo.Dataset = FirstDataset;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ----- the AbuseIPDB page -----

    private async Task<AbuseIpDbViewModel> ShownPageAsync(bool onShow)
    {
        _gate.SetStarted(true);
        var vm = new AbuseIpDbViewModel(_abuse, _ipinfo, _ui, _gate, null, _time, Path.Combine(_folder, "exports"))
        {
            ProgressFactory = handler => new ImmediateProgress<GroupingProgress>(handler),
            IsOnShow = onShow,
        };
        await vm.ActivateAsync();
        await vm.PendingGrouping!;
        Assert.Equal(1, _abuse.NetworksCalls);
        return vm;
    }

    private static async Task SettleAsync(AbuseIpDbViewModel vm)
    {
        if (vm.PendingListenerRead is { } read)
            await read;
        if (vm.PendingGrouping is { } grouping)
            await grouping;
    }

    [Fact]
    public async Task A_list_change_while_the_page_is_hidden_groups_once_at_the_next_show()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: false);

        _abuse.RaiseListChanged(1, AbuseListChangeKind.Merged);
        _ui.RunPosted();
        await SettleAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        vm.IsOnShow = true;
        await vm.ActivateAsync();
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);

        await vm.ActivateAsync();
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_list_change_while_the_page_is_shown_reads_and_groups_only_once_posted()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseListChanged(1, AbuseListChangeKind.Merged);
        Assert.Equal(1, _ui.PostedCount);
        Assert.Equal(reads, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);

        _ui.RunPosted();
        await SettleAsync(vm);
        Assert.Equal(reads + 1, _abuse.StatusCalls);
        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task The_pages_own_download_groups_once_and_its_change_is_ignored()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        _abuse.DownloadMerges = true;
        _abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 10_000, 0, 50, 3, 0, Now, null));

        await vm.DownloadCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);
        int reads = _abuse.StatusCalls;

        _ui.RunPosted();
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.Equal(reads, _abuse.StatusCalls);
    }

    [Fact]
    public async Task A_list_change_while_the_page_runs_is_left_to_the_runs_end()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 10_000, 0, 50, 3, 0, Now, null));
        Task download = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseListChanged(1, AbuseListChangeKind.Merged);
        _ui.RunPosted();
        await SettleAsync(vm);
        Assert.Equal(reads, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);

        held.SetResult();
        await download;
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_disposed_page_hears_nothing_and_stops_listening()
    {
        AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        Assert.Equal(2, _abuse.ListenerCount);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseListChanged(1, AbuseListChangeKind.Cleared);
        _abuse.RaiseWorkChanged();
        vm.Dispose();
        _ui.RunPosted();

        Assert.Equal(0, _abuse.ListenerCount);
        Assert.Equal(reads, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task Work_done_elsewhere_reads_the_status_of_a_shown_page_without_grouping()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseWorkChanged();
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(reads + 1, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task Work_still_under_way_reads_nothing()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        int reads = _abuse.StatusCalls;
        _abuse.WorkUnderWay = new AbuseWork(AbuseWorkKind.RefreshFeed);

        _abuse.RaiseWorkChanged();
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(reads, _abuse.StatusCalls);
    }

    [Fact]
    public async Task A_post_landing_while_the_page_closes_reads_nothing()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseListChanged(1, AbuseListChangeKind.Merged);
        _abuse.RaiseWorkChanged();
        await vm.StopForCloseAsync();
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(reads, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_listener_read_waits_for_new_addresses_explaining_read_so_its_failure_line_is_shown()
    {
        var usable = new AbuseListStatus(AbuseListState.Usable, 10, 0, Now.AddHours(-1), null, null, 1, null, NewCount: 3);
        for (int i = 0; i < 4; i++)
            _abuse.StatusResults.Enqueue(usable);
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        _abuse.NewAddresses = new AbuseNewAddresses(AbuseNewAddressesFailure.StorageError, [], null);
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        int reads = _abuse.StatusCalls;
        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        for (int i = 0; i < 1000 && _abuse.StatusCalls == reads; i++)
            await Task.Delay(1);
        Assert.Equal(reads + 1, _abuse.StatusCalls);

        _abuse.RaiseWorkChanged();
        _ui.RunPosted();
        var listenerHeld = new TaskCompletionSource();
        _abuse.StatusGate = listenerHeld;
        held.SetResult();
        await press;

        Assert.Equal(AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.StorageError), vm.StatusText);
        listenerHeld.SetResult();
        await SettleAsync(vm);
    }

    [Fact]
    public async Task The_pages_own_write_feed_starts_no_second_status_read()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        _abuse.RaisesWork = true;
        _abuse.FeedResults.Enqueue(new AbuseFeedWriteResult(AbuseFeedWriteFailure.None, ["AbuseIPDB_rolling_ipv4.txt"],
            100, 2, 0, 0, []));
        var feedHeld = new TaskCompletionSource();
        _abuse.FeedGate = feedHeld;
        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        int reads = _abuse.StatusCalls;
        var statusHeld = new TaskCompletionSource();
        _abuse.StatusGate = statusHeld;

        feedHeld.SetResult();
        for (int i = 0; i < 1000 && _abuse.StatusCalls == reads; i++)
            await Task.Delay(1);
        Assert.True(vm.IsWorking);
        Assert.Equal(2, _ui.PostedCount);
        _ui.RunPosted();
        statusHeld.SetResult();
        await write;
        await SettleAsync(vm);

        Assert.Equal(reads + 1, _abuse.StatusCalls);
    }

    [Fact]
    public async Task The_pages_own_clear_list_groups_once()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: true);
        _abuse.ClearRaisesListChanged = true;

        await vm.ClearListCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);

        _ui.RunPosted();
        await SettleAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task Work_done_while_the_page_is_hidden_reads_nothing()
    {
        using AbuseIpDbViewModel vm = await ShownPageAsync(onShow: false);
        int reads = _abuse.StatusCalls;

        _abuse.RaiseWorkChanged();
        _ui.RunPosted();
        await SettleAsync(vm);

        Assert.Equal(reads, _abuse.StatusCalls);
    }

    // ----- the dataset card -----

    private async Task<MainViewModel> StartCardAsync(TaskCompletionSource? heldCheck)
    {
        _ipinfo.HasToken = true;
        _ipinfo.StartupResult = new StartupResult(StartupFailure.None, FirstDataset, true);
        _ipinfo.CheckGate = heldCheck;
        var vm = new MainViewModel(_ipinfo, _ui, null, null, _gate, null, null)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        await vm.InitializeAsync();
        if (heldCheck is null && vm.PendingCheck is { } check)
            await check;
        return vm;
    }

    private static (DatasetState, string, bool, string, string) Card(MainViewModel vm) =>
        (vm.State, vm.PillText, vm.IsPillWarning, vm.DatasetStatusText, vm.DatasetDetailText);

    [Fact]
    public async Task A_refresh_cancels_a_check_still_out_and_its_late_answer_changes_nothing()
    {
        var held = new TaskCompletionSource();
        _ipinfo.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable);
        MainViewModel vm = await StartCardAsync(held);
        Task check = vm.PendingCheck!;
        var refreshing = (DatasetState.Current, AppText.DatasetRefreshTitle, false, AppText.DatasetRefreshTitle,
            AppText.DatasetWaitsForRefresh);

        _gate.SetRefreshing(true);
        Assert.Equal(refreshing, Card(vm));

        held.SetResult();
        await check;
        Assert.Equal(refreshing, Card(vm));
    }

    [Fact]
    public async Task A_refresh_that_changed_no_dataset_gives_the_card_back_as_it_was()
    {
        MainViewModel vm = await StartCardAsync(heldCheck: null);
        var before = Card(vm);
        int checks = _ipinfo.CheckCalls;

        _gate.SetRefreshing(true);
        Assert.Equal((before.Item1, before.Item2, before.Item3, AppText.DatasetRefreshTitle, AppText.DatasetWaitsForRefresh),
            Card(vm));

        _gate.SetRefreshing(false);
        Assert.Equal(before, Card(vm));
        Assert.Equal(checks, _ipinfo.CheckCalls);
    }

    [Fact]
    public async Task A_check_the_refresh_cancelled_runs_again_when_it_ends()
    {
        var held = new TaskCompletionSource();
        MainViewModel vm = await StartCardAsync(held);
        Assert.Equal(1, _ipinfo.CheckCalls);

        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);

        Assert.Equal(2, _ipinfo.CheckCalls);
        await vm.PendingCheck!;
        Assert.Equal((DatasetState.Current, AppText.UpToDate), (vm.State, vm.PillText));
    }

    [Fact]
    public async Task A_refresh_that_installed_a_dataset_shows_it_as_updated_and_checks_nothing()
    {
        var held = new TaskCompletionSource();
        MainViewModel vm = await StartCardAsync(held);
        int changes = 0;
        vm.DatasetChanged += (_, _) => changes++;

        _gate.SetRefreshing(true);
        _ipinfo.Dataset = NewDataset;
        _gate.SetRefreshing(false);

        Assert.Equal(1, changes);
        Assert.Equal(1, _ipinfo.CheckCalls);
        Assert.Equal(AppText.DatasetFacts(NewDataset.DownloadedOn, NewDataset.RangeCount, NewDataset.IncludesIPv6),
            vm.DatasetFactsText);
        Assert.Equal((DatasetState.Current, AppText.UpToDate, false, AppText.UpdatedTitle, ""), Card(vm));
    }

    // ----- the dataset card, heard through the runner -----

    private RefreshAllRunner Listen(MainViewModel vm)
    {
        var runner = new RefreshAllRunner(_refresher, _gate, _ui)
        {
            ProgressFactory = handler => new ImmediateProgress<RefreshProgress>(handler),
        };
        vm.ListenToRefresh(runner);
        return runner;
    }

    private static RefreshRunResult RunWith(UpdateResult? dataset) => new(true, dataset, [], null, [], false, false);

    private async Task RefreshAsync(RefreshAllRunner runner, RefreshRunResult result, Action? during = null)
    {
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        during?.Invoke();
        _refresher.Finish(result);
        await runner.PendingRun!;
    }

    [Fact]
    public async Task A_refresh_whose_update_failed_ends_on_the_failure_not_the_up_to_date_it_started_on()
    {
        MainViewModel vm = await StartCardAsync(heldCheck: null);
        Assert.Equal((DatasetState.Current, AppText.UpToDate), (vm.State, vm.PillText));
        RefreshAllRunner runner = Listen(vm);
        int checks = _ipinfo.CheckCalls;

        await RefreshAsync(runner, RunWith(new UpdateResult(UpdateFailure.Offline, false, null)));

        Assert.Equal((DatasetState.UpdateFailed, AppText.UpdateFailedTitle), (vm.State, vm.DatasetStatusText));
        Assert.Equal(AppText.UpdateOffline + " " + AppText.StillInUse, vm.DatasetDetailText);
        Assert.Equal(checks, _ipinfo.CheckCalls);
    }

    [Fact]
    public async Task A_refresh_that_kept_the_download_for_the_user_gives_the_card_back_as_it_was()
    {
        MainViewModel vm = await StartCardAsync(heldCheck: null);
        var before = Card(vm);
        RefreshAllRunner runner = Listen(vm);
        int checks = _ipinfo.CheckCalls;

        await RefreshAsync(runner, RunWith(new UpdateResult(UpdateFailure.KeptForUser, false, FirstDataset)));

        Assert.Equal(before, Card(vm));
        Assert.Equal((DatasetState.Current, AppText.UpToDate), (vm.State, vm.PillText));
        Assert.Equal(checks, _ipinfo.CheckCalls);
    }

    [Fact]
    public async Task A_failed_refresh_update_after_a_cancelled_check_names_the_failure_in_the_pill()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        RefreshAllRunner runner = Listen(vm);

        await RefreshAsync(runner, RunWith(new UpdateResult(UpdateFailure.Offline, false, null)));

        Assert.Equal((DatasetState.UpdateFailed, AppText.UpdateFailedTitle, true, AppText.UpdateFailedTitle),
            (vm.State, vm.PillText, vm.IsPillWarning, vm.DatasetStatusText));
    }

    [Fact]
    public async Task A_refresh_that_found_the_dataset_current_after_a_cancelled_check_shows_it_up_to_date_unchecked()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        RefreshAllRunner runner = Listen(vm);

        await RefreshAsync(runner, RunWith(new UpdateResult(UpdateFailure.None, true, FirstDataset)));

        Assert.Equal((DatasetState.Current, AppText.UpToDate, false, AppText.UpToDate, AppText.AlreadyCurrentDetail), Card(vm));
        Assert.Equal(1, _ipinfo.CheckCalls);
    }

    [Fact]
    public async Task A_refresh_that_updated_the_dataset_shows_it_updated_and_raises_one_change()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        RefreshAllRunner runner = Listen(vm);
        int changes = 0;
        vm.DatasetChanged += (_, _) => changes++;

        await RefreshAsync(runner, RunWith(new UpdateResult(UpdateFailure.None, false, NewDataset)),
            () => _ipinfo.Dataset = NewDataset);

        Assert.Equal(1, changes);
        Assert.Equal(1, _ipinfo.CheckCalls);
        Assert.Equal((DatasetState.Current, AppText.UpToDate, false, AppText.UpdatedTitle, ""), Card(vm));
    }

    [Fact]
    public async Task A_refresh_whose_dataset_step_did_not_run_checks_again_after_a_cancelled_check()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        RefreshAllRunner runner = Listen(vm);

        await RefreshAsync(runner, RunWith(null));

        Assert.Equal(2, _ipinfo.CheckCalls);
        await vm.PendingCheck!;
        Assert.Equal((DatasetState.Current, AppText.UpToDate), (vm.State, vm.PillText));
    }

    [Fact]
    public async Task A_refresh_that_threw_gives_the_card_back_as_it_was()
    {
        MainViewModel vm = await StartCardAsync(heldCheck: null);
        var before = Card(vm);
        RefreshAllRunner runner = Listen(vm);
        _refresher.Failure = new InvalidOperationException("refresh failed");

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        await runner.PendingRun!;

        Assert.Null(runner.LastRun);
        Assert.Equal(before, Card(vm));
        Assert.Equal(1, _ipinfo.CheckCalls);
    }

    [Fact]
    public async Task A_refresh_stopped_for_the_close_reads_no_facts_raises_nothing_and_checks_nothing()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        RefreshAllRunner runner = Listen(vm);
        _refresher.FinishWhenCancelled = true;
        string facts = vm.DatasetFactsText;
        int changes = 0;
        vm.DatasetChanged += (_, _) => changes++;
        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());
        _ipinfo.Dataset = NewDataset;

        await runner.StopForCloseAsync();

        Assert.Equal(1, _ipinfo.CheckCalls);
        Assert.Equal(0, changes);
        Assert.Equal(facts, vm.DatasetFactsText);
    }

    [Fact]
    public async Task While_refreshing_the_card_names_the_refresh_and_a_cancelled_check_leaves_no_checking_pill()
    {
        MainViewModel vm = await StartCardAsync(new TaskCompletionSource());
        Assert.Equal(AppText.CheckingForUpdates, vm.PillText);
        RefreshAllRunner runner = Listen(vm);

        Assert.Equal(RefreshStartRefusal.None, runner.TryStart());

        Assert.Equal((DatasetState.Current, AppText.DatasetRefreshTitle, false, AppText.DatasetRefreshTitle,
            AppText.DatasetWaitsForRefresh), Card(vm));
        _refresher.Finish(RunWith(null));
        await runner.PendingRun!;
    }
}
