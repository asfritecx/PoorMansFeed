using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
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
    private readonly string _defaultExportFolder;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCts;
    private TaskCompletionSource? _operationCompletion;
    private CancellationTokenSource? _checkCts;
    private SearchQuery? _lastQuery;
    private int _totalCount;
    private int _searchVersion;
    private bool _started;
    private bool _interactionLocked;
    private bool _disposed;
    private bool _isBusy;
    private bool _isSearching;
    private string _pillBeforeUpdate = "";
    private bool _pillWarningBeforeUpdate;

    private string _pillText = AppText.Starting;
    private bool _isPillWarning;
    private string _datasetStatusText = AppText.Starting;
    private string _datasetDetailText = "";
    private string _datasetFactsText = AppText.NoDatasetFacts;
    private int _progressValue;
    private bool _isProgressIndeterminate;
    private bool _showStartupError;
    private string _startupErrorText = "";
    private string _searchText = "";
    private bool _hasResults;
    private string _resultsSummaryText = AppText.SummaryInitial;
    private string _resultsEmptyText = AppText.ResultsInitial;
    private string _resultsActionText = "";
    private bool _hasResultsAction;
    private string _exportFolder = "";

    public MainViewModel(IIpInfoService service, IUiServices ui, string? defaultExportFolder = null)
    {
        _service = service;
        _ui = ui;
        _defaultExportFolder = defaultExportFolder ?? Path.Combine(service.DataFolder, "exports");
        _exportFolder = ResolveExportFolder();
        _datasetFactsText = FactsFor(service.Dataset);
        State = DatasetState.Starting;

        UpdateDatasetCommand = new AsyncRelayCommand(UpdateDatasetAsync, CanStart);
        CancelUpdateCommand = new RelayCommand(CancelUpdate, () => IsBusy);
        SearchCommand = new AsyncRelayCommand(SearchFromBoxAsync, CanSearch);
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        CopyResultsCommand = new AsyncRelayCommand(CopyResultsAsync, CanCopy);
        OpenExportFolderCommand = new AsyncRelayCommand(OpenExportFolderAsync, () => !_disposed);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, CanStart);
        ResetTokenCommand = new AsyncRelayCommand(ResetTokenAsync, CanStart);
    }

    public IAsyncRelayCommand UpdateDatasetCommand { get; }
    public IRelayCommand CancelUpdateCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand CopyResultsCommand { get; }
    public IAsyncRelayCommand OpenExportFolderCommand { get; }
    public IAsyncRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand ResetTokenCommand { get; }

    /// <summary>Where the dataset card is. Read by the tests.</summary>
    internal DatasetState State { get; private set; }

    /// <summary>The start-up dataset check, or null before one has started. Tests await it.</summary>
    internal Task? PendingCheck { get; private set; }

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
                RefreshCommandStates();
            }
        }
    }

    public bool ShowStartupError { get => _showStartupError; private set => SetProperty(ref _showStartupError, value); }

    public string StartupErrorText { get => _startupErrorText; private set => SetProperty(ref _startupErrorText, value); }

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

    // One predicate gates every action, so a new command cannot forget a case
    private bool CanStart() => _started && !IsBusy && !_interactionLocked && !_disposed;

    private bool HasDataset => _service.Dataset is not null;

    private bool CanSearch() => CanStart() && HasDataset && !_isSearching && !string.IsNullOrWhiteSpace(_searchText);

    private bool CanExport() => CanStart() && _lastQuery is { Kind: SearchKind.Asn } && _totalCount > 0;

    private bool CanCopy() => CanStart() && _lastQuery is not null && _totalCount > 0;

    /// <summary>
    /// Runs the start-up sequence once: the service opens, a first run asks for a token and downloads, and a
    /// database that exists is checked for updates in the background (search stays available meanwhile).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_started || _disposed)
        {
            return;
        }

        StartupResult startup;
        try
        {
            startup = await _service.InitializeAsync(_lifetime.Token);
        }
        catch (Exception)
        {
            startup = new StartupResult(StartupFailure.StorageUnavailable, null, false);
        }

        if (_disposed)
        {
            return;
        }

        switch (startup.Failure)
        {
            case StartupFailure.Privileged:
                await ShowClosingNoticeAndCloseAsync(AppText.PrivilegedTitle, AppText.PrivilegedBody);
                return;
            case StartupFailure.AlreadyRunning:
                await ShowClosingNoticeAndCloseAsync(AppText.AlreadyOpenTitle, AppText.AlreadyOpenBody);
                return;
            case StartupFailure.StorageUnavailable:
                ShowStartupFailure(AppText.StorageUnavailable);
                return;
            case StartupFailure.DatabaseNewer:
                ShowStartupFailure(AppText.DatabaseNewer);
                return;
        }

        _started = true;
        RefreshDatasetFacts();

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

    private void ShowStartupFailure(string text)
    {
        ShowStartupError = true;
        StartupErrorText = text;
        SetProgress(0, false);
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

    /// <summary>Returns true when a token is saved. Asks for one only when none is saved.</summary>
    private async Task<bool> EnsureTokenAsync(TokenPromptReason reason)
    {
        if (_service.HasToken)
        {
            return true;
        }

        string? token = await ShowDialogAsync(() => _ui.PromptForTokenAsync(reason));
        if (string.IsNullOrWhiteSpace(token) || _disposed)
        {
            return false;
        }

        if (await SaveTokenAsync(token))
        {
            return true;
        }

        ResultsActionText = AppText.TokenSaveFailed;
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

    private async Task RunUpdateAsync()
    {
        if (_disposed)
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

    private async Task RunSearchAsync(string input)
    {
        if (_disposed)
        {
            return;
        }

        int version = ++_searchVersion;
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

        _isSearching = false;
        RefreshCommandStates();
        if (result is not null && !_disposed)
        {
            ApplySearch(result);
        }
    }

    private void ApplySearch(SearchResult result)
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
                SetResultsAction(AppText.ExportIpNote);
            }

            return;
        }

        ReplaceResults([]);
        ResultsSummaryText = "";
        SetResultsAction("");
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

        SetResultsAction(ExportMessage(result));
    }

    private static string ExportMessage(ExportResult result)
    {
        switch (result.Failure)
        {
            case ExportFailure.FolderUnavailable:
                return AppText.ExportFolderUnavailable;
            case ExportFailure.WriteFailed:
                return AppText.ExportWriteFailed;
            case ExportFailure.NothingToExport:
                return AppText.NothingExported;
        }

        if (result.NetworksWritten == 0)
        {
            return AppText.NothingExported;
        }

        string text = AppText.Exported(result.NetworksWritten, result.Files.Count);
        return result.LargeRangesLeftOut > 0 ? text + AppText.LargeRangesLeftOut(result.LargeRangesLeftOut) : text;
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

    private async Task OpenSettingsAsync()
    {
        if (!CanStart())
        {
            return;
        }

        UserSettings? edited = await ShowDialogAsync(() => _ui.EditSettingsAsync(_service.Settings, _defaultExportFolder));
        if (edited is null || !CanStart())
        {
            return;
        }

        bool ipv6Changed = edited.IPv6Enabled != _service.Settings.IPv6Enabled;
        bool saved;
        try
        {
            saved = await _service.SaveSettingsAsync(edited);
        }
        catch (Exception)
        {
            saved = false;
        }

        if (!saved)
        {
            SetResultsAction(AppText.SettingsSaveFailed);
            return;
        }

        ExportFolder = ResolveExportFolder();
        // Turning IPv6 on or off changes what the dataset card says (IPv4 only, or an update to add IPv6), so the check
        // runs again. Search is not held up by it.
        if (ipv6Changed && HasDataset && _service.HasToken)
        {
            StartCheck();
        }

        // The badges of the open results depend on the settings, so the search runs again
        if (_lastQuery is { } query)
        {
            await RunSearchAsync(query.Text);
        }
    }

    private async Task ResetTokenAsync()
    {
        if (!CanStart())
        {
            return;
        }

        bool confirmed = await ConfirmAsync(AppText.ResetTokenTitle, AppText.ResetTokenBody, AppText.ResetTokenButton);
        if (!confirmed || !CanStart())
        {
            return;
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
            SetResultsAction(AppText.Generic);
            return;
        }

        string? token = await ShowDialogAsync(() => _ui.PromptForTokenAsync(TokenPromptReason.Reset));
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ShowNoTokenAfterReset();
            return;
        }

        if (!await SaveTokenAsync(token))
        {
            SetResultsAction(AppText.TokenSaveFailed);
            ShowNoTokenAfterReset();
            return;
        }

        if (!HasDataset)
        {
            // Without a dataset, a new token goes straight to the download, as on the first run
            await RunUpdateAsync();
            return;
        }

        StartCheck();
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

    // The dialog can fail to show; the lock stops a second click opening a second dialog meanwhile
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        _interactionLocked = true;
        RefreshCommandStates();
        try
        {
            return await _ui.ConfirmAsync(title, message, confirmText);
        }
        catch (Exception)
        {
            ResultsActionText = AppText.Generic;
            return false;
        }
        finally
        {
            _interactionLocked = false;
            RefreshCommandStates();
        }
    }

    private async Task<T?> ShowDialogAsync<T>(Func<Task<T?>> show) where T : class
    {
        _interactionLocked = true;
        RefreshCommandStates();
        try
        {
            return await show();
        }
        catch (Exception)
        {
            ResultsActionText = AppText.Generic;
            return null;
        }
        finally
        {
            _interactionLocked = false;
            RefreshCommandStates();
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

    private void SetResultsAction(string text)
    {
        ResultsActionText = text;
        HasResultsAction = text.Length > 0;
    }

    private void RefreshDatasetFacts()
    {
        DatasetFactsText = FactsFor(_service.Dataset);
        RefreshCommandStates();
    }

    private static string FactsFor(DatasetInfo? dataset) =>
        dataset is null ? AppText.NoDatasetFacts : AppText.DatasetFacts(dataset.DownloadedOn, dataset.RangeCount, dataset.IncludesIPv6);

    private void RefreshCommandStates()
    {
        UpdateDatasetCommand.NotifyCanExecuteChanged();
        CancelUpdateCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CopyResultsCommand.NotifyCanExecuteChanged();
        OpenExportFolderCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
        ResetTokenCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCheck();
        _lifetime.Cancel();
        CancelUpdate();
        RefreshCommandStates();
    }
}
