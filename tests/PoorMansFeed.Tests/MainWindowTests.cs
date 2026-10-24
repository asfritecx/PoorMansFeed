using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.GoogleCloud;
using GraphiteUi;

namespace PoorMansFeed.Tests;

/// <summary>The real main window, driven headlessly through the fake core and fake UI services.</summary>
public sealed class MainWindowTests
{
    private static readonly DatasetInfo SampleDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true);

    // ----- helpers -----

    private static FakeIpInfoService WithDataset() => new()
    {
        Dataset = SampleDataset,
        HasToken = true,
        StartupResult = new StartupResult(StartupFailure.None, SampleDataset, true),
    };

    // Progress is delivered inline, so a report has taken effect by the time the test asserts on it
    private static MainViewModel NewViewModel(FakeIpInfoService service, FakeUi ui) =>
        new(service, ui)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };

    // The dataset card's two progress bars: the determinate one and the indeterminate one
    private static (ProgressBar Determinate, ProgressBar Indeterminate) ProgressBars(Border dataset)
    {
        List<ProgressBar> bars = dataset.GetVisualDescendants().OfType<ProgressBar>().ToList();
        return (bars.Single(bar => !bar.IsIndeterminate), bars.Single(bar => bar.IsIndeterminate));
    }

    // Starts the view model and waits for its background check, as the window does on open
    private static async Task<MainViewModel> StartAsync(FakeIpInfoService service)
    {
        var vm = NewViewModel(service, new FakeUi());
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return vm;
    }

    // The same, with the AbuseIPDB check on the scripted service
    private static async Task<MainViewModel> StartAsync(FakeIpInfoService service, FakeAbuseIpDbService abuse)
    {
        var vm = new MainViewModel(service, new FakeUi(), abuse: abuse)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return vm;
    }

    private static NetworkRecord Row(int index) =>
        new($"10.{index / 256}.{index % 256}.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static SearchResult ManyRows(int count) => new(
        SearchOutcome.Found,
        new SearchQuery(SearchKind.Asn, "AS15169", 15169, null),
        Enumerable.Range(0, count).Select(i => Row(i)).ToList(),
        count);

    // Runs queued UI work and one layout pass so Bounds and visuals reflect the latest state
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    // The Azure page's view model on a scripted service: the saved file is the small test catalogue and the update check says "up to date"
    // Work started by a handler finishes on later dispatcher turns; the wait is bounded so a regression fails rather than hangs
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }

    private static AzureFeedViewModel NewAzureViewModel(FakeIpInfoService ipinfo, out FakeAzureFeedService feeds)
    {
        var http = new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));
        var real = new AzureFeedService(
            ipinfo, http, new CoreOptions(), TimeProvider.System, null, Path.Combine(Path.GetTempPath(), "ipinfo-window-azure"));
        feeds = new FakeAzureFeedService(real)
        {
            Cached = AzureTestData.Catalogue(),
            UpdateFunc = current => new AzureLoadResult(AzureUpdateStatus.UpToDate, AzureSourceFailure.None, current, DateTimeOffset.UtcNow),
        };
        ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS8075", "Microsoft Corporation", "microsoft.com", 1)])).ToList(),
            SampleDataset);
        return new AzureFeedViewModel(feeds, ipinfo, new FakeUi(), TimeProvider.System, new WorkGate());
    }

    // The AWS page's view model on a scripted service: the saved file is the small test catalogue and the update check says "up to date"
    private static AwsFeedViewModel NewAwsViewModel(FakeIpInfoService ipinfo, out FakeAwsFeedService feeds)
    {
        var http = new HttpClient(new StubHttpHandler((_, _) => throw new InvalidOperationException("no request expected")));
        var real = new AwsFeedService(
            ipinfo, http, new CoreOptions(), TimeProvider.System, null, Path.Combine(Path.GetTempPath(), "ipinfo-window-aws"));
        feeds = new FakeAwsFeedService(real)
        {
            Cached = AwsTestData.Catalogue(),
            UpdateFunc = current => new AwsLoadResult(AwsUpdateStatus.UpToDate, AwsSourceFailure.None, current, DateTimeOffset.UtcNow),
        };
        ipinfo.OverlapFunc = blocks => new OverlapResult(
            OverlapOutcome.Found,
            blocks.Select(block => new NetworkOverlap(block, [new OverlapGroup("US", "United States", "AS16509", "Amazon.com, Inc.", "amazon.com", 1)])).ToList(),
            SampleDataset);
        return new AwsFeedViewModel(feeds, ipinfo, new FakeUi(), TimeProvider.System, new WorkGate());
    }

    private static CloudRangesPanel PanelOf(MainWindow window, object viewModel) =>
        window.GetVisualDescendants().OfType<CloudRangesPanel>().Single(panel => ReferenceEquals(panel.DataContext, viewModel));

    // Selects a nav item, as a click or the arrow keys do
    private static void Navigate(MainWindow window, string item)
    {
        window.FindControl<ListBox>("NavList")!.SelectedItem = window.FindControl<ListBoxItem>(item);
        Settle(window);
    }

    private static void Resize(MainWindow window, double height)
    {
        window.Height = height;
        Settle(window);
    }

    // The list card of a page, found by the page so it works with or without a view model
    private static Border ListCard(MainWindow window, string page) => page == "SettingsPage"
        ? window.FindControl<Border>("ExceptionsListCard")!
        : page == "LookupPage"
        ? window.FindControl<Border>("ResultsCard")!
        : page == "AbuseIpDbPage"
        ? window.FindControl<Border>("AbuseNetworksCard")!
        : window.FindControl<Control>(page)!.GetVisualDescendants().OfType<CloudRangesPanel>().Single()
            .FindControl<Border>("CloudRangesCard")!;

    // The page fills the window when its content is exactly the viewport: nothing scrolls and nothing is left below
    private static void AssertPageFills(ScrollViewer page, string when)
    {
        var content = (Control)page.Content!;
        Assert.True(content.DesiredSize.Height > 400, $"{when}: the page content was not measured.");
        Assert.True(Math.Abs(page.Extent.Height - page.Viewport.Height) < 0.5,
            $"{when}: the page is {page.Extent.Height} px high in a {page.Viewport.Height} px viewport.");
    }

    private static readonly (string Item, string Page, string Heading)[] Pages =
    [
        ("NavLookup", "LookupPage", "LookupHeading"),
        ("NavGoogleCloud", "ToolsPage", "ToolsHeading"),
        ("NavAzure", "AzurePage", "AzureHeading"),
        ("NavAws", "AwsPage", "AwsHeading"),
        ("NavAbuseIpDb", "AbuseIpDbPage", "AbuseIpDbHeading"),
        ("NavSettings", "SettingsPage", "SettingsHeading"),
    ];

    private static void AssertShowing(MainWindow window, string item)
    {
        Assert.Same(window.FindControl<ListBoxItem>(item), window.FindControl<ListBox>("NavList")!.SelectedItem);
        foreach (var (navItem, page, heading) in Pages)
        {
            Assert.Equal(navItem == item, window.FindControl<Control>(page)!.IsVisible);
            Assert.Equal(navItem == item, window.FindControl<Control>(heading)!.IsVisible);
        }

        Assert.Equal(item == "NavLookup", window.FindControl<Button>("SearchButton")!.IsDefault);
        // Add is never the default button: only the exception boxes take Enter
        Assert.False(window.FindControl<Button>("AddExceptionButton")!.IsDefault);
    }

    // The main view model and the Settings page on one gate, as the app composes them; not started yet
    // With an AbuseIPDB service, its key prompt is on the same gate, as the app composes it.
    private static (MainViewModel Vm, SettingsPageViewModel Page) NewWithSettingsPage(FakeIpInfoService service, FakeUi? ui = null,
        FakeAbuseIpDbService? abuse = null, TimeProvider? time = null)
    {
        ui ??= new FakeUi();
        var gate = new WorkGate();
        AbuseKeyPrompt? keys = abuse is null ? null : new AbuseKeyPrompt(ui, abuse, gate);
        var vm = new MainViewModel(service, ui, null, abuse, gate, keys)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        var page = new SettingsPageViewModel(service, ui, "/exports/default", gate, vm.ResetTokenAsync, abuse, keys, time);
        return (vm, page);
    }

    // The same, started as the window starts it
    private static async Task<(MainViewModel Vm, SettingsPageViewModel Page)> StartWithSettingsPageAsync(
        FakeIpInfoService service, FakeUi? ui = null, FakeAbuseIpDbService? abuse = null, TimeProvider? time = null)
    {
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service, ui, abuse, time);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return (vm, page);
    }

    // The main view model and the AbuseIPDB page on one gate, as the app composes them; not started yet
    private static (MainViewModel Vm, AbuseIpDbViewModel Page, WorkGate Gate) NewWithAbusePage(FakeIpInfoService service,
        FakeAbuseIpDbService? abuse, FakeUi? ui = null)
    {
        ui ??= new FakeUi();
        var gate = new WorkGate();
        var vm = new MainViewModel(service, ui, null, abuse, gate)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        var page = new AbuseIpDbViewModel(abuse, service, ui, gate, null, TimeProvider.System, "/exports/default");
        return (vm, page, gate);
    }

    // The same, started as the window starts it
    private static async Task<(MainViewModel Vm, AbuseIpDbViewModel Page)> StartWithAbusePageAsync(FakeIpInfoService service,
        FakeAbuseIpDbService? abuse, FakeUi? ui = null)
    {
        (MainViewModel vm, AbuseIpDbViewModel page, _) = NewWithAbusePage(service, abuse, ui);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return (vm, page);
    }

    // A usable rolling list downloaded an hour ago
    private static AbuseListStatus ListReady() => new(AbuseListState.Usable, 120, 4, DateTimeOffset.UtcNow.AddHours(-1),
        DateTimeOffset.UtcNow.AddHours(-1), new AbuseRateLimit(5, 3, DateTimeOffset.UtcNow.AddHours(16)), 2, null);

    // A grouping of count networks, AS15169 first
    private static AbuseNetworksResult Networks(int count) => new(AbuseNetworksFailure.None,
        Enumerable.Range(1, count)
            .Select(i => new AbuseNetworkGroup((uint)(15168 + i), "Example " + i, "example.com", 10, [new AbuseCountryCount("US", 10)]))
            .ToList(),
        0, [], 0, count * 10, 0, true, 30, DateTimeOffset.UtcNow.AddHours(-1), SampleDataset, 0);

    // The Look up buttons of the realised offending-network rows, in row order
    private static List<Button> LookUpButtons(MainWindow window) =>
        window.FindControl<ItemsControl>("AbuseNetworkList")!.GetVisualDescendants().OfType<Button>().ToList();

    private static readonly string[] AbuseButtons =
        ["DownloadBlacklistButton", "ClearListButton", "AbuseOpenFolderButton", "EmptyFeedButton", "AbuseWriteFeedButton"];

    private static void Click(MainWindow window, Control control)
    {
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Settle(window);
    }

    // ----- tests -----

    [AvaloniaFact]
    public async Task TheWindowOpensOnIpInfoLookupWithTheSearchBoxFocused()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            AssertShowing(window, "NavLookup");
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            Assert.True(window.CanResize);
            Assert.True(window.MinHeight > 0 && window.MinWidth > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnterInTheSearchBoxRunsTheSearch()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            Assert.Equal(AppText.SearchBoxName, AutomationProperties.GetName(window.FindControl<TextBox>("SearchBox")!));

            window.KeyTextInput("AS15169");
            Settle(window);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await (vm.SearchCommand.ExecutionTask ?? Task.CompletedTask);

            Assert.Equal("AS15169", service.LastSearchInput);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CardHeightsStayTheSameWhenResultsFillAndWhenAnUpdateRuns()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Border dataset = window.FindControl<Border>("DatasetCard")!;
            Border results = window.FindControl<Border>("ResultsCard")!;
            double datasetHeight = dataset.Bounds.Height;
            double resultsHeight = results.Bounds.Height;
            var (determinate, indeterminate) = ProgressBars(dataset);
            Assert.Equal(AppText.ProgressName, AutomationProperties.GetName(determinate));
            Assert.Equal(AppText.ProgressName, AutomationProperties.GetName(indeterminate));
            Assert.True(resultsHeight >= 216, $"The results card is {resultsHeight} px high.");

            // Idle: the determinate bar is shown at 0 and the indeterminate bar is hidden
            Assert.True(determinate.IsVisible);
            Assert.False(indeterminate.IsVisible);
            Assert.Equal(0.0, determinate.Value);

            service.SearchFunc = (_, _) => ManyRows(300);
            vm.SearchText = "AS15169";
            await vm.SearchCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal(datasetHeight, dataset.Bounds.Height);
            Assert.Equal(resultsHeight, results.Bounds.Height);

            // The fake clears UpdateGate once an update starts, so keep our own reference to release it
            var gate = new TaskCompletionSource();
            service.UpdateGate = gate;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.Equal(datasetHeight, dataset.Bounds.Height);
            Assert.Equal(resultsHeight, results.Bounds.Height);

            // Checking: only the indeterminate bar is shown
            Assert.False(determinate.IsVisible);
            Assert.True(indeterminate.IsVisible);

            // A determinate report switches the visible bar; the card keeps its height
            service.LastProgress!.Report(new UpdateProgress(UpdatePhase.Downloading, 40, 100));
            Settle(window);
            Assert.True(determinate.IsVisible);
            Assert.False(indeterminate.IsVisible);
            Assert.Equal(40.0, determinate.Value);
            Assert.Equal(datasetHeight, dataset.Bounds.Height);
            Assert.Equal(resultsHeight, results.Bounds.Height);

            gate.SetResult();
            await (vm.UpdateDatasetCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);
            Assert.Equal(datasetHeight, dataset.Bounds.Height);
            Assert.Equal(resultsHeight, results.Bounds.Height);
            Assert.True(determinate.IsVisible);
            Assert.False(indeterminate.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheResultsCardFillsTheWindowAndGrowsWithIt()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Border results = window.FindControl<Border>("ResultsCard")!;
            AssertPageFills(page, "Before a search");
            double resultsHeight = results.Bounds.Height;

            service.SearchFunc = (_, _) => ManyRows(300);
            vm.SearchText = "AS15169";
            await vm.SearchCommand.ExecuteAsync(null);
            Settle(window);
            AssertPageFills(page, "With 300 results");
            Assert.Equal(resultsHeight, results.Bounds.Height);

            // Every extra pixel of window goes to the list
            Resize(window, window.Height + 200);
            AssertPageFills(page, "In a taller window");
            Assert.Equal(resultsHeight + 200, results.Bounds.Height, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BelowTheTallestPageTheListsKeepTheirMinimumAndThePageScrolls()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Resize(window, window.MinHeight);
            var heights = new List<double>();
            foreach (var (item, pageName, _) in Pages)
            {
                Navigate(window, item);
                double height = ListCard(window, pageName).Bounds.Height;
                Assert.True(height >= 216, $"{pageName}: the list card is {height} px high.");
                Assert.True(page.Extent.Height > page.Viewport.Height, $"{pageName}: the page does not scroll.");
                heights.Add(height);
            }

            // The frame's MinHeight is set by the tallest page: there the list is exactly at its minimum
            Assert.InRange(heights.Min(), 216, 217);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShowingGoogleCloudAndReturningToLookupFocusesSearch()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Navigate(window, "NavGoogleCloud");
            AssertShowing(window, "NavGoogleCloud");
            AssertPageFills(page, "Google Cloud Lookup");
            Assert.False(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            Assert.False(vm.IsBusy);

            Navigate(window, "NavLookup");
            AssertShowing(window, "NavLookup");
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            AssertPageFills(page, "IPInfo Lookup");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShowingSettingsFillsThePageAndReturningToLookupFocusesSearch()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Navigate(window, "NavSettings");
            AssertShowing(window, "NavSettings");
            AssertPageFills(page, "Settings");
            Assert.False(window.FindControl<TextBox>("SearchBox")!.IsFocused);

            Navigate(window, "NavLookup");
            AssertShowing(window, "NavLookup");
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            AssertPageFills(page, "IPInfo Lookup");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheMenusSettingsItemSelectsTheSettingsPage()
    {
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(WithDataset());
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings);
        window.Show();
        try
        {
            Settle(window);
            Navigate(window, "NavAzure");
            window.ShowSettingsPage();
            Settle(window);
            AssertShowing(window, "NavSettings");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ASettingsSaveOnThePageRerunsTheLookupSearch()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings);
        window.Show();
        try
        {
            Settle(window);
            service.SearchFunc = (_, _) => ManyRows(3);
            vm.SearchText = "AS15169";
            await vm.SearchCommand.ExecuteAsync(null);
            Assert.Equal(1, service.SearchCalls);

            Navigate(window, "NavSettings");
            settings.Settings.AbuseIpDbKeepDays = 60;
            await settings.SaveSettingsCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => service.SearchCalls == 2);

            Assert.Equal(1, service.SaveSettingsCalls);
            Assert.Equal("AS15169", service.LastSearchInput);
            Assert.Equal(AppText.SettingsSaved, settings.SettingsStatusText);

            // The line is trimmed to one row, so its tooltip holds the whole text
            settings.Settings.ExportFolder = "/exports/moved";
            await settings.SaveSettingsCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => service.SearchCalls == 3);
            Settle(window);
            Assert.Equal(AppText.SettingsSavedFolderChanged, settings.SettingsStatusText);
            Assert.Equal(settings.SettingsStatusText, ToolTip.GetTip(window.FindControl<TextBlock>("SettingsStatus")!));
        }
        finally
        {
            window.Close();
        }
    }

    // The macOS menu's Reset token shows the Settings page and runs the page's own reset, so its line reports the result
    [AvaloniaFact]
    public async Task TheMenusResetTokenShowsSettingsAndRunsThePagesReset()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings);
        window.Show();
        try
        {
            Settle(window);
            window.ResetTokenFromMenu();
            await WaitUntilAsync(() => settings.KeysStatusText.Length > 0);
            Settle(window);

            AssertShowing(window, "NavSettings");
            Assert.Equal(1, service.ClearTokenCalls);
            Assert.Equal(1, service.SaveTokenCalls);
            Assert.Equal(AppText.TokenSaved, window.FindControl<TextBlock>("KeysStatus")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheNavItemsAreNamedAfterTheirPagesAndAClickShowsThePage()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Assert.Equal(AppText.NavName, AutomationProperties.GetName(window.FindControl<ListBox>("NavList")!));
            Assert.Equal(AppText.Title, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavLookup")!));
            Assert.Equal(AppText.GoogleCloudTitle, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavGoogleCloud")!));
            Assert.Equal(AppText.AzureTitle, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavAzure")!));
            Assert.Equal(AppText.AwsTitle, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavAws")!));
            Assert.Equal(AppText.AbuseIpDbTitle, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavAbuseIpDb")!));
            Assert.Equal(AppText.SettingsTitle, AutomationProperties.GetName(window.FindControl<ListBoxItem>("NavSettings")!));

            ListBoxItem azure = window.FindControl<ListBoxItem>("NavAzure")!;
            Point centre = azure.TranslatePoint(new Point(azure.Bounds.Width / 2, azure.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);
            AssertShowing(window, "NavAzure");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AzureLookupOpensFromTheNavPaneLoadsOnceAndFillsTheWindow()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out FakeAzureFeedService feeds);
        var window = new MainWindow();
        window.AttachViewModel(vm, azure: azure);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Assert.Equal(0, feeds.UpdateCalls);

            Navigate(window, "NavAzure");
            AssertShowing(window, "NavAzure");
            Assert.Equal(1, feeds.UpdateCalls);
            AssertPageFills(page, "Azure Lookup");
            ComboBox serviceBox = window.FindControl<ComboBox>("ServiceBox")!;
            Assert.Equal(4, serviceBox.ItemCount);
            Assert.Equal("AzureCloud", ((AzureServiceOption)serviceBox.SelectedItem!).BaseName);
            Assert.Equal("AzureCloud", ((AzureTagOption)window.FindControl<ComboBox>("AzureRegionBox")!.SelectedItem!).TagName);
            Assert.Equal(azure.StatusText, window.FindControl<TextBlock>("AzureStatus")!.Text);

            Navigate(window, "NavLookup");
            AssertShowing(window, "NavLookup");

            Navigate(window, "NavAzure");
            Assert.Equal(1, feeds.UpdateCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AzureLookupShowsCheckedRangesWithoutChangingThePageHeight()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out _);
        var window = new MainWindow();
        window.AttachViewModel(vm, azure: azure);
        window.Show();
        try
        {
            Navigate(window, "NavAzure");
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            CloudRangesPanel panel = PanelOf(window, azure);
            Border card = panel.FindControl<Border>("CloudRangesCard")!;
            double before = page.Extent.Height;
            double cardHeight = card.Bounds.Height;
            Assert.Equal(azure.EmptyText, panel.FindControl<TextBlock>("CloudEmpty")!.Text);
            Assert.False(panel.FindControl<Button>("WriteFeedButton")!.Command!.CanExecute(null));

            azure.SelectedService = azure.Services.Single(option => option.BaseName == "Storage");
            azure.SelectedRegion = azure.Regions.Single(option => option.TagName == "Storage.SoutheastAsia");
            await azure.CheckCommand.ExecuteAsync(null);
            Settle(window);

            Assert.Equal(3, panel.FindControl<ItemsControl>("CloudRangeList")!.ItemCount);
            Assert.True(panel.FindControl<Button>("WriteFeedButton")!.Command!.CanExecute(null));
            Assert.Same(azure.WriteFeedCommand, panel.FindControl<Button>("WriteFeedButton")!.Command);
            Assert.Equal(before, page.Extent.Height);
            Assert.Equal(cardHeight, card.Bounds.Height);
            AssertPageFills(page, "Azure ranges listed");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAzurePickersFollowTheViewModelWhenTheirListsAreReplaced()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out FakeAzureFeedService feeds);
        var window = new MainWindow();
        window.AttachViewModel(vm, azure: azure);
        window.Show();
        try
        {
            Navigate(window, "NavAzure");
            ComboBox serviceBox = window.FindControl<ComboBox>("ServiceBox")!;
            ComboBox regionBox = window.FindControl<ComboBox>("AzureRegionBox")!;

            // The operator picks a service in the box: the view model follows and the region box shows its first region
            serviceBox.SelectedItem = azure.Services.Single(option => option.BaseName == "Twin");
            Settle(window);
            Assert.Equal("Twin", azure.SelectedService!.BaseName);
            Assert.Equal("Twin.One", azure.SelectedRegion!.TagName);
            Assert.Equal("Twin.One", ((AzureTagOption)regionBox.SelectedItem!).TagName);

            // The operator picks a region in the box
            regionBox.SelectedItem = azure.Regions.Single(option => option.TagName == "Twin.Two");
            Settle(window);
            Assert.Equal("Twin.Two", azure.SelectedRegion!.TagName);

            // A newer file replaces both lists: the tag stays picked in the view model and in both boxes
            feeds.UpdateFunc = _ => new AzureLoadResult(
                AzureUpdateStatus.Updated, AzureSourceFailure.None,
                AzureTestData.Catalogue(change: 423, date: "20261019", folder: "bbbb"), DateTimeOffset.UtcNow);
            await azure.CheckForUpdatesCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal("Twin", azure.SelectedService!.BaseName);
            Assert.Equal("Twin.Two", azure.SelectedRegion!.TagName);
            Assert.Same(azure.SelectedService, serviceBox.SelectedItem);
            Assert.Same(azure.SelectedRegion, regionBox.SelectedItem);
            Assert.StartsWith("Service tags of 19 October 2026", azure.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAzureButtonsHaveNamesAndTooltipsAndRunTheViewModelCommands()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out _);
        var window = new MainWindow();
        window.AttachViewModel(vm, azure: azure);
        window.Show();
        try
        {
            Settle(window);
            Button update = window.FindControl<Button>("AzureCheckForUpdatesButton")!;
            Assert.Equal(AppText.CheckForUpdates, AutomationProperties.GetName(update));
            Assert.Equal(AppText.CheckForUpdatesTooltip, ToolTip.GetTip(update));
            Assert.Same(azure.CheckForUpdatesCommand, update.Command);
            Assert.Same(azure.CheckCommand, window.FindControl<Button>("AzureCheckRangesButton")!.Command);
            Button folder = PanelOf(window, azure).FindControl<Button>("OpenFeedFolderButton")!;
            Assert.Equal(AppText.OpenFolderName, AutomationProperties.GetName(folder));
            Assert.NotNull(ToolTip.GetTip(folder));
            Assert.Same(azure.OpenFeedFolderCommand, folder.Command);
        }
        finally
        {
            window.Close();
        }
    }

    // The Azure page writes with a readable list, so its action line holds its own write result
    private static async Task<string> WriteAzureAsync(AzureFeedViewModel azure)
    {
        await azure.ActivateAsync();
        azure.SelectedService = azure.Services.Single(option => option.BaseName == "Storage");
        azure.SelectedRegion = azure.Regions.Single(option => option.TagName == "Storage.SoutheastAsia");
        await azure.CheckCommand.ExecuteAsync(null);
        await azure.WriteFeedCommand.ExecuteAsync(null);
        Assert.StartsWith("Wrote ", azure.ActionText);
        return azure.ActionText;
    }

    // The Azure page writes with the list unreadable, so its action line holds the refusal
    private static async Task RefuseAzureWriteAsync(AzureFeedViewModel azure)
    {
        await azure.ActivateAsync();
        azure.SelectedService = azure.Services.Single(option => option.BaseName == "Storage");
        azure.SelectedRegion = azure.Regions.Single(option => option.TagName == "Storage.SoutheastAsia");
        await azure.CheckCommand.ExecuteAsync(null);
        await azure.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ExceptionsUnreadable, azure.ActionText);
    }

    [AvaloniaFact]
    public async Task ACloudWriteRefusedForUnreadableExceptionsKeepsItsLineThroughASave()
    {
        var service = WithDataset();
        service.Exceptions = ExceptionList.Unreadable;
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out _);
        var window = new MainWindow();
        window.AttachViewModel(vm, azure: azure, settings: settings);
        window.Show();
        try
        {
            Navigate(window, "NavAzure");
            AssertShowing(window, "NavAzure");
            await RefuseAzureWriteAsync(azure);

            // The list is saved on the Settings page: its own line says so, and the cloud line and Lookup's are left alone
            Navigate(window, "NavSettings");
            settings.Exceptions.NetworkText = "192.0.2.0/24";
            await settings.SaveExceptionsCommand.ExecuteAsync(null);

            Assert.True(service.Exceptions.IsReadable);
            Assert.Equal(AppText.ExceptionsSaved(1), settings.ExceptionsStatusText);
            Assert.Equal("", vm.ResultsActionText);
            Assert.Equal(AppText.ExceptionsUnreadable, azure.ActionText);
            Assert.True(azure.HasActionText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AzureLookupFillsTheWindowBeforeAViewModelIsAttached()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Navigate(window, "NavAzure");

            AssertShowing(window, "NavAzure");
            AssertPageFills(window.FindControl<ScrollViewer>("PageScroll")!, "Azure Lookup without a view model");
        }
        finally
        {
            window.Close();
        }
    }

    // ----- the AbuseIPDB Blacklist page -----

    [AvaloniaFact]
    public async Task TheAbuseIpDbPageOpensFromTheNavPaneAndReadsTheListOnEveryShow()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.StatusResults.Enqueue(ListReady());
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Settle(window);
            Assert.Equal(0, abuse.StatusCalls);

            Navigate(window, "NavAbuseIpDb");
            AssertShowing(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1);
            Settle(window);
            AssertPageFills(window.FindControl<ScrollViewer>("PageScroll")!, "AbuseIPDB Blacklist");
            Assert.Equal(page.FactsHeadText, window.FindControl<TextBlock>("AbuseFacts")!.Text);
            Assert.Equal(page.FactsDetailText, window.FindControl<TextBlock>("AbuseFactsDetail")!.Text);
            Assert.Equal(page.FactsText, ToolTip.GetTip(window.FindControl<TextBlock>("AbuseFacts")!));
            Assert.Equal(AppText.AbuseGroupingBarName,
                AutomationProperties.GetName(window.FindControl<ProgressBar>("AbuseGroupingBar")!));
            Assert.Same(page.DownloadCommand, window.FindControl<Button>("DownloadBlacklistButton")!.Command);
            Assert.Same(page.ClearListCommand, window.FindControl<Button>("ClearListButton")!.Command);
            Assert.Same(page.OpenFolderCommand, window.FindControl<Button>("AbuseOpenFolderButton")!.Command);
            Assert.Same(page.EmptyFeedCommand, window.FindControl<Button>("EmptyFeedButton")!.Command);
            Assert.Same(page.WriteFeedCommand, window.FindControl<Button>("AbuseWriteFeedButton")!.Command);
            Assert.Equal(page.WriteFeedTooltip, ToolTip.GetTip(window.FindControl<Button>("AbuseWriteFeedButton")!));
            foreach (string name in AbuseButtons)
            {
                Button button = window.FindControl<Button>(name)!;
                Assert.True(button.Command!.CanExecute(null), name);
                Assert.NotNull(ToolTip.GetTip(button));
            }

            Navigate(window, "NavLookup");
            AssertShowing(window, "NavLookup");
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAbuseIpDbHeadingHasTheNewAddressesButtonBoundWithItsTooltipAndName()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.StatusResults.Enqueue(ListReady() with { NewCount = 3 });
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Settle(window);
            Button button = window.FindControl<Button>("ShowNewAddressesButton")!;
            Grid heading = window.FindControl<Grid>("AbuseIpDbHeading")!;
            Assert.Same(heading, button.GetVisualAncestors().OfType<Grid>().First());
            Assert.Equal(1, Grid.GetColumn(button));
            Assert.Same(page, heading.DataContext);
            Assert.Same(page.ShowNewAddressesCommand, button.Command);
            Assert.Equal(AppText.NewAddresses, button.Content);
            Assert.Equal(AppText.NewAddressesTooltip, ToolTip.GetTip(button));
            Assert.Equal(AppText.NewAddresses, AutomationProperties.GetName(button));

            // Off until a status names new addresses
            Assert.False(button.IsEffectivelyEnabled);
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1);
            Settle(window);
            Assert.True(button.IsVisible && heading.IsVisible);
            Assert.True(button.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheDatasetChangingReadsTheAbuseIpDbPageAgainOnlyWhileItIsShown()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1);
            Settle(window);

            // Another page on show: the new dataset is read at the next show, not now
            Navigate(window, "NavLookup");
            service.UpdateResult = new UpdateResult(UpdateFailure.None, false,
                new DatasetInfo(new DateOnly(2026, 10, 9), 42, "newer", DateTimeOffset.UtcNow, true));
            await vm.UpdateDatasetCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal(1, abuse.StatusCalls);

            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 2);
            Settle(window);

            // The page on show: the new dataset is read at once
            service.UpdateResult = new UpdateResult(UpdateFailure.None, false,
                new DatasetInfo(new DateOnly(2026, 10, 10), 43, "newest", DateTimeOffset.UtcNow, true));
            await vm.UpdateDatasetCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => abuse.StatusCalls == 3);
        }
        finally
        {
            window.Close();
        }
    }

    // A launch raises DatasetChanged with the gate started, before Started. With Lookup on show nothing of the list is
    // read, so a launch never starts a long grouping while the user searches.
    [AvaloniaFact]
    public async Task ALaunchWithADatasetReadsNothingForTheAbuseIpDbPageWhileLookupIsShown()
    {
        var abuse = new FakeAbuseIpDbService();
        (MainViewModel vm, AbuseIpDbViewModel page, WorkGate gate) = NewWithAbusePage(WithDataset(), abuse);
        var raised = new List<(bool GateStarted, bool StartedRaised)>();
        bool startedRaised = false;
        vm.Started += (_, _) => startedRaised = true;
        vm.DatasetChanged += (_, _) => raised.Add((gate.IsStarted, startedRaised));
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Settle(window);
            await vm.InitializeAsync();
            if (vm.PendingCheck is { } check)
            {
                await check;
            }

            Settle(window);
            Assert.Equal([(true, false)], raised);
            Assert.True(startedRaised);
            Assert.DoesNotContain(nameof(IAbuseIpDbService.GetStatusAsync), abuse.Calls);
            Assert.DoesNotContain(nameof(IAbuseIpDbService.FindOffendingNetworksAsync), abuse.Calls);

            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARightClickOnAnOffendingNetworkOpensItsMenuAndItsItemOpensTheAsPage()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var abuse = new FakeAbuseIpDbService { NetworksResult = Networks(3) };
        abuse.StatusResults.Enqueue(ListReady());
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await page.PendingGrouping!;
            Settle(window);

            // The second row, so the first row's AS can't pass by accident; the right-click lands in the gap between
            // its first two columns, where no text is
            ItemsControl list = window.FindControl<ItemsControl>("AbuseNetworkList")!;
            Border row = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("row"))
                .ElementAt(1);
            Assert.Same(page.Rows[1], row.DataContext);
            TextBlock asn = row.GetVisualDescendants().OfType<TextBlock>().First();
            Point gap = asn.TranslatePoint(new Point(asn.Bounds.Width + 6, asn.Bounds.Height / 2), window)!.Value;
            Assert.Same(row, window.InputHitTest(gap));
            // Headless drawing hit-tests a border's whole rectangle, with or without a fill, so the fill that makes
            // the gap hit-testable on a real renderer is pinned here
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);

            window.MouseDown(gap, MouseButton.Right);
            window.MouseUp(gap, MouseButton.Right);
            Settle(window);

            ContextMenu menu = row.ContextMenu!;
            Assert.True(menu.IsOpen);
            MenuItem open = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal(AppText.OpenInIpinfo, open.Header);
            // The cast parent binding resolves from the row's menu to the page's command, with the row as its parameter
            Assert.Same(page.OpenNetworkInIpinfoCommand, open.Command);
            Assert.Same(page.Rows[1], open.CommandParameter);
            Assert.True(open.IsEffectivelyEnabled);

            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => ui.OpenedPages.Count == 1);
            Settle(window);

            Assert.Equal("https://ipinfo.io/AS15170", Assert.Single(ui.OpenedPages).Uri.AbsoluteUri);
            Assert.Equal("", page.ActionText);
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LookUpOnAnOffendingNetworkSearchesItsAsnOnIpInfoLookup()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService { NetworksResult = Networks(3) };
        abuse.StatusResults.Enqueue(ListReady());
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await page.PendingGrouping!;
            Settle(window);
            Button lookUp = LookUpButtons(window).First();
            Assert.Same(page.Rows[0], lookUp.CommandParameter);
            Assert.Same(page.LookUpCommand, lookUp.Command);
            Assert.Equal("AS15169", page.Rows[0].AsnText);
            int searches = service.SearchCalls;

            Click(window, lookUp);
            await WaitUntilAsync(() => service.SearchCalls == searches + 1);
            Settle(window);

            AssertShowing(window, "NavLookup");
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
            Assert.Equal("AS15169", service.LastSearchInput);
            Assert.Equal("AS15169", vm.SearchText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAbuseIpDbPageListsNetworksAndGroupsWithoutChangingThePageHeight()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        abuse.StatusResults.Enqueue(ListReady());
        abuse.StatusResults.Enqueue(ListReady());
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await page.PendingGrouping!;
            Settle(window);
            ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScroll")!;
            Border card = window.FindControl<Border>("AbuseNetworksCard")!;
            ProgressBar bar = window.FindControl<ProgressBar>("AbuseGroupingBar")!;
            double before = scroll.Extent.Height;
            double cardHeight = card.Bounds.Height;
            Assert.False(page.HasRows);
            Assert.Equal(page.EmptyText, window.FindControl<TextBlock>("AbuseNetworksEmpty")!.Text);
            Assert.True(window.FindControl<TextBlock>("AbuseNetworksEmpty")!.IsVisible);
            Assert.Contains("placeholder", bar.Classes);
            AssertPageFills(scroll, "AbuseIPDB Blacklist, no networks");

            // A new dataset makes the grouping stale, so the next show groups again; it is held a quarter of the way
            var held = new TaskCompletionSource();
            abuse.NetworksGate = held;
            abuse.NetworksProgress.Add(new GroupingProgress(25, 100));
            abuse.NetworksResult = Networks(300);
            service.Dataset = new DatasetInfo(new DateOnly(2026, 10, 9), 7654321, "def", DateTimeOffset.UtcNow, true);
            Navigate(window, "NavLookup");
            Navigate(window, "NavAbuseIpDb");
            Settle(window);

            Assert.True(page.IsGrouping);
            Assert.DoesNotContain("placeholder", bar.Classes);
            Assert.Equal(0.25, bar.Value, 3);
            Assert.Equal(before, scroll.Extent.Height);
            Assert.Equal(cardHeight, card.Bounds.Height);

            held.SetResult();
            await page.PendingGrouping!;
            Settle(window);

            Assert.Equal(300, window.FindControl<ItemsControl>("AbuseNetworkList")!.ItemCount);
            Assert.False(window.FindControl<TextBlock>("AbuseNetworksEmpty")!.IsVisible);
            Assert.Contains("placeholder", bar.Classes);
            Assert.Equal(before, scroll.Extent.Height);
            Assert.Equal(cardHeight, card.Bounds.Height);
            AssertPageFills(scroll, "AbuseIPDB Blacklist, 300 networks");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAbuseIpDbPageFillsTheWindowBeforeAViewModelIsAttached()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Navigate(window, "NavAbuseIpDb");

            AssertShowing(window, "NavAbuseIpDb");
            AssertPageFills(window.FindControl<ScrollViewer>("PageScroll")!, "AbuseIPDB Blacklist without a view model");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WithNoAbuseIpDbServiceThePageSaysSoAndEveryCommandIsOff()
    {
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), null);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");

            Assert.Equal(AppText.AbuseKeysUnavailable, window.FindControl<TextBlock>("AbuseFacts")!.Text);
            foreach (string name in AbuseButtons)
            {
                Button button = window.FindControl<Button>(name)!;
                Assert.False(button.Command!.CanExecute(null), name);
                Assert.False(button.IsEffectivelyEnabled, name);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AwsLookupOpensFromTheNavPaneLoadsOnceAndFillsTheWindow()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AwsFeedViewModel aws = NewAwsViewModel(service, out FakeAwsFeedService feeds);
        var window = new MainWindow();
        window.AttachViewModel(vm, aws: aws);
        window.Show();
        try
        {
            Settle(window);
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            Assert.Equal(0, feeds.UpdateCalls);

            Navigate(window, "NavAws");
            AssertShowing(window, "NavAws");
            Assert.Equal(1, feeds.UpdateCalls);
            AssertPageFills(page, "AWS Lookup");
            ComboBox serviceBox = window.FindControl<ComboBox>("AwsServiceBox")!;
            Assert.Equal(4, serviceBox.ItemCount);
            Assert.Equal("EC2", ((AwsServiceOption)serviceBox.SelectedItem!).Name);
            Assert.Null(((AwsRegionOption)window.FindControl<ComboBox>("AwsRegionBox")!.SelectedItem!).Region);
            Assert.Equal(aws.StatusText, window.FindControl<TextBlock>("AwsStatus")!.Text);

            Navigate(window, "NavLookup");
            AssertShowing(window, "NavLookup");

            Navigate(window, "NavAws");
            Assert.Equal(1, feeds.UpdateCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AwsLookupShowsCheckedRangesWithoutChangingThePageHeight()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AwsFeedViewModel aws = NewAwsViewModel(service, out _);
        var window = new MainWindow();
        window.AttachViewModel(vm, aws: aws);
        window.Show();
        try
        {
            Navigate(window, "NavAws");
            ScrollViewer page = window.FindControl<ScrollViewer>("PageScroll")!;
            CloudRangesPanel panel = PanelOf(window, aws);
            Border card = panel.FindControl<Border>("CloudRangesCard")!;
            double before = page.Extent.Height;
            double cardHeight = card.Bounds.Height;
            Assert.Equal(aws.EmptyText, panel.FindControl<TextBlock>("CloudEmpty")!.Text);
            Assert.False(panel.FindControl<Button>("WriteFeedButton")!.Command!.CanExecute(null));

            aws.SelectedRegion = aws.Regions.Single(option => option.Region == "cn-north-1");
            await aws.CheckCommand.ExecuteAsync(null);
            Settle(window);

            Assert.Equal(3, panel.FindControl<ItemsControl>("CloudRangeList")!.ItemCount);
            Assert.True(panel.FindControl<Button>("WriteFeedButton")!.Command!.CanExecute(null));
            Assert.Same(aws.WriteFeedCommand, panel.FindControl<Button>("WriteFeedButton")!.Command);
            Assert.Equal(before, page.Extent.Height);
            Assert.Equal(cardHeight, card.Bounds.Height);
            AssertPageFills(page, "AWS ranges listed");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAwsPickersFollowTheViewModelWhenTheirListsAreReplaced()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AwsFeedViewModel aws = NewAwsViewModel(service, out FakeAwsFeedService feeds);
        var window = new MainWindow();
        window.AttachViewModel(vm, aws: aws);
        window.Show();
        try
        {
            Navigate(window, "NavAws");
            ComboBox serviceBox = window.FindControl<ComboBox>("AwsServiceBox")!;
            ComboBox regionBox = window.FindControl<ComboBox>("AwsRegionBox")!;

            // The operator picks a service in the box: the view model follows and the region box shows All regions
            serviceBox.SelectedItem = aws.Services.Single(option => option.Name == "S3");
            Settle(window);
            Assert.Equal("S3", aws.SelectedService!.Name);
            Assert.Null(aws.SelectedRegion!.Region);
            Assert.Same(aws.SelectedRegion, regionBox.SelectedItem);

            // Back to EC2, then a region in the box
            serviceBox.SelectedItem = aws.Services.Single(option => option.Name == "EC2");
            Settle(window);
            regionBox.SelectedItem = aws.Regions.Single(option => option.Region == "cn-north-1");
            Settle(window);
            Assert.Equal("cn-north-1", aws.SelectedRegion!.Region);

            // A newer file replaces both lists: the pick stays in the view model and in both boxes
            feeds.UpdateFunc = _ => new AwsLoadResult(
                AwsUpdateStatus.Updated, AwsSourceFailure.None,
                AwsTestData.Catalogue(AwsTestData.RangesJson.Replace("1792136826", "1792200000")), DateTimeOffset.UtcNow);
            await aws.CheckForUpdatesCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal("EC2", aws.SelectedService!.Name);
            Assert.Equal("cn-north-1", aws.SelectedRegion!.Region);
            Assert.Same(aws.SelectedService, serviceBox.SelectedItem);
            Assert.Same(aws.SelectedRegion, regionBox.SelectedItem);
            Assert.StartsWith("AWS ranges of 16 October 2026", aws.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAwsButtonsHaveNamesAndTooltipsAndRunTheViewModelCommands()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AwsFeedViewModel aws = NewAwsViewModel(service, out _);
        var window = new MainWindow();
        window.AttachViewModel(vm, aws: aws);
        window.Show();
        try
        {
            Settle(window);
            Button update = window.FindControl<Button>("AwsCheckForUpdatesButton")!;
            Assert.Equal(AppText.CheckForUpdates, AutomationProperties.GetName(update));
            Assert.Equal(AppText.AwsCheckForUpdatesTooltip, ToolTip.GetTip(update));
            Assert.Same(aws.CheckForUpdatesCommand, update.Command);
            Assert.Same(aws.CheckCommand, window.FindControl<Button>("AwsCheckRangesButton")!.Command);
            Button folder = PanelOf(window, aws).FindControl<Button>("OpenFeedFolderButton")!;
            Assert.Equal(AppText.OpenFolderName, AutomationProperties.GetName(folder));
            Assert.NotNull(ToolTip.GetTip(folder));
            Assert.Same(aws.OpenFeedFolderCommand, folder.Command);
            Assert.Equal(AppText.ColumnService, AutomationProperties.GetName(window.FindControl<ComboBox>("AwsServiceBox")!));
            Assert.Equal(AppText.ColumnRegion, AutomationProperties.GetName(window.FindControl<ComboBox>("AwsRegionBox")!));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AwsLookupFillsTheWindowBeforeAViewModelIsAttached()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Navigate(window, "NavAws");

            AssertShowing(window, "NavAws");
            AssertPageFills(window.FindControl<ScrollViewer>("PageScroll")!, "AWS Lookup without a view model");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowDisposesEveryPage()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel settings) = await StartWithSettingsPageAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out _);
        AwsFeedViewModel aws = NewAwsViewModel(service, out _);
        var google = new GoogleCloudFeedViewModel(
            new GoogleCloudFeedService(service, new HttpClient(), new CoreOptions(), TimeProvider.System, Path.GetTempPath()),
            service, new FakeUi(), new WorkGate());
        var abuseGate = new WorkGate();
        abuseGate.SetStarted(true);
        var abuse = new AbuseIpDbViewModel(new FakeAbuseIpDbService { NetworksResult = Networks(1) }, service, new FakeUi(),
            abuseGate, null, TimeProvider.System, null);
        await abuse.ActivateAsync();
        await abuse.PendingGrouping!;
        OffendingNetworkRow row = Assert.Single(abuse.Rows);
        var window = new MainWindow();
        window.AttachViewModel(vm, google, azure, aws, settings, abuseIpDb: abuse);
        window.Show();
        Settle(window);
        Assert.All(AbusePageCommands(abuse), command => Assert.True(command.CanExecute(null)));
        Assert.True(abuse.LookUpCommand.CanExecute(row));
        settings.Settings.AbuseIpDbKeepDays = 60;
        Assert.True(settings.SaveSettingsCommand.CanExecute(null));
        Assert.True(azure.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(aws.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(google.CheckCommand.CanExecute(null));

        window.Close();

        Assert.False(azure.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(aws.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(google.CheckCommand.CanExecute(null));
        Assert.False(settings.SaveSettingsCommand.CanExecute(null));
        Assert.All(AbusePageCommands(abuse), command => Assert.False(command.CanExecute(null)));
        Assert.False(abuse.LookUpCommand.CanExecute(row));
    }

    private static IEnumerable<IAsyncRelayCommand> AbusePageCommands(AbuseIpDbViewModel page) =>
        [page.DownloadCommand, page.ClearListCommand, page.WriteFeedCommand, page.EmptyFeedCommand, page.OpenFolderCommand];

    // ----- closing while the AbuseIPDB page has work with the service -----

    // The main view model, the Settings page and the AbuseIPDB page on one gate and one window service, started
    private static async Task<(MainViewModel Vm, SettingsPageViewModel Settings, AbuseIpDbViewModel Page)> StartWithAllPagesAsync(
        FakeIpInfoService service, FakeUi ui, FakeAbuseIpDbService abuse)
    {
        var gate = new WorkGate();
        var keys = new AbuseKeyPrompt(ui, abuse, gate);
        var vm = new MainViewModel(service, ui, null, abuse, gate, keys)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        var settings = new SettingsPageViewModel(service, ui, "/exports/default", gate, vm.ResetTokenAsync, abuse, keys);
        var page = new AbuseIpDbViewModel(abuse, service, ui, gate, keys, TimeProvider.System, "/exports/default");
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return (vm, settings, page);
    }

    private static AbuseDownloadResult DownloadDone() =>
        new(AbuseIpDbFailure.None, true, 10_000, 0, 50, 3, 0, DateTimeOffset.UtcNow, null);

    [AvaloniaFact]
    public async Task ClosingDuringAnAbuseDownloadAsksFirstAndClosesOnlyOnceTheDownloadHasStopped()
    {
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        var runEndedAtClose = new List<bool>();
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            Task run = page.DownloadCommand.ExecutionTask!;
            Assert.True(page.AsksBeforeClose);
            window.Closing += (_, _) => runEndedAtClose.Add(run.IsCompleted);

            // Declined: the window stays and the download goes on
            window.Close();
            Settle(window);
            var (title, message, button) = Assert.Single(ui.Confirms);
            Assert.Equal(AppText.CloseAbuseWorkTitle, title);
            Assert.Equal(AppText.CloseAbuseDownloadBody, message);
            Assert.Equal(AppText.CloseAbuseWorkButton, button);
            Assert.False(closed);
            Assert.True(page.IsWorking);
            Assert.False(abuse.DownloadTokens[0].IsCancellationRequested);

            // Agreed: the download is stopped, and the window closes once its run has ended
            ui.ConfirmResult = true;
            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Equal(2, ui.Confirms.Count);
            Assert.True(abuse.DownloadTokens[0].IsCancellationRequested);
            Assert.True(run.IsCompleted);
            Assert.True(runEndedAtClose[^1]);
            Assert.Equal(1, abuse.DownloadCalls);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingDuringAMergeUnderWayWaitsForItToFinish()
    {
        var abuse = new FakeAbuseIpDbService { DownloadIgnoresToken = true };
        abuse.DownloadResults.Enqueue(DownloadDone());
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        bool releasedAtClose = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            releasedAtClose = held.Task.IsCompleted;
        };
        window.Show();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);

            window.Close();
            Settle(window);
            await Task.Delay(100);
            Settle(window);
            Assert.Equal(AppText.CloseAbuseWorkTitle, Assert.Single(ui.Confirms).Title);
            Assert.False(closed);
            Assert.True(abuse.DownloadTokens[0].IsCancellationRequested);

            held.SetResult();
            await WaitUntilAsync(() => closed);
            Assert.True(releasedAtClose);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingWhileOnlyTheGroupingRunsAsksNothing()
    {
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.NetworksGate = held;
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            await page.ActivateAsync();
            Assert.True(page.IsGrouping);

            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Empty(ui.Confirms);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // The session ending is let through, even with a download out; the close button and the Dock's Quit still ask
    [AvaloniaFact]
    public async Task TheSessionEndingIsNotHeldByAnAbuseDownload()
    {
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            Assert.True(page.AsksBeforeClose);

            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: true));
            Assert.False(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: true));
            Settle(window);
            Assert.Empty(ui.Confirms);

            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: false));
            Settle(window);
            Assert.Equal(AppText.CloseAbuseWorkTitle, Assert.Single(ui.Confirms).Title);
            Assert.False(closed);
            Assert.False(abuse.DownloadTokens[0].IsCancellationRequested);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // The page's question comes before the update's and cancels nothing, so a no to the update's leaves the download
    [AvaloniaFact]
    public async Task ClosingDuringAnUpdateAndAnAbuseDownloadAsksThePageFirstAndANoToTheUpdateKeepsTheDownload()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(vm.IsBusy);
            Assert.True(page.AsksBeforeClose);

            // Yes to the page's question, no to the update's
            ui.OnDialogOpen = () => ui.ConfirmResult = ui.Confirms.Count == 1;
            window.Close();
            await WaitUntilAsync(() => ui.Confirms.Count == 2);
            Settle(window);
            Assert.Equal([AppText.CloseAbuseWorkTitle, AppText.CloseBusyTitle], ui.Confirms.Select(confirm => confirm.Title));
            Assert.False(closed);
            Assert.True(vm.IsBusy);
            Assert.True(page.IsWorking);
            Assert.False(abuse.DownloadTokens[0].IsCancellationRequested);
        }
        finally
        {
            ui.OnDialogOpen = null;
            ui.ConfirmResult = true;
            update.TrySetResult();
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingWithUnsavedEditsAnUpdateAndAnAbuseDownloadAsksInOrderThenStopsBoth()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel settings, AbuseIpDbViewModel page) =
            await StartWithAllPagesAsync(service, ui, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Settle(window);
            settings.Settings.AbuseIpDbKeepDays = 60;
            page.DownloadCommand.Execute(null);
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(settings.HasUnsavedChanges);
            Assert.True(vm.IsBusy);
            Assert.True(page.AsksBeforeClose);

            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Equal([AppText.CloseUnsavedTitle, AppText.CloseAbuseWorkTitle, AppText.CloseBusyTitle],
                ui.Confirms.Select(confirm => confirm.Title));
            Assert.True(abuse.DownloadTokens[0].IsCancellationRequested);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            update.TrySetResult();
            held.TrySetResult();
            if (!closed)
            {
                settings.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // Once the page's close question is agreed (here not needed), nothing on the page starts while the update's question
    // is open or the update winds down; a no to the update's question turns the page's runs on again
    [AvaloniaFact]
    public async Task ClosingHoldsTheAbusePageRunsOffFromThePageQuestionUntilTheUpdateQuestionIsDeclined()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        var question = new TaskCompletionSource();
        var canRunWhileAsked = new List<bool>();
        var canRunWhenUpdateEnded = new List<bool>();
        try
        {
            Settle(window);
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(vm.IsBusy);
            Assert.False(page.AsksBeforeClose);
            Assert.All(AbuseRunCommands(page), command => Assert.True(command.CanExecute(null)));

            // Declined: the hold goes and nothing was cancelled
            ui.ConfirmGate = question;
            ui.OnDialogOpen = () => canRunWhileAsked.AddRange(AbuseRunCommands(page).Select(command => command.CanExecute(null)));
            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseBusyTitle, Assert.Single(ui.Confirms).Title);
            Assert.Equal([false, false, false, false], canRunWhileAsked);
            Assert.All(AbuseRunCommands(page), command => Assert.False(command.CanExecute(null)));

            question.SetResult();
            await WaitUntilAsync(() => page.DownloadCommand.CanExecute(null));
            Settle(window);
            Assert.All(AbuseRunCommands(page), command => Assert.True(command.CanExecute(null)));
            Assert.False(closed);
            Assert.True(vm.IsBusy);
            Assert.Equal(0, abuse.DownloadCalls + abuse.ClearCalls + abuse.FeedCalls + abuse.EmptyCalls);

            // Agreed: still held while the update winds down
            ui.OnDialogOpen = null;
            ui.ConfirmResult = true;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsBusy) && !vm.IsBusy)
                    canRunWhenUpdateEnded.AddRange(AbuseRunCommands(page).Select(command => command.CanExecute(null)));
            };
            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Equal([false, false, false, false], canRunWhenUpdateEnded);
        }
        finally
        {
            ui.OnDialogOpen = null;
            question.TrySetResult();
            update.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // A download that ends while the update's question is open leaves the page's runs off: the hold, not IsWorking,
    // keeps them so; a no to the update's question turns them on again and cancels nothing
    [AvaloniaFact]
    public async Task ClosingHoldsTheAbusePageRunsOffWhenTheirDownloadEndsUnderTheUpdateQuestion()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 10, 0, 10, 0, 0,
            DateTimeOffset.UtcNow, null));
        var ui = new FakeUi();
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(service, abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        var question = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(vm.IsBusy);
            Assert.True(page.AsksBeforeClose);

            // Yes to the page's question; the update's stays open, then answers no
            ui.OnDialogOpen = () =>
            {
                ui.ConfirmResult = ui.Confirms.Count == 1;
                if (ui.Confirms.Count == 2)
                    ui.ConfirmGate = question;
            };
            window.Close();
            await WaitUntilAsync(() => ui.Confirms.Count == 2);
            Settle(window);
            Assert.Equal([AppText.CloseAbuseWorkTitle, AppText.CloseBusyTitle], ui.Confirms.Select(confirm => confirm.Title));
            Assert.True(page.IsWorking);

            held.SetResult();
            await WaitUntilAsync(() => !page.IsWorking);
            Settle(window);
            Assert.False(page.IsWorking);
            Assert.All(AbuseRunCommands(page), command => Assert.False(command.CanExecute(null)));
            Assert.False(closed);

            question.SetResult();
            await WaitUntilAsync(() => page.DownloadCommand.CanExecute(null));
            Settle(window);
            Assert.All(AbuseRunCommands(page), command => Assert.True(command.CanExecute(null)));
            Assert.False(closed);
            Assert.True(vm.IsBusy);
            Assert.Equal(1, service.UpdateCalls);
            Assert.False(abuse.DownloadTokens[0].IsCancellationRequested);
        }
        finally
        {
            ui.OnDialogOpen = null;
            ui.ConfirmResult = true;
            question.TrySetResult();
            update.TrySetResult();
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // A download that ends while the page's question is open: agreed, the window closes with nothing cancelled, and the
    // key prompt the download wanted was refused under the question
    [AvaloniaFact]
    public async Task ADownloadEndingWhileThePageQuestionIsOpenClosesWithNothingCancelledAndNoKeyPrompt()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService();
        abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.NoKey, false, 0, 0, 0, 0, 0, null, null));
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue("sentinel-abuse-key-0123456789");
        (MainViewModel vm, SettingsPageViewModel settings, AbuseIpDbViewModel page) =
            await StartWithAllPagesAsync(service, ui, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var question = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            Task run = page.DownloadCommand.ExecutionTask!;
            Assert.True(page.AsksBeforeClose);

            // The page's Dispose cancels its token after the close, so the cancel is read as the window closes
            var cancelledAtClose = new List<bool>();
            window.Closing += (_, _) => cancelledAtClose.Add(abuse.DownloadTokens[0].IsCancellationRequested);
            ui.ConfirmGate = question;
            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseAbuseWorkTitle, Assert.Single(ui.Confirms).Title);

            held.SetResult();
            await WaitUntilAsync(() => run.IsCompleted);
            Assert.Equal(AppText.AbuseDownloadKeyCancelled, page.StatusText);
            Assert.False(closed);

            question.SetResult();
            await WaitUntilAsync(() => closed);
            Assert.Single(ui.Confirms);
            Assert.Empty(ui.AbuseKeyPrompts);
            Assert.False(cancelledAtClose[^1]);
            Assert.Equal(1, abuse.DownloadCalls);
        }
        finally
        {
            question.TrySetResult();
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // The session ends while the close waits for a merge: the window goes, and the run starts nothing afterwards
    [AvaloniaFact]
    public async Task TheSessionEndingWhileTheCloseWaitsForAMergeClosesAndNothingRunsAfterwards()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService { DownloadIgnoresToken = true };
        abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.NoKey, false, 0, 0, 0, 0, 0, null, null));
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue("sentinel-abuse-key-0123456789");
        (MainViewModel vm, SettingsPageViewModel settings, AbuseIpDbViewModel page) =
            await StartWithAllPagesAsync(service, ui, abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: settings, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);
            Task run = page.DownloadCommand.ExecutionTask!;

            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseAbuseWorkTitle, Assert.Single(ui.Confirms).Title);
            Assert.True(abuse.DownloadTokens[0].IsCancellationRequested);
            Assert.False(closed);
            List<string> callsAtClose = [.. abuse.Calls];

            // The session's end is let through; the operating system then closes the window
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            window.CloseApproved();
            Settle(window);
            Assert.True(closed);

            held.SetResult();
            await WaitUntilAsync(() => run.IsCompleted);
            Settle(window);
            await Task.Delay(50);
            Settle(window);
            Assert.Single(ui.Confirms);
            Assert.Empty(ui.AbuseKeyPrompts);
            Assert.Equal(callsAtClose, abuse.Calls);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    // A second close while the first waits for a merge asks nothing more and closes only once the call has ended
    [AvaloniaFact]
    public async Task ASecondCloseWhileTheCloseWaitsForAMergeAsksNothingAndWaits()
    {
        var abuse = new FakeAbuseIpDbService { DownloadIgnoresToken = true };
        abuse.DownloadResults.Enqueue(DownloadDone());
        var held = new TaskCompletionSource();
        abuse.DownloadGate = held;
        var ui = new FakeUi();
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            page.DownloadCommand.Execute(null);

            window.Close();
            Settle(window);
            Assert.Single(ui.Confirms);

            window.Close();
            Settle(window);
            await Task.Delay(100);
            Settle(window);
            Assert.Single(ui.Confirms);
            Assert.False(closed);

            held.SetResult();
            await WaitUntilAsync(() => closed);
            Assert.Single(ui.Confirms);
        }
        finally
        {
            held.TrySetResult();
            if (!closed)
            {
                window.Close();
            }
        }
    }

    private static IEnumerable<IAsyncRelayCommand> AbuseRunCommands(AbuseIpDbViewModel page) =>
        [page.DownloadCommand, page.ClearListCommand, page.WriteFeedCommand, page.EmptyFeedCommand];

    // The summary, status and action lines: their text, the whole text in a tooltip, their classes and live setting
    [AvaloniaFact]
    public async Task TheAbuseIpDbLinesFollowThePage()
    {
        var abuse = new FakeAbuseIpDbService { NetworksResult = Networks(3) };
        abuse.StatusResults.Enqueue(ListReady());
        abuse.FeedResults.Enqueue(new AbuseFeedWriteResult(AbuseFeedWriteFailure.None, ["AbuseIPDB_rolling_ipv4.txt"],
            100, 2, 0, 0, []));
        abuse.DownloadResults.Enqueue(new AbuseDownloadResult(AbuseIpDbFailure.ServiceError, false, 0, 0, 0, 0, 0,
            DateTimeOffset.UtcNow, null));
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1 && page.PendingGrouping is { IsCompleted: true });
            Settle(window);
            TextBlock summary = window.FindControl<TextBlock>("AbuseSummary")!;
            TextBlock status = window.FindControl<TextBlock>("AbuseStatus")!;
            TextBlock action = window.FindControl<TextBlock>("AbuseAction")!;

            Assert.StartsWith("A report, so exceptions aren't applied: ", page.SummaryText);
            Assert.Equal(page.SummaryText, summary.Text);
            Assert.Equal(page.SummaryText, ToolTip.GetTip(summary));
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(summary));

            Assert.False(page.IsStatusWarning);
            Assert.DoesNotContain("caution", status.Classes);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));

            Assert.False(page.HasActionText);
            Assert.Contains("placeholder", action.Classes);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(action));

            // The facts: the last download over the downloads left and the counts, each with the whole line as its tip
            TextBlock facts = window.FindControl<TextBlock>("AbuseFacts")!;
            TextBlock detail = window.FindControl<TextBlock>("AbuseFactsDetail")!;
            Assert.StartsWith("Downloaded ", page.FactsHeadText);
            Assert.NotEqual("", page.FactsDetailText);
            Assert.Equal(page.FactsHeadText, facts.Text);
            Assert.Equal(page.FactsDetailText, detail.Text);
            Assert.Equal(page.FactsText, ToolTip.GetTip(facts));
            Assert.Equal(page.FactsText, ToolTip.GetTip(detail));
            Assert.Equal(page.FactsText, AutomationProperties.GetName(facts));
            Assert.Contains("caption", detail.Classes);

            // A failed download is a warning line
            await page.DownloadCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(page.IsStatusWarning);
            Assert.NotEqual("", page.StatusText);
            Assert.Equal(page.StatusText, status.Text);
            Assert.Equal(page.StatusText, ToolTip.GetTip(status));
            Assert.Contains("caution", status.Classes);

            await page.WriteFeedCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(page.HasActionText);
            Assert.Equal(page.ActionText, action.Text);
            Assert.Equal(page.ActionText, ToolTip.GetTip(action));
            Assert.DoesNotContain("placeholder", action.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    // The facts take two lines inside the buttons' height, so the card is as tall with a second line as without one
    [AvaloniaTheory]
    [InlineData(1280, 880)]
    [InlineData(960, 600)]
    public async Task TheAbuseFactsCardKeepsItsHeightWithAndWithoutASecondLine(double width, double height)
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.StatusResults.Enqueue(ListReady());
        abuse.StatusResults.Enqueue(new AbuseListStatus(AbuseListState.Newer, 0, 0, null, null,
            new AbuseRateLimit(5, 3, null), 2, null));
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Width = width;
        window.Height = height;
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1 && page.PendingGrouping is { IsCompleted: true });
            Settle(window);
            TextBlock facts = window.FindControl<TextBlock>("AbuseFacts")!;
            TextBlock detail = window.FindControl<TextBlock>("AbuseFactsDetail")!;
            Border card = facts.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("card"));
            var lines = (Control)facts.GetVisualParent()!;
            Button download = window.FindControl<Button>("DownloadBlacklistButton")!;
            ScrollViewer scroll = window.FindControl<ScrollViewer>("PageScroll")!;
            Assert.NotEqual("", detail.Text);
            double withDetail = card.Bounds.Height;
            double extent = scroll.Extent.Height;
            double detailHeight = detail.Bounds.Height;
            Assert.True(lines.Bounds.Height <= download.Bounds.Height,
                $"The two lines are {lines.Bounds.Height} px in a {download.Bounds.Height} px row.");

            Navigate(window, "NavLookup");
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 2 && page.PendingGrouping is { IsCompleted: true });
            Settle(window);
            Assert.Equal("", detail.Text ?? "");
            Assert.True(detail.IsVisible);
            Assert.Equal(withDetail, card.Bounds.Height);
            Assert.Equal(detailHeight, detail.Bounds.Height);
            Assert.Equal(extent, scroll.Extent.Height);
        }
        finally
        {
            window.Close();
        }
    }

    // Geist Mono's width of the widest ASN, twelve characters, at the row's text size, measured with Skia
    private const double WidestAsnWidth = 93.6;

    // The AS column is wide enough for the widest ASN at the narrowest window, in the header and in every row
    [AvaloniaFact]
    public async Task TheAbuseNetworksAsColumnHoldsTheWidestAsnAtTheMinimumWidth()
    {
        var abuse = new FakeAbuseIpDbService
        {
            NetworksResult = new AbuseNetworksResult(AbuseNetworksFailure.None,
                [new AbuseNetworkGroup(uint.MaxValue, "Example", "example.com", 10, [new AbuseCountryCount("US", 10)])],
                0, [], 0, 10, 0, true, 30, DateTimeOffset.UtcNow.AddHours(-1), SampleDataset, 0),
        };
        abuse.StatusResults.Enqueue(ListReady());
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Width = 960;
        window.Height = 600;
        window.Show();
        try
        {
            Navigate(window, "NavAbuseIpDb");
            await WaitUntilAsync(() => abuse.StatusCalls == 1 && page.PendingGrouping is { IsCompleted: true });
            Settle(window);
            Border card = window.FindControl<Border>("AbuseNetworksCard")!;
            Border row = card.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("row"));
            TextBlock asn = row.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("mono"));
            Grid header = card.GetVisualDescendants().OfType<Grid>()
                .First(grid => grid.ColumnDefinitions.Count == 6);
            Grid cells = row.GetVisualDescendants().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 6);

            Assert.Equal("AS4294967295", asn.Text);
            Assert.True(asn.Bounds.Width >= WidestAsnWidth, $"The AS cell is {asn.Bounds.Width} px wide.");
            foreach (Grid grid in new[] { header, cells })
            {
                Assert.Equal(new GridLength(1.4, GridUnitType.Star), grid.ColumnDefinitions[0].Width);
                Assert.Equal(new GridLength(1.9, GridUnitType.Star), grid.ColumnDefinitions[1].Width);
                Assert.Equal(7.7, grid.ColumnDefinitions.Take(5).Sum(column => column.Width.Value), 6);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ThreeHundredRowsRealiseOnlyTheVisibleRows()
    {
        var service = WithDataset();
        service.SearchFunc = (_, _) => ManyRows(300);
        var vm = await StartAsync(service);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            ItemsControl list = window.FindControl<ItemsControl>("ResultsList")!;
            int realised = list.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("row"));

            Assert.Equal(300, vm.Results.Count);
            Assert.InRange(realised, 1, 59);
        }
        finally
        {
            window.Close();
        }
    }

    // A right-click in the gap between the first row's first two columns, where no text is: the row's own
    // background is all that can take it there
    private static (ContextMenu Menu, MenuItem Ipinfo, MenuItem AbuseIpDb) RightClickFirstRowGap(MainWindow window)
    {
        ItemsControl list = window.FindControl<ItemsControl>("ResultsList")!;
        Border row = list.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("row"));
        TextBlock network = row.GetVisualDescendants().OfType<TextBlock>().First();
        Point gap = network.TranslatePoint(new Point(network.Bounds.Width + 6, network.Bounds.Height / 2), window)!.Value;
        IInputElement? hit = window.InputHitTest(gap);
        Assert.Same(row, hit);
        // Headless drawing hit-tests a border's whole rectangle, with or without a fill, so the fill that makes the
        // gap hit-testable on a real renderer is pinned here
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);

        window.MouseDown(gap, MouseButton.Right);
        window.MouseUp(gap, MouseButton.Right);
        Settle(window);

        ContextMenu menu = list.ContextMenu!;
        Assert.True(menu.IsOpen);
        List<MenuItem> items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal([AppText.OpenInIpinfo, AppText.OpenInAbuseIpDb], items.Select(item => item.Header as string));
        return (menu, items[0], items[1]);
    }

    [AvaloniaFact]
    public async Task ARightClickOnAnAddressRowOpensTheMenuAndItsItemsOpenThePages()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);
        await vm.InitializeAsync();
        var address = new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, address, [Row(0), Row(1)], 2);
        vm.SearchText = "8.8.8.8";
        await vm.SearchCommand.ExecuteAsync(null);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            (ContextMenu menu, MenuItem ipinfo, MenuItem abuse) = RightClickFirstRowGap(window);

            Assert.Same(vm.OpenInIpinfoCommand, ipinfo.Command);
            Assert.Same(vm.OpenInAbuseIpDbCommand, abuse.Command);
            Assert.True(ipinfo.IsEffectivelyEnabled);
            Assert.True(abuse.IsEffectivelyEnabled);

            ipinfo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Settle(window);
            Assert.Equal(["https://ipinfo.io/8.8.8.8"], ui.OpenedPages.Select(page => page.Uri.AbsoluteUri));
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AfterAnAsnSearchTheMenuOffersOnlyIpinfo()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);
        await vm.InitializeAsync();
        service.SearchFunc = (_, _) => ManyRows(3);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            (ContextMenu menu, MenuItem ipinfo, MenuItem abuse) = RightClickFirstRowGap(window);

            Assert.True(ipinfo.IsEffectivelyEnabled);
            Assert.False(abuse.IsEffectivelyEnabled);

            ipinfo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            abuse.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Settle(window);
            Assert.Equal(["https://ipinfo.io/AS15169"], ui.OpenedPages.Select(page => page.Uri.AbsoluteUri));
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    // The headless launcher answers no, as a launcher with no browser does: the no must throw, because the view
    // model's failure line comes from the throw
    [AvaloniaFact]
    public async Task ALauncherThatAnswersNoMakesOpeningAPageThrow()
    {
        var window = new MainWindow(await StartAsync(WithDataset()));
        window.Show();
        try
        {
            Settle(window);
            var ui = new AvaloniaUiServices(window);

            await Assert.ThrowsAsync<InvalidOperationException>(() => ui.OpenWebPageAsync(WebPage.IpInfoAsn(13335)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BusyUpdateDisablesTheGatedButtonsAndEnablesCancel()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, abuse: new FakeAbuseIpDbService());
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            Button update = window.FindControl<Button>("UpdateButton")!;
            // The page's buttons wait on the same gate as Lookup's
            Button settings = window.FindControl<Button>("ChooseFolderButton")!;
            Button resetToken = window.FindControl<Button>("ResetTokenButton")!;
            Button setKey = window.FindControl<Button>("SetAbuseKeyButton")!;
            Button export = window.FindControl<Button>("ExportButton")!;
            Button cancel = window.FindControl<Button>("CancelUpdateButton")!;
            Assert.Same(page.ResetTokenCommand, resetToken.Command);
            Assert.True(update.IsEffectivelyEnabled);
            Assert.True(settings.IsEffectivelyEnabled);
            Assert.True(resetToken.IsEffectivelyEnabled);
            Assert.True(setKey.IsEffectivelyEnabled);
            Assert.False(cancel.IsEffectivelyEnabled);

            var gate = new TaskCompletionSource();
            service.UpdateGate = gate;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.False(update.IsEffectivelyEnabled);
            Assert.False(settings.IsEffectivelyEnabled);
            Assert.False(resetToken.IsEffectivelyEnabled);
            Assert.False(setKey.IsEffectivelyEnabled);
            Assert.False(export.IsEffectivelyEnabled);
            Assert.True(cancel.IsEffectivelyEnabled);

            gate.SetResult();
            await (vm.UpdateDatasetCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);
            Assert.True(update.IsEffectivelyEnabled);
            Assert.True(settings.IsEffectivelyEnabled);
            Assert.True(resetToken.IsEffectivelyEnabled);
            Assert.True(setKey.IsEffectivelyEnabled);
            Assert.False(cancel.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CheckAbuseIpDbSitsBetweenCopyAndOpenFolderAsAPlainTextButton()
    {
        var vm = await StartAsync(WithDataset(), new FakeAbuseIpDbService());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Button check = window.FindControl<Button>("CheckAbuseButton")!;
            Button copy = window.FindControl<Button>("CopyResultsButton")!;
            Button openFolder = window.FindControl<Button>("OpenFolderButton")!;
            var header = Assert.IsType<StackPanel>(check.Parent);
            Assert.Same(header, copy.Parent);
            Assert.Same(header, openFolder.Parent);
            Assert.Equal(header.Children.IndexOf(copy) + 1, header.Children.IndexOf(check));
            Assert.Equal(header.Children.IndexOf(openFolder) - 1, header.Children.IndexOf(check));
            Assert.DoesNotContain("accent", check.Classes);
            Assert.Equal(AppText.CheckAbuse, check.Content);
            Assert.Equal(AppText.CheckAbuseTooltip, ToolTip.GetTip(check));
            Assert.Same(vm.CheckAbuseCommand, check.Command);
        }
        finally
        {
            window.Close();
        }
    }

    // At the minimum width the buttons leave too little room beside the title for a long IPv6 address, so the summary
    // has its own row under them, as wide as the header
    [AvaloniaFact]
    public async Task TheResultsSummaryHasTheHeadersWholeWidthUnderTheButtons()
    {
        var vm = await StartAsync(WithDataset(), new FakeAbuseIpDbService());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            window.Width = window.MinWidth;
            Settle(window);
            Grid header = window.FindControl<Grid>("ResultsTitleBar")!;
            TextBlock summary = window.FindControl<TextBlock>("ResultsSummary")!;
            Button check = window.FindControl<Button>("CheckAbuseButton")!;

            Assert.Same(header, summary.Parent);
            Assert.Equal(header.Bounds.Width, summary.Bounds.Width, 1);
            Point checkBottom = check.TranslatePoint(new Point(0, check.Bounds.Height), header)!.Value;
            Point summaryTop = summary.TranslatePoint(new Point(0, 0), header)!.Value;
            Assert.True(summaryTop.Y >= checkBottom.Y, $"The summary starts at {summaryTop.Y} px, above the buttons' foot at {checkBottom.Y} px.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheResultsButtonsFollowTheSearchThatJustFinished()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeAbuseIpDbService());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Button check = window.FindControl<Button>("CheckAbuseButton")!;
            Button export = window.FindControl<Button>("ExportButton")!;
            Assert.False(export.IsEffectivelyEnabled);
            Assert.False(check.IsEffectivelyEnabled);

            service.SearchFunc = (_, _) => ManyRows(5);
            vm.SearchText = "AS15169";
            await vm.SearchCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(export.IsEffectivelyEnabled);
            Assert.False(check.IsEffectivelyEnabled);

            var address = new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });
            service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, address, [Row(0)], 1);
            vm.SearchText = "8.8.8.8";
            await vm.SearchCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(check.IsEffectivelyEnabled);
            Assert.False(export.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task IconOnlyButtonsHaveAnAccessibleNameAndATooltip()
    {
        var abuse = new FakeAbuseIpDbService { NetworksResult = Networks(2) };
        (MainViewModel vm, AbuseIpDbViewModel page) = await StartWithAbusePageAsync(WithDataset(), abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, abuseIpDb: page);
        window.Show();
        try
        {
            Settle(window);
            foreach (string name in new[] { "HelpButton", "AboutButton", "CopyResultsButton" })
            {
                Button button = window.FindControl<Button>(name)!;
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), name);
                Assert.NotNull(ToolTip.GetTip(button));
            }

            // Each offending network's Look up button is icon-only, named and explained by its row
            Navigate(window, "NavAbuseIpDb");
            await page.PendingGrouping!;
            Settle(window);
            List<Button> lookUps = LookUpButtons(window);
            Assert.Equal(2, lookUps.Count);
            for (int i = 0; i < lookUps.Count; i++)
            {
                Assert.False(string.IsNullOrWhiteSpace(page.Rows[i].LookUpName));
                Assert.Equal(page.Rows[i].LookUpName, AutomationProperties.GetName(lookUps[i]));
                Assert.Equal(page.Rows[i].LookUpName, ToolTip.GetTip(lookUps[i]));
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ----- the Settings page's unsaved edits, start-up and keys -----

    // The mark is on the nav item, so an unsaved edit of either section is in view from every page; hidden, it keeps
    // its space
    [AvaloniaFact]
    public async Task TheSettingsNavItemMarksUnsavedChangesFromEveryPage()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Width = 960;
        window.Height = 600;
        window.Show();
        try
        {
            Settle(window);
            TextBlock mark = window.FindControl<TextBlock>("NavSettingsMark")!;
            ListBoxItem item = window.FindControl<ListBoxItem>("NavSettings")!;
            Assert.Equal(AppText.SettingsNotSavedMark, mark.Text);
            Assert.Contains("caution", mark.Classes);
            Assert.Contains("placeholder", mark.Classes);
            double widthHidden = mark.Bounds.Width;
            double itemHeight = item.Bounds.Height;
            Assert.True(widthHidden > 0);

            // A settings edit, then its revert
            page.Settings.AbuseIpDbKeepDays = 60;
            Settle(window);
            Assert.True(page.HasUnsavedChanges);
            Assert.DoesNotContain("placeholder", mark.Classes);
            page.RevertSettingsCommand.Execute(null);
            Settle(window);
            Assert.False(page.HasUnsavedChanges);
            Assert.Contains("placeholder", mark.Classes);

            // An exception added on Settings shows from every other page until it is saved
            Navigate(window, "NavSettings");
            page.Exceptions.NetworkText = "192.0.2.0/24";
            page.AddExceptionCommand.Execute(null);
            foreach (string other in new[] { "NavLookup", "NavGoogleCloud", "NavAzure", "NavAws" })
            {
                Navigate(window, other);
                Assert.True(page.HasUnsavedChanges);
                Assert.DoesNotContain("placeholder", mark.Classes);
                Assert.Equal(widthHidden, mark.Bounds.Width);
                Assert.Equal(itemHeight, item.Bounds.Height);
            }

            // At the minimum width the item shows "Settings" and the mark whole, inside the item
            TextBlock title = item.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == AppText.SettingsTitle);
            Rect itemBounds = new(item.TranslatePoint(new Point(0, 0), window)!.Value, item.Bounds.Size);
            foreach (TextBlock text in new[] { title, mark })
            {
                Rect bounds = new(text.TranslatePoint(new Point(0, 0), window)!.Value, text.Bounds.Size);
                Assert.True(bounds.Right <= itemBounds.Right, $"{text.Text} ends at {bounds.Right}, the item at {itemBounds.Right}.");
                text.Measure(Size.Infinity);
                Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5, $"{text.Text} is cut: {text.DesiredSize.Width} > {text.Bounds.Width}.");
            }

            await page.SaveExceptionsCommand.ExecuteAsync(null);
            Settle(window);
            Assert.False(page.HasUnsavedChanges);
            Assert.Contains("placeholder", mark.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    // Without a Settings page the mark stays hidden
    [AvaloniaFact]
    public async Task TheNotSavedMarkIsHiddenWithoutASettingsPage()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Assert.Contains("placeholder", window.FindControl<TextBlock>("NavSettingsMark")!.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingWithUnsavedChangesAsksAndDecliningKeepsTheWindowAndTheEdits()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            page.Exceptions.NetworkText = "192.0.2.0/24";
            page.AddExceptionCommand.Execute(null);

            window.Close();
            Settle(window);
            Assert.False(closed);
            (string title, string body, string button) = Assert.Single(ui.Confirms);
            Assert.Equal(AppText.CloseUnsavedTitle, title);
            Assert.Equal(AppText.CloseUnsavedBody, body);
            Assert.Equal(AppText.CloseUnsavedButton, button);
            Assert.True(page.HasUnsavedChanges);
            Assert.Single(page.Exceptions.Rows);
            Assert.Equal(0, service.SaveExceptionsCalls);

            ui.ConfirmResult = true;
            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Equal(2, ui.Confirms.Count);
            Assert.Equal(0, service.SaveExceptionsCalls);
        }
        finally
        {
            if (!closed)
            {
                page.RevertExceptionsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // Nothing unsaved and no update: the window closes without a question
    [AvaloniaFact]
    public async Task ClosingWithNothingUnsavedAsksNothing()
    {
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(WithDataset(), ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Settle(window);

        window.Close();

        Assert.True(closed);
        Assert.Empty(ui.Confirms);
    }

    // The unsaved question comes first, because agreeing to the update's question cancels the update
    [AvaloniaFact]
    public async Task ClosingDuringAnUpdateWithUnsavedChangesAsksAboutTheEditsFirst()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.Settings.AbuseIpDbKeepDays = 60;
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(vm.IsBusy);

            // Declining the first question asks nothing more and keeps the update
            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseUnsavedTitle, Assert.Single(ui.Confirms).Title);
            Assert.False(closed);
            Assert.True(vm.IsBusy);

            ui.ConfirmResult = true;
            window.Close();
            await WaitUntilAsync(() => closed);
            Assert.Equal([AppText.CloseUnsavedTitle, AppText.CloseUnsavedTitle, AppText.CloseBusyTitle],
                ui.Confirms.Select(confirm => confirm.Title));
            Assert.False(vm.IsBusy);
        }
        finally
        {
            update.TrySetResult();
            if (!closed)
            {
                page.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // The result lines are one trimmed line; their tooltip holds the whole text, notes included
    [AvaloniaFact]
    public async Task TheResultLinesShowTheirWholeTextInATooltip()
    {
        var service = WithDataset();
        var ui = new FakeUi { FailOpenFolder = true };
        var vm = NewViewModel(service, ui);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
            await check;
        var google = new GoogleCloudFeedViewModel(
            new GoogleCloudFeedService(service, new HttpClient(), new CoreOptions(), TimeProvider.System, Path.GetTempPath()),
            service, ui, new WorkGate());
        var window = new MainWindow();
        window.AttachViewModel(vm, google);
        window.Show();
        try
        {
            Settle(window);
            await vm.OpenExportFolderCommand.ExecuteAsync(null);
            await google.OpenFeedFolderCommand.ExecuteAsync(null);
            Settle(window);

            Assert.Equal(AppText.FolderOpenFailed, vm.ResultsActionText);
            Assert.Equal(vm.ResultsActionText, ToolTip.GetTip(window.FindControl<TextBlock>("ResultsAction")!));
            Assert.Equal(AppText.FolderOpenFailed, google.ActionText);
            TextBlock cloudAction = PanelOf(window, google).FindControl<TextBlock>("CloudAction")!;
            Assert.Equal(google.ActionText, ToolTip.GetTip(cloudAction));
        }
        finally
        {
            window.Close();
        }
    }

    // ----- the operating system ending the session -----

    // Every reason, programmatic or not, on Windows or not. Only a log-out, restart or shut down (OSShutdown) ends the
    // session everywhere; on Windows a sign-out or Restart Manager comes as a non-programmatic ApplicationShutdown
    [Fact]
    public void EndsTheSessionOnlyForTheOperatingSystemsOwnShutdown()
    {
        var endsTheSession = new HashSet<(WindowCloseReason, bool, bool)>
        {
            (WindowCloseReason.OSShutdown, false, false),
            (WindowCloseReason.OSShutdown, false, true),
            (WindowCloseReason.OSShutdown, true, false),
            (WindowCloseReason.OSShutdown, true, true),
            (WindowCloseReason.ApplicationShutdown, false, true),
        };
        WindowCloseReason[] reasons = Enum.GetValues<WindowCloseReason>();
        Assert.Equal(
            [WindowCloseReason.Undefined, WindowCloseReason.WindowClosing, WindowCloseReason.OwnerWindowClosing,
             WindowCloseReason.ApplicationShutdown, WindowCloseReason.OSShutdown],
            reasons);

        int checkedCases = 0;
        foreach (WindowCloseReason reason in reasons)
        {
            foreach (bool isProgrammatic in new[] { false, true })
            {
                foreach (bool isWindows in new[] { false, true })
                {
                    Assert.True(
                        endsTheSession.Contains((reason, isProgrammatic, isWindows))
                            == MainWindow.EndsTheSession(reason, isProgrammatic, isWindows),
                        $"{reason}, programmatic {isProgrammatic}, Windows {isWindows}");
                    checkedCases++;
                }
            }
        }

        Assert.Equal(20, checkedCases);
    }

    // The session ending closes with no question, even with an update running and unsaved edits;
    // the macOS Dock's Quit and Cmd+Q, and a Windows quit from code, still ask
    [AvaloniaFact]
    public async Task TheSessionEndingIsNotHeldByAnUpdateOrUnsavedEdits()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.Settings.AbuseIpDbKeepDays = 60;
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.True(vm.IsBusy);
            Assert.True(page.HasUnsavedChanges);

            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: true));
            Assert.False(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: true));
            Settle(window);
            Assert.Empty(ui.Confirms);
            Assert.True(vm.IsBusy);

            // The Dock's Quit on macOS asks, as the close button does; declined, the window and the update stay
            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: false));
            Settle(window);
            Assert.Equal(AppText.CloseUnsavedTitle, Assert.Single(ui.Confirms).Title);
            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: true, isWindows: true));
            Settle(window);
            Assert.Equal(2, ui.Confirms.Count);
            Assert.False(closed);
            Assert.True(vm.IsBusy);
        }
        finally
        {
            update.TrySetResult();
            if (!closed)
            {
                page.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // A start step still holds the session's end, and the window closes itself once the step returns
    [AvaloniaFact]
    public async Task TheSessionEndingDuringAStartStepStillWaitsForTheStep()
    {
        var service = WithDataset();
        var gate = new TaskCompletionSource();
        service.InitializeGate = gate;
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        Task launch = Task.CompletedTask;
        try
        {
            Settle(window);
            launch = vm.InitializeAsync();
            Assert.True(vm.IsStartStepRunning);

            Assert.True(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            Assert.True(window.HoldsClose(WindowCloseReason.ApplicationShutdown, isProgrammatic: false, isWindows: true));
            Settle(window);
            Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
            Assert.False(closed);

            gate.SetResult();
            await WaitUntilAsync(() => closed);
            await launch;
            Assert.Empty(ui.Confirms);
            Assert.Equal(1, service.InitializeCalls);
        }
        finally
        {
            gate.TrySetResult();
            if (!closed)
                window.Close();
        }
    }

    // The session ends while the close question is open: the window goes, and the question's answer then does nothing
    [AvaloniaFact]
    public async Task AnAnswerToACloseQuestionAfterTheWindowHasClosedDoesNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        int closings = 0;
        bool closed = false;
        window.Closing += (_, _) => closings++;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var update = new TaskCompletionSource();
        var question = new TaskCompletionSource();
        try
        {
            Settle(window);
            page.Settings.AbuseIpDbKeepDays = 60;
            service.UpdateGate = update;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            ui.ConfirmGate = question;

            window.Close();
            Settle(window);
            Assert.Equal(AppText.CloseUnsavedTitle, Assert.Single(ui.Confirms).Title);
            Assert.False(closed);

            // The session's end is let through; the operating system then closes the window
            Assert.False(window.HoldsClose(WindowCloseReason.OSShutdown, isProgrammatic: false, isWindows: false));
            window.CloseApproved();
            Settle(window);
            Assert.True(closed);
            int closingsAtClose = closings;

            question.SetResult();
            Settle(window);
            await Task.Delay(50);
            Settle(window);

            Assert.Single(ui.Confirms);
            Assert.Equal(closingsAtClose, closings);
        }
        finally
        {
            question.TrySetResult();
            update.TrySetResult();
            if (!closed)
            {
                page.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // The composition's holder reads the Settings page once it is bound, and nothing unsaved before
    [AvaloniaFact]
    public async Task TheLateHolderFollowsTheSettingsPageOnceBound()
    {
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(WithDataset());
        var unsaved = new LateUnsavedSettings();
        Assert.False(unsaved.HasUnsavedChanges);

        unsaved.Source = page;
        Assert.False(unsaved.HasUnsavedChanges);
        page.Settings.AbuseIpDbKeepDays = 60;
        Assert.True(unsaved.HasUnsavedChanges);
        page.RevertSettingsCommand.Execute(null);
        Assert.False(unsaved.HasUnsavedChanges);
        vm.Dispose();
        page.Dispose();
    }

    // Before start-up the page shows the defaults, and start-up then replaces them, so nothing can be typed until then
    [AvaloniaFact]
    public async Task TheSettingsInputsAreOffUntilStartUp()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            string[] inputs = ["SettingsInputs", "ExceptionsEditor", "ExceptionsListCard", "NetworkBox", "AddExceptionButton"];
            // The section has its own name scope, and is laid out only once start-up shows the scroll viewer, so before
            // that its place under SettingsInputs is what keeps it off
            FeedTypesSection section = window.FindControl<FeedTypesSection>("FeedTypesSection")!;
            string[] sectionInputs = ["WriteFortigateFeedsBox", "WriteModSecurityListsBox"];
            Assert.False(page.CanEditInputs);
            Assert.All(inputs, name => Assert.False(window.FindControl<Control>(name)!.IsEffectivelyEnabled, name));
            Assert.Contains(window.FindControl<Control>("SettingsInputs")!, section.GetLogicalAncestors());
            Assert.False(page.AddExceptionCommand.CanExecute(null));

            await vm.InitializeAsync();
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);

            Assert.True(page.CanEditInputs);
            Assert.All(inputs, name => Assert.True(window.FindControl<Control>(name)!.IsEffectivelyEnabled, name));
            Assert.All(sectionInputs, name => Assert.True(section.FindControl<Control>(name)!.IsEffectivelyEnabled, name));
        }
        finally
        {
            window.Close();
        }
    }

    // The SAS expiry is judged on the clock of the page's show, so a link that has come near its expiry is flagged
    [AvaloniaFact]
    public async Task ShowingTheSettingsPageAgainJudgesTheSasExpiryOnTheClockOfTheShow()
    {
        var now = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(now);
        var service = WithDataset();
        service.Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = true };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, time: time);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            page.Settings.ModSecurityLocation = "https://acct.blob.core.windows.net/feeds?sv=2026-01-01&sr=c&sp=rl&se="
                + now.AddDays(20).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
                + "&sig=SAMPLEONLY%2Bnot%3Dreal";
            Settle(window);
            TextBlock note = window.FindControl<FeedTypesSection>("FeedTypesSection")!.FindControl<TextBlock>("ModSecurityLocationNote")!;
            string later = note.Text!;
            Assert.DoesNotContain("caution", note.Classes);
            Assert.DoesNotContain("placeholder", note.Classes);

            Navigate(window, "NavLookup");
            time.Advance(TimeSpan.FromDays(10));
            Navigate(window, "NavSettings");

            Assert.Contains("caution", note.Classes);
            Assert.NotEqual(later, note.Text);
        }
        finally
        {
            page.RevertSettingsCommand.Execute(null);
            window.Close();
        }
    }

    // Before start-up the page holds only the defaults and the stand-in unreadable list, so one line stands in place of
    // the cards, and the unreadable-list warning is never seen; once started, the cards show and the line goes
    [AvaloniaFact]
    public async Task BeforeStartUpTheSettingsPageShowsOneLineInPlaceOfItsCards()
    {
        var service = WithDataset();
        service.Exceptions = ExceptionList.Unreadable;
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            // A control inside a scroll viewer that was never shown has no visual parent yet, and then reads as
            // effectively visible, so being seen also needs a place in the window and a laid-out size
            bool Seen(Control control) =>
                control.IsEffectivelyVisible && control.GetVisualAncestors().Contains(window) && control.Bounds.Width > 0;
            string[] cards = ["SettingsInputs", "KeysCard", "ExceptionsEditor"];
            TextBlock line = window.FindControl<TextBlock>("SettingsNotStartedText")!;
            TextBlock warning = window.FindControl<TextBlock>("FeedbackTextBlock")!;
            Assert.Equal(AppText.SettingsNotStarted, line.Text);
            Assert.Contains("secondary", line.Classes);
            Assert.True(Seen(line));
            Assert.False(window.FindControl<ScrollViewer>("SettingsScroll")!.IsVisible);
            Assert.All(cards, name => Assert.False(Seen(window.FindControl<Control>(name)!), name));
            Assert.Equal(AppText.ExceptionsUnreadableWarning, warning.Text);
            Assert.False(Seen(warning));
            Assert.True(window.FindControl<StackPanel>("SettingsHeading")!.IsEffectivelyVisible);

            service.Exceptions = ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Head office")]);
            await vm.InitializeAsync();
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);

            Assert.True(page.IsStarted);
            Assert.False(Seen(line));
            Assert.All(cards, name => Assert.True(Seen(window.FindControl<Control>(name)!), name));
            Assert.NotEqual(AppText.ExceptionsUnreadableWarning, warning.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ----- the start-up stop -----

    // A control inside a scroll viewer that was never shown has no visual parent yet, and then reads as effectively
    // visible, so being seen also needs a place in the window and a laid-out size
    private static bool Seen(Window window, Control control) =>
        control.IsEffectivelyVisible && control.GetVisualAncestors().Contains(window) && control.Bounds.Width > 0;

    private static readonly string[] SettingsCards = ["SettingsInputs", "KeysCard", "ExceptionsEditor"];

    // A window over the main view model and the Settings page, with start-up stopped by the given failure
    private static async Task<(MainWindow Window, MainViewModel Vm, SettingsPageViewModel Page)> StoppedWindowAsync(
        FakeIpInfoService service, FakeUi ui, StartupFailure failure)
    {
        service.StartupResult = new StartupResult(failure, null, false);
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        Settle(window);
        Assert.True(vm.IsStopped);
        return (window, vm, page);
    }

    [AvaloniaFact]
    public async Task AStoppedStartShowsOnlyTheStopAndDisablesTheNav()
    {
        (MainWindow window, MainViewModel vm, _) = await StoppedWindowAsync(new FakeIpInfoService(), new FakeUi(), StartupFailure.ConfigLocked);
        try
        {
            Assert.True(window.FindControl<ScrollViewer>("StartupStop")!.IsVisible);
            Assert.True(Seen(window, window.FindControl<Border>("StartupStopCard")!));
            Assert.False(window.FindControl<ScrollViewer>("PageScroll")!.IsVisible);
            Assert.False(Seen(window, window.FindControl<Control>("LookupPage")!));
            Assert.False(window.FindControl<ListBox>("NavList")!.IsEnabled);
            Assert.Equal(AppText.ConfigLockedTitle, window.FindControl<TextBlock>("StartupStopTitle")!.Text);
            Assert.Equal(AppText.ConfigLocked, window.FindControl<SelectableTextBlock>("StartupStopText")!.Text);
            Assert.Contains("infobar", window.FindControl<Border>("StartupStopCard")!.Classes);
            Assert.Contains("error", window.FindControl<Border>("StartupStopCard")!.Classes);
            // Help and About stay
            Assert.True(window.FindControl<Button>("HelpButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("AboutButton")!.IsEffectivelyEnabled);
            Assert.True(vm.IsStopped);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(StartupFailure.ConfigUnreadable, true, false)]
    [InlineData(StartupFailure.ExceptionsImportUnreadable, false, true)]
    [InlineData(StartupFailure.ConfigKeyUnavailable, false, false)]
    public async Task TheStopsButtonsShowOnlyWhenOffered(StartupFailure failure, bool startFresh, bool withoutExceptions)
    {
        (MainWindow window, _, _) = await StoppedWindowAsync(new FakeIpInfoService(), new FakeUi(), failure);
        try
        {
            Button fresh = window.FindControl<Button>("StartFreshButton")!;
            Button without = window.FindControl<Button>("StartWithoutOldExceptionsButton")!;
            Assert.Equal(startFresh, fresh.IsVisible);
            Assert.Equal(withoutExceptions, without.IsVisible);
            Assert.Equal(startFresh, fresh.IsEffectivelyEnabled);
            Assert.Equal(withoutExceptions, without.IsEffectivelyEnabled);
            Assert.Equal(AppText.StartFreshButton, fresh.Content);
            Assert.Equal(AppText.StartWithoutOldExceptionsButton, without.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheStopsActionLineKeepsItsHeight()
    {
        var ui = new FakeUi { ConfirmResult = true };
        var service = new FakeIpInfoService { StartFreshResult = new StartupResult(StartupFailure.ConfigUnreadable, null, false) };
        (MainWindow window, MainViewModel vm, _) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigUnreadable);
        try
        {
            TextBlock line = window.FindControl<TextBlock>("StartupStopAction")!;
            Border card = window.FindControl<Border>("StartupStopCard")!;
            double cardHeight = card.Bounds.Height;
            Assert.Equal(18, line.Bounds.Height);
            Assert.Contains("placeholder", line.Classes);

            await vm.StartFreshCommand.ExecuteAsync(null);
            Settle(window);

            Assert.Equal(AppText.StartFreshFailed, line.Text);
            Assert.DoesNotContain("placeholder", line.Classes);
            Assert.Contains("critical", line.Classes);
            Assert.Equal(18, line.Bounds.Height);
            Assert.Equal(cardHeight, card.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheMenuCantReachSettingsWhileStopped()
    {
        (MainWindow window, _, _) = await StoppedWindowAsync(new FakeIpInfoService(), new FakeUi(), StartupFailure.ConfigNewer);
        try
        {
            window.ShowSettingsPage();
            Settle(window);

            Assert.Same(window.FindControl<ListBoxItem>("NavLookup"), window.FindControl<ListBox>("NavList")!.SelectedItem);
            Assert.False(window.FindControl<Control>("SettingsPage")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheMenusResetDoesNothingWhileStopped()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        (MainWindow window, _, _) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigKeyMismatch);
        try
        {
            window.ResetTokenFromMenu();
            Settle(window);

            Assert.Same(window.FindControl<ListBoxItem>("NavLookup"), window.FindControl<ListBox>("NavList")!.SelectedItem);
            Assert.Empty(ui.Confirms);
            Assert.Equal(0, service.ClearTokenCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingWhileStoppedAsksNothing()
    {
        var ui = new FakeUi();
        (MainWindow window, _, SettingsPageViewModel page) = await StoppedWindowAsync(new FakeIpInfoService(), ui, StartupFailure.ConfigUnreadable);
        bool closed = false;
        window.Closed += (_, _) => closed = true;

        // The Settings mark stays hidden too: nothing on the page can be edited while stopped
        Assert.False(page.HasUnsavedChanges);
        Assert.Contains("placeholder", window.FindControl<TextBlock>("NavSettingsMark")!.Classes);

        window.Close();
        Settle(window);

        Assert.True(closed);
        Assert.Empty(ui.Confirms);
    }

    [AvaloniaFact]
    public async Task AStopThatLandsWhileSettingsIsShownHidesThePage()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            TextBlock line = window.FindControl<TextBlock>("SettingsNotStartedText")!;
            Control settingsPage = window.FindControl<Control>("SettingsPage")!;
            Assert.True(Seen(window, line));
            Assert.True(Seen(window, settingsPage));

            service.StartupResult = new StartupResult(StartupFailure.ConfigStorageError, null, false);
            await vm.InitializeAsync();
            Settle(window);

            Assert.True(vm.IsStopped);
            Assert.False(Seen(window, line));
            Assert.False(Seen(window, settingsPage));
            Assert.False(window.FindControl<ScrollViewer>("PageScroll")!.IsVisible);
            Assert.True(Seen(window, window.FindControl<Border>("StartupStopCard")!));
            Assert.False(window.FindControl<ListBox>("NavList")!.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    // The close is held with no question and applies nothing the step gives. The step may also throw
    // OperationCanceledException, as the real one does for a cancel before its one look at the token.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClosePendingDuringAStartStepClosesTheWindowOnceTheStepReturns(bool throws)
    {
        var service = WithDataset();
        service.HeldStepThrowsWhenCancelled = throws;
        var gate = new TaskCompletionSource();
        service.InitializeGate = gate;
        var ui = new FakeUi();
        (MainViewModel vm, SettingsPageViewModel page) = NewWithSettingsPage(service, ui);
        int started = 0;
        vm.Started += (_, _) => started++;
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        string? pillAtClose = null;
        window.Closed += (_, _) =>
        {
            closed = true;
            pillAtClose = vm.PillText;
        };
        window.Show();
        Task launch = Task.CompletedTask;
        try
        {
            Settle(window);
            launch = vm.InitializeAsync();
            Assert.True(vm.IsStartStepRunning);

            window.Close();
            Settle(window);
            Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
            window.Close();
            Settle(window);
            Assert.False(closed);
            Assert.Empty(ui.Confirms);

            gate.SetResult();
            await WaitUntilAsync(() => closed);
            await launch;
            Assert.Empty(ui.Confirms);
            Assert.Equal(0, started);
            Assert.Equal(1, service.InitializeCalls);
            Assert.Equal(0, service.CheckCalls);
            Assert.Equal(throws ? 1 : 0, service.HeldStepsCancelled);
            Assert.Equal(AppText.ClosingAfterStartUp, pillAtClose);
            Assert.False(vm.IsStopped);
            Assert.Empty(ui.TokenPrompts);
        }
        finally
        {
            gate.TrySetResult();
            if (!closed)
                window.Close();
        }
    }

    // Avalonia 12.1.4, pinned headlessly: focusing the owner while an owned dialog is active moves the focus out of the
    // dialog, so its keys go to the owner. So the window moves no focus while it owns an open window.
    [AvaloniaFact]
    public void FocusingTheOwnerWhileAnOwnedDialogIsActiveTakesTheDialogsKeys()
    {
        var a = new TextBox();
        var owner = new Window { Content = a };
        var b = new TextBox();
        var owned = new Window { Content = b };
        owner.Show();
        try
        {
            _ = owned.ShowDialog(owner);
            owned.Activate();
            b.Focus();
            Settle(owned);
            Assert.True(b.IsFocused);
            Assert.Contains(owned, owner.OwnedWindows);

            a.Focus();
            Settle(owner);
            owned.KeyTextInput("xyz");
            Settle(owned);

            Assert.Equal("xyz", a.Text ?? "");
            Assert.Equal("", b.Text ?? "");
            Assert.False(b.IsFocused);
            Assert.Same(a, owner.FocusManager?.GetFocusedElement());
            Assert.Same(a, owned.FocusManager?.GetFocusedElement());
        }
        finally
        {
            owned.Close();
            owner.Close();
        }
    }

    // With no dataset and no token the token dialog opens before the posted focus runs. On Lookup the search box is
    // off while the prompt holds the gate; the nav item of another page is not.
    [AvaloniaTheory]
    [InlineData("NavLookup")]
    [InlineData("NavGoogleCloud")]
    public async Task AfterStartFreshTheTokenDialogKeepsTheKeysAndThePageTakesFocusOnceItCloses(string navItem)
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        (MainWindow window, MainViewModel vm, _) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigUnreadable);
        Navigate(window, navItem);
        Control focusTarget = navItem == "NavLookup"
            ? window.FindControl<TextBox>("SearchBox")!
            : window.FindControl<ListBoxItem>(navItem)!;
        var box = new TextBox();
        var dialog = new Window { Content = box };
        var answer = new TaskCompletionSource<string?>();
        ui.TokenPrompt = reason =>
        {
            _ = dialog.ShowDialog(window);
            dialog.Activate();
            box.Focus();
            return answer.Task;
        };
        try
        {
            service.StartFreshResult = new StartupResult(StartupFailure.None, null, false);
            Task leaving = vm.StartFreshCommand.ExecuteAsync(null);
            Settle(window);
            Settle(dialog);

            Assert.False(vm.IsStopped);
            Assert.Single(ui.TokenPrompts);
            Assert.True(box.IsFocused);
            Assert.False(focusTarget.IsFocused);
            dialog.KeyTextInput("abc");
            Assert.Equal("abc", box.Text ?? "");

            dialog.Close();
            answer.SetResult(null);
            await leaving;
            Settle(window);
            AssertShowing(window, navItem);
            Assert.True(focusTarget.IsFocused);
        }
        finally
        {
            answer.TrySetResult(null);
            dialog.Close();
            window.Close();
        }
    }

    // A page other than Lookup takes no focus into its boxes when shown, so after Start fresh the selected nav item has it
    [AvaloniaFact]
    public async Task AfterStartFreshOnAnotherPageTheSelectedNavItemHasFocus()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true };
        (MainWindow window, MainViewModel vm, _) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigUnreadable);
        try
        {
            Navigate(window, "NavGoogleCloud");
            AssertShowing(window, "NavGoogleCloud");
            window.FindControl<Button>("StartFreshButton")!.Focus();
            service.StartFreshResult = new StartupResult(StartupFailure.None, SampleDataset, true);

            await vm.StartFreshCommand.ExecuteAsync(null);
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);

            Assert.False(vm.IsStopped);
            AssertShowing(window, "NavGoogleCloud");
            Assert.True(window.FindControl<ListBoxItem>("NavGoogleCloud")!.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AfterStartFreshOnLookupTheSearchBoxHasFocus()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true };
        (MainWindow window, MainViewModel vm, _) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigUnreadable);
        try
        {
            Button startFresh = window.FindControl<Button>("StartFreshButton")!;
            startFresh.Focus();
            Assert.True(startFresh.IsFocused);
            service.StartFreshResult = new StartupResult(StartupFailure.None, SampleDataset, true);

            await vm.StartFreshCommand.ExecuteAsync(null);
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);

            Assert.False(vm.IsStopped);
            AssertShowing(window, "NavLookup");
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AfterStartFreshThePagesComeBackAndSettingsReloads()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true };
        (MainWindow window, MainViewModel vm, SettingsPageViewModel page) = await StoppedWindowAsync(service, ui, StartupFailure.ConfigUnreadable);
        try
        {
            // The fresh file's settings, as the service gives them once it has made the file
            service.Settings = service.Settings with { AbuseIpDbKeepDays = 90 };
            service.StartFreshResult = new StartupResult(StartupFailure.None, SampleDataset, true);

            Assert.Same(vm.StartFreshCommand, window.FindControl<Button>("StartFreshButton")!.Command);
            await vm.StartFreshCommand.ExecuteAsync(null);
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);

            Assert.Equal(1, service.StartFreshCalls);
            Assert.False(vm.IsStopped);
            Assert.False(window.FindControl<ScrollViewer>("StartupStop")!.IsVisible);
            Assert.True(window.FindControl<ScrollViewer>("PageScroll")!.IsVisible);
            Assert.True(window.FindControl<ListBox>("NavList")!.IsEnabled);
            AssertShowing(window, "NavLookup");

            Navigate(window, "NavSettings");
            Settle(window);
            Assert.True(page.IsStarted);
            Assert.Equal(90, page.Settings.AbuseIpDbKeepDays);
            Assert.Equal(90m, window.FindControl<NumericUpDown>("AbuseIpDbKeepDaysBox")!.Value);
            Assert.False(Seen(window, window.FindControl<TextBlock>("SettingsNotStartedText")!));
            Assert.All(SettingsCards, name => Assert.True(Seen(window, window.FindControl<Control>(name)!), name));
            Assert.True(page.CanEditInputs);
        }
        finally
        {
            window.Close();
        }
    }

    // A network typed on Settings but not added shows the mark from every page, and the close asks before dropping it
    [AvaloniaFact]
    public async Task ANetworkTypedButNotAddedMarksTheNavItemAndTheCloseAsks()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            Settle(window);
            Navigate(window, "NavSettings");
            TextBlock mark = window.FindControl<TextBlock>("NavSettingsMark")!;
            Assert.Contains("placeholder", mark.Classes);

            window.FindControl<TextBox>("NetworkBox")!.Text = "192.0.2.0/24";
            Navigate(window, "NavLookup");

            Assert.Empty(page.Exceptions.Rows);
            Assert.True(page.HasUnsavedChanges);
            Assert.DoesNotContain("placeholder", mark.Classes);
            Navigate(window, "NavSettings");
            Assert.Equal(AppText.ChangesNotSaved, window.FindControl<TextBlock>("ExceptionsStatus")!.Text);
            Assert.True(window.FindControl<Button>("ExceptionsSaveButton")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Button>("ExceptionsRevertButton")!.IsEffectivelyEnabled);

            window.Close();
            Settle(window);
            Assert.False(closed);
            Assert.Equal(AppText.CloseUnsavedTitle, Assert.Single(ui.Confirms).Title);
            Assert.Equal("192.0.2.0/24", page.Exceptions.NetworkText);

            page.RevertExceptionsCommand.Execute(null);
            Settle(window);
            Assert.Equal("", window.FindControl<TextBox>("NetworkBox")!.Text);
            Assert.Contains("placeholder", mark.Classes);
            Assert.Equal(0, service.SaveExceptionsCalls);
        }
        finally
        {
            if (!closed)
            {
                page.RevertExceptionsCommand.Execute(null);
                page.RevertSettingsCommand.Execute(null);
                window.Close();
            }
        }
    }

    // With no dataset a reset starts the download and gives the page back at once: its keys line shows the outcome
    // while the download runs
    [AvaloniaFact]
    public async Task AResetWithNoDatasetGivesThePageBackWhileTheDownloadRuns()
    {
        var service = new FakeIpInfoService { HasToken = true };
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, ui);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        var download = new TaskCompletionSource();
        try
        {
            Navigate(window, "NavSettings");
            Assert.Null(service.Dataset);
            Assert.Equal(1, service.UpdateCalls);
            service.UpdateGate = download;

            Task reset = page.ResetTokenCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => reset.IsCompleted);
            Settle(window);

            Assert.Equal(2, service.UpdateCalls);
            Assert.True(vm.IsBusy);
            Assert.False(page.IsWorking);
            Assert.True(page.CanEditInputs);
            Assert.Equal(AppText.TokenSaved, window.FindControl<TextBlock>("KeysStatus")!.Text);
            Assert.False(window.FindControl<Button>("ResetTokenButton")!.IsEffectivelyEnabled);
            Assert.NotNull(vm.PendingUpdate);

            download.SetResult();
            await vm.PendingUpdate!;
            Settle(window);
            Assert.False(vm.IsBusy);
            Assert.True(window.FindControl<Button>("ResetTokenButton")!.IsEffectivelyEnabled);
        }
        finally
        {
            download.TrySetResult();
            window.Close();
        }
    }

    // A token removed while another page was on show is read again when Settings is shown
    [AvaloniaFact]
    public async Task ShowingSettingsReadsTheKeyLinesAgain()
    {
        var service = WithDataset();
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service, abuse: abuse);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Settle(window);
            Assert.Equal(AppText.TokenSavedStatus, page.TokenStatusText);
            var raised = new List<string?>();
            page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            service.HasToken = false;
            abuse.HasKey = false;
            Navigate(window, "NavAzure");
            Assert.DoesNotContain(nameof(SettingsPageViewModel.TokenStatusText), raised);

            Navigate(window, "NavSettings");
            Assert.Contains(nameof(SettingsPageViewModel.TokenStatusText), raised);
            Assert.Contains(nameof(SettingsPageViewModel.AbuseKeyStatusText), raised);
            Assert.Equal(AppText.NoTokenStatus, window.FindControl<TextBlock>("TokenStatus")!.Text);
            Assert.Equal(AppText.NoAbuseKeyStatus, window.FindControl<TextBlock>("AbuseKeyStatus")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    // Turning IPv6 on or off on the page runs the update check again under the shared gate; another setting doesn't
    [AvaloniaFact]
    public async Task APageSaveThatTurnsIPv6OffRunsTheUpdateCheckAgain()
    {
        var service = WithDataset();
        (MainViewModel vm, SettingsPageViewModel page) = await StartWithSettingsPageAsync(service);
        var window = new MainWindow();
        window.AttachViewModel(vm, settings: page);
        window.Show();
        try
        {
            Navigate(window, "NavSettings");
            int checks = service.CheckCalls;
            Assert.Equal(1, checks);

            page.Settings.AbuseIpDbKeepDays = 60;
            await page.SaveSettingsCommand.ExecuteAsync(null);
            Settle(window);
            await (vm.PendingCheck ?? Task.CompletedTask);
            Assert.Equal(1, service.SaveSettingsCalls);
            Assert.Equal(checks, service.CheckCalls);

            page.Settings.IPv6Enabled = false;
            await page.SaveSettingsCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => service.CheckCalls == checks + 1);
            await (vm.PendingCheck ?? Task.CompletedTask);
            Settle(window);
            Assert.Equal(2, service.SaveSettingsCalls);
            Assert.False(service.Settings.IPv6Enabled);
        }
        finally
        {
            window.Close();
        }
    }

    // ----- the nav panel's keyboard answers -----

    // The nav list's panel is a DockPanel, so Settings can sit at the foot; for the arrow keys it must answer every
    // question exactly as a vertical StackPanel with as many children does, which is asked here, not assumed
    [AvaloniaFact]
    public void TheNavPanelAnswersEveryKeyAsAVerticalStackPanelDoes()
    {
        var differences = new List<string>();
        foreach (int count in new[] { 0, 1, 5 })
        {
            var nav = new NavItemsPanel();
            var stack = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
            foreach (Panel panel in new Panel[] { nav, stack })
            {
                for (int i = 0; i < count; i++)
                    panel.Children.Add(new Border { Child = new TextBlock { Text = "Item " + i } });
            }

            var outsideNav = new Border();
            var outsideStack = new Border();
            var host = new Window { Content = new StackPanel { Children = { nav, stack, outsideNav, outsideStack } } };
            host.Show();
            try
            {
                var froms = new List<(string Name, Func<Panel, Control?> Pick)>
                {
                    ("null", _ => null),
                    ("outside", panel => ReferenceEquals(panel, nav) ? outsideNav : outsideStack),
                };
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    froms.Add(($"child {index}", panel => panel.Children[index]));
                    froms.Add(($"inside child {index}", panel => ((Border)panel.Children[index]).Child));
                }

                foreach (NavigationDirection direction in Enum.GetValues<NavigationDirection>())
                {
                    foreach (bool wrap in new[] { false, true })
                    {
                        foreach ((string name, Func<Panel, Control?> pick) in froms)
                        {
                            int expected = IndexOfAnswer(stack, direction, pick(stack), wrap);
                            int actual = IndexOfAnswer(nav, direction, pick(nav), wrap);
                            if (expected != actual)
                                differences.Add($"{count} children, {direction} wrap={wrap} from {name}: StackPanel {expected}, nav {actual}");
                        }
                    }
                }
            }
            finally
            {
                host.Close();
            }
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    // The child index a panel answers, -1 for null, -2 for anything that isn't one of its children
    private static int IndexOfAnswer(Panel panel, NavigationDirection direction, Control? from, bool wrap)
    {
        IInputElement? answer = ((INavigableContainer)panel).GetControl(direction, from, wrap);
        return answer is null ? -1 : answer is Control control && panel.Children.Contains(control) ? panel.Children.IndexOf(control) : -2;
    }

    [AvaloniaFact]
    public void HelpDialogTitleMatchesTheAppInfoTitle()
    {
        var dialog = new HelpDialog();
        dialog.Show();
        try
        {
            Settle(dialog);
            Assert.Equal(AppInfo.HelpTitle, dialog.FindControl<TextBlock>("TitleText")!.Text);
            Assert.Equal(AppInfo.HelpTitle, dialog.Title);
            Assert.Equal(AppText.HelpSearchText(), dialog.FindControl<TextBlock>("SearchText")!.Text);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == AppText.HelpAbuseHeading && block.Classes.Contains("card-title"));
            Assert.Equal(AppText.HelpAbuseText(), dialog.FindControl<TextBlock>("AbuseText")!.Text);
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), button => button.Content is string text && text == AppText.Close);
        }
        finally
        {
            dialog.Close();
        }
    }

    // The sections scroll inside a capped height, so Close stays inside the dialog on a small screen
    [AvaloniaFact]
    public void HelpDialogIsCappedAndItsSectionsScrollAboveClose()
    {
        var dialog = new HelpDialog();
        dialog.Show();
        try
        {
            Settle(dialog);
            ScrollViewer scroll = dialog.FindControl<ScrollViewer>("HelpScroll")!;
            Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content is string text && text == AppText.Close);
            TextBlock abuseText = dialog.FindControl<TextBlock>("AbuseText")!;
            Point closeEnd = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), dialog)!.Value;

            Assert.True(dialog.ClientSize.Height <= 640, $"The dialog is {dialog.ClientSize.Height} px high.");
            Assert.True(closeEnd.Y <= dialog.ClientSize.Height && closeEnd.X <= dialog.ClientSize.Width,
                $"Close ends at {closeEnd} in {dialog.ClientSize}.");
            Assert.Contains(scroll, abuseText.GetVisualAncestors());
            Assert.Contains(scroll, dialog.FindControl<TextBlock>("TitleText")!.GetVisualAncestors());
            Assert.DoesNotContain(scroll, close.GetVisualAncestors());
        }
        finally
        {
            dialog.Close();
        }
    }

    // Close is the only focusable control, so the scroll keys must move the sections while it keeps the focus
    [AvaloniaFact]
    public void HelpDialogSectionsScrollFromTheKeyboardAndEscapeStillCloses() => HelpDialogScrollKeys(Key.Escape, PhysicalKey.Escape);

    [AvaloniaFact]
    public void HelpDialogSectionsScrollFromTheKeyboardAndEnterStillCloses() => HelpDialogScrollKeys(Key.Enter, PhysicalKey.Enter);

    private static void HelpDialogScrollKeys(Key closeKey, PhysicalKey closePhysicalKey)
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var dialog = new HelpDialog();
            Task shown = dialog.ShowDialog(owner);
            Settle(dialog);
            ScrollViewer scroll = dialog.FindControl<ScrollViewer>("HelpScroll")!;
            Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content is string text && text == AppText.Close);
            close.Focus();
            Settle(dialog);
            Assert.True(close.IsFocused);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, $"The sections fit: {scroll.Extent} in {scroll.Viewport}.");
            Assert.Equal(0, scroll.Offset.Y);

            double Press(Key key, PhysicalKey physicalKey)
            {
                dialog.KeyPress(key, RawInputModifiers.None, physicalKey, null);
                Settle(dialog);
                return scroll.Offset.Y;
            }

            double afterDown = Press(Key.Down, PhysicalKey.ArrowDown);
            Assert.True(afterDown > 0, $"Down left the offset at {afterDown}.");
            double afterPageDown = Press(Key.PageDown, PhysicalKey.PageDown);
            Assert.True(afterPageDown > afterDown, $"Page Down moved {afterDown} to {afterPageDown}.");
            double afterEnd = Press(Key.End, PhysicalKey.End);
            Assert.True(afterEnd >= afterPageDown, $"End moved {afterPageDown} to {afterEnd}.");
            Assert.Equal(scroll.Extent.Height, afterEnd + scroll.Viewport.Height, 3);

            double afterUp = Press(Key.Up, PhysicalKey.ArrowUp);
            Assert.True(afterUp < afterEnd, $"Up moved {afterEnd} to {afterUp}.");
            Assert.True(afterUp > 0, $"Up reached the top from {afterEnd}, so Home is not shown moving it.");
            Assert.Equal(0, Press(Key.Home, PhysicalKey.Home));

            Assert.Equal(afterEnd, Press(Key.End, PhysicalKey.End));
            double afterPageUp = Press(Key.PageUp, PhysicalKey.PageUp);
            Assert.True(afterPageUp < afterEnd, $"Page Up moved {afterEnd} to {afterPageUp}.");

            Assert.True(close.IsFocused);
            Assert.False(shown.IsCompleted);
            dialog.KeyPress(closeKey, RawInputModifiers.None, closePhysicalKey, null);
            Settle(dialog);
            Assert.True(shown.IsCompleted);
        }
        finally
        {
            owner.Close();
        }
    }
}
