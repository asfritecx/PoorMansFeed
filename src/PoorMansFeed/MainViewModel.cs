using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;
using GraphiteUi;

namespace PoorMansFeed;

/// <summary>Where the dataset card is in its life, for tests and for reading the state in one place.</summary>
internal enum DatasetState { Starting, NoDataset, Checking, Current, UpdateAvailable, CheckFailed, Updating, UpdateFailed }

/// <summary>
/// House shape: a sealed ObservableObject with hand-written properties and commands built in the constructor.
/// No source generators and no converters: the view binds to ready-made *Text properties. Everything that needs a
/// window goes through <see cref="IUiServices"/>, and the core goes through <see cref="IIpInfoService"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    // The results list is capped: the app can run on a server next to other work, so memory must level off
    internal const int ResultLimit = 300;

    private const double MegabyteBytes = 1024.0 * 1024.0;

    private readonly IIpInfoService _service;
    private readonly IUiServices _ui;
    private readonly IAbuseIpDbService? _abuse;
    private readonly WorkGate _gate;
    private readonly AbuseKeyPrompt? _abuseKeys;
    // The Settings page's unsaved edits, read just before an export writes; null when there is no page
    private readonly IUnsavedSettings? _unsaved;
    private readonly string _defaultExportFolder;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _abuseCheckCts;
    private SearchQuery? _lastQuery;
    private int _totalCount;
    private int _searchVersion;
    // The input of the newest search started; while it is out, a settings save runs it again rather than the last shown
    private string? _newestSearchInput;
    // Whether the newest search started may keep an AbuseIPDB check (a settings rerun); a user's own search may not
    private bool _newestSearchKeepsCheck;
    // Whether the AbuseIPDB check wrote the results line; a kept settings rerun keeps the line only while it is the check's
    private bool _checkOwnsLine;
    private bool _started;
    private bool _disposed;
    private bool _isBusy;
    private bool _isSearching;
    private bool _isCheckingAbuse;
    private string _pillBeforeUpdate = "";
    private bool _pillWarningBeforeUpdate;

    private string _pillText = AppText.Starting;
    private bool _isPillWarning;
    private string _datasetStatusText = AppText.Starting;
    private string _datasetDetailText = "";
    private string _datasetFactsText = AppText.NoDatasetFacts;
    private int _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isStopped;
    private bool _canStartFresh;
    private bool _canStartWithoutOldExceptions;
    private string _stopTitle = "";
    private string _stopText = "";
    private string _stopActionText = "";
    private bool _hasStopAction;
    private bool _isStopActionError;
    private StartupFailure _stoppedBy;
    private bool _stopWorking;
    // True while a stop button's question is open, so neither button asks a second time meanwhile
    private bool _stopAsking;
    // The start-up step out now (a launch or a way on from the stop), completed when the service call has returned
    private TaskCompletionSource? _startStep;
    // Set by a close during a start-up step: nothing the step returns is applied
    private bool _closingAfterStartStep;
    // The dataset the last refresh of the facts showed, null before the first
    private DatasetInfo? _datasetShown;
    private string _searchText = "";
    private bool _hasResults;
    private string _resultsSummaryText = AppText.SummaryInitial;
    private string _resultsEmptyText = AppText.ResultsInitial;
    private string _resultsActionText = "";
    private bool _hasResultsAction;
    private string _exportFolder = "";

    public MainViewModel(IIpInfoService service, IUiServices ui, string? defaultExportFolder = null, IAbuseIpDbService? abuse = null)
        : this(service, ui, defaultExportFolder, abuse, gate: null)
    {
    }

    /// <summary>
    /// The window's composition: the gate is shared with the other pages, so a dialog or an update on one holds them all.
    /// </summary>
    internal MainViewModel(IIpInfoService service, IUiServices ui, string? defaultExportFolder, IAbuseIpDbService? abuse,
        WorkGate? gate, AbuseKeyPrompt? abuseKeys = null, IUnsavedSettings? unsaved = null)
    {
        _service = service;
        _ui = ui;
        _abuse = abuse;
        _unsaved = unsaved;
        _gate = gate ?? new WorkGate();
        if (abuseKeys is not null && !ReferenceEquals(abuseKeys.Gate, _gate))
        {
            throw new ArgumentException("The key prompt must hold the view model's own gate.", nameof(abuseKeys));
        }

        _gate.Changed += GateChanged;
        _abuseKeys = abuseKeys ?? (abuse is null ? null : new AbuseKeyPrompt(ui, abuse, _gate));
        _defaultExportFolder = defaultExportFolder ?? Path.Combine(service.DataFolder, "exports");
        _exportFolder = ResolveExportFolder();
        _datasetFactsText = FactsFor(service.Dataset);
        State = DatasetState.Starting;

        UpdateDatasetCommand = new AsyncRelayCommand(UpdateDatasetAsync, CanStart);
        CancelUpdateCommand = new RelayCommand(CancelUpdate, () => IsBusy);
        SearchCommand = new AsyncRelayCommand(SearchFromBoxAsync, CanSearch);
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        CopyResultsCommand = new AsyncRelayCommand(CopyResultsAsync, CanCopy);
        // Outside CanStart, so it works while an update runs, but off once a close is held
        OpenExportFolderCommand = new AsyncRelayCommand(OpenExportFolderAsync, () => !_disposed && !_closingAfterStartStep);
        CheckAbuseCommand = new AsyncRelayCommand(CheckAbuseAsync, CanCheckAbuse);
        // Outside the work gate too: a browser is not work, and a modal dialog already blocks the results menu
        OpenInIpinfoCommand = new AsyncRelayCommand(OpenInIpinfoAsync, () => CanOpenWebPage(IpinfoPage()));
        OpenInAbuseIpDbCommand = new AsyncRelayCommand(OpenInAbuseIpDbAsync, () => CanOpenWebPage(AbuseIpDbPage()));
        StartFreshCommand = new AsyncRelayCommand(StartFreshAsync, () => CanLeaveStop() && _canStartFresh);
        StartWithoutOldExceptionsCommand = new AsyncRelayCommand(StartWithoutOldExceptionsAsync,
            () => CanLeaveStop() && _canStartWithoutOldExceptions);
    }

    public IAsyncRelayCommand UpdateDatasetCommand { get; }
    public IRelayCommand CancelUpdateCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand CopyResultsCommand { get; }
    public IAsyncRelayCommand OpenExportFolderCommand { get; }
    public IAsyncRelayCommand CheckAbuseCommand { get; }

    /// <summary>Opens the searched ASN or public address on ipinfo.io, from the results menu.</summary>
    public IAsyncRelayCommand OpenInIpinfoCommand { get; }

    /// <summary>Opens the searched public address on AbuseIPDB's website, from the results menu.</summary>
    public IAsyncRelayCommand OpenInAbuseIpDbCommand { get; }

    /// <summary>Asks, then makes a new settings file; offered only for a settings file that can't be used.</summary>
    public IAsyncRelayCommand StartFreshCommand { get; }

    /// <summary>Asks, then starts with no exceptions; offered only when the old exceptions file can't be read.</summary>
    public IAsyncRelayCommand StartWithoutOldExceptionsCommand { get; }

    /// <summary>Where the dataset card is. Read by the tests.</summary>
    internal DatasetState State { get; private set; }

    /// <summary>The start-up dataset check, or null before one has started. Tests await it.</summary>
    internal Task? PendingCheck { get; private set; }

    /// <summary>The update a token reset started without awaiting it, or null before one has. Tests await it.</summary>
    internal Task? PendingUpdate { get; private set; }

    /// <summary>Builds the progress sink for an update. The default posts to the captured context; tests inject a synchronous one.</summary>
    internal Func<Action<UpdateProgress>, IProgress<UpdateProgress>> ProgressFactory { get; set; } =
        handler => new Progress<UpdateProgress>(handler);

    public string PillText { get => _pillText; private set => SetProperty(ref _pillText, value); }

    public bool IsPillWarning { get => _isPillWarning; private set => SetProperty(ref _isPillWarning, value); }

    public string DatasetStatusText { get => _datasetStatusText; private set => SetProperty(ref _datasetStatusText, value); }

    public string DatasetDetailText { get => _datasetDetailText; private set => SetProperty(ref _datasetDetailText, value); }

    public string DatasetFactsText { get => _datasetFactsText; private set => SetProperty(ref _datasetFactsText, value); }

    /// <summary>0 to 100.</summary>
    public int ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _gate.SetBusy(value);
                RefreshCommandStates();
            }
        }
    }

    /// <summary>True while start-up has stopped: the window shows the stop in place of the pages.</summary>
    public bool IsStopped { get => _isStopped; private set => SetProperty(ref _isStopped, value); }

    public bool CanStartFresh { get => _canStartFresh; private set => SetProperty(ref _canStartFresh, value); }

    public bool CanStartWithoutOldExceptions
    {
        get => _canStartWithoutOldExceptions;
        private set => SetProperty(ref _canStartWithoutOldExceptions, value);
    }

    public string StopTitle { get => _stopTitle; private set => SetProperty(ref _stopTitle, value); }

    public string StopText { get => _stopText; private set => SetProperty(ref _stopText, value); }

    public string StopActionText { get => _stopActionText; private set => SetProperty(ref _stopActionText, value); }

    public bool HasStopAction { get => _hasStopAction; private set => SetProperty(ref _hasStopAction, value); }

    public bool IsStopActionError { get => _isStopActionError; private set => SetProperty(ref _isStopActionError, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public ObservableCollection<ResultRow> Results { get; } = [];

    public bool HasResults { get => _hasResults; private set => SetProperty(ref _hasResults, value); }

    public string ResultsSummaryText { get => _resultsSummaryText; private set => SetProperty(ref _resultsSummaryText, value); }

    public string ResultsEmptyText { get => _resultsEmptyText; private set => SetProperty(ref _resultsEmptyText, value); }

    public string ResultsActionText { get => _resultsActionText; private set => SetProperty(ref _resultsActionText, value); }

    public bool HasResultsAction { get => _hasResultsAction; private set => SetProperty(ref _hasResultsAction, value); }

    public string ExportFolder { get => _exportFolder; private set => SetProperty(ref _exportFolder, value); }

    // One predicate gates every action, so a new command cannot forget a case. A held close turns every action off.
    private bool CanStart() => _gate.CanStart && !_disposed && !_closingAfterStartStep;

    // The stop's buttons are outside the gate, which stays closed while start-up has stopped
    private bool CanLeaveStop() => !_disposed && !_closingAfterStartStep && _isStopped && !_stopWorking && !_stopAsking;

    /// <summary>True while a start-up step runs: the service call of a launch, a Start fresh or a Start without exceptions.</summary>
    internal bool IsStartStepRunning => _startStep is not null;

    /// <summary>
    /// The window's close during a start-up step. The step can't be cut short, so it is left to return, and nothing it
    /// returns is applied: no stop, no start, no notice and no prompt. Completes once the step has returned.
    /// The pill says the close is waiting, and every command is off from here on.
    /// </summary>
    internal async Task CloseAfterStartStepAsync()
    {
        _closingAfterStartStep = true;
        PillText = AppText.ClosingAfterStartUp;
        IsPillWarning = false;
        RefreshCommandStates();
        _lifetime.Cancel();
        if (_startStep is { } step)
        {
            await step.Task;
        }
    }

    // A step's service call, marked as running from the call until it has returned or thrown
    private async Task<StartupResult> RunStartStepAsync(Func<CancellationToken, Task<StartupResult>> step)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startStep = running;
        try
        {
            return await step(_lifetime.Token);
        }
        finally
        {
            _startStep = null;
            running.SetResult();
        }
    }

    private bool HasDataset => _service.Dataset is not null;

    private bool CanSearch() => CanStart() && HasDataset && !_isSearching && !string.IsNullOrWhiteSpace(_searchText);

    private bool CanExport() => CanStart() && _lastQuery is { Kind: SearchKind.Asn } && _totalCount > 0;

    private bool CanCopy() => CanStart() && _lastQuery is not null && _totalCount > 0;

    // Any address search counts, found or not: the score is about the address, not about a network in the dataset
    private bool CanCheckAbuse() =>
        CanStart() && _abuse is not null && !_isSearching && !_isCheckingAbuse && _lastQuery is { Kind: SearchKind.IpAddress };

    /// <summary>
    /// Runs the start-up sequence once: the service opens, a first run asks for a token and downloads, and a
    /// database that exists is checked for updates in the background (search stays available meanwhile).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_started || _disposed || _closingAfterStartStep)
        {
            return;
        }

        StartupResult startup;
        try
        {
            startup = await RunStartStepAsync(_service.InitializeAsync);
        }
        catch (Exception)
        {
            startup = new StartupResult(StartupFailure.StorageUnavailable, null, false);
        }

        if (_disposed || _closingAfterStartStep)
        {
            return;
        }

        await ApplyStartupAsync(startup);
    }

    // What a start-up result does: a closing notice, the stop, or the start. A way on from the stop comes back here.
    private async Task ApplyStartupAsync(StartupResult startup)
    {
        switch (startup.Failure)
        {
            case StartupFailure.Privileged:
                await ShowClosingNoticeAndCloseAsync(AppText.PrivilegedTitle, AppText.PrivilegedBody);
                return;
            case StartupFailure.AlreadyRunning:
                await ShowClosingNoticeAndCloseAsync(AppText.AlreadyOpenTitle, AppText.AlreadyOpenBody);
                return;
            case StartupFailure.None:
                break;
            default:
                Stop(startup.Failure);
                return;
        }

        // As a first launch shows it, until the check, the update or the no-dataset line sets it
        PillText = AppText.Starting;
        IsPillWarning = false;
        IsStopped = false;
        CanStartFresh = false;
        CanStartWithoutOldExceptions = false;
        SetStopAction("", false);
        _started = true;
        _gate.SetStarted(true);
        // The settings are the saved ones only once the service has opened, so the folder is resolved again
        ExportFolder = ResolveExportFolder();
        RefreshDatasetFacts();
        Started?.Invoke(this, EventArgs.Empty);

        if (!HasDataset)
        {
            ResultsEmptyText = AppText.ResultsNoDataset;
            bool hasToken = await EnsureTokenAsync(TokenPromptReason.FirstRun);
            if (_disposed)
            {
                return;
            }

            if (hasToken)
            {
                await RunUpdateAsync();
            }
            else
            {
                ShowNoDataset(AppText.NoTokenFirstRun);
            }

            return;
        }

        if (_service.HasToken)
        {
            StartCheck();
        }
        else
        {
            ShowCheckFailed(AppText.NoTokenCheck);
        }
    }

    /// <summary>Raised once, when the start-up has opened the service and the app's commands can run.</summary>
    internal event EventHandler? Started;

    /// <summary>Raised when the dataset in use changes by value (a new one, or one removed), after the dataset facts are
    /// shown. Not raised when a refresh finds the same dataset.</summary>
    internal event EventHandler? DatasetChanged;

    private async Task ShowClosingNoticeAndCloseAsync(string title, string body)
    {
        try
        {
            await _ui.ShowClosingNoticeAsync(title, body);
        }
        catch (Exception)
        {
            // The window still closes below; the notice is only a message
        }

        _ui.CloseApplication();
    }

    private void Stop(StartupFailure failure)
    {
        _stoppedBy = failure;
        PillText = AppText.NotStarted;
        IsPillWarning = true;
        IsStopped = true;
        StopTitle = AppText.StopTitle(failure);
        StopText = AppText.StopText(failure);
        CanStartFresh = failure is StartupFailure.ConfigNewer or StartupFailure.ConfigUnreadable
            or StartupFailure.ConfigKeyMismatch or StartupFailure.ConfigMissing;
        CanStartWithoutOldExceptions = failure == StartupFailure.ExceptionsImportUnreadable;
        SetStopAction("", false);
        SetProgress(0, false);
        RefreshStopCommands();
    }

    private Task StartFreshAsync() => LeaveStopAsync(
        _canStartFresh,
        AppText.StartFreshTitle,
        _stoppedBy == StartupFailure.ConfigMissing ? AppText.StartFreshMissingBody : AppText.StartFreshBody,
        AppText.StartFreshButton,
        AppText.StartingFresh,
        AppText.StartFreshFailed,
        _service.StartFreshAsync);

    private Task StartWithoutOldExceptionsAsync() => LeaveStopAsync(
        _canStartWithoutOldExceptions,
        AppText.StartWithoutOldExceptionsTitle,
        AppText.StartWithoutOldExceptionsBody,
        AppText.StartWithoutOldExceptionsButton,
        AppText.StartingWithoutOldExceptions,
        AppText.StartWithoutOldExceptionsFailed,
        _service.StartWithoutOldExceptionsAsync);

    // Asks first. The same failure back says it couldn't be done and keeps the button; any other result is applied
    // as a launch's would be, so a different failure shows its own stop and None starts the app in place.
    // offered is the button's own offer; the check uses this class's state, not the running command's.
    private async Task LeaveStopAsync(bool offered, string title, string body, string confirmText,
        string working, string failed, Func<CancellationToken, Task<StartupResult>> leave)
    {
        if (!offered || !CanLeaveStop())
        {
            return;
        }

        bool confirmed;
        _stopAsking = true;
        RefreshStopCommands();
        try
        {
            confirmed = await _ui.ConfirmAsync(title, body, confirmText);
        }
        catch (Exception)
        {
            SetStopAction(AppText.Generic, true);
            return;
        }
        finally
        {
            _stopAsking = false;
            RefreshStopCommands();
        }

        if (!confirmed || _disposed || _closingAfterStartStep)
        {
            return;
        }

        StartupFailure stoppedBy = _stoppedBy;
        StartupResult result;
        _stopWorking = true;
        SetStopAction(working, false);
        RefreshStopCommands();
        try
        {
            result = await RunStartStepAsync(leave);
        }
        catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = new StartupResult(stoppedBy, null, false);
        }
        finally
        {
            _stopWorking = false;
            RefreshStopCommands();
        }

        if (_disposed || _closingAfterStartStep)
        {
            return;
        }

        if (result.Failure == stoppedBy)
        {
            SetStopAction(failed, true);
            return;
        }

        await ApplyStartupAsync(result);
    }

    private void SetStopAction(string text, bool error)
    {
        StopActionText = text;
        HasStopAction = text.Length > 0;
        IsStopActionError = error;
    }

    private void RefreshStopCommands()
    {
        StartFreshCommand.NotifyCanExecuteChanged();
        StartWithoutOldExceptionsCommand.NotifyCanExecuteChanged();
    }

    private async Task UpdateDatasetAsync()
    {
        if (!CanStart())
        {
            return;
        }

        if (!await EnsureTokenAsync(TokenPromptReason.FirstRun) || !CanStart())
        {
            return;
        }

        await RunUpdateAsync();
    }

    /// <summary>
    /// Returns true when a token is saved. Asks for one only when none is saved, and holds the gate from the prompt
    /// until the save has returned.
    /// </summary>
    private async Task<bool> EnsureTokenAsync(TokenPromptReason reason)
    {
        if (_service.HasToken)
        {
            return true;
        }

        using (_gate.Hold())
        {
            string? token = await ShowDialogAsync(() => _ui.PromptForTokenAsync(reason));
            if (string.IsNullOrWhiteSpace(token) || _disposed)
            {
                return false;
            }

            if (await SaveTokenAsync(token))
            {
                return true;
            }
        }

        SetResultsAction(AppText.TokenSaveFailed);
        return false;
    }

    private async Task<bool> SaveTokenAsync(string token)
    {
        try
        {
            return await _service.SaveTokenAsync(token);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // One update at a time: IsBusy is set before the first await, so the guard leaves no gap
    private async Task RunUpdateAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        CancelCheck();
        _pillBeforeUpdate = PillText;
        _pillWarningBeforeUpdate = IsPillWarning;

        var cts = new CancellationTokenSource();
        _operationCts = cts;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operationCompletion = completion;
        CancellationToken ct = cts.Token;

        IsBusy = true;
        SetDatasetState(DatasetState.Updating, AppText.Updating, false, AppText.CheckingForUpdates, "");
        SetProgress(0, true);

        // Created here, on the UI thread, so reports arrive on it
        IProgress<UpdateProgress> progress = ProgressFactory(OnUpdateProgress);
        UpdateResult? result = null;
        bool cancelled = false;
        try
        {
            result = await _service.UpdateDatasetAsync(progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            result = new UpdateResult(UpdateFailure.ServiceError, false, null);
        }
        finally
        {
            _operationCts = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
            completion.TrySetResult();
        }

        if (_disposed)
        {
            return;
        }

        if (cancelled || result is null)
        {
            ShowUpdateCancelled();
        }
        else
        {
            ApplyUpdateResult(result);
        }
    }

    private void OnUpdateProgress(UpdateProgress report)
    {
        // A late report that arrives after the update has ended must not overwrite its outcome
        if (!_isBusy || _disposed)
        {
            return;
        }

        switch (report.Phase)
        {
            case UpdatePhase.Checking:
                SetUpdatingText(AppText.CheckingForUpdates, "");
                SetProgress(0, true);
                break;
            case UpdatePhase.Downloading when report.Total > 0:
                SetUpdatingText(AppText.DownloadingTitle,
                    AppText.DownloadProgress(report.Done / MegabyteBytes, report.Total / MegabyteBytes));
                SetProgress(Percent(report.Done, report.Total), false);
                break;
            case UpdatePhase.Downloading:
                SetUpdatingText(AppText.DownloadingTitle, AppText.DownloadedSoFar(report.Done / MegabyteBytes));
                SetProgress(0, true);
                break;
            case UpdatePhase.Importing:
                int percent = Percent(report.Done, report.Total);
                SetUpdatingText(AppText.ImportingTitle, AppText.ImportPercentRead(percent));
                SetProgress(percent, false);
                break;
            case UpdatePhase.Indexing:
                SetUpdatingText(AppText.IndexingTitle, AppText.IndexingDetail);
                SetProgress(0, true);
                break;
            case UpdatePhase.Finishing:
                SetUpdatingText(AppText.FinishingTitle, "");
                SetProgress(0, true);
                break;
        }
    }

    private void SetUpdatingText(string title, string detail)
    {
        DatasetStatusText = title;
        DatasetDetailText = detail;
    }

    private static int Percent(long done, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private void ApplyUpdateResult(UpdateResult result)
    {
        if (result.Failure == UpdateFailure.None)
        {
            RefreshDatasetFacts();
            if (result.AlreadyCurrent)
            {
                SetDatasetState(DatasetState.Current, AppText.UpToDate, false, AppText.UpToDate, AppText.AlreadyCurrentDetail);
                SetProgress(0, false);
            }
            else
            {
                SetDatasetState(DatasetState.Current, AppText.UpToDate, false, AppText.UpdatedTitle, "");
                SetProgress(100, false);
            }

            return;
        }

        string detail = UpdateFailureText(result.Failure, result.RetryAfter);
        RefreshDatasetFacts();
        if (HasDataset)
        {
            SetDatasetState(DatasetState.UpdateFailed, _pillBeforeUpdate, _pillWarningBeforeUpdate,
                AppText.UpdateFailedTitle, detail + " " + AppText.StillInUse);
        }
        else
        {
            ShowNoDataset(detail, AppText.UpdateFailedTitle);
        }

        SetProgress(0, false);
    }

    private void ShowUpdateCancelled()
    {
        RefreshDatasetFacts();
        if (HasDataset)
        {
            SetDatasetState(DatasetState.UpdateFailed, _pillBeforeUpdate, _pillWarningBeforeUpdate,
                AppText.UpdateCancelledTitle, AppText.StillInUse);
        }
        else
        {
            ShowNoDataset(AppText.NoDatasetDownloaded, AppText.UpdateCancelledTitle);
        }

        SetProgress(0, false);
    }

    private void ShowNoDataset(string detail, string? title = null)
    {
        SetDatasetState(DatasetState.NoDataset, AppText.NoDataset, true, title ?? AppText.NoDataset, detail);
        SetProgress(0, false);
    }

    private void StartCheck()
    {
        CancelCheck();
        var cts = new CancellationTokenSource();
        _checkCts = cts;
        SetDatasetState(DatasetState.Checking, AppText.CheckingForUpdates, false, AppText.CheckingForUpdates, AppText.CheckingDetail);
        SetProgress(0, true);
        PendingCheck = RunCheckAsync(cts.Token);
    }

    private async Task RunCheckAsync(CancellationToken ct)
    {
        UpdateCheckResult result;
        try
        {
            result = await _service.CheckForUpdateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = new UpdateCheckResult(UpdateCheckOutcome.ServiceError);
        }

        // A result is ignored when an update has started (it cancels the check), or the view model is gone
        if (_disposed || _isBusy || ct.IsCancellationRequested)
        {
            return;
        }

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                SetDatasetState(DatasetState.Current, AppText.UpToDate, false, AppText.UpToDate, "");
                SetProgress(0, false);
                break;
            case UpdateCheckOutcome.UpdateAvailable:
                SetDatasetState(DatasetState.UpdateAvailable, AppText.UpdateAvailable, true,
                    AppText.UpdateAvailable, result.AddsIPv6 ? AppText.UpdateAvailableIPv6Detail : AppText.UpdateAvailableDetail);
                SetProgress(0, false);
                break;
            default:
                ShowCheckFailed(CheckFailureText(result.Outcome, result.RetryAfter));
                break;
        }
    }

    private void ShowCheckFailed(string detail)
    {
        SetDatasetState(DatasetState.CheckFailed, AppText.CheckFailed, true, AppText.CheckFailed, detail);
        SetProgress(0, false);
    }

    private void CancelCheck()
    {
        _checkCts?.Cancel();
        _checkCts = null;
    }

    private static string CheckFailureText(UpdateCheckOutcome outcome, TimeSpan? retryAfter) => outcome switch
    {
        UpdateCheckOutcome.NoToken => AppText.NoTokenCheck,
        UpdateCheckOutcome.TokenRejected => AppText.TokenRejected,
        UpdateCheckOutcome.RateLimited => AppText.RateLimited(IpInfoLimits.DownloadsPerDay, retryAfter),
        UpdateCheckOutcome.Offline => AppText.CheckOffline,
        _ => AppText.ServiceError,
    };

    private static string UpdateFailureText(UpdateFailure failure, TimeSpan? retryAfter) => failure switch
    {
        UpdateFailure.NoToken => AppText.NoTokenCheck,
        UpdateFailure.TokenRejected => AppText.TokenRejected,
        UpdateFailure.RateLimited => AppText.RateLimited(IpInfoLimits.DownloadsPerDay, retryAfter),
        UpdateFailure.Offline => AppText.UpdateOffline,
        UpdateFailure.ChecksumMismatch => AppText.ChecksumMismatch,
        UpdateFailure.NotEnoughDiskSpace => AppText.DiskFull,
        UpdateFailure.CorruptData => AppText.CorruptData,
        UpdateFailure.StorageError => AppText.StorageError,
        _ => AppText.ServiceError,
    };

    private async Task SearchFromBoxAsync()
    {
        if (CanSearch())
        {
            await RunSearchAsync(_searchText);
        }
    }

    /// <summary>Searches for <paramref name="text"/> from another page: sets the box and runs the search, superseding
    /// one still running. Returns false, with a fixed results line, when no search can start now or the text is blank;
    /// the box is then left as it was.</summary>
    internal async Task<bool> SearchForAsync(string text)
    {
        // CanStart covers start-up, a gate holder (an update, a dialog), the held close and dispose
        if (string.IsNullOrWhiteSpace(text) || !CanStart())
        {
            SetResultsAction(AppText.SearchCantStart);
            return false;
        }

        if (!HasDataset)
        {
            SetResultsAction(AppText.ResultsNoDataset);
            return false;
        }

        SearchText = text;
        await RunSearchAsync(text);
        return true;
    }

    private async Task RunSearchAsync(string input, bool keepCheckIfSameQuery = false)
    {
        if (_disposed)
        {
            return;
        }

        int version = ++_searchVersion;
        _newestSearchInput = input;
        _newestSearchKeepsCheck = keepCheckIfSameQuery;
        // This search owns the results line now, so an AbuseIPDB check still out for the last one is called off. A
        // settings rerun waits for its result instead: the same address keeps the check and its line
        if (!keepCheckIfSameQuery)
        {
            _abuseCheckCts?.Cancel();
        }

        _isSearching = true;
        RefreshCommandStates();
        ResultsSummaryText = AppText.Searching;

        SearchResult? result;
        try
        {
            result = await _service.SearchAsync(input, ResultLimit, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested)
        {
            result = null;
        }
        catch (Exception)
        {
            result = new SearchResult(SearchOutcome.Failed, null, [], 0);
        }

        // A newer search has started since this one: its result replaces this one, so this one is dropped
        if (version != _searchVersion)
        {
            return;
        }

        // The commands are refreshed after the result is applied, because their gates read the query and the count it sets
        _isSearching = false;
        if (result is not null && !_disposed)
        {
            // A settings rerun that shows the same address again keeps the check, and keeps the line only while it is the
            // check's (a copy or a failed action is replaced by the address note); anything else replaces them both
            bool keep = keepCheckIfSameQuery
                && result.Outcome is SearchOutcome.Found or SearchOutcome.NoResults
                && result.Query is { Kind: SearchKind.IpAddress }
                && _lastQuery is { Kind: SearchKind.IpAddress } shown
                && string.Equals(shown.Text, result.Query.Text, StringComparison.Ordinal);
            if (keep)
            {
                ApplySearch(result, keepActionLine: _checkOwnsLine);
            }
            else
            {
                _abuseCheckCts?.Cancel();
                ApplySearch(result);
            }
        }

        RefreshCommandStates();
    }

    private void ApplySearch(SearchResult result, bool keepActionLine = false)
    {
        if (result.Outcome == SearchOutcome.Found && result.Query is { } found)
        {
            List<ResultRow> rows = result.Rows.Take(ResultLimit).Select(ResultRow.From).ToList();
            _lastQuery = found;
            _totalCount = result.TotalCount;
            ReplaceResults(rows);
            if (found.Kind == SearchKind.Asn)
            {
                string asName = result.Rows.Select(row => row.AsName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "";
                ResultsSummaryText = rows.Count < result.TotalCount
                    ? AppText.ShowingCapped(rows.Count, result.TotalCount, found.Text, asName)
                    : AppText.AllShown(result.TotalCount, found.Text, asName);
                SetResultsAction("");
            }
            else
            {
                ResultsSummaryText = AppText.IpContained(result.TotalCount, found.Text);
                if (!keepActionLine)
                {
                    SetResultsAction(AppText.ExportIpNote);
                }
            }

            return;
        }

        ReplaceResults([]);
        ResultsSummaryText = "";
        if (!keepActionLine)
        {
            SetResultsAction("");
        }

        _totalCount = 0;
        switch (result.Outcome)
        {
            case SearchOutcome.NoResults when result.Query is { } query:
                _lastQuery = query;
                ResultsEmptyText = query.Kind == SearchKind.Asn
                    ? AppText.NoAsnResults(query.Text)
                    : AppText.NoIpResult(query.Text);
                break;
            case SearchOutcome.InvalidInput:
                _lastQuery = null;
                ResultsEmptyText = AppText.ResultsInvalid;
                break;
            case SearchOutcome.NoDataset:
                _lastQuery = null;
                ResultsEmptyText = AppText.ResultsNoDataset;
                break;
            case SearchOutcome.IPv6Disabled:
                _lastQuery = null;
                ResultsEmptyText = AppText.ResultsIPv6Disabled;
                break;
            default:
                _lastQuery = null;
                ResultsEmptyText = AppText.SearchFailed;
                break;
        }
    }

    private void ReplaceResults(IReadOnlyList<ResultRow> rows)
    {
        Results.Clear();
        foreach (ResultRow row in rows)
        {
            Results.Add(row);
        }

        HasResults = Results.Count > 0;
    }

    private async Task ExportAsync()
    {
        if (!CanExport() || _lastQuery is not { } query)
        {
            return;
        }

        ExportPlan plan;
        try
        {
            plan = await _service.PrepareExportAsync(query, _lifetime.Token);
        }
        catch (Exception)
        {
            SetResultsAction(AppText.Generic);
            return;
        }

        if (!CanStart())
        {
            return;
        }

        IReadOnlyList<string> included = [];
        if (plan.LargeRanges.Count > 0)
        {
            if (_service.Settings.LargeRangeCheckEnabled)
            {
                var choices = plan.LargeRanges.Select(LargeRangeChoice.From).ToList();
                var prompt = new LargeRangePrompt(
                    AppText.LargeRangesTitle(query.Text),
                    AppText.LargeRangesMessage(choices.Count, _service.Settings.IPv4MinPrefix, _service.Settings.IPv6MinPrefix),
                    choices);
                IReadOnlyList<string>? chosen = await ShowDialogAsync(() => _ui.ChooseLargeRangesAsync(prompt));
                // The window can start closing while the dialog is open
                if (chosen is null || !CanStart())
                {
                    return;
                }

                included = chosen;
            }
            else
            {
                // The check is off, so nothing is held back: every large range is exported
                included = plan.LargeRanges.Select(range => range.Network).ToList();
            }
        }

        // Read once, just before the write, so the note says what the export read; a save during it doesn't change it
        bool unsaved = _unsaved?.HasUnsavedChanges == true;
        ExportResult result;
        try
        {
            result = await _service.ExportAsync(query, included, _lifetime.Token);
        }
        catch (Exception)
        {
            SetResultsAction(AppText.ExportWriteFailed);
            return;
        }

        SetResultsAction(ExportMessage(result, unsaved));
    }

    private static string ExportMessage(ExportResult result, bool unsaved)
    {
        switch (result.Failure)
        {
            case ExportFailure.FolderUnavailable:
                return AppText.ExportFolderUnavailable;
            case ExportFailure.WriteFailed:
                return result.FailedFile is { } failed
                    ? AppText.ExportFileFailed(failed, result.Files, result.OldFilesLeft, unsaved)
                    : AppText.ExportWriteFailed;
            case ExportFailure.NothingToExport:
                return AppText.NothingExported;
            case ExportFailure.ExceptionsUnreadable:
                return AppText.ExceptionsUnreadable;
        }

        if (result.NetworksWritten == 0)
        {
            return AppText.NothingExported;
        }

        string text = AppText.Exported(result.NetworksWritten, result.Files.Count);
        if (result.LargeRangesLeftOut > 0)
        {
            text += AppText.LargeRangesLeftOut(result.LargeRangesLeftOut);
        }

        return text + AppText.ExceptionsApplied(result.ExceptionCuts, result.ExceptionRemovals, "network", "networks")
            + AppText.OldFeedFilesLeft(result.OldFilesLeft) + (unsaved ? AppText.UnsavedSettingsNote : "");
    }

    private Task CopyResultsAsync()
    {
        if (!CanCopy() || _lastQuery is not { } query)
        {
            return Task.CompletedTask;
        }

        // Every result is copied, not only the rows on screen, so the copy matches the search
        return SafeUiActionAsync(async () =>
        {
            string text = await _service.GetResultsTextAsync(query, _lifetime.Token);
            await _ui.CopyTextAsync(text);
            SetResultsAction(AppText.Copied(_totalCount));
        }, AppText.CopyFailed);
    }

    private Task OpenExportFolderAsync() => SafeUiActionAsync(async () =>
    {
        string folder = ExportFolder;
        Directory.CreateDirectory(folder);
        await _ui.OpenFolderAsync(folder);
    }, AppText.FolderOpenFailed);

    // Off while a search is out: the rows shown are the earlier search's, and the page would be the newer one's
    private bool CanOpenWebPage(WebPage? page) =>
        !_disposed && !_closingAfterStartStep && !_isSearching && _totalCount > 0 && page is not null;

    // The pages come from the search shown, never from a row: every row of a search shares its ASN or address. An
    // address goes out only once PublicAddress has judged it, so a private or documentation address opens nothing.
    private WebPage? IpinfoPage() => _lastQuery switch
    {
        { Kind: SearchKind.Asn } query => WebPage.IpInfoAsn(query.Asn),
        _ => SearchedPublicAddress() is { } address ? WebPage.IpInfoAddress(address) : null,
    };

    private WebPage? AbuseIpDbPage() => SearchedPublicAddress() is { } address ? WebPage.AbuseIpDbCheck(address) : null;

    private PublicAddress? SearchedPublicAddress() =>
        _lastQuery is { Kind: SearchKind.IpAddress, IpBytes: { } bytes } && PublicAddress.TryCreate(bytes, out PublicAddress? address)
            ? address
            : null;

    // No success line: the launcher's yes means only that the browser was asked
    private Task OpenInIpinfoAsync() => OpenWebPageAsync(IpinfoPage());

    private Task OpenInAbuseIpDbAsync() => OpenWebPageAsync(AbuseIpDbPage());

    private Task OpenWebPageAsync(WebPage? page)
    {
        if (!CanOpenWebPage(page) || page is null)
        {
            return Task.CompletedTask;
        }

        return SafeUiActionAsync(() => _ui.OpenWebPageAsync(page), AppText.WebPageOpenFailed);
    }

    /// <summary>
    /// Follows a settings save made anywhere: the export folder is resolved again, the update check runs again when
    /// IPv6 was turned on or off, and the open search runs again.
    /// </summary>
    internal async Task ApplySavedSettingsAsync(bool ipv6Changed)
    {
        if (_disposed || !_started)
        {
            return;
        }

        ExportFolder = ResolveExportFolder();
        // Turning IPv6 on or off changes what the dataset card says (IPv4 only, or an update to add IPv6), so the check
        // runs again. Search is not held up by it.
        if (ipv6Changed && HasDataset && _service.HasToken)
        {
            StartCheck();
        }

        // The badges of the open results depend on the settings, so the search runs again: the newest one started
        // when it is still out (its answer used the old settings), else the one shown. A rerun of the same address keeps
        // an AbuseIPDB check, and its line only while the check wrote it; a user's search still out has already called
        // its check off, so it keeps none
        if (_isSearching && _newestSearchInput is { } newest)
        {
            await RunSearchAsync(newest, _newestSearchKeepsCheck);
        }
        else if (_lastQuery is { } query)
        {
            await RunSearchAsync(query.Text, keepCheckIfSameQuery: true);
        }
    }

    /// <summary>
    /// Sends the searched address to AbuseIPDB and writes the answer to the results line. A missing or refused key
    /// asks for one once, saves it and checks once more. A search the user starts, or a settings rerun that doesn't show
    /// the same address again, calls the check off and owns the line. The key passes straight from the dialog to the
    /// service and is kept nowhere here.
    /// </summary>
    private async Task CheckAbuseAsync()
    {
        if (!CanCheckAbuse() || _abuse is not { } abuse || _lastQuery is not { } query)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _abuseCheckCts = cts;
        _isCheckingAbuse = true;
        RefreshCommandStates();
        SetResultsAction(AppText.CheckingAbuse, fromCheck: true);
        try
        {
            AbuseCheckResult result = await abuse.CheckAsync(query, cts.Token);
            if (IsStaleCheck(cts))
            {
                return;
            }

            if (result.Failure is AbuseIpDbFailure.NoKey or AbuseIpDbFailure.KeyRejected)
            {
                bool replacing = result.Failure == AbuseIpDbFailure.KeyRejected;

                // The prompt asks only while nothing else holds the gate (another dialog, an update, the Settings
                // page's own key prompt); otherwise it is Busy and the line says nothing was checked
                AbuseKeyOutcome asked = _abuseKeys is null
                    ? AbuseKeyOutcome.Busy
                    : await _abuseKeys.AskAndSaveAsync(replacing ? AbuseKeyAsk.Rejected : AbuseKeyAsk.Missing);
                if (IsStaleCheck(cts))
                {
                    return;
                }

                switch (asked)
                {
                    case AbuseKeyOutcome.Saved:
                        // One more check with the saved key; whatever it says goes to the line, and nothing asks again
                        result = await abuse.CheckAsync(query, cts.Token);
                        if (IsStaleCheck(cts))
                        {
                            return;
                        }

                        break;
                    case AbuseKeyOutcome.DialogFailed:
                        SetResultsAction(AppText.Generic, fromCheck: true);
                        return;
                    case AbuseKeyOutcome.NotAKey:
                        SetResultsAction(AppText.AbuseKeyNotAKey, fromCheck: true);
                        return;
                    case AbuseKeyOutcome.SaveFailed:
                        SetResultsAction(AppText.AbuseKeySaveFailed, fromCheck: true);
                        return;
                    default:
                        // Busy or Cancelled
                        SetResultsAction(replacing ? AppText.AbuseKeyReplaceCancelled : AppText.AbuseKeyCancelled, fromCheck: true);
                        return;
                }
            }

            SetResultsAction(AbuseLineFor(result), fromCheck: true);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // The check was called off by a search that owns the line, or the window is closing: nothing to report
        }
        catch (Exception)
        {
            if (!IsStaleCheck(cts))
            {
                SetResultsAction(AppText.AbuseCheckFailed, fromCheck: true);
            }
        }
        finally
        {
            if (_abuseCheckCts == cts)
            {
                _abuseCheckCts = null;
            }

            _isCheckingAbuse = false;
            RefreshCommandStates();
        }
    }

    // The check was called off (a search the user started, or a rerun that doesn't keep it), or the window has closed
    private bool IsStaleCheck(CancellationTokenSource cts) => _disposed || cts.IsCancellationRequested;

    // The final answer of a check. NoKey and KeyRejected reach here only after a saved key, so they are not asked about again
    private static string AbuseLineFor(AbuseCheckResult result) => result.Failure switch
    {
        AbuseIpDbFailure.None when result.Check is { } check => AppText.AbuseScore(check),
        AbuseIpDbFailure.NotPublic => AppText.AbuseNotSent,
        AbuseIpDbFailure.KeyUnavailable => AppText.AbuseKeyUnavailable,
        AbuseIpDbFailure.NoKey => AppText.AbuseKeyUnreadable,
        AbuseIpDbFailure.KeyRejected => AppText.AbuseKeyRejected,
        AbuseIpDbFailure.RateLimited => AppText.AbuseRateLimited(result.RetryAt),
        AbuseIpDbFailure.Offline => AppText.AbuseOffline,
        AbuseIpDbFailure.ServiceError => AppText.AbuseCheckFailed,
        AbuseIpDbFailure.BadResponse => AppText.AbuseAnswerUnreadable,
        _ => AppText.AbuseCheckFailed,
    };

    /// <summary>
    /// Asks to confirm, removes the saved token and asks for a new one. Writes no results line: the outcome says what
    /// happened. A saved token starts the update when there is no dataset and the check otherwise, and returns once
    /// either has started: the update holds the gate itself (IsBusy) until it ends, and its task is
    /// <see cref="PendingUpdate"/>.
    /// </summary>
    internal async Task<TokenResetOutcome> ResetTokenAsync()
    {
        if (!CanStart())
        {
            return TokenResetOutcome.Busy;
        }

        TokenResetOutcome outcome;
        // One hold from the confirmation to the save, so nothing else starts while the token is half replaced
        using (_gate.Hold())
        {
            outcome = await ConfirmClearAndAskAsync();
        }

        if (outcome != TokenResetOutcome.Saved)
        {
            return outcome;
        }

        // The window closed while the token was saved: the token stays saved, and nothing more starts
        if (_disposed)
        {
            return TokenResetOutcome.Saved;
        }

        // Nothing is awaited between the release and the update setting IsBusy, which it does before its first await,
        // so the gate is busy by the time this returns
        if (!HasDataset)
        {
            // Without a dataset, a new token goes straight to the download, as on the first run. It isn't awaited, so
            // the page that asked has its outcome now rather than after the download. RunUpdateAsync catches the
            // service's failures itself. The task is kept only so the tests can await it; keeping it doesn't observe it.
            PendingUpdate = RunUpdateAsync();
            return TokenResetOutcome.Saved;
        }

        StartCheck();
        return TokenResetOutcome.Saved;
    }

    // The held part of the reset. Saved means the new token is saved; the caller then starts the update or the check.
    private async Task<TokenResetOutcome> ConfirmClearAndAskAsync()
    {
        bool confirmed;
        try
        {
            confirmed = await _ui.ConfirmAsync(AppText.ResetTokenTitle, AppText.ResetTokenBody, AppText.ResetTokenButton);
        }
        catch (Exception)
        {
            return TokenResetOutcome.DialogFailed;
        }

        if (!confirmed)
        {
            return TokenResetOutcome.Declined;
        }

        if (_disposed)
        {
            return TokenResetOutcome.Busy;
        }

        bool cleared;
        try
        {
            cleared = await _service.ClearTokenAsync();
        }
        catch (Exception)
        {
            cleared = false;
        }

        if (!cleared)
        {
            return TokenResetOutcome.ClearFailed;
        }

        // The window closed while the token was removed: no prompt opens over a closed window
        if (_disposed)
        {
            return TokenResetOutcome.NoNewToken;
        }

        bool failedToOpen = false;
        string? token = await ShowDialogAsync(() => _ui.PromptForTokenAsync(TokenPromptReason.Reset), () => failedToOpen = true);
        if (_disposed)
        {
            return TokenResetOutcome.NoNewToken;
        }

        if (failedToOpen)
        {
            ShowNoTokenAfterReset();
            return TokenResetOutcome.DialogFailed;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ShowNoTokenAfterReset();
            return TokenResetOutcome.NoNewToken;
        }

        if (!await SaveTokenAsync(token))
        {
            ShowNoTokenAfterReset();
            return TokenResetOutcome.SaveFailed;
        }

        return TokenResetOutcome.Saved;
    }

    private void ShowNoTokenAfterReset()
    {
        if (HasDataset)
        {
            ShowCheckFailed(AppText.NoTokenCheck);
        }
        else
        {
            ShowNoDataset(AppText.NoTokenFirstRun);
        }
    }

    /// <summary>Called by the window before it closes. Closing while busy cancels the work first and waits for it.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsBusy)
        {
            return true;
        }

        bool close = await ConfirmAsync(AppText.CloseBusyTitle, AppText.CloseBusyBody, AppText.CloseBusyButton);
        if (!close)
        {
            return false;
        }

        CancelUpdate();
        if (_operationCompletion is { } completion)
        {
            await completion.Task;
        }

        return true;
    }

    private void CancelUpdate()
    {
        try
        {
            _operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The update has just finished and released its source
        }
    }

    // The dialog can fail to show; the hold stops a second click opening a second dialog meanwhile
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        using IDisposable hold = _gate.Hold();
        try
        {
            return await _ui.ConfirmAsync(title, message, confirmText);
        }
        catch (Exception)
        {
            SetResultsAction(AppText.Generic);
            return false;
        }
    }

    // A dialog that fails to show writes the generic line to the results line, or calls failed instead when given
    private async Task<T?> ShowDialogAsync<T>(Func<Task<T?>> show, Action? failed = null) where T : class
    {
        using IDisposable hold = _gate.Hold();
        try
        {
            return await show();
        }
        catch (Exception)
        {
            if (failed is null)
            {
                SetResultsAction(AppText.Generic);
            }
            else
            {
                failed();
            }

            return null;
        }
    }

    // A failed clipboard or launcher call becomes a message in the results line, never a crash
    private async Task SafeUiActionAsync(Func<Task> action, string errorMessage)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            SetResultsAction(errorMessage);
        }
    }

    private string ResolveExportFolder() => ExportFolders.Resolve(_service.Settings, _defaultExportFolder);

    private void SetDatasetState(DatasetState state, string pill, bool warning, string title, string detail)
    {
        State = state;
        PillText = pill;
        IsPillWarning = warning;
        DatasetStatusText = title;
        DatasetDetailText = detail;
    }

    private void SetProgress(int value, bool indeterminate)
    {
        ProgressValue = value;
        IsProgressIndeterminate = indeterminate;
    }

    private void SetResultsAction(string text, bool fromCheck = false)
    {
        _checkOwnsLine = fromCheck;
        ResultsActionText = text;
        HasResultsAction = text.Length > 0;
    }

    private void RefreshDatasetFacts()
    {
        DatasetInfo? dataset = _service.Dataset;
        DatasetFactsText = FactsFor(dataset);
        RefreshCommandStates();
        // DatasetInfo is a record, so a refresh that reads the same dataset again compares equal and raises nothing
        if (dataset != _datasetShown)
        {
            _datasetShown = dataset;
            DatasetChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string FactsFor(DatasetInfo? dataset) =>
        dataset is null ? AppText.NoDatasetFacts : AppText.DatasetFacts(dataset.DownloadedOn, dataset.RangeCount, dataset.IncludesIPv6);

    private void GateChanged(object? sender, EventArgs e) => RefreshCommandStates();

    private void RefreshCommandStates()
    {
        UpdateDatasetCommand.NotifyCanExecuteChanged();
        CancelUpdateCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CopyResultsCommand.NotifyCanExecuteChanged();
        OpenExportFolderCommand.NotifyCanExecuteChanged();
        CheckAbuseCommand.NotifyCanExecuteChanged();
        OpenInIpinfoCommand.NotifyCanExecuteChanged();
        OpenInAbuseIpDbCommand.NotifyCanExecuteChanged();
        RefreshStopCommands();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Changed -= GateChanged;
        CancelCheck();
        _lifetime.Cancel();
        CancelUpdate();
        RefreshCommandStates();
    }
}
