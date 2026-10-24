using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>When each page shows the ModSecurity rules button, what its press turns off, and the button in the window.</summary>
public sealed class ModSecurityRulesButtonTests : IDisposable
{
    private static readonly DatasetInfo Dataset = new(new DateOnly(2026, 10, 8), 3, "abc", DateTimeOffset.UnixEpoch, true);
    private static readonly IReadOnlyList<string> ModFiles = [Path.Combine("x", "modsecurity", "AS15169_ipv4.txt")];
    private static readonly ModSecurityRuleIdsResult NoIds = new(ModSecurityRuleIdsFailure.StorageError, []);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-rules-button", Guid.NewGuid().ToString("N"));
    private readonly FakeIpInfoService _ipinfo = new()
    {
        Dataset = Dataset,
        HasToken = true,
        StartupResult = new StartupResult(StartupFailure.None, Dataset, true),
    };
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();
    private readonly FakeAbuseIpDbService _abuse = new();
    private readonly HttpClient _http;

    public ModSecurityRulesButtonTests()
    {
        Directory.CreateDirectory(_folder);
        _ipinfo.DataFolder = Path.Combine(_folder, "data");
        _http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CatalogueJson, Encoding.UTF8, "application/json"),
        }));
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block,
                [new OverlapGroup("US", "United States", "AS15169", "Google LLC", "google.com", 1)])).ToList(),
            Dataset);
    }

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ----- Lookup -----

    private async Task<MainViewModel> LookupAsync()
    {
        var vm = new MainViewModel(_ipinfo, _ui, _folder, null, _gate)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
            await check;

        await SearchAsnAsync(vm);
        _ipinfo.ExportPlan = new ExportPlan(2, "Google LLC", []);
        return vm;
    }

    private async Task SearchAsnAsync(MainViewModel vm)
    {
        var query = new SearchQuery(SearchKind.Asn, "AS15169", 15169, null);
        _ipinfo.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, query,
            [new NetworkRecord("10.0.0.0/16", "US", "United States", "NA", "AS15169", "Google LLC", "google.com")], 1);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
    }

    private async Task ExportAsync(MainViewModel vm, ExportFailure failure = ExportFailure.None,
                                   IReadOnlyList<string>? modFiles = null)
    {
        _ipinfo.ExportResult = new ExportResult(failure, ["AS15169.txt"], 1, 0, ModSecurityFiles: modFiles);
        await vm.ExportCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Lookup_shows_it_only_after_an_export_that_wrote_ModSecurity_copies()
    {
        MainViewModel vm = await LookupAsync();
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));

        await ExportAsync(vm);
        Assert.False(vm.ShowsModSecurityRules);

        await ExportAsync(vm, modFiles: []);
        Assert.False(vm.ShowsModSecurityRules);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await ExportAsync(vm, modFiles: ModFiles);
        Assert.True(vm.ShowsModSecurityRules);
        Assert.True(vm.ModSecurityRulesCommand.CanExecute(null));
        Assert.Contains(nameof(vm.ShowsModSecurityRules), raised);

        await ExportAsync(vm, ExportFailure.WriteFailed, ModFiles);
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Lookup_drops_it_when_an_export_starts()
    {
        MainViewModel vm = await LookupAsync();
        await ExportAsync(vm, modFiles: ModFiles);
        var held = new TaskCompletionSource();
        _ipinfo.ExportGate = held;

        Task export = vm.ExportCommand.ExecuteAsync(null);
        Assert.False(vm.ShowsModSecurityRules);
        held.SetResult();
        await export;
    }

    [Fact]
    public async Task Lookup_drops_it_on_a_search_from_the_box_or_from_Look_up()
    {
        MainViewModel vm = await LookupAsync();
        await ExportAsync(vm, modFiles: ModFiles);
        await SearchAsnAsync(vm);
        Assert.False(vm.ShowsModSecurityRules);

        await ExportAsync(vm, modFiles: ModFiles);
        Assert.True(await vm.SearchForAsync("AS15169"));
        Assert.False(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task Lookup_keeps_it_through_the_rerun_a_settings_save_makes()
    {
        MainViewModel vm = await LookupAsync();
        await ExportAsync(vm, modFiles: ModFiles);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.True(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task Lookup_keeps_it_hidden_when_a_search_starts_while_the_export_writes()
    {
        MainViewModel vm = await LookupAsync();
        var held = new TaskCompletionSource();
        _ipinfo.ExportGate = held;
        _ipinfo.ExportResult = new ExportResult(ExportFailure.None, ["AS15169.txt"], 1, 0, ModSecurityFiles: ModFiles);
        Task export = vm.ExportCommand.ExecuteAsync(null);

        vm.SearchText = "AS15169";
        Assert.True(vm.SearchCommand.CanExecute(null));
        await vm.SearchCommand.ExecuteAsync(null);
        held.SetResult();
        await export;

        Assert.Equal(1, _ipinfo.ExportCalls);
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Lookup_keeps_it_hidden_when_the_export_starts_while_a_search_is_out()
    {
        MainViewModel vm = await LookupAsync();
        var searching = new TaskCompletionSource();
        _ipinfo.SearchGate = searching;
        vm.SearchText = "AS15169";
        Task search = vm.SearchCommand.ExecuteAsync(null);

        var held = new TaskCompletionSource();
        _ipinfo.ExportGate = held;
        _ipinfo.ExportResult = new ExportResult(ExportFailure.None, ["AS15169.txt"], 1, 0, ModSecurityFiles: ModFiles);
        Assert.True(vm.ExportCommand.CanExecute(null));
        Task export = vm.ExportCommand.ExecuteAsync(null);
        searching.SetResult();
        await search;
        held.SetResult();
        await export;

        Assert.Equal(1, _ipinfo.ExportCalls);
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Lookup_shows_it_when_a_settings_save_reruns_the_search_while_the_export_writes()
    {
        MainViewModel vm = await LookupAsync();
        var held = new TaskCompletionSource();
        _ipinfo.ExportGate = held;
        _ipinfo.ExportResult = new ExportResult(ExportFailure.None, ["AS15169.txt"], 1, 0, ModSecurityFiles: ModFiles);
        Task export = vm.ExportCommand.ExecuteAsync(null);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        held.SetResult();
        await export;

        Assert.True(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task Lookup_runs_nothing_from_the_press_until_the_dialog_closes()
    {
        MainViewModel vm = await LookupAsync();
        await ExportAsync(vm, modFiles: ModFiles);
        IAsyncRelayCommand[] runs =
            [vm.SearchCommand, vm.ExportCommand, vm.CopyResultsCommand, vm.UpdateDatasetCommand, vm.ModSecurityRulesCommand];
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;
        var open = new TaskCompletionSource();
        _ui.ModSecurityRulesAnswer = _ => open.Task;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.False(_gate.IsHeld);
        Assert.All(runs, command => Assert.False(command.CanExecute(null)));
        assigning.SetResult();
        Assert.True(_gate.IsHeld);
        Assert.All(runs, command => Assert.False(command.CanExecute(null)));
        open.SetResult();
        await press;

        Assert.Single(_ui.ModSecurityRulesPrompts);
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task Lookup_puts_the_press_line_on_the_results_line_unless_disposed()
    {
        MainViewModel vm = await LookupAsync();
        await ExportAsync(vm, modFiles: ModFiles);
        _ipinfo.AssignRuleIdsResult = NoIds;

        await vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.StorageError), vm.ResultsActionText);

        await ExportAsync(vm, modFiles: ModFiles);
        string before = vm.ResultsActionText;
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;
        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        vm.Dispose();
        assigning.SetResult();
        await press;
        Assert.Equal(before, vm.ResultsActionText);
    }

    // ----- the cloud pages -----

    private sealed class RulesFeeds(IGoogleCloudFeedService inner) : IGoogleCloudFeedService
    {
        public CloudFeedWriteResult Result { get; set; } = new(CloudFeedWriteFailure.None, ["f.txt"], 1, 0, 0);

        public TaskCompletionSource? Pending { get; set; }

        public Task<CatalogueResult> LoadCatalogueAsync(CancellationToken ct) => inner.LoadCatalogueAsync(ct);

        public Task<CheckResult> CheckAsync(CloudRangeCatalogue catalogue, RegionSelection selection, CancellationToken ct) =>
            inner.CheckAsync(catalogue, selection, ct);

        public CloudFeedPlan PlanWrite(CloudFeedCheck check, IReadOnlyCollection<CidrBlock> ticked) =>
            inner.PlanWrite(check, ticked);

        public Task<CloudFeedWriteResult> WriteAsync(CloudFeedPlan plan, IReadOnlyCollection<CidrBlock> includedLargeRanges,
                                                     CancellationToken ct) =>
            Pending is { } pending ? AfterAsync(pending) : Task.FromResult(Result);

        private async Task<CloudFeedWriteResult> AfterAsync(TaskCompletionSource pending)
        {
            await pending.Task;
            return Result;
        }
    }

    private async Task<(GoogleCloudFeedViewModel Vm, RulesFeeds Feeds)> CloudAsync()
    {
        var feeds = new RulesFeeds(new GoogleCloudFeedService(_ipinfo, _http, new CoreOptions(), new FakeTimeProvider(), _folder));
        var vm = new GoogleCloudFeedViewModel(feeds, _ipinfo, _ui, _gate, _folder);
        await vm.ActivateAsync();
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "us-central1");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        return (vm, feeds);
    }

    private static async Task WriteAsync(GoogleCloudFeedViewModel vm, RulesFeeds feeds,
                                         CloudFeedWriteFailure failure = CloudFeedWriteFailure.None,
                                         IReadOnlyList<string>? modFiles = null)
    {
        feeds.Result = new CloudFeedWriteResult(failure, ["f.txt"], 1, 0, 0, ModSecurityFiles: modFiles);
        await vm.WriteFeedCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Cloud_shows_it_only_after_a_write_that_wrote_ModSecurity_copies()
    {
        (GoogleCloudFeedViewModel vm, RulesFeeds feeds) = await CloudAsync();
        Assert.False(vm.ShowsModSecurityRules);

        await WriteAsync(vm, feeds);
        Assert.False(vm.ShowsModSecurityRules);

        await WriteAsync(vm, feeds, modFiles: ModFiles);
        Assert.True(vm.ShowsModSecurityRules);
        Assert.True(vm.ModSecurityRulesCommand.CanExecute(null));

        await WriteAsync(vm, feeds, CloudFeedWriteFailure.WriteFailed, ModFiles);
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cloud_drops_it_when_a_check_starts_or_the_pick_changes()
    {
        (GoogleCloudFeedViewModel vm, RulesFeeds feeds) = await CloudAsync();
        await WriteAsync(vm, feeds, modFiles: ModFiles);

        Task check = vm.CheckCommand.ExecuteAsync(null);
        Assert.False(vm.ShowsModSecurityRules);
        await check;

        await WriteAsync(vm, feeds, modFiles: ModFiles);
        Assert.True(vm.ShowsModSecurityRules);
        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");
        Assert.False(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task Cloud_keeps_it_hidden_when_the_pick_changes_while_the_write_is_out()
    {
        (GoogleCloudFeedViewModel vm, RulesFeeds feeds) = await CloudAsync();
        var pending = new TaskCompletionSource();
        feeds.Pending = pending;
        feeds.Result = new CloudFeedWriteResult(CloudFeedWriteFailure.None, ["f.txt"], 1, 0, 0, ModSecurityFiles: ModFiles);
        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);

        vm.SelectedRegion = vm.Regions.Single(region => region.Scope.Id == "europe-west1");
        pending.SetResult();
        await write;

        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cloud_runs_nothing_from_the_press_until_the_dialog_closes()
    {
        (GoogleCloudFeedViewModel vm, RulesFeeds feeds) = await CloudAsync();
        await WriteAsync(vm, feeds, modFiles: ModFiles);
        IAsyncRelayCommand[] runs = [vm.CheckCommand, vm.WriteFeedCommand, vm.ReloadRangesCommand, vm.ModSecurityRulesCommand];
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
        var open = new TaskCompletionSource();
        _ui.ModSecurityRulesAnswer = _ => open.Task;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.True(_gate.IsHeld);
        Assert.All(runs, command => Assert.False(command.CanExecute(null)));
        open.SetResult();
        await press;

        Assert.False(vm.IsWorking);
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task Cloud_puts_the_press_line_on_the_action_line_unless_disposed()
    {
        (GoogleCloudFeedViewModel vm, RulesFeeds feeds) = await CloudAsync();
        await WriteAsync(vm, feeds, modFiles: ModFiles);
        _ipinfo.AssignRuleIdsResult = NoIds;

        await vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.StorageError), vm.ActionText);

        await WriteAsync(vm, feeds, modFiles: ModFiles);
        string before = vm.ActionText;
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;
        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        vm.Dispose();
        assigning.SetResult();
        await press;
        Assert.Equal(before, vm.ActionText);
    }

    // ----- the AbuseIPDB Blacklist page -----

    private AbuseIpDbViewModel Abuse()
    {
        _gate.SetStarted(true);
        var vm = new AbuseIpDbViewModel(_abuse, _ipinfo, _ui, _gate, null, new FakeTimeProvider(), Path.Combine(_folder, "exports"))
        {
            ProgressFactory = handler => new ImmediateProgress<GroupingProgress>(handler),
        };
        return vm;
    }

    private static AbuseFeedWriteResult Feed(AbuseFeedWriteFailure failure = AbuseFeedWriteFailure.None,
                                             IReadOnlyList<string>? modFiles = null) =>
        new(failure, ["AbuseIPDB_rolling_ipv4.txt"], 100, 0, 0, 0, [], ModSecurityFiles: modFiles);

    private async Task WriteFeedAsync(AbuseIpDbViewModel vm, AbuseFeedWriteFailure failure = AbuseFeedWriteFailure.None,
                                      IReadOnlyList<string>? modFiles = null)
    {
        _abuse.FeedResults.Enqueue(Feed(failure, modFiles));
        await vm.WriteFeedCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AbuseIpDb_shows_it_only_after_a_Write_feed_that_wrote_ModSecurity_copies()
    {
        using AbuseIpDbViewModel vm = Abuse();
        Assert.False(vm.ShowsModSecurityRules);

        await WriteFeedAsync(vm);
        Assert.False(vm.ShowsModSecurityRules);

        await WriteFeedAsync(vm, modFiles: ModFiles);
        Assert.True(vm.ShowsModSecurityRules);
        Assert.True(vm.ModSecurityRulesCommand.CanExecute(null));

        await WriteFeedAsync(vm, AbuseFeedWriteFailure.WriteFailed, ModFiles);
        Assert.False(vm.ShowsModSecurityRules);
        Assert.False(vm.ModSecurityRulesCommand.CanExecute(null));
    }

    [Fact]
    public async Task AbuseIpDb_drops_it_on_Empty_feed_and_Clear_list_and_Empty_feed_never_shows_it()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);

        _abuse.EmptyResults.Enqueue(Feed(modFiles: ModFiles));
        await vm.EmptyFeedCommand.ExecuteAsync(null);
        Assert.Equal(1, _abuse.EmptyCalls);
        Assert.False(vm.ShowsModSecurityRules);

        await WriteFeedAsync(vm, modFiles: ModFiles);
        Assert.True(vm.ShowsModSecurityRules);
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal(1, _abuse.ClearCalls);
        Assert.False(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task AbuseIpDb_runs_nothing_from_the_press_until_the_dialog_closes()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);
        OffendingNetworkRow row = OffendingNetworkRow.From(new AbuseNetworkGroup(15169, "Google LLC", "google.com", 3, []));
        IAsyncRelayCommand[] runs =
            [vm.DownloadCommand, vm.ClearListCommand, vm.WriteFeedCommand, vm.EmptyFeedCommand, vm.ModSecurityRulesCommand];
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.LookUpCommand.CanExecute(row));
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;
        var open = new TaskCompletionSource();
        _ui.ModSecurityRulesAnswer = _ => open.Task;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.False(vm.IsWorking);
        Assert.All(runs, command => Assert.False(command.CanExecute(null)));
        Assert.False(vm.LookUpCommand.CanExecute(row));
        assigning.SetResult();
        Assert.True(_gate.IsHeld);
        Assert.All(runs, command => Assert.False(command.CanExecute(null)));
        open.SetResult();
        await press;

        Assert.Single(_ui.ModSecurityRulesPrompts);
        Assert.All(runs, command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.LookUpCommand.CanExecute(row));
    }

    [Fact]
    public async Task AbuseIpDb_puts_the_press_line_on_the_action_line_unless_disposed()
    {
        AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);
        _ipinfo.AssignRuleIdsResult = NoIds;

        await vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.StorageError), vm.ActionText);

        await WriteFeedAsync(vm, modFiles: ModFiles);
        string before = vm.ActionText;
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;
        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        vm.Dispose();
        assigning.SetResult();
        await press;
        Assert.Equal(before, vm.ActionText);
    }

    [Fact]
    public async Task AbuseIpDb_keeps_it_through_a_Clear_list_answered_No_and_drops_it_on_Yes()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);

        _ui.ConfirmResult = false;
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Single(_ui.Confirms);
        Assert.Equal(0, _abuse.ClearCalls);
        Assert.True(vm.ShowsModSecurityRules);

        _ui.ConfirmResult = true;
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal(1, _abuse.ClearCalls);
        Assert.False(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task AbuseIpDb_keeps_it_through_a_Clear_list_refused_as_busy()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);

        using (_gate.Hold())
            await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Empty(_ui.Confirms);
        Assert.Equal(0, _abuse.ClearCalls);
        Assert.True(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task AbuseIpDb_keeps_it_through_a_download()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);

        _abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 10_000, 0, 50, 3, 0,
            DateTimeOffset.UnixEpoch, null));
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.True(vm.ShowsModSecurityRules);
    }

    [Fact]
    public async Task AbuseIpDb_gives_the_busy_line_and_no_dialog_when_the_close_holds_the_runs_during_the_press()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        using IDisposable hold = vm.HoldRunsForClose();
        assigning.SetResult();
        await press;

        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.Equal(AppText.ModSecurityRulesBusy, vm.ActionText);
    }

    [Fact]
    public async Task AbuseIpDb_shows_nothing_and_sets_no_line_when_the_close_stops_the_page_during_the_press()
    {
        using AbuseIpDbViewModel vm = Abuse();
        await WriteFeedAsync(vm, modFiles: ModFiles);
        string before = vm.ActionText;
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        await vm.StopForCloseAsync();
        assigning.SetResult();
        await press;

        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.Equal(before, vm.ActionText);
    }

    // ----- Azure and AWS: the press turns off the update check -----

    private static readonly IReadOnlyList<OverlapGroup> Microsoft =
        [new OverlapGroup("US", "United States", "AS8075", "Microsoft Corporation", "microsoft.com", 1)];

    private static readonly IReadOnlyList<OverlapGroup> Amazon =
        [new OverlapGroup("US", "United States", "AS16509", "Amazon.com, Inc.", "amazon.com", 1)];

    private void AnswerOverlapsWith(IReadOnlyList<OverlapGroup> groups)
    {
        _ipinfo.Settings = new UserSettings { WriteModSecurityLists = true };
        _ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found, blocks.Select(block => new NetworkOverlap(block, groups)).ToList(), Dataset);
    }

    private async Task PressHoldsTheUpdateCheckAsync(CloudFeedPageViewModel vm, IAsyncRelayCommand checkForUpdates)
    {
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasRows);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(vm.ShowsModSecurityRules);
        Assert.True(checkForUpdates.CanExecute(null));
        var assigning = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = assigning;

        Task press = vm.ModSecurityRulesCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.False(checkForUpdates.CanExecute(null));
        assigning.SetResult();
        await press;

        Assert.False(vm.IsWorking);
        Assert.True(checkForUpdates.CanExecute(null));
    }

    [Fact]
    public async Task Azure_turns_off_Check_for_updates_while_the_press_is_out()
    {
        using var temp = new ServiceTempFolder();
        AnswerOverlapsWith(Microsoft);
        var time = new FakeTimeProvider(AzureTestData.Now);
        var feeds = new FakeAzureFeedService(new AzureFeedService(_ipinfo, _http, new CoreOptions(), time, temp.Paths, temp.ExportFolder))
        {
            Cached = AzureTestData.Catalogue(),
            UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, AzureTestData.Now),
        };
        using var vm = new AzureFeedViewModel(feeds, _ipinfo, _ui, time, _gate, temp.ExportFolder);
        await vm.ActivateAsync();

        await PressHoldsTheUpdateCheckAsync(vm, vm.CheckForUpdatesCommand);
    }

    [Fact]
    public async Task Aws_turns_off_Check_for_updates_while_the_press_is_out()
    {
        using var temp = new ServiceTempFolder();
        AnswerOverlapsWith(Amazon);
        var time = new FakeTimeProvider(AwsTestData.Now);
        var feeds = new FakeAwsFeedService(new AwsFeedService(_ipinfo, _http, new CoreOptions(), time, temp.Paths, temp.ExportFolder))
        {
            Cached = AwsTestData.Catalogue(),
            UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, current, AwsTestData.Now),
        };
        using var vm = new AwsFeedViewModel(feeds, _ipinfo, _ui, time, _gate, temp.ExportFolder);
        await vm.ActivateAsync();

        await PressHoldsTheUpdateCheckAsync(vm, vm.CheckForUpdatesCommand);
    }

    // ----- the window -----

    private static Button FirstInRow(Control scope, string name)
    {
        Button button = scope.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);
        var row = Assert.IsType<StackPanel>(button.Parent);
        Assert.Same(button, row.Children[0]);
        Assert.Equal(AppText.ModSecurityRulesButton, button.Content);
        return button;
    }

    [AvaloniaFact]
    public async Task The_window_has_each_button_first_in_its_row_hidden_until_its_page_shows_it()
    {
        MainViewModel main = await LookupAsync();
        (GoogleCloudFeedViewModel google, RulesFeeds feeds) = await CloudAsync();
        using AbuseIpDbViewModel abuse = Abuse();
        var window = new MainWindow();
        window.AttachViewModel(main, google: google, abuseIpDb: abuse);
        window.Show();
        try
        {
            Button lookup = FirstInRow(window, "ModSecurityRulesButton");
            Button cloud = FirstInRow(window.FindControl<Control>("ToolsPage")!, "CloudModSecurityRulesButton");
            Button abuseButton = FirstInRow(window, "AbuseModSecurityRulesButton");
            Assert.False(lookup.IsVisible);
            Assert.False(cloud.IsVisible);
            Assert.False(abuseButton.IsVisible);

            await ExportAsync(main, modFiles: ModFiles);
            await WriteAsync(google, feeds, modFiles: ModFiles);
            await WriteFeedAsync(abuse, modFiles: ModFiles);

            Assert.True(lookup.IsVisible);
            Assert.True(lookup.IsEffectivelyEnabled);
            Assert.True(cloud.IsVisible);
            Assert.True(abuseButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // Beside the rules button at the minimum width the AbuseIPDB summary's report note and a long cloud summary are cut,
    // so each summary has its own row under the buttons, as wide as the header
    [AvaloniaFact]
    public async Task Each_summary_beside_a_rules_button_has_the_headers_whole_width_under_the_buttons()
    {
        MainViewModel main = await LookupAsync();
        (GoogleCloudFeedViewModel google, RulesFeeds feeds) = await CloudAsync();
        using AbuseIpDbViewModel abuse = Abuse();
        var window = new MainWindow();
        window.AttachViewModel(main, google: google, abuseIpDb: abuse);
        window.Show();
        try
        {
            window.Width = window.MinWidth;
            await WriteAsync(google, feeds, modFiles: ModFiles);
            await WriteFeedAsync(abuse, modFiles: ModFiles);

            ShowPage(window, "NavGoogleCloud");
            Button cloud = FirstInRow(window.FindControl<Control>("ToolsPage")!, "CloudModSecurityRulesButton");
            Grid cloudHeader = Assert.IsType<Grid>(cloud.Parent!.Parent);
            AssertSummaryUnderTheButtons(cloudHeader, cloud,
                cloudHeader.Children.OfType<TextBlock>().Single(block => Grid.GetRow(block) == 1));

            ShowPage(window, "NavAbuseIpDb");
            Button abuseButton = FirstInRow(window, "AbuseModSecurityRulesButton");
            Grid abuseHeader = Assert.IsType<Grid>(abuseButton.Parent!.Parent);
            AssertSummaryUnderTheButtons(abuseHeader, abuseButton, window.FindControl<TextBlock>("AbuseSummary")!);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ShowPage(MainWindow window, string navItem)
    {
        window.FindControl<ListBox>("NavList")!.SelectedItem = window.FindControl<ListBoxItem>(navItem);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertSummaryUnderTheButtons(Grid header, Button rules, TextBlock summary)
    {
        Assert.True(rules.IsVisible);
        Assert.Same(header, summary.Parent);
        Assert.Equal(2, Grid.GetColumnSpan(summary));
        Assert.Equal(header.Bounds.Width, summary.Bounds.Width, 1);
        Point rulesBottom = rules.TranslatePoint(new Point(0, rules.Bounds.Height), header)!.Value;
        Point summaryTop = summary.TranslatePoint(new Point(0, 0), header)!.Value;
        Assert.True(summaryTop.Y >= rulesBottom.Y, $"The summary starts at {summaryTop.Y} px, above the buttons' foot at {rulesBottom.Y} px.");
    }

    private const string CatalogueJson = """
        {
          "syncToken": "1",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "34.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "8.8.8.0/24", "service": "Google Cloud", "scope": "europe-west1"}
          ]
        }
        """;
}
