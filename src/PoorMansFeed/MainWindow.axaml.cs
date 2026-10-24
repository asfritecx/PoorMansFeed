using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GraphiteUi;

namespace PoorMansFeed;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closeCheckRunning;
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
    private MainViewModel? _main;
    private SettingsPageViewModel? _settings;
    private AbuseIpDbViewModel? _abuseIpDb;
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
    // google, azure, aws, settings and abuseIpDb may be null: their pages stay empty and their bindings are not touched.
    internal void AttachViewModel(MainViewModel viewModel, GoogleCloudFeedViewModel? google = null, AzureFeedViewModel? azure = null,
                                  AwsFeedViewModel? aws = null, SettingsPageViewModel? settings = null,
                                  bool initializeOnOpen = false, AbuseIpDbViewModel? abuseIpDb = null)
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

        DataContext = viewModel;
        _main = viewModel;
        _google = google;
        _azure = azure;
        _aws = aws;
        _settings = settings;
        _abuseIpDb = abuseIpDb;
        _initializeOnOpen = initializeOnOpen;
        SetDataContext("ToolsPage", google);
        SetDataContext("AzureHeading", azure);
        SetDataContext("AzurePage", azure);
        SetDataContext("AwsHeading", aws);
        SetDataContext("AwsPage", aws);
        SetDataContext("SettingsPage", settings);
        SetDataContext("NavSettingsMark", settings);
        SetDataContext("AbuseIpDbHeading", abuseIpDb);
        SetDataContext("AbuseIpDbPage", abuseIpDb);

        _wasStopped = viewModel.IsStopped;
        viewModel.Started += MainStarted;
        viewModel.PropertyChanged += MainPropertyChanged;
        viewModel.DatasetChanged += MainDatasetChanged;
        if (settings is not null)
            settings.SettingsSaved += SettingsSaved;
        if (abuseIpDb is not null)
            abuseIpDb.LookUpRequested += AbuseLookUpRequested;
    }

    // The settings and exceptions are read when start-up has opened the service, so the page takes them again then
    private void MainStarted(object? sender, EventArgs e) => _settings?.Reload();

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

    // A save on the Settings page is followed on Lookup: the export folder, the dataset check and the open search
    private void SettingsSaved(object? sender, SettingsSavedEventArgs e) => _ = _main?.ApplySavedSettingsAsync(e.IPv6Changed);

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

        bool busy = DataContext is MainViewModel { IsBusy: true } || _abuseIpDb is { AsksBeforeClose: true };
        bool unsaved = _settings?.HasUnsavedChanges == true;
        if (_closeApproved || (!busy && !unsaved))
        {
            return false;
        }

        // Hold the close now; close again from code once the user agrees and the work has stopped
        if (!_closeCheckRunning)
        {
            _closeCheckRunning = true;
            _ = ConfirmCloseAsync(DataContext as MainViewModel);
        }

        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _google?.Dispose();
        _azure?.Dispose();
        _aws?.Dispose();
        _settings?.Dispose();
        _abuseIpDb?.Dispose();
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
            "NavAbuseIpDb" => WindowPage.AbuseIpDb,
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
        SetVisible("AbuseIpDbHeading", page == WindowPage.AbuseIpDb);
        SetVisible("AbuseIpDbPage", page == WindowPage.AbuseIpDb);
        SetVisible("SettingsHeading", page == WindowPage.Settings);
        SetVisible("SettingsPage", page == WindowPage.Settings);

        // Enter runs a search only on the lookup page. On the Settings page only the two exception boxes take Enter
        // (ExceptionBoxKeyDown). Showing Settings moves no focus into its boxes.
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
            case WindowPage.AbuseIpDb when _abuseIpDb is not null:
                // Every show reads the list again; the view model joins a read still out
                _ = _abuseIpDb.ActivateAsync();
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

    // The update is asked about last, because agreeing to its question cancels the update; the page's work is stopped
    // only once every question was agreed, and its runs are held once its own question is agreed, until a later no.
    private async Task ConfirmCloseAsync(MainViewModel? viewModel)
    {
        IDisposable? runsHeld = null;
        bool closing = false;
        try
        {
            if (_settings is { } settings && (!await settings.ConfirmDiscardAsync() || _closed))
            {
                return;
            }

            if (_abuseIpDb is { } abuseIpDb)
            {
                if (!await abuseIpDb.AskBeforeCloseAsync() || _closed)
                {
                    return;
                }

                runsHeld = abuseIpDb.HoldRunsForClose();
            }

            if (viewModel is not null && (!await viewModel.ConfirmCloseAsync() || _closed))
            {
                return;
            }

            // Stops a download not yet answered and waits for work already under way, a merge included
            if (_abuseIpDb is { } stopping)
            {
                await stopping.StopForCloseAsync();
                if (_closed)
                {
                    return;
                }
            }

            closing = true;
            CloseApproved();
        }
        finally
        {
            if (!closing)
            {
                runsHeld?.Dispose();
            }

            _closeCheckRunning = false;
        }
    }

    private async void ShowAboutClicked(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
    }

    private async void ShowHelpClicked(object? sender, RoutedEventArgs e) => await new HelpDialog().ShowDialog(this);

    private enum WindowPage { Lookup, GoogleCloud, Azure, Aws, AbuseIpDb, Settings }
}
