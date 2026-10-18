using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
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
        return new AzureFeedViewModel(feeds, ipinfo, new FakeUi(), TimeProvider.System);
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
        return new AwsFeedViewModel(feeds, ipinfo, new FakeUi(), TimeProvider.System);
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
    private static Border ListCard(MainWindow window, string page) => page == "LookupPage"
        ? window.FindControl<Border>("ResultsCard")!
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
    public async Task ClosingTheWindowDisposesAllThreeToolPages()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        AzureFeedViewModel azure = NewAzureViewModel(service, out _);
        AwsFeedViewModel aws = NewAwsViewModel(service, out _);
        var google = new GoogleCloudFeedViewModel(
            new GoogleCloudFeedService(service, new HttpClient(), new CoreOptions(), TimeProvider.System, Path.GetTempPath()),
            service, new FakeUi());
        var window = new MainWindow();
        window.AttachViewModel(vm, google, azure, aws);
        window.Show();
        Settle(window);
        Assert.True(azure.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(aws.CheckForUpdatesCommand.CanExecute(null));
        Assert.True(google.CheckCommand.CanExecute(null));

        window.Close();

        Assert.False(azure.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(aws.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(google.CheckCommand.CanExecute(null));
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

    [AvaloniaFact]
    public async Task BusyUpdateDisablesTheGatedButtonsAndEnablesCancel()
    {
        var service = WithDataset();
        var vm = await StartAsync(service);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Button update = window.FindControl<Button>("UpdateButton")!;
            Button settings = window.FindControl<Button>("SettingsButton")!;
            Button resetToken = window.FindControl<Button>("ResetTokenButton")!;
            Button export = window.FindControl<Button>("ExportButton")!;
            Button cancel = window.FindControl<Button>("CancelUpdateButton")!;
            Assert.True(update.IsEffectivelyEnabled);
            Assert.True(settings.IsEffectivelyEnabled);
            Assert.True(resetToken.IsEffectivelyEnabled);
            Assert.False(cancel.IsEffectivelyEnabled);

            var gate = new TaskCompletionSource();
            service.UpdateGate = gate;
            vm.UpdateDatasetCommand.Execute(null);
            Settle(window);
            Assert.False(update.IsEffectivelyEnabled);
            Assert.False(settings.IsEffectivelyEnabled);
            Assert.False(resetToken.IsEffectivelyEnabled);
            Assert.False(export.IsEffectivelyEnabled);
            Assert.True(cancel.IsEffectivelyEnabled);

            gate.SetResult();
            await (vm.UpdateDatasetCommand.ExecutionTask ?? Task.CompletedTask);
            Settle(window);
            Assert.True(update.IsEffectivelyEnabled);
            Assert.True(settings.IsEffectivelyEnabled);
            Assert.True(resetToken.IsEffectivelyEnabled);
            Assert.False(cancel.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task IconOnlyButtonsHaveAnAccessibleNameAndATooltip()
    {
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            foreach (string name in new[] { "SettingsButton", "ResetTokenButton", "HelpButton", "AboutButton", "CopyResultsButton" })
            {
                Button button = window.FindControl<Button>(name)!;
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), name);
                Assert.NotNull(ToolTip.GetTip(button));
            }
        }
        finally
        {
            window.Close();
        }
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
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), button => (string?)button.Content == AppText.Close);
        }
        finally
        {
            dialog.Close();
        }
    }
}
