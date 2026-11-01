using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.CloudSearch;

namespace PoorMansFeed.Tests;

/// <summary>Cloud Address Lookup's page in the real main window, over a stub search service.</summary>
public sealed partial class MainWindowTests
{
    private static readonly DateTimeOffset CloudSearchSaved = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    private static async Task<(MainWindow Window, CloudAddressSearchViewModel Page, CloudSearchStub Search, FakeUi Ui)>
        ShowCloudSearchAsync()
    {
        var ui = new FakeUi();
        var search = new CloudSearchStub();
        var page = new CloudAddressSearchViewModel(search, ui);
        var vm = await StartAsync(WithDataset());
        var window = new MainWindow();
        window.AttachViewModel(vm, cloudSearch: page);
        window.Show();
        Navigate(window, "NavCloudSearch");
        return (window, page, search, ui);
    }

    private static CloudAddressSearchResult CloudSearchFound(params CloudAddressMatch[] matches)
    {
        Assert.True(SearchQuery.TryParse("2600:1f18::1", out SearchQuery? query));
        SavedRangesStatus[] sources =
        [
            new(CloudProvider.GoogleCloud, SavedRangesState.Read, CloudSearchSaved),
            new(CloudProvider.Azure, SavedRangesState.Read, CloudSearchSaved),
            new(CloudProvider.Aws, SavedRangesState.Read, CloudSearchSaved),
        ];
        return new CloudAddressSearchResult(CloudAddressSearchOutcome.Found, query, matches, sources);
    }

    private static CloudAddressMatch CloudMatch(string network, string service, string region, string? scope)
    {
        Assert.True(CidrBlock.TryParse(network, out CidrBlock? block));
        return new CloudAddressMatch(CloudProvider.Aws, service, region, scope, block, service == "AMAZON");
    }

    private static CloudAddressMatch[] TwoCloudMatches() =>
    [
        CloudMatch("2600:1f18:4000::/36", "EC2", "us-east-1", "us-east-1-iah-1"),
        CloudMatch("2600:1f18::/33", "AMAZON", "us-east-1", "us-east-1"),
    ];

    [AvaloniaFact]
    public async Task CloudAddressLookupShowsItsPageOverTheAttachedModel()
    {
        (MainWindow window, CloudAddressSearchViewModel page, _, _) = await ShowCloudSearchAsync();
        try
        {
            Assert.Same(page, window.FindControl<Control>("CloudSearchPage")!.DataContext);
            AssertShowing(window, "NavCloudSearch");
            Assert.Equal(AppText.CloudSearchTitle,
                window.FindControl<StackPanel>("CloudSearchHeading")!.Children.OfType<TextBlock>().First().Text);

            Navigate(window, "NavAws");
            AssertShowing(window, "NavAws");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupSearchesOnEnterWithNoModifierOnly()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            TextBox box = window.FindControl<TextBox>("CloudSearchBox")!;
            box.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.Equal(0, search.Calls);

            window.KeyTextInput("8.8.8.8");
            Settle(window);
            Assert.Equal("8.8.8.8", page.Query);

            window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, null);
            Settle(window);
            Assert.Equal(0, search.Calls);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitUntilAsync(() => !page.IsWorking && search.Calls == 1);
            Assert.Equal("8.8.8.8", search.LastInput);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupSwapsTheEmptyLineForTheRowsAfterAnAnswer()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            TextBlock empty = window.FindControl<TextBlock>("CloudSearchEmpty")!;
            ItemsControl list = window.FindControl<ItemsControl>("CloudSearchList")!;
            var scroll = (ScrollViewer)list.Parent!;
            Assert.True(empty.IsEffectivelyVisible);
            Assert.Equal(AppText.CloudSearchEmptyInitial, empty.Text);
            Assert.False(scroll.IsVisible);

            search.Next = CloudSearchFound(TwoCloudMatches());
            page.Query = "2600:1f18::1";
            await page.SearchCommand.ExecuteAsync(null);
            Settle(window);

            Assert.False(empty.IsVisible);
            Assert.True(scroll.IsVisible);
            List<Border> rows = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("row")).ToList();
            Assert.Equal(page.Rows, rows.Select(row => row.DataContext));
            // Two lines in a notice row: the network, provider and region, then the service and the scope
            Assert.Equal(["2600:1f18:4000::/36", "AWS", "us-east-1", "EC2", "us-east-1-iah-1"],
                rows[0].GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            Assert.All(rows, row => Assert.Contains("notice", row.Classes));
            List<TextBlock> cells = rows[0].GetVisualDescendants().OfType<TextBlock>().ToList();
            double top = cells[0].TranslatePoint(default, rows[0])!.Value.Y;
            Assert.All(cells.Take(3), cell => Assert.Equal(top, cell.TranslatePoint(default, rows[0])!.Value.Y, 0));
            Assert.All(cells.Skip(3), cell => Assert.True(cell.TranslatePoint(default, rows[0])!.Value.Y >= top + cells[0].Bounds.Height));
            Assert.All(cells.Skip(3), cell => Assert.Contains("caption", cell.Classes));
            Assert.Equal(page.Rows[0].AccessibleText, Avalonia.Automation.AutomationProperties.GetName(rows[0]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupsRowMenuCopiesThatRowsNetwork()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, FakeUi ui) = await ShowCloudSearchAsync();
        try
        {
            search.Next = CloudSearchFound(TwoCloudMatches());
            page.Query = "2600:1f18::1";
            await page.SearchCommand.ExecuteAsync(null);
            Settle(window);

            ItemsControl list = window.FindControl<ItemsControl>("CloudSearchList")!;
            CloudAddressRow shown = page.Rows[1];
            Border row = RightClickGap(window, list, shown, textIndex: 0);
            ContextMenu menu = row.ContextMenu!;
            Assert.True(menu.IsOpen);
            MenuItem copy = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Equal(AppText.CopyNetwork, copy.Header);
            Assert.Same(page.CopyNetworkCommand, copy.Command);
            Assert.Same(shown, copy.CommandParameter);
            Assert.True(copy.IsEffectivelyEnabled);

            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntilAsync(() => ui.ClipboardText is not null);
            Assert.Equal("2600:1f18::/33", ui.ClipboardText);
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupChangesNoHeightWhileASearchRuns()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            string[] names = ["CloudSearchControls", "CloudSearchCard", "CloudSearchGoogleSource", "CloudSearchAzureSource",
                              "CloudSearchAwsSource", "CloudSearchAction"];
            Rect[] Bounds() => names.Select(name => window.FindControl<Control>(name)!.Bounds).ToArray();

            Rect[] before = Bounds();
            Assert.All(before, bounds => Assert.True(bounds.Height > 0));
            var held = new TaskCompletionSource<CloudAddressSearchResult>();
            search.Held = held;
            page.Query = "2600:1f18::1";
            Task press = page.SearchCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(page.IsWorking);
            Assert.Equal(before, Bounds());

            held.SetResult(CloudSearchFound(TwoCloudMatches()));
            await press;
            Settle(window);
            Assert.True(page.HasRows && page.HasSourceTexts);
            Assert.Equal(before, Bounds());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupSearchesItsLastAddressAgainWhenShownAgain()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            Assert.Equal(0, search.Calls);
            search.Next = CloudSearchFound(TwoCloudMatches());
            page.Query = "2600:1f18::1";
            await page.SearchCommand.ExecuteAsync(null);
            page.Query = "8.8.8.8";

            Navigate(window, "NavAws");
            Settle(window);
            Assert.Equal(1, search.Calls);

            Navigate(window, "NavCloudSearch");
            await WaitUntilAsync(() => !page.IsWorking && search.Calls == 2);
            Assert.Equal("2600:1f18::1", search.LastInput);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CloudAddressLookupsEnterSearchesNothingWhileASearchIsOut()
    {
        (MainWindow window, CloudAddressSearchViewModel page, CloudSearchStub search, _) = await ShowCloudSearchAsync();
        try
        {
            var held = new TaskCompletionSource<CloudAddressSearchResult>();
            search.Held = held;
            page.Query = "2600:1f18::1";
            Task press = page.SearchCommand.ExecuteAsync(null);
            Assert.True(page.IsWorking);

            window.FindControl<TextBox>("CloudSearchBox")!.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.Equal(1, search.Calls);

            held.SetResult(CloudSearchFound(TwoCloudMatches()));
            await press;
            Assert.Equal(1, search.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowDisposesCloudAddressLookup()
    {
        (MainWindow window, CloudAddressSearchViewModel page, _, _) = await ShowCloudSearchAsync();
        try
        {
            page.Query = "8.8.8.8";
            Assert.True(page.SearchCommand.CanExecute(null));

            window.Close();

            Assert.False(page.SearchCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class CloudSearchStub : ICloudAddressSearchService
    {
        public CloudAddressSearchResult? Next { get; set; }
        public TaskCompletionSource<CloudAddressSearchResult>? Held { get; set; }
        public int Calls { get; private set; }
        public string? LastInput { get; private set; }

        public IReadOnlyList<SavedRangesStatus> Sources { get; set; } = [];
        public TaskCompletionSource<IReadOnlyList<SavedRangesStatus>>? HeldSources { get; set; }
        public Exception? SourcesThrow { get; set; }
        public int SourceReads { get; private set; }

        public async Task<IReadOnlyList<SavedRangesStatus>> ReadSourcesAsync(CancellationToken ct)
        {
            SourceReads++;
            if (SourcesThrow is not null)
                throw SourcesThrow;
            if (HeldSources is { } held)
            {
                HeldSources = null;
                return await held.Task.WaitAsync(ct);
            }
            return Sources;
        }

        public async Task<CloudAddressSearchResult> SearchAsync(string? input, CancellationToken ct)
        {
            Calls++;
            LastInput = input;
            if (Held is { } held)
            {
                Held = null;
                return await held.Task;
            }

            return Next ?? new CloudAddressSearchResult(CloudAddressSearchOutcome.NoMatches, null, [], []);
        }
    }
}
