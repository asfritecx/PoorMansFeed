using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>A checked Google Cloud page over a stub catalogue, for the tests that need its Write feed on.</summary>
internal sealed class CheckedGooglePage : IDisposable
{
    private const string CatalogueJson = """
        {
          "syncToken": "1",
          "creationTime": "2026-01-15T00:00:00Z",
          "prefixes": [
            {"ipv4Prefix": "34.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"},
            {"ipv4Prefix": "35.1.0.0/16", "service": "Google Cloud", "scope": "us-central1"}
          ]
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-refresh-excl", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;

    private CheckedGooglePage(IWorkGate gate)
    {
        Directory.CreateDirectory(_folder);
        _http = new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CatalogueJson, Encoding.UTF8, "application/json"),
        }));
        IpInfo.OverlapFunc = blocks =>
        {
            Overlaps++;
            return new OverlapResult(OverlapOutcome.Found,
                blocks.Select(block => new NetworkOverlap(block,
                    [new OverlapGroup("US", "United States", "AS15169", "Google LLC", "google.com", 1)])).ToList(),
                IpInfo.Dataset);
        };
        IpInfo.Records = Records;
        var feeds = new GoogleCloudFeedService(IpInfo, _http, new CoreOptions(), new FakeTimeProvider(), null, _folder);
        Page = new GoogleCloudFeedViewModel(feeds, IpInfo, new FakeUi(), gate, _folder);
    }

    public FakeIpInfoService IpInfo { get; } = new()
    {
        Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 3, "abc", DateTimeOffset.UnixEpoch, true),
    };

    public RecordingRecords Records { get; } = new() { Locks = FeedRecords.None };

    public GoogleCloudFeedViewModel Page { get; }

    public int Overlaps { get; private set; }

    public static async Task<CheckedGooglePage> CreateAsync(IWorkGate gate, bool check = true)
    {
        var made = new CheckedGooglePage(gate);
        if (check)
        {
            await made.Page.ActivateAsync();
            made.Page.SelectedRegion = made.Page.Regions.Single(region => region.Scope.Id == "us-central1");
            await made.Page.CheckCommand.ExecuteAsync(null);
            Assert.True(made.Page.HasRows);
        }

        return made;
    }

    public void Dispose()
    {
        Page.Dispose();
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
}

/// <summary>Each page refuses its writes and source checks during a refresh and marks its calls in the gate.</summary>
public sealed class RefreshExclusionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly WorkGate _gate = new();
    private readonly FakeUi _ui = new();

    public RefreshExclusionTests() => _gate.SetStarted(true);

    // Every IsWorking flip, with the gate's page work as the page's own listeners see it
    private static List<(bool Working, bool PageWork)> WatchWork(INotifyPropertyChanged page, Func<bool> working,
                                                                 IWorkGate gate)
    {
        var seen = new List<(bool, bool)>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsWorking")
                seen.Add((working(), gate.IsPageWorking));
        };
        return seen;
    }

    // ---- the cloud page ----

    [Fact]
    public async Task TheCloudPageRefusesCheckAndWriteWhileARefreshRunsAndKeepsItsTicks()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate);
        GoogleCloudFeedViewModel vm = made.Page;
        int raised = 0;
        vm.WriteFeedCommand.CanExecuteChanged += (_, _) => raised++;
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.WriteFeedCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.True(raised > 0);
        Assert.False(vm.CheckCommand.CanExecute(null));
        Assert.False(vm.WriteFeedCommand.CanExecute(null));
        Assert.True(vm.TickAllCommand.CanExecute(null));
        Assert.True(vm.UntickAllCommand.CanExecute(null));
        Assert.True(vm.OpenFeedFolderCommand.CanExecute(null));

        int overlaps = made.Overlaps;
        int records = made.Records.Requests.Count;
        await vm.CheckCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(overlaps, made.Overlaps);
        Assert.Equal(records, made.Records.Requests.Count);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.IsWorking);

        raised = 0;
        _gate.SetRefreshing(false);

        Assert.True(raised > 0);
        Assert.True(vm.CheckCommand.CanExecute(null));
        Assert.True(vm.WriteFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACloudPageCallIsPageWorkInTheGateUntilItEnds()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate);
        List<(bool, bool)> seen = WatchWork(made.Page, () => made.Page.IsWorking, _gate);

        await made.Page.CheckCommand.ExecuteAsync(null);

        Assert.Equal([(true, true), (false, false)], seen);
        Assert.False(_gate.IsPageWorking);
    }

    [Fact]
    public async Task ADisposedCloudPageReleasesItsPageWork()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate);
        bool? markedBeforeDispose = null;
        made.Page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(made.Page.IsWorking) && made.Page.IsWorking && markedBeforeDispose is null)
            {
                markedBeforeDispose = _gate.IsPageWorking;
                made.Page.Dispose();
            }
        };

        await made.Page.CheckCommand.ExecuteAsync(null);

        Assert.True(markedBeforeDispose);
        Assert.True(made.Page.IsWorking);
        Assert.False(_gate.IsPageWorking);
    }

    // ---- a cloud page's own source loads ----

    // Counts the page's loads: each one sets IsWorking for its whole run
    private static Func<int> CountLoads(CloudFeedPageViewModel page)
    {
        int loads = 0;
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(page.IsWorking) && page.IsWorking)
                loads++;
        };
        return () => loads;
    }

    private static async Task WaitUntilIdleAsync(CloudFeedPageViewModel page)
    {
        for (int i = 0; i < 500 && page.IsWorking; i++)
            await Task.Delay(10);

        Assert.False(page.IsWorking);
    }

    private sealed class CloudPages : IDisposable
    {
        private readonly ServiceTempFolder _temp = new();
        private readonly FakeTimeProvider _time = new(Now);
        private readonly HttpClient _http =
            new(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));
        private readonly FakeIpInfoService _ipinfo = new()
        {
            Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 4, "abc", Now, true),
            Records = new RecordingRecords { Locks = FeedRecords.None },
        };

        public FakeAzureFeedService AzureFeeds() =>
            new(new AzureFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder))
            {
                Cached = AzureTestData.Catalogue(),
                UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, Now),
            };

        public FakeAwsFeedService AwsFeeds() =>
            new(new AwsFeedService(_ipinfo, _http, new CoreOptions(), _time, _temp.Paths, _temp.ExportFolder))
            {
                Cached = AwsTestData.Catalogue(),
                UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, current, Now),
            };

        public AzureFeedViewModel Azure(FakeAzureFeedService feeds, IWorkGate gate) =>
            new(feeds, _ipinfo, new FakeUi(), _time, gate, _temp.ExportFolder);

        public AwsFeedViewModel Aws(FakeAwsFeedService feeds, IWorkGate gate) =>
            new(feeds, _ipinfo, new FakeUi(), _time, gate, _temp.ExportFolder);

        public void Dispose()
        {
            _http.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public async Task TheGooglePageFirstShownDuringARefreshLoadsOnceWhenItEnds()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate, check: false);
        GoogleCloudFeedViewModel vm = made.Page;
        Func<int> loads = CountLoads(vm);
        _gate.SetRefreshing(true);

        await vm.ActivateAsync();

        Assert.Equal(0, loads());
        Assert.Empty(vm.Regions);
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);
        Assert.False(vm.IsWorking);
        await vm.ActivateAsync();
        Assert.Equal(0, loads());

        _gate.SetRefreshing(false);
        await WaitUntilIdleAsync(vm);

        Assert.Equal(1, loads());
        Assert.NotEmpty(vm.Regions);
        Assert.Equal(AppText.GoogleCloudEmptyInitial, vm.EmptyText);

        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await vm.ActivateAsync();
        await WaitUntilIdleAsync(vm);
        Assert.Equal(1, loads());
    }

    [Fact]
    public async Task TheGooglePageRefusesReloadRangesWhileARefreshRuns()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate, check: false);
        GoogleCloudFeedViewModel vm = made.Page;
        await vm.ActivateAsync();
        Func<int> loads = CountLoads(vm);
        Assert.True(vm.ReloadRangesCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.False(vm.ReloadRangesCommand.CanExecute(null));
        await vm.ReloadRangesCommand.ExecuteAsync(null);
        Assert.Equal(0, loads());

        _gate.SetRefreshing(false);

        Assert.True(vm.ReloadRangesCommand.CanExecute(null));
        Assert.Equal(0, loads());
    }

    [Fact]
    public async Task AGooglePageDisposedWhileItsFirstShowWaitsNeverLoads()
    {
        using CheckedGooglePage made = await CheckedGooglePage.CreateAsync(_gate, check: false);
        Func<int> loads = CountLoads(made.Page);
        _gate.SetRefreshing(true);
        await made.Page.ActivateAsync();

        made.Page.Dispose();
        _gate.SetRefreshing(false);

        Assert.Equal(0, loads());
        Assert.False(made.Page.IsWorking);
    }

    [Fact]
    public async Task TheAzurePageFirstShownDuringARefreshLoadsOnceWhenItEnds()
    {
        using var pages = new CloudPages();
        FakeAzureFeedService feeds = pages.AzureFeeds();
        using AzureFeedViewModel vm = pages.Azure(feeds, _gate);
        _gate.SetRefreshing(true);

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Empty(feeds.Calls);
        Assert.Equal(0, feeds.LoadSavedCalls);
        Assert.Equal(0, feeds.CheckCalls);
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);
        Assert.False(vm.IsWorking);

        _gate.SetRefreshing(false);
        await WaitUntilIdleAsync(vm);

        Assert.Equal(["cache", "update"], feeds.Calls);
        Assert.NotEmpty(vm.Services);

        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await vm.ActivateAsync();
        await WaitUntilIdleAsync(vm);
        Assert.Equal(["cache", "update"], feeds.Calls);
    }

    [Fact]
    public async Task TheAzurePageRefusesCheckForUpdatesWhileARefreshRuns()
    {
        using var pages = new CloudPages();
        FakeAzureFeedService feeds = pages.AzureFeeds();
        using AzureFeedViewModel vm = pages.Azure(feeds, _gate);
        await vm.ActivateAsync();
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(1, feeds.UpdateCalls);

        _gate.SetRefreshing(false);

        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal(1, feeds.UpdateCalls);
    }

    [Fact]
    public async Task AnAzurePageDisposedWhileItsFirstShowWaitsNeverLoads()
    {
        using var pages = new CloudPages();
        FakeAzureFeedService feeds = pages.AzureFeeds();
        AzureFeedViewModel vm = pages.Azure(feeds, _gate);
        _gate.SetRefreshing(true);
        await vm.ActivateAsync();

        vm.Dispose();
        _gate.SetRefreshing(false);

        Assert.Empty(feeds.Calls);
        Assert.Equal(0, feeds.LoadSavedCalls);
    }

    [Fact]
    public async Task TheAwsPageFirstShownDuringARefreshLoadsOnceWhenItEnds()
    {
        using var pages = new CloudPages();
        FakeAwsFeedService feeds = pages.AwsFeeds();
        using AwsFeedViewModel vm = pages.Aws(feeds, _gate);
        _gate.SetRefreshing(true);

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Empty(feeds.Calls);
        Assert.Equal(0, feeds.LoadSavedCalls);
        Assert.Equal(0, feeds.CheckCalls);
        Assert.Equal(AppText.CloudWaitsForRefresh, vm.EmptyText);
        Assert.False(vm.IsWorking);

        _gate.SetRefreshing(false);
        await WaitUntilIdleAsync(vm);

        Assert.Equal(["cache", "update"], feeds.Calls);
        Assert.NotEmpty(vm.Services);

        _gate.SetRefreshing(true);
        _gate.SetRefreshing(false);
        await vm.ActivateAsync();
        await WaitUntilIdleAsync(vm);
        Assert.Equal(["cache", "update"], feeds.Calls);
    }

    [Fact]
    public async Task TheAwsPageRefusesCheckForUpdatesWhileARefreshRuns()
    {
        using var pages = new CloudPages();
        FakeAwsFeedService feeds = pages.AwsFeeds();
        using AwsFeedViewModel vm = pages.Aws(feeds, _gate);
        await vm.ActivateAsync();
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(1, feeds.UpdateCalls);

        _gate.SetRefreshing(false);

        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));
        Assert.Equal(1, feeds.UpdateCalls);
    }

    [Fact]
    public async Task AnAwsPageDisposedWhileItsFirstShowWaitsNeverLoads()
    {
        using var pages = new CloudPages();
        FakeAwsFeedService feeds = pages.AwsFeeds();
        AwsFeedViewModel vm = pages.Aws(feeds, _gate);
        _gate.SetRefreshing(true);
        await vm.ActivateAsync();

        vm.Dispose();
        _gate.SetRefreshing(false);

        Assert.Empty(feeds.Calls);
        Assert.Equal(0, feeds.LoadSavedCalls);
    }

    // ---- the AbuseIPDB page ----

    private static AbuseListStatus WithNew(int newCount) =>
        new AbuseListStatus(AbuseListState.Usable, 120, 4, null, null, new AbuseRateLimit(5, 3, Now.AddHours(16)), 2, null)
            with { NewCount = newCount };

    private static AbuseDownloadResult Downloaded() => new(AbuseIpDbFailure.None, true, 10_000, 0, 50, 3, 0, Now, null);

    private static AbuseIpDbViewModel NewAbusePage(FakeAbuseIpDbService abuse, IWorkGate gate, FakeUi ui)
    {
        var vm = new AbuseIpDbViewModel(abuse, new FakeIpInfoService(), ui, gate, null, new FakeTimeProvider(Now),
                                        Path.Combine(Path.GetTempPath(), "ipinfo-refresh-excl-abuse"));
        vm.ProgressFactory = handler => new ImmediateProgress<GroupingProgress>(handler);
        return vm;
    }

    [Fact]
    public async Task TheAbuseIpDbPageRefusesItsWritesWhileARefreshRunsAndKeepsNewAddresses()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.StatusResults.Enqueue(WithNew(2));
        using AbuseIpDbViewModel vm = NewAbusePage(abuse, _gate, _ui);
        await vm.ActivateAsync();
        IAsyncRelayCommand[] writes = [vm.DownloadCommand, vm.ClearListCommand, vm.WriteFeedCommand, vm.EmptyFeedCommand];
        Assert.All(writes, command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.All(writes, command => Assert.False(command.CanExecute(null)));
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
        int calls = abuse.Calls.Count;
        foreach (IAsyncRelayCommand command in writes)
            await command.ExecuteAsync(null);
        Assert.Equal(calls, abuse.Calls.Count);
        Assert.Empty(_ui.Confirms);
        Assert.False(vm.IsWorking);

        _gate.SetRefreshing(false);

        Assert.All(writes, command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task AnAbuseIpDbCallIsPageWorkInTheGateUntilItEnds()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        using AbuseIpDbViewModel vm = NewAbusePage(abuse, _gate, _ui);
        List<(bool, bool)> seen = WatchWork(vm, () => vm.IsWorking, _gate);

        Task download = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.True(_gate.IsPageWorking);
        held.SetResult();
        await download;

        Assert.Equal([(true, true), (false, false)], seen);
        Assert.False(_gate.IsPageWorking);
    }

    [Fact]
    public async Task ADisposedAbuseIpDbPageReleasesItsPageWork()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        AbuseIpDbViewModel vm = NewAbusePage(abuse, _gate, _ui);

        Task download = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(_gate.IsPageWorking);
        vm.Dispose();
        Assert.False(_gate.IsPageWorking);
        held.TrySetResult();
        await download;
        Assert.False(_gate.IsPageWorking);
    }

    // ---- the Saved feeds and Change history pages ----

    private static readonly SavedFeed Asn = SavedFeedsTextTests.Feed("AS64500", FeedKind.Asn);

    private static async Task<SavedFeedsViewModel> NewSavedFeedsPageAsync(FakeSavedFeedsService feeds, IWorkGate gate,
                                                                          FakeUi ui)
    {
        feeds.Saved = new SavedFeedsRead(true, [Asn]);
        var vm = new SavedFeedsViewModel(feeds, ui, gate, new FakeTimeProvider(Now));
        await vm.ActivateAsync();
        return vm;
    }

    [Fact]
    public async Task TheSavedFeedsPageRefusesRetireRemoveAndClearWhileARefreshRunsAndKeepsSearch()
    {
        var feeds = new FakeSavedFeedsService();
        using SavedFeedsViewModel vm = await NewSavedFeedsPageAsync(feeds, _gate, _ui);
        SavedFeedRow row = Assert.Single(vm.Feeds);
        Assert.True(vm.RetireCommand.CanExecute(row));
        Assert.True(vm.RemoveCommand.CanExecute(row));
        Assert.True(vm.ClearHistoryCommand.CanExecute(null));

        _gate.SetRefreshing(true);

        Assert.False(vm.RetireCommand.CanExecute(row));
        Assert.False(vm.RemoveCommand.CanExecute(row));
        Assert.False(vm.ClearHistoryCommand.CanExecute(null));
        Assert.True(vm.SearchCommand.CanExecute(null));
        await vm.RetireCommand.ExecuteAsync(row);
        await vm.RemoveCommand.ExecuteAsync(row);
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Empty(feeds.Retires);
        Assert.Empty(feeds.Removes);
        Assert.Equal(0, feeds.Clears);
        Assert.Empty(_ui.Confirms);
        int queries = feeds.Queries.Count;
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(queries + 1, feeds.Queries.Count);

        _gate.SetRefreshing(false);

        Assert.True(vm.RetireCommand.CanExecute(row));
        Assert.True(vm.RemoveCommand.CanExecute(row));
        Assert.True(vm.ClearHistoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASavedFeedsCallIsPageWorkInTheGateUntilItEnds()
    {
        var feeds = new FakeSavedFeedsService();
        using SavedFeedsViewModel vm = await NewSavedFeedsPageAsync(feeds, _gate, _ui);
        var held = new TaskCompletionSource();
        feeds.ClearGate = held;
        List<(bool, bool)> seen = WatchWork(vm, () => vm.IsWorking, _gate);

        Task clear = vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.True(_gate.IsPageWorking);
        held.SetResult();
        await clear;

        Assert.Equal([(true, true), (false, false)], seen);
        Assert.False(_gate.IsPageWorking);
    }

    [Fact]
    public async Task ADisposedSavedFeedsPageReleasesItsPageWork()
    {
        var feeds = new FakeSavedFeedsService();
        SavedFeedsViewModel vm = await NewSavedFeedsPageAsync(feeds, _gate, _ui);
        var held = new TaskCompletionSource();
        feeds.ClearGate = held;

        Task clear = vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.True(_gate.IsPageWorking);
        vm.Dispose();
        Assert.False(_gate.IsPageWorking);
        held.TrySetResult();
        await clear;
        Assert.False(_gate.IsPageWorking);
    }

    // ---- the sweep ----

    // Settings holds the gate itself for each of its runs, and Cloud Address Lookup only reads
    private static readonly string[] PagesOutsideTheSweep = ["SettingsPageViewModel.cs", "CloudAddressSearchViewModel.cs"];

    private static List<string> PagesThatDontMarkTheirCalls(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !PagesOutsideTheSweep.Contains(Path.GetFileName(path)))
            .Where(path =>
            {
                string code = CodeOnly(File.ReadAllText(path));
                return Regex.IsMatch(code, @"\b(bool|Boolean)\??\s+IsWorking\b")
                    && !Regex.IsMatch(code, @"\bEnterPageWork\s*\(");
            })
            .Select(Path.GetFileName)
            .Select(name => name!)
            .ToList();

    // Verbatim and interpolated strings aren't read exactly, which can only hide a call, never invent one
    private static string CodeOnly(string source) =>
        Regex.Replace(source, @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])'", " ",
                      RegexOptions.Singleline);

    private static string PagesFolder([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "src", "PoorMansFeed"));

    [Fact]
    public void EveryPageWithIsWorkingMarksItsCallsInTheGate()
    {
        string folder = PagesFolder();
        Assert.True(File.Exists(Path.Combine(folder, "SavedFeedsViewModel.cs")));

        Assert.Empty(PagesThatDontMarkTheirCalls(folder));
    }

    [Fact]
    public void TheSweepFindsAPageThatForgetsToMarkItsCalls()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ipinfo-refresh-sweep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = File.ReadAllText(Path.Combine(PagesFolder(), "SavedFeedsViewModel.cs"));
            File.WriteAllText(Path.Combine(folder, "SavedFeedsViewModel.cs"),
                source.Replace("EnterPageWork", "EnterOtherWork", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(folder, "AbuseIpDbViewModel.cs"),
                File.ReadAllText(Path.Combine(PagesFolder(), "AbuseIpDbViewModel.cs")));

            Assert.Equal(["SavedFeedsViewModel.cs"], PagesThatDontMarkTheirCalls(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheSweepFindsAPageThatNamesTheCallOnlyInACommentOrAString()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ipinfo-refresh-sweep", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "LineComment.cs"),
                "class A { public bool IsWorking => false; // EnterPageWork(\n }");
            File.WriteAllText(Path.Combine(folder, "BlockComment.cs"),
                "class B { public bool? IsWorking => null; /* _gate.EnterPageWork() */ }");
            File.WriteAllText(Path.Combine(folder, "InAString.cs"),
                "class C { public Boolean IsWorking => false; string s = \"EnterPageWork(\"; }");
            File.WriteAllText(Path.Combine(folder, "Marks.cs"),
                "class D { public bool IsWorking => false; void M() => _gate.EnterPageWork(); }");
            File.WriteAllText(Path.Combine(folder, "OnlyNamesIt.cs"),
                "class E { public bool IsWorking => false; // calls EnterPageWork\n void M() => Use(EnterPageWork); }");

            Assert.Equal(["BlockComment.cs", "InAString.cs", "LineComment.cs", "OnlyNamesIt.cs"],
                         PagesThatDontMarkTheirCalls(folder).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
