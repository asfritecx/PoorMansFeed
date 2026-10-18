using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GraphiteUi;

namespace PoorMansFeed;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closeCheckRunning;
    private bool _initializeOnOpen;
    private bool _initializationStarted;
    private GoogleCloudFeedViewModel? _google;
    private AzureFeedViewModel? _azure;
    private AwsFeedViewModel? _aws;
    private bool _pagesLoaded;

    public MainWindow()
    {
        InitializeComponent();
        _pagesLoaded = true;
    }

    internal MainWindow(MainViewModel viewModel) : this()
    {
        AttachViewModel(viewModel);
    }

    // initializeOnOpen starts the view model's start-up when the window opens; headless tests leave it off.
    // google, azure and aws may be null: their pages stay hidden and their bindings are not touched.
    internal void AttachViewModel(MainViewModel viewModel, GoogleCloudFeedViewModel? google = null, AzureFeedViewModel? azure = null,
                                  AwsFeedViewModel? aws = null, bool initializeOnOpen = false)
    {
        DataContext = viewModel;
        _google = google;
        _azure = azure;
        _aws = aws;
        _initializeOnOpen = initializeOnOpen;
        SetDataContext("ToolsPage", google);
        SetDataContext("AzureHeading", azure);
        SetDataContext("AzurePage", azure);
        SetDataContext("AwsHeading", aws);
        SetDataContext("AwsPage", aws);
    }

    private void SetDataContext(string name, object? dataContext)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.DataContext = dataContext;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
        if (this.FindControl<Control>("LookupPage") is { IsVisible: true })
            this.FindControl<TextBox>("SearchBox")?.Focus();
        if (!_initializeOnOpen || _initializationStarted || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _initializationStarted = true;
        await viewModel.InitializeAsync();
    }

    // House convention, not an Avalonia rule: a small display (such as a 1024x768 server session) gets the largest
    // size that fits its working area, re-centred, instead of a window that runs off the screen. The page
    // ScrollViewer keeps everything reachable below the page's minimum height.
    private void FitToScreen()
    {
        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        // WorkingArea and Position are physical pixels on Windows (Scaling = monitor DPI / 96) and points on macOS,
        // where Avalonia.Native always reports Scaling = 1, so dividing by Scaling gives DIPs on both
        Size chrome = (FrameSize ?? ClientSize) - ClientSize;
        double availableWidth = screen.WorkingArea.Width / screen.Scaling - chrome.Width;
        double availableHeight = screen.WorkingArea.Height / screen.Scaling - chrome.Height;
        if (Width <= availableWidth && Height <= availableHeight)
        {
            return;
        }

        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        var frame = PixelSize.FromSize(new Size(Width + chrome.Width, Height + chrome.Height), screen.Scaling);
        Position = screen.WorkingArea.CenterRect(new PixelRect(frame)).Position;
    }

    /// <summary>Closes without the busy prompt; used once a shutdown that already did its work asks the window to close.</summary>
    internal void CloseApproved()
    {
        _closeApproved = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeApproved || DataContext is not MainViewModel { IsBusy: true } viewModel)
        {
            return;
        }

        // Cancel the close now; close again from code once the user agrees and the work has stopped
        e.Cancel = true;
        if (_closeCheckRunning)
        {
            return;
        }

        _closeCheckRunning = true;
        _ = ConfirmCloseAsync(viewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _google?.Dispose();
        _azure?.Dispose();
        _aws?.Dispose();
        base.OnClosed(e);
    }

    // The nav list always has one item selected. Its first selection fires while the XAML loads, before the name
    // scope exists; the XAML already shows that page.
    private void NavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_pagesLoaded)
            return;

        WindowPage? page = (sender as ListBox)?.SelectedItem is Control { Name: var name } ? name switch
        {
            "NavLookup" => WindowPage.Lookup,
            "NavGoogleCloud" => WindowPage.GoogleCloud,
            "NavAzure" => WindowPage.Azure,
            "NavAws" => WindowPage.Aws,
            _ => null,
        } : null;
        if (page is { } shown)
            ShowPage(shown);
    }

    private void ShowPage(WindowPage page)
    {
        SetVisible("LookupHeading", page == WindowPage.Lookup);
        SetVisible("LookupPage", page == WindowPage.Lookup);
        SetVisible("ToolsHeading", page == WindowPage.GoogleCloud);
        SetVisible("ToolsPage", page == WindowPage.GoogleCloud);
        SetVisible("AzureHeading", page == WindowPage.Azure);
        SetVisible("AzurePage", page == WindowPage.Azure);
        SetVisible("AwsHeading", page == WindowPage.Aws);
        SetVisible("AwsPage", page == WindowPage.Aws);

        // Enter runs a search only on the lookup page
        if (this.FindControl<Button>("SearchButton") is { } search)
            search.IsDefault = page == WindowPage.Lookup;

        switch (page)
        {
            case WindowPage.Lookup:
                this.FindControl<TextBox>("SearchBox")?.Focus();
                break;
            case WindowPage.GoogleCloud when _google is not null:
                _ = _google.ActivateAsync();
                break;
            case WindowPage.Azure when _azure is not null:
                _ = _azure.ActivateAsync();
                break;
            case WindowPage.Aws when _aws is not null:
                _ = _aws.ActivateAsync();
                break;
        }
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.IsVisible = visible;
    }

    private async Task ConfirmCloseAsync(MainViewModel viewModel)
    {
        try
        {
            if (await viewModel.ConfirmCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeCheckRunning = false;
        }
    }

    private async void ShowAboutClicked(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private async void ShowHelpClicked(object? sender, RoutedEventArgs e) => await new HelpDialog().ShowDialog(this);

    private enum WindowPage { Lookup, GoogleCloud, Azure, Aws }
}
