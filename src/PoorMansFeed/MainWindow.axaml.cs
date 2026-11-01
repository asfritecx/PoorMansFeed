using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using GraphiteUi;
using PoorMansFeed.Core;

namespace PoorMansFeed;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _confirmingClose;
    private bool _startStepWaitRunning;
    // Set once the window has closed, so a close question still open then (the session ended under it) does nothing
    private bool _closed;
    private bool _wasStopped;
    private WindowPage _shownPage = WindowPage.Lookup;
    private bool _initializeOnOpen;
    private bool _initializationStarted;
    private GoogleCloudFeedViewModel? _google;
    private AzureFeedViewModel? _azure;
    private AwsFeedViewModel? _aws;
    private CloudAddressSearchViewModel? _cloudSearch;
    private MainViewModel? _main;
    private SettingsPageViewModel? _settings;
    private AbuseIpDbViewModel? _abuseIpDb;
    private SavedFeedsViewModel? _savedFeeds;
    private RefreshAllRunner? _refreshAll;
    private RefreshTimer? _refreshTimer;
    private bool _pagesLoaded;
    // One holder of the App Nap hold: Avalonia's token isn't counted, so a second request would be ended by the first
    private Task<IDisposable>? _appNapRequest;
    private IDisposable? _appNapHold;
    // A request that failed isn't made again until the run ends, since the runner changes at every progress report
    private bool _appNapRefused;
    // One update window for the window's life: closing it only hides it
    private DatasetUpdateWindow? _updateWindow;

    public MainWindow()
    {
        RequestAppNapHold = RequestAppNapHoldFromPlatform;
        InitializeComponent();
        _pagesLoaded = true;
        // Tunnel, so the row is recorded before the list's menu, a bubble handler, sees the request
        this.FindControl<ItemsControl>("ResultsList")!.AddHandler(ContextRequestedEvent, RecordMenuRow, RoutingStrategies.Tunnel);
    }

    // The list's own view model, which the menu's commands bind to; a request from no row, such as a gap, records null
    private void RecordMenuRow(object? sender, ContextRequestedEventArgs e) =>
        ((sender as Control)?.DataContext as MainViewModel)?.SetMenuRow((e.Source as Control)?.DataContext as ResultRow);

    internal MainWindow(MainViewModel viewModel) : this()
    {
        AttachViewModel(viewModel);
    }

    // initializeOnOpen starts the view model's start-up when the window opens; headless tests leave it off. Every
    // page's model (google to cloudSearch) may be null: its page stays empty and its bindings are not touched.
    internal void AttachViewModel(MainViewModel viewModel, GoogleCloudFeedViewModel? google = null, AzureFeedViewModel? azure = null,
                                  AwsFeedViewModel? aws = null, SettingsPageViewModel? settings = null,
                                  bool initializeOnOpen = false, AbuseIpDbViewModel? abuseIpDb = null,
                                  CloudAddressSearchViewModel? cloudSearch = null,
                                  SavedFeedsViewModel? savedFeeds = null, IFeedRecords? records = null,
                                  IUiServices? ui = null, IWorkGate? gate = null,
                                  RefreshAllRunner? refreshAll = null, RefreshTimer? refreshTimer = null)
    {
        if (_main is not null)
        {
            _main.Started -= MainStarted;
            _main.PropertyChanged -= MainPropertyChanged;
            _main.DatasetChanged -= MainDatasetChanged;
        }
        if (_settings is not null)
            _settings.SettingsSaved -= SettingsSaved;
        if (_abuseIpDb is not null)
            _abuseIpDb.LookUpRequested -= AbuseLookUpRequested;
        if (_refreshAll is not null)
            _refreshAll.Changed -= RefreshAllChanged;

        DataContext = viewModel;
        _main = viewModel;
        _google = google;
        _azure = azure;
        _aws = aws;
        _cloudSearch = cloudSearch;
        _settings = settings;
        _abuseIpDb = abuseIpDb;
        _savedFeeds = savedFeeds;
        _refreshAll = refreshAll;
        _refreshTimer = refreshTimer;
        _initializeOnOpen = initializeOnOpen;
        SetDataContext("ToolsPage", google);
        SetDataContext("AzureHeading", azure);
        SetDataContext("AzurePage", azure);
        SetDataContext("AwsHeading", aws);
        SetDataContext("AwsPage", aws);
        SetDataContext("CloudSearchPage", cloudSearch);
        SetDataContext("SettingsPage", settings);
        SetDataContext("NavSettingsMark", settings);
        SetDataContext("AbuseIpDbHeading", abuseIpDb);
        SetDataContext("AbuseIpDbPage", abuseIpDb);
        SetDataContext("SavedFeedsHeading", savedFeeds);
        SetDataContext("SavedFeedsPage", savedFeeds);
        SetDataContext("ChangeHistoryHeading", savedFeeds);
        SetDataContext("ChangeHistoryPage", savedFeeds);

        viewModel.UseCloseChain(BuildCloseChain(viewModel, settings, refreshAll, abuseIpDb, savedFeeds, records, ui, gate));
        _wasStopped = viewModel.IsStopped;
        viewModel.Started += MainStarted;
        viewModel.PropertyChanged += MainPropertyChanged;
        viewModel.DatasetChanged += MainDatasetChanged;
        if (settings is not null)
            settings.SettingsSaved += SettingsSaved;
        if (abuseIpDb is not null)
            abuseIpDb.LookUpRequested += AbuseLookUpRequested;
        if (refreshAll is not null)
            refreshAll.Changed += RefreshAllChanged;
    }

    // The settings and exceptions are read when start-up has opened the service, so the page takes them again then,
    // and the timer starts then, so its first reading of the setting is the saved one
    private void MainStarted(object? sender, EventArgs e)
    {
        _settings?.Reload();
        _refreshTimer?.Start();
    }

    /// <summary>Asks the platform to keep App Nap off; replaced by the tests, since headless has no App Nap.</summary>
    internal Func<Task<IDisposable>> RequestAppNapHold { get; set; }

    private Task<IDisposable> RequestAppNapHoldFromPlatform() =>
        RequestPlatformInhibition(PlatformInhibitionType.AppSleep, AppNapHoldReason);

    // macOS may list it among the app's activities; nothing in the window shows it
    private const string AppNapHoldReason = "Refreshing saved feeds";

    // A timed run happens with the window hidden, when App Nap would slow the download and the import
    private void RefreshAllChanged(object? sender, EventArgs e)
    {
        if (_closed || _refreshAll is not { IsRunning: true })
        {
            ReleaseAppNapHold();
            _appNapRefused = false;
            return;
        }

        if (_appNapHold is not null || _appNapRequest is not null || _appNapRefused)
            return;

        Task<IDisposable> request;
        try
        {
            request = RequestAppNapHold();
        }
        catch (Exception ex)
        {
            _appNapRefused = true;
            TraceAppNapFailure(ex);
            return;
        }

        _appNapRequest = request;
        _ = KeepAppNapHoldAsync(request);
    }

    // The request completes later, so the run may have ended or the window closed by then
    private async Task KeepAppNapHoldAsync(Task<IDisposable> request)
    {
        IDisposable? hold = null;
        try
        {
            hold = await request;
        }
        catch (Exception ex)
        {
            _appNapRefused = _refreshAll is { IsRunning: true };
            TraceAppNapFailure(ex);
        }

        _appNapRequest = null;
        if (hold is null)
            return;

        if (_closed || _appNapHold is not null || _refreshAll is not { IsRunning: true })
            hold.Dispose();
        else
            _appNapHold = hold;
    }

    private void ReleaseAppNapHold()
    {
        _appNapHold?.Dispose();
        _appNapHold = null;
    }

    private static void TraceAppNapFailure(Exception ex) =>
        System.Diagnostics.Trace.TraceWarning($"The App Nap hold couldn't be taken: {ex.GetType().Name}: {ex.Message}");

    // When the stop gives way, its buttons are hidden, so focus goes where showing the page would put it. The
    // bindings may not have shown the page yet when this runs, so the focus waits for the dispatcher.
    private void MainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsStopped) || sender is not MainViewModel viewModel)
            return;

        bool wasStopped = _wasStopped;
        _wasStopped = viewModel.IsStopped;
        if (wasStopped && !viewModel.IsStopped)
            Dispatcher.UIThread.Post(FocusShownPageWhenNoDialog);
    }

    // Focus moved into this window while it owns an open dialog (the token dialog after Start fresh) would take the
    // dialog's keys, so the focus waits until the last owned window has closed
    private void FocusShownPageWhenNoDialog()
    {
        if (OwnedWindows.FirstOrDefault(owned => owned.IsVisible) is { } open)
        {
            open.Closed += OwnedWindowClosed;
            return;
        }

        FocusShownPage();
    }

    private void OwnedWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window owned)
            owned.Closed -= OwnedWindowClosed;
        Dispatcher.UIThread.Post(FocusShownPageWhenNoDialog);
    }

    // Lookup takes the search box, as ShowPage gives it. The other pages move no focus into their boxes when shown,
    // so they take the nav list's selected item, which names the page and is where the arrow keys go on from.
    private void FocusShownPage()
    {
        if (_shownPage == WindowPage.Lookup)
            this.FindControl<TextBox>("SearchBox")?.Focus();
        else if (this.FindControl<ListBox>("NavList")?.SelectedItem is Control item)
            item.Focus();
    }

    // Only the AbuseIPDB page on show groups again (ActivateAsync never throws): at a launch the dataset arrives while
    // Lookup is on show, and a grouping then would run beside the user's searches.
    private void MainDatasetChanged(object? sender, EventArgs e)
    {
        if (_shownPage == WindowPage.AbuseIpDb)
            _ = _abuseIpDb?.ActivateAsync();
    }

    // Look up on an offending network shows Lookup, as its nav item does, and searches the network's ASN there
    private void AbuseLookUpRequested(object? sender, string asnText)
    {
        if (this.FindControl<ListBox>("NavList") is { } nav && this.FindControl<ListBoxItem>("NavLookup") is { } item)
            nav.SelectedItem = item;
        _ = _main?.SearchForAsync(asnText);
    }

    // A save on the Settings page is followed on Lookup (the export folder, the dataset check and the open search)
    // and by the timer
    private void SettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _refreshTimer?.SettingsSaved();
        _ = _main?.ApplySavedSettingsAsync(e.IPv6Changed);
    }

    /// <summary>
    /// Shows the Settings page, as its nav item does. The macOS menu's Settings item calls it. While start-up has
    /// stopped no page is reachable, so it does nothing.
    /// </summary>
    internal void ShowSettingsPage()
    {
        if (DataContext is MainViewModel { IsStopped: true })
            return;

        if (this.FindControl<ListBox>("NavList") is { } nav && this.FindControl<ListBoxItem>("NavSettings") is { } item)
            nav.SelectedItem = item;
    }

    /// <summary>
    /// The macOS menu's Reset API token: shows the Settings page and runs its Reset token, so the outcome is on the
    /// page's keys line. Nothing runs while the page's command can't.
    /// </summary>
    internal void ResetTokenFromMenu()
    {
        ShowSettingsPage();
        if (_settings?.ResetTokenCommand.CanExecute(null) == true)
            _settings.ResetTokenCommand.Execute(null);
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
    // size that fits its working area, re-centred, instead of a window that runs off the screen.
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
        if (_closed)
        {
            return;
        }

        _closeApproved = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (HoldsClose(e.CloseReason, e.IsProgrammatic, OperatingSystem.IsWindows()))
        {
            e.Cancel = true;
        }
    }

    /// <summary>The operating system ending the session: closes with no question, even with an update running or
    /// unsaved edits. A start step still holds the close.</summary>
    internal static bool EndsTheSession(WindowCloseReason reason, bool isProgrammatic, bool isWindows) =>
        reason == WindowCloseReason.OSShutdown
        || (isWindows && reason == WindowCloseReason.ApplicationShutdown && !isProgrammatic);

    // Whether a close is held, starting the wait or the question that closes the window later. Tests call it with
    // each reason, because Avalonia's WindowClosingEventArgs has no public constructor.
    internal bool HoldsClose(WindowCloseReason reason, bool isProgrammatic, bool isWindows)
    {
        if (!_closeApproved && DataContext is MainViewModel { IsStartStepRunning: true } starting)
        {
            if (!_startStepWaitRunning)
            {
                _startStepWaitRunning = true;
                _ = CloseAfterStartStepAsync(starting);
            }

            return true;
        }

        // The session ending is let through: the edits are lost, and closing cancels an update (build-then-swap keeps
        // the old dataset usable)
        if (EndsTheSession(reason, isProgrammatic, isWindows))
        {
            return false;
        }

        if (_closeApproved || _main?.CloseChain is not { HoldsClose: true } chain)
        {
            return false;
        }

        // Hold the close now; close again from code once the user agrees and the work has stopped
        if (!chain.IsRunning && !_confirmingClose)
        {
            _ = ConfirmAndCloseAsync(chain);
        }

        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        // First, so no tick starts a run while the pages and the runner go
        _refreshTimer?.Dispose();
        ReleaseAppNapHold();
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _google?.Dispose();
        _azure?.Dispose();
        _aws?.Dispose();
        _cloudSearch?.Dispose();
        _settings?.Dispose();
        _abuseIpDb?.Dispose();
        _savedFeeds?.Dispose();
        _refreshAll?.Dispose();
        _updateWindow?.CloseWithOwner();
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
            "NavCloudSearch" => WindowPage.CloudSearch,
            "NavAbuseIpDb" => WindowPage.AbuseIpDb,
            "NavSavedFeeds" => WindowPage.SavedFeeds,
            "NavChangeHistory" => WindowPage.ChangeHistory,
            "NavSettings" => WindowPage.Settings,
            _ => null,
        } : null;
        if (page is { } shown)
            ShowPage(shown);
    }

    private void ShowPage(WindowPage page)
    {
        _shownPage = page;
        SetVisible("LookupHeading", page == WindowPage.Lookup);
        SetVisible("LookupPage", page == WindowPage.Lookup);
        SetVisible("ToolsHeading", page == WindowPage.GoogleCloud);
        SetVisible("ToolsPage", page == WindowPage.GoogleCloud);
        SetVisible("AzureHeading", page == WindowPage.Azure);
        SetVisible("AzurePage", page == WindowPage.Azure);
        SetVisible("AwsHeading", page == WindowPage.Aws);
        SetVisible("AwsPage", page == WindowPage.Aws);
        SetVisible("CloudSearchHeading", page == WindowPage.CloudSearch);
        SetVisible("CloudSearchPage", page == WindowPage.CloudSearch);
        SetVisible("AbuseIpDbHeading", page == WindowPage.AbuseIpDb);
        SetVisible("AbuseIpDbPage", page == WindowPage.AbuseIpDb);
        SetVisible("SavedFeedsHeading", page == WindowPage.SavedFeeds);
        SetVisible("SavedFeedsPage", page == WindowPage.SavedFeeds);
        SetVisible("ChangeHistoryHeading", page == WindowPage.ChangeHistory);
        SetVisible("ChangeHistoryPage", page == WindowPage.ChangeHistory);
        SetVisible("SettingsHeading", page == WindowPage.Settings);
        SetVisible("SettingsPage", page == WindowPage.Settings);

        // Enter runs a search only on the lookup page. On the Settings page only the two exception boxes take Enter
        // (ExceptionBoxKeyDown). Showing Settings moves no focus into its boxes.
        if (this.FindControl<Button>("SearchButton") is { } search)
            search.IsDefault = page == WindowPage.Lookup;

        // Before the show's read, so a list change heard during it is handled as on show
        if (_abuseIpDb is not null)
            _abuseIpDb.IsOnShow = page == WindowPage.AbuseIpDb;

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
            case WindowPage.CloudSearch when _cloudSearch is not null:
                // A provider page may have saved new ranges since the last answer
                _ = _cloudSearch.RefreshAsync();
                break;
            case WindowPage.AbuseIpDb when _abuseIpDb is not null:
                // Every show reads the list again; the view model joins a read still out
                _ = _abuseIpDb.ActivateAsync();
                break;
            case WindowPage.SavedFeeds or WindowPage.ChangeHistory when _savedFeeds is not null:
                // Every show reads again; the page joins a read still out and waits for the start itself
                _ = _savedFeeds.ActivateAsync();
                break;
            case WindowPage.Settings:
                // A token or key saved or removed while another page was on show is read again
                _settings?.RefreshKeyTexts();
                break;
        }
    }

    // Enter in the network or note box adds the network, as Add does. Enter anywhere else on the page adds nothing.
    private void ExceptionBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        if (_settings?.AddExceptionCommand is { } add && add.CanExecute(null))
            add.Execute(null);
    }

    // Enter in Cloud Address Lookup's box searches there; its button is never the default, which only Lookup's is
    private void CloudSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        if (_cloudSearch?.SearchCommand is { } search && search.CanExecute(null))
            search.Execute(null);
    }

    // Enter in the history's box searches there, as Search does
    private void SavedFeedsSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        e.Handled = true;
        if (_savedFeeds?.SearchCommand is { } search && search.CanExecute(null))
            search.Execute(null);
    }

    private void SetVisible(string name, bool visible)
    {
        if (this.FindControl<Control>(name) is { } control)
            control.IsVisible = visible;
    }

    private async Task CloseAfterStartStepAsync(MainViewModel viewModel)
    {
        await viewModel.CloseAfterStartStepAsync();
        CloseApproved();
    }

    // Held until CloseApproved, so a close between the chain's end and the approval doesn't start a second chain
    private async Task ConfirmAndCloseAsync(CloseChain chain)
    {
        _confirmingClose = true;
        try
        {
            if (await chain.ConfirmAndStopAsync(() => _closed))
            {
                CloseApproved();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    // Settings is asked first and the update last, because agreeing to the update's question cancels it. The refresh is
    // asked before the pages and stopped at its yes, so no page is then asked about work the refresh had under way.
    private static CloseChain BuildCloseChain(MainViewModel viewModel, SettingsPageViewModel? settings,
                                              RefreshAllRunner? refreshAll,
                                              AbuseIpDbViewModel? abuseIpDb, SavedFeedsViewModel? savedFeeds,
                                              IFeedRecords? records, IUiServices? ui, IWorkGate? gate)
    {
        var beforeFeedWrites = new List<ICloseParticipant>();
        if (settings is not null)
            beforeFeedWrites.Add(CloseChain.ForSettings(settings));
        if (refreshAll is not null)
            beforeFeedWrites.Add(refreshAll);
        if (abuseIpDb is not null)
            beforeFeedWrites.Add(abuseIpDb);
        if (savedFeeds is not null)
            beforeFeedWrites.Add(savedFeeds);
        return new CloseChain(beforeFeedWrites, [CloseChain.ForUpdate(viewModel)], records, ui, gate);
    }

    /// <summary>Shows the dataset update window over this one, or brings it forward when it is already shown.</summary>
    internal void ShowDatasetUpdateWindow()
    {
        if (_closed || !IsVisible || DataContext is not MainViewModel viewModel)
            return;

        if (_updateWindow is null)
        {
            _updateWindow = new DatasetUpdateWindow();
            _updateWindow.Closed += UpdateWindowClosed;
        }

        _updateWindow.DataContext = viewModel;
        // Hide clears the owner, so each show sets it again; a show of a shown window does nothing but Activate does
        if (!_updateWindow.IsVisible)
            _updateWindow.Show(this);
        _updateWindow.Activate();
    }

    /// <summary>The update window, or null before the first show. Read by the tests.</summary>
    internal DatasetUpdateWindow? UpdateWindow => _updateWindow;

    // Closed only with this window or the app, never by its own close, which hides it
    private void UpdateWindowClosed(object? sender, EventArgs e)
    {
        if (sender is DatasetUpdateWindow closed)
            closed.Closed -= UpdateWindowClosed;
        _updateWindow = null;
    }

    // Each action in the dataset drop-down closes it once its command has run: Click comes first, and hiding the flyout
    // then would take the content's DataContext, and with it the command, before the button reads it
    private void DatasetActionClicked(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => this.FindControl<Button>("DatasetPillButton")?.Flyout?.Hide());

    // A flyout gives no focus back when it closes, so the pill takes it, as a menu's button would
    private void DatasetFlyoutClosed(object? sender, EventArgs e)
    {
        if (!_closed)
            this.FindControl<Button>("DatasetPillButton")?.Focus();
    }

    private async void ShowAboutClicked(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private async void ShowHelpClicked(object? sender, RoutedEventArgs e) => await new HelpDialog().ShowDialog(this);

    private enum WindowPage { Lookup, GoogleCloud, Azure, Aws, CloudSearch, AbuseIpDb, SavedFeeds, ChangeHistory, Settings }
}
