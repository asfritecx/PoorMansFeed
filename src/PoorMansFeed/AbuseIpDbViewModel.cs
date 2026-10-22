using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed;

/// <summary>
/// The AbuseIPDB Blacklist page: the rolling list's status, Download blacklist (with the key prompt and one retry),
/// Clear list, Write feed, Empty feed and Open folder. Its runs keep their own IsWorking rather than the work gate, so
/// an update never disables the page; only its dialogs (the two confirmations, the key prompt and the new addresses, whose
/// press holds it only while its dialog is open) hold the gate, so no dialog opens over another. It never reads or sets MainViewModel.IsBusy. The offending networks group beside the
/// runs with their own IsGrouping, so a 12-second grouping and a download never lock each other.
/// </summary>
public sealed class AbuseIpDbViewModel : ObservableObject, IDisposable
{
    /// <summary>The most offending networks shown; the summary names the rest.</summary>
    public const int RowLimit = 300;

    /// <summary>The most new addresses the dialog lists; its count line names the rest and its Copy covers every one.</summary>
    public const int NewAddressRowLimit = 300;

    private static readonly AbuseFeedWriteResult FailedWrite = new(AbuseFeedWriteFailure.WriteFailed, [], 0, 0, 0, 0, []);

    private readonly IAbuseIpDbService? _abuse;
    private readonly IIpInfoService _ipinfo;
    private readonly IUiServices _ui;
    private readonly IWorkGate _gate;
    private readonly AbuseKeyPrompt? _abuseKeys;
    private readonly TimeProvider _time;
    private readonly IUnsavedSettings? _unsaved;
    private readonly string _defaultExportFolder;
    private readonly CancellationTokenSource _lifetime = new();

    private int _statusVersion;
    private bool _disposed;
    private bool _isWorking;
    private string _factsText;
    private string _factsHeadText;
    private string _factsDetailText = "";
    private string _statusText = "";
    private bool _isStatusWarning;
    private bool _lineOwned;
    private string _actionLine = "";
    private bool _actionLineWarning;
    private (AbuseListStatus status, int keepDays)? _lastStatus;
    private Task? _activation;
    private bool _activateWhenStarted;
    private string _actionText = "";
    private string _writeFeedTooltip;

    // The New addresses button: the last status's count, and whether a press is out, from the press to the dialog's close
    private int _newCount;
    private bool _showingNew;

    // The close: the service call out right now (and whether it is a download), the window's holds on the runs, and
    // whether the stop has begun, after which a run starts nothing more once its call ends
    private Task? _call;
    private bool _callOut;
    private bool _callIsDownload;
    private int _closeHolds;
    private bool _closing;

    // The offending networks. Their own version, so a grouping start never drops a status answer or the other way round
    private int _groupingVersion;
    private CancellationTokenSource? _groupingCts;
    private GroupingInputs? _groupingInputs;
    private AbuseNetworksFailure? _groupingOutcome;
    private bool _isGrouping;
    private double _groupingProgress;
    private string _summaryText = "";
    private string _emptyText = AppText.AbuseNetworksEmpty;
    private bool _hasRows;

    internal AbuseIpDbViewModel(IAbuseIpDbService? abuse, IIpInfoService ipinfo, IUiServices ui, IWorkGate gate,
                                AbuseKeyPrompt? abuseKeys, TimeProvider time, string? defaultExportFolder,
                                IUnsavedSettings? unsaved = null)
    {
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = time ?? throw new ArgumentNullException(nameof(time));

        // The prompt holds the gate while it asks, so it must be this page's gate or a dialog could open over another
        if (abuseKeys is not null && !ReferenceEquals(abuseKeys.Gate, gate))
        {
            throw new ArgumentException("The key prompt must hold the view model's own gate.", nameof(abuseKeys));
        }

        _abuse = abuse;
        _abuseKeys = abuseKeys ?? (abuse is null ? null : new AbuseKeyPrompt(ui, abuse, gate));
        _unsaved = unsaved;
        _defaultExportFolder = defaultExportFolder ?? Path.Combine(ipinfo.DataFolder, "exports");
        _factsText = abuse is null ? AppText.AbuseKeysUnavailable : AppText.ReadingAbuseList;
        _factsHeadText = _factsText;
        _writeFeedTooltip = AppText.WriteAbuseFeedTooltip(ReadIPv6Enabled() ?? true);

        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanRun);
        ClearListCommand = new AsyncRelayCommand(ClearListAsync, CanRun);
        WriteFeedCommand = new AsyncRelayCommand(WriteFeedAsync, CanRun);
        EmptyFeedCommand = new AsyncRelayCommand(EmptyFeedAsync, CanRun);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => IsAvailable && !_disposed);
        ShowNewAddressesCommand = new AsyncRelayCommand(ShowNewAddressesAsync, CanShowNewAddresses);

        // Not on IsWorking, IsGrouping or the gate: Look up only hands an ASN to the window, which searches it there
        LookUpCommand = new RelayCommand<OffendingNetworkRow>(LookUp, row => row is not null && IsAvailable && !_disposed && !_showingNew);

        // Not on the new addresses press either: opening a browser is no page run, and that dialog is modal anyway
        OpenNetworkInIpinfoCommand = new AsyncRelayCommand<OffendingNetworkRow>(OpenNetworkInIpinfoAsync,
            row => row is not null && IsAvailable && !_disposed);

        // IsStarted flips once, when start-up opens the service; until then nothing the page shows is the saved state
        _gate.Changed += GateChanged;
    }

    public bool IsAvailable => _abuse is not null;

    /// <summary>A download, Clear list, Write feed or Empty feed is running, from the press to the end.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
                RefreshCommands();
        }
    }

    /// <summary>The counts within the keep days, the last download and the downloads left.</summary>
    public string FactsText
    {
        get => _factsText;
        private set => SetProperty(ref _factsText, value);
    }

    /// <summary>The facts row's first line: the last download, or the whole text when there is no detail.</summary>
    public string FactsHeadText
    {
        get => _factsHeadText;
        private set => SetProperty(ref _factsHeadText, value);
    }

    /// <summary>The facts row's second line: the downloads left, then the counts; empty when there is none.</summary>
    public string FactsDetailText
    {
        get => _factsDetailText;
        private set => SetProperty(ref _factsDetailText, value);
    }

    // The two lines and the whole line change together, so the tooltip never names another state than the lines
    private void SetFacts(string head, string detail, string whole)
    {
        FactsHeadText = head;
        FactsDetailText = detail;
        FactsText = whole;
    }

    /// <summary>
    /// While Download or Clear list runs, its own line. Otherwise the newest status chooses: the list's state, then the
    /// clock behind the last download, then the refusal, then the stale warning, and with none of them the last download
    /// or Clear list line, so a line whose condition has gone gives way.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsStatusWarning
    {
        get => _isStatusWarning;
        private set => SetProperty(ref _isStatusWarning, value);
    }

    /// <summary>The line under the actions: Write feed, Empty feed and Open folder.</summary>
    public string ActionText
    {
        get => _actionText;
        private set
        {
            if (SetProperty(ref _actionText, value))
                OnPropertyChanged(nameof(HasActionText));
        }
    }

    public bool HasActionText => ActionText.Length > 0;

    public string WriteFeedTooltip
    {
        get => _writeFeedTooltip;
        private set => SetProperty(ref _writeFeedTooltip, value);
    }

    /// <summary>The offending networks are being looked up. Never part of IsWorking, so no run waits for it.</summary>
    public bool IsGrouping
    {
        get => _isGrouping;
        private set => SetProperty(ref _isGrouping, value);
    }

    /// <summary>0 to 1, and within one grouping never backwards; a new grouping starts again at 0.</summary>
    public double GroupingProgress
    {
        get => _groupingProgress;
        private set => SetProperty(ref _groupingProgress, value);
    }

    /// <summary>While grouping, how far it has got; after a grouping, its networks, buckets and that it is a report.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>The list's line when it has no rows: what will appear, or why nothing was grouped.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set => SetProperty(ref _emptyText, value);
    }

    public bool HasRows
    {
        get => _hasRows;
        private set => SetProperty(ref _hasRows, value);
    }

    /// <summary>At most <see cref="RowLimit"/>; its contents are replaced, never the collection, which the window binds once.</summary>
    public ObservableCollection<OffendingNetworkRow> Rows { get; } = [];

    /// <summary>Builds the progress sink for a grouping. The default posts to the captured context; tests inject a synchronous one.</summary>
    internal Func<Action<GroupingProgress>, IProgress<GroupingProgress>> ProgressFactory { get; set; } =
        handler => new Progress<GroupingProgress>(handler);

    /// <summary>The newest grouping started, for tests to await; ActivateAsync doesn't wait for it.</summary>
    internal Task? PendingGrouping { get; private set; }

    /// <summary>Carries the row's "AS{digits}", for the window to search on IPInfo Lookup.</summary>
    public event EventHandler<string>? LookUpRequested;

    public IAsyncRelayCommand DownloadCommand { get; }
    public IAsyncRelayCommand ClearListCommand { get; }
    public IAsyncRelayCommand WriteFeedCommand { get; }
    public IAsyncRelayCommand EmptyFeedCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IAsyncRelayCommand ShowNewAddressesCommand { get; }
    public IRelayCommand<OffendingNetworkRow> LookUpCommand { get; }

    /// <summary>Opens a row's AS on ipinfo.io, from the row's menu. The page is built from the grouping's ASN alone.</summary>
    public IAsyncRelayCommand<OffendingNetworkRow> OpenNetworkInIpinfoCommand { get; }

    /// <summary>A download, Clear list, Write feed or Empty feed is with the service: from just before its call to the call's end.
    /// Not while a confirmation or the key prompt is open, and never for the grouping or a status read. The close asks
    /// about it first, and the stop waits for that call.</summary>
    public bool AsksBeforeClose => _callOut && !_disposed;

    /// <summary>Asks whether to stop the page's work and close. True at once when nothing is with the service. Asking
    /// cancels nothing; a dialog that fails to show keeps the window open (false).</summary>
    public async Task<bool> AskBeforeCloseAsync()
    {
        if (!AsksBeforeClose)
            return true;

        string body = _callIsDownload ? AppText.CloseAbuseDownloadBody : AppText.CloseAbuseWorkBody;
        return await ConfirmAsync(AppText.CloseAbuseWorkTitle, body, AppText.CloseAbuseWorkButton) == true;
    }

    /// <summary>Stops the page's work for the close. From now on a run whose call ends starts nothing more: no key
    /// prompt, retry, line, status read or grouping. A download not yet answered is cancelled; once a 2xx answer's
    /// headers are in, its body (up to the request's remaining timeout) and its merge still finish. Clear list, Write
    /// feed and Empty feed are never cancelled, only waited for. Returns once the
    /// call with the service has ended, not the run around it, and at once when nothing is with the service. Never
    /// throws.</summary>
    public async Task StopForCloseAsync()
    {
        _closing = true;
        if (!AsksBeforeClose || _call is not { } call)
            return;

        // The window closes and disposes the page next, so a download's lifetime can go now; the service ignores it
        // once the answer is in, and the call then ends after the merge
        if (_callIsDownload)
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            await call;
        }
        catch (Exception)
        {
            // The call's outcome no longer matters: the window is closing
        }
    }

    /// <summary>Turns the page's run commands off (Download, Clear list, Write feed, Empty feed) until the returned hold
    /// is disposed. The window takes it once the page's close question is agreed or not needed, so nothing starts while
    /// the update winds down or the page's work is stopped; a later question declined releases it. A run already going
    /// goes on. Holds nest, and a second Dispose does nothing.</summary>
    public IDisposable HoldRunsForClose()
    {
        _closeHolds++;
        RefreshCommands();
        return new RunsHold(this);
    }

    /// <summary>
    /// Called on every show: reads the status again, because a download, the keep days or the clock may have moved it
    /// since. A run reads the status itself when it ends, so nothing is read while one is out. A show while an earlier
    /// show's read is out joins that read: the list answers one read at a time, so repeated shows would queue reads
    /// ahead of a download. The offending networks are checked on every show, a run or a joined read included, and
    /// grouped again only when stale; that isn't awaited.
    /// </summary>
    public Task ActivateAsync()
    {
        // The tooltip reads only the settings, so it follows the IPv6 switch even while the page can't read the list
        if (ReadIPv6Enabled() is { } ipv6Enabled)
            WriteFeedTooltip = AppText.WriteAbuseFeedTooltip(ipv6Enabled);

        if (_abuse is null || _disposed)
            return Task.CompletedTask;

        // Before start the settings are the defaults and the dataset unknown, so a read now would show the wrong list
        // until the next show; the ask waits for the gate to start instead
        if (!_gate.IsStarted)
        {
            _activateWhenStarted = true;
            return Task.CompletedTask;
        }

        GroupIfStale(_abuse);

        if (IsWorking)
            return Task.CompletedTask;

        if (_activation is { IsCompleted: false } running)
            return running;

        _activation = ActivateCoreAsync(_abuse);
        return _activation;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gate.Changed -= GateChanged;

        // Linked to the lifetime, so it goes first. Its run can end inside Cancel while it is still the newest grouping,
        // so only the disposed check in its finally keeps it from setting anything
        _groupingCts?.Cancel();
        _groupingCts?.Dispose();
        _groupingCts = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }

    // Never on the gate's CanStart: an update or another page's dialog must not disable the page's own runs. The
    // window's close hold is its own, not the gate's
    private bool CanRun() => IsAvailable && _gate.IsStarted && !IsWorking && !_disposed && _closeHolds == 0 && !_showingNew;

    private bool CanShowNewAddresses() => CanRun() && _newCount > 0;

    // Marks the service call as out for the close question, from just before the call to its end, and keeps the call
    // itself for the stop to wait on
    private async Task<T> CallServiceAsync<T>(Func<CancellationToken, Task<T>> call, bool isDownload)
    {
        _callIsDownload = isDownload;
        _callOut = true;
        try
        {
            Task<T> task = call(_lifetime.Token);
            _call = task;
            return await task;
        }
        finally
        {
            _callOut = false;
            _call = null;
        }
    }

    private sealed class RunsHold(AbuseIpDbViewModel page) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;

            _released = true;
            page._closeHolds--;
            page.RefreshCommands();
        }
    }

    private void GateChanged(object? sender, EventArgs e)
    {
        RefreshCommands();

        // A show before start runs once, when the gate starts; a later change asks nothing again
        if (_activateWhenStarted && _gate.IsStarted && !_disposed)
        {
            _activateWhenStarted = false;
            _ = ActivateAsync();
        }
    }

    private void RefreshCommands()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        ClearListCommand.NotifyCanExecuteChanged();
        WriteFeedCommand.NotifyCanExecuteChanged();
        EmptyFeedCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        ShowNewAddressesCommand.NotifyCanExecuteChanged();
        LookUpCommand.NotifyCanExecuteChanged();
        OpenNetworkInIpinfoCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, bool warning)
    {
        StatusText = text;
        IsStatusWarning = warning;
    }

    // A download's or Clear list's end line wins now, and is what the line falls back to once nothing else applies
    private void SetActionLine(string text, bool warning)
    {
        _actionLine = text;
        _actionLineWarning = warning;
        SetStatus(text, warning);
    }

    // Runs after every read that wins, and after Clear list answered no; a running Download or Clear list keeps its line
    private void ChooseStatusLine()
    {
        if (_lineOwned || _disposed)
            return;

        if (_lastStatus is { } answer)
        {
            string stateLine = AppText.AbuseListStateLine(answer.status.State);
            if (stateLine.Length > 0)
            {
                SetStatus(stateLine, warning: true);
                return;
            }

            // Before the refusal time, because a download refuses for the clock before it looks at the limit; and the
            // stale warning, which counts from the same clock, can't apply
            if (answer.status.ClockBehind)
            {
                SetStatus(AppText.AbuseClockBehind, warning: true);
                return;
            }

            if (answer.status.RefusedUntil is { } refusedUntil)
            {
                SetStatus(AppText.AbuseNextDownloadAfter(refusedUntil), warning: false);
                return;
            }

            if (answer.status.LastDownloadAt is { } last && _time.GetUtcNow() - last > TimeSpan.FromDays(answer.keepDays))
            {
                SetStatus(AppText.AbuseListStale(answer.keepDays), warning: true);
                return;
            }
        }

        SetStatus(_actionLine, _actionLineWarning);
    }

    private bool? ReadIPv6Enabled()
    {
        try
        {
            return _ipinfo.Settings.IPv6Enabled;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The service clamps the keep days too; clamping here keeps the facts row's day count the one it used
    private int KeepDays(UserSettings settings) =>
        Math.Clamp(settings.AbuseIpDbKeepDays, AbuseIpDbLimits.MinKeepDays, AbuseIpDbLimits.MaxKeepDays);

    // The read chooses the line itself, so an activation is the read alone
    private async Task ActivateCoreAsync(IAbuseIpDbService abuse) => await ReadStatusAsync(abuse);

    /// <summary>
    /// Reads the status, shows the facts row, keeps the answer as the last status and chooses the line from it. Null
    /// when the read failed, was cancelled, or was overtaken by a newer read, whose answer is the one shown; then the
    /// last status and the line stay as they were. <paramref name="callerRegroups"/> is true for a caller that groups
    /// again itself right after the read (a successful download, Clear list), so the read starts no grouping it would
    /// supersede at once.
    /// </summary>
    private async Task<(AbuseListStatus status, int keepDays)?> ReadStatusAsync(IAbuseIpDbService abuse,
                                                                              bool callerRegroups = false)
    {
        int version = ++_statusVersion;
        try
        {
            AbuseListStatus status = await abuse.GetStatusAsync(_lifetime.Token);
            if (_disposed || version != _statusVersion)
                return null;

            int keepDays = KeepDays(_ipinfo.Settings);
            DateTimeOffset now = _time.GetUtcNow();
            SetFacts(AppText.AbuseFactsHead(status, now), AppText.AbuseFactsDetail(status, keepDays, now),
                AppText.AbuseFacts(status, keepDays, now));
            _lastStatus = (status, keepDays);
            _newCount = status.NewCount;
            ShowNewAddressesCommand.NotifyCanExecuteChanged();
            ChooseStatusLine();

            // A grouping refused for a clock behind the last download groups again once a read finds the clock put
            // right, also after an action, not only on a show; not once the close has stopped the runs. A grouping never
            // reads the status, so this can't loop
            if (!callerRegroups && !_closing && !status.ClockBehind && !IsGrouping
                && _groupingOutcome == AbuseNetworksFailure.ClockWrong && ReadGroupingInputs() is { } inputs)
            {
                StartGrouping(abuse, inputs);
            }
            return (status, keepDays);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            if (!_disposed && version == _statusVersion)
            {
                SetFacts(AppText.Generic, "", AppText.Generic);

                // The count is unknown now, so the button goes off until a status names new addresses again
                _newCount = 0;
                ShowNewAddressesCommand.NotifyCanExecuteChanged();
            }

            return null;
        }
    }

    private async Task DownloadAsync()
    {
        if (_abuse is not { } abuse || !CanRun())
            return;

        IsWorking = true;
        _lineOwned = true;
        SetStatus(AppText.DownloadingBlacklist, warning: false);
        try
        {
            AbuseDownloadResult? result = null;
            string? line = null;
            try
            {
                result = await CallServiceAsync(abuse.DownloadBlacklistAsync, isDownload: true);
                if (_disposed || _closing)
                    return;

                if (result.Failure is AbuseIpDbFailure.NoKey or AbuseIpDbFailure.KeyRejected)
                {
                    bool replacing = result.Failure == AbuseIpDbFailure.KeyRejected;

                    // The prompt asks only while nothing else holds the gate (another dialog, an update, the Settings
                    // page's own key prompt); otherwise it is Busy and the line says nothing was downloaded
                    AbuseKeyOutcome asked = _abuseKeys is null
                        ? AbuseKeyOutcome.Busy
                        : await _abuseKeys.AskAndSaveAsync(replacing ? AbuseKeyAsk.Rejected : AbuseKeyAsk.Missing);
                    if (_disposed || _closing)
                        return;

                    switch (asked)
                    {
                        case AbuseKeyOutcome.Saved:
                            // One more download with the saved key; whatever it says goes to the line, and nothing asks again
                            result = await CallServiceAsync(abuse.DownloadBlacklistAsync, isDownload: true);
                            if (_disposed || _closing)
                                return;
                            break;
                        case AbuseKeyOutcome.DialogFailed:
                            line = AppText.Generic;
                            break;
                        case AbuseKeyOutcome.NotAKey:
                            line = AppText.AbuseKeyNotAKey;
                            break;
                        case AbuseKeyOutcome.SaveFailed:
                            line = AppText.AbuseKeySaveFailed;
                            break;
                        default:
                            // Busy or Cancelled
                            line = replacing ? AppText.AbuseDownloadKeyReplaceCancelled : AppText.AbuseDownloadKeyCancelled;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (_disposed || _closing)
                    return;
                line = AppText.Generic;
            }

            // The status after the download names the refusal the service now holds, which can be later than the
            // answer's own retry time. A download that succeeded groups again below, so its read starts no grouping
            bool downloaded = line is null && result is { Failure: AbuseIpDbFailure.None };
            (AbuseListStatus status, int keepDays)? read = await ReadStatusAsync(abuse, callerRegroups: downloaded);
            if (_disposed)
                return;

            if (line is not null || result is null)
            {
                SetActionLine(line ?? AppText.Generic, warning: true);
            }
            else if (downloaded)
            {
                SetActionLine(AppText.AbuseDownloaded(result, read?.keepDays ?? KeepDays(_ipinfo.Settings)), warning: false);

                // The list changed, whatever the inputs say
                Regroup(abuse);
            }
            else
            {
                DateTimeOffset? at = result.Failure is AbuseIpDbFailure.RateLimited or AbuseIpDbFailure.LimitReached
                    ? read?.status.RefusedUntil ?? result.RetryAt
                    : null;
                SetActionLine(AppText.AbuseDownloadFailure(result.Failure, at, result.Counted), warning: true);
            }
        }
        catch (Exception)
        {
            // Only the settings read above can land here; the line must still end the run
            if (!_disposed)
                SetActionLine(AppText.Generic, warning: true);
        }
        finally
        {
            if (!_disposed)
            {
                _lineOwned = false;
                IsWorking = false;
            }
        }
    }

    private async Task ClearListAsync()
    {
        if (_abuse is not { } abuse || !CanRun())
            return;

        // Working from the press to the end, the confirmation included, and it owns the line for as long
        IsWorking = true;
        _lineOwned = true;
        try
        {
            // Not an end line: the next read brings back the last download's line, as New addresses' busy line does
            if (_gate.IsHeld)
            {
                SetStatus(AppText.AbuseClearListBusy, warning: true);
                return;
            }

            bool? confirmed = await ConfirmAsync(AppText.ClearAbuseListTitle, AppText.ClearAbuseListBody,
                AppText.ClearAbuseListButton);
            if (_disposed)
                return;
            if (confirmed is null)
            {
                SetActionLine(AppText.Generic, warning: true);
                return;
            }

            if (confirmed == false)
            {
                // Nothing ran, so a read that returned while the dialog was open gets its say now
                _lineOwned = false;
                ChooseStatusLine();
                return;
            }

            SetStatus(AppText.ClearingAbuseList, warning: false);
            AbuseIpDbFailure failure;
            try
            {
                failure = await CallServiceAsync(abuse.ClearListAsync, isDownload: false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                failure = AbuseIpDbFailure.StorageError;
            }

            if (_disposed || _closing)
                return;

            if (failure == AbuseIpDbFailure.None)
                SetActionLine(AppText.AbuseListCleared, warning: false);
            else
                SetActionLine(AppText.AbuseListClearFailed, warning: true);

            await ReadStatusAsync(abuse, callerRegroups: true);

            // Even a failed clear: one that fails part way may already have deleted the database
            Regroup(abuse);
        }
        finally
        {
            if (!_disposed)
            {
                _lineOwned = false;
                IsWorking = false;
            }
        }
    }

    private async Task WriteFeedAsync()
    {
        if (_abuse is not { } abuse || !CanRun())
            return;

        IsWorking = true;
        ActionText = AppText.WritingAbuseFeed;
        try
        {
            int keepDays = AbuseIpDbLimits.DefaultKeepDays;
            bool ipv6Enabled = true;
            try
            {
                // Read once, just before the write, so the line says what the write read; a save during it doesn't change it
                bool unsaved = _unsaved?.HasUnsavedChanges == true;
                UserSettings settings = _ipinfo.Settings;
                string folder = FortigateFolder(settings);
                keepDays = KeepDays(settings);
                ipv6Enabled = settings.IPv6Enabled;

                AbuseFeedWriteResult result = await CallServiceAsync(abuse.WriteFeedAsync, isDownload: false);
                if (_disposed || _closing)
                    return;

                ActionText = result.Failure switch
                {
                    AbuseFeedWriteFailure.None =>
                        AppText.AbuseFeedWritten(result, folder) + (unsaved ? AppText.UnsavedSettingsNote : ""),
                    // Exceptions emptied the feed, so the unsaved ones might have left something to write
                    AbuseFeedWriteFailure.NothingToWrite => AppText.AbuseFeedWriteFailureLine(result, keepDays, ipv6Enabled)
                        + (unsaved && result.ExceptionRemovals > 0 ? AppText.UnsavedSettingsNote : ""),
                    _ => AppText.AbuseFeedWriteFailureLine(result, keepDays, ipv6Enabled, unsaved),
                };
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (_disposed || _closing)
                    return;
                ActionText = AppText.AbuseFeedWriteFailureLine(FailedWrite, keepDays, ipv6Enabled);
            }

            await ReadStatusAsync(abuse);
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    private async Task EmptyFeedAsync()
    {
        if (_abuse is not { } abuse || !CanRun())
            return;

        IsWorking = true;
        try
        {
            if (_gate.IsHeld)
            {
                ActionText = AppText.AbuseEmptyFeedBusy;
                return;
            }

            bool? confirmed = await ConfirmAsync(AppText.EmptyAbuseFeedTitle, AppText.EmptyAbuseFeedBody,
                AppText.EmptyAbuseFeedButton);
            if (_disposed)
                return;
            if (confirmed is null)
            {
                ActionText = AppText.Generic;
                return;
            }

            if (confirmed == false)
                return;

            ActionText = AppText.EmptyingAbuseFeed;
            string folder = "";
            try
            {
                folder = FortigateFolder(_ipinfo.Settings);
                AbuseFeedWriteResult result = await CallServiceAsync(abuse.EmptyFeedAsync, isDownload: false);
                if (_disposed || _closing)
                    return;

                // No unsaved note: Empty feed writes no address, so the exceptions it could have used don't matter
                ActionText = result.Failure == AbuseFeedWriteFailure.None
                    ? AppText.AbuseFeedEmptied(result, folder)
                    : AppText.AbuseFeedEmptyFailureLine(result, folder);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (_disposed || _closing)
                    return;
                ActionText = AppText.AbuseFeedEmptyFailureLine(FailedWrite, folder);
            }

            await ReadStatusAsync(abuse);
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    /// <summary>
    /// Reads the last download's new addresses and shows them in the dialog. The page's runs and this button are off
    /// from the press until the dialog closes; the gate is held only while the dialog is open, so Lookup, the Settings
    /// page and an update stay usable during the read. When the rows are in, the press shows nothing while anything
    /// else holds the gate or the window's close holds the runs, because a close question or another dialog may have
    /// opened during the read. The read is a read like the status's: it isn't a run, the close doesn't ask about it and
    /// the stop doesn't wait for it. The press never sets the action line.
    /// </summary>
    private async Task ShowNewAddressesAsync()
    {
        if (_abuse is not { } abuse || !CanShowNewAddresses())
            return;

        _showingNew = true;
        RefreshCommands();
        try
        {
            AbuseNewAddresses read;
            try
            {
                read = await abuse.GetNewAddressesAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                read = new AbuseNewAddresses(AbuseNewAddressesFailure.StorageError, [], null);
            }

            if (_disposed || _closing)
                return;

            if (read.Failure != AbuseNewAddressesFailure.None || read.Addresses.Count == 0 || read.AsOf is not { } asOf)
            {
                AbuseNewAddressesFailure failure = read.Failure == AbuseNewAddressesFailure.None
                    ? AbuseNewAddressesFailure.NotKnown
                    : read.Failure;

                // The status chooses the line and the count; only while the list is usable and still names new
                // addresses does the press say why it showed none, and that line gives way at the next read
                // The read is the page's activation while it runs, so a show meanwhile joins it rather than overtaking
                // it and dropping the line
                Task<(AbuseListStatus status, int keepDays)?> reading = ReadStatusAsync(abuse);
                _activation = reading;
                (AbuseListStatus status, int keepDays)? after = await reading;
                if (_disposed || _closing)
                    return;

                if (after is { } answer && AppText.AbuseListStateLine(answer.status.State).Length == 0
                    && answer.status.NewCount > 0)
                {
                    SetStatus(AppText.AbuseNewAddressesFailureLine(failure), warning: true);
                }

                return;
            }

            NewAddressesPrompt prompt = BuildNewAddressesPrompt(read.Addresses, asOf);

            // A test at the moment of showing, not CanStart, so an update alone doesn't refuse the dialog; nothing is
            // awaited between the test and the hold, so no other dialog can open in between
            if (_gate.IsHeld || _closeHolds > 0)
            {
                SetStatus(AppText.AbuseNewAddressesBusy, warning: true);
                return;
            }

            using IDisposable hold = _gate.Hold();
            try
            {
                await _ui.ShowNewAddressesAsync(prompt);
            }
            catch (Exception)
            {
                if (!_disposed)
                    SetStatus(AppText.AbuseNewAddressesNotShown, warning: true);
            }
        }
        finally
        {
            // A hold taken is released by now, at the end of the block above
            _showingNew = false;
            RefreshCommands();
        }
    }

    private static NewAddressesPrompt BuildNewAddressesPrompt(IReadOnlyList<AbuseNewAddress> addresses, DateTimeOffset asOf)
    {
        var rows = addresses.Take(NewAddressRowLimit)
            .Select(a => new NewAddressRow(a.Address.Text, AppText.NewAddressScore(a.Score),
                AppText.NewAddressLastReported(a.LastReportedAt), WebPage.IpInfoAddress(a.Address),
                WebPage.AbuseIpDbCheck(a.Address)))
            .ToList();

        // Copy covers every address, not only the rows shown, in the read's order
        string copyText = string.Join(Environment.NewLine, addresses.Select(a => a.Address.Text));
        return new NewAddressesPrompt(AppText.NewAddressesTitle, AppText.NewAddressesMessage(asOf),
            AppText.NewAddressesCount(rows.Count, addresses.Count), rows, copyText, AppText.NewAddressesCopied(addresses.Count));
    }

    private async Task OpenFolderAsync()
    {
        if (!IsAvailable || _disposed)
            return;

        try
        {
            string folder = ExportFolders.Resolve(_ipinfo.Settings, _defaultExportFolder);
            Directory.CreateDirectory(folder);
            await _ui.OpenFolderAsync(folder);
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = AppText.FolderOpenFailed;
        }
    }

    private string FortigateFolder(UserSettings settings) =>
        Path.Combine(ExportFolders.Resolve(settings, _defaultExportFolder), ExportWriter.FortigateFolderName);

    // A browser that launched proves nothing, so only a failure says anything
    private async Task OpenNetworkInIpinfoAsync(OffendingNetworkRow? row)
    {
        if (row is null || !IsAvailable || _disposed)
            return;

        try
        {
            await _ui.OpenWebPageAsync(WebPage.IpInfoAsn(row.Asn));
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = AppText.WebPageOpenFailed;
        }
    }

    private void LookUp(OffendingNetworkRow? row)
    {
        if (row is null || !IsAvailable || _disposed)
            return;

        LookUpRequested?.Invoke(this, row.AsnText);
    }

    /// <summary>What a grouping depends on besides the list: a change in any of them makes its answer stale.</summary>
    private readonly record struct GroupingInputs(DatasetInfo? Dataset, bool IPv6Enabled, int KeepDays);

    // Null when a read throws; the caller then starts nothing, because ActivateAsync must never throw into the
    // dataset update that calls it
    private GroupingInputs? ReadGroupingInputs()
    {
        try
        {
            UserSettings settings = _ipinfo.Settings;
            return new GroupingInputs(_ipinfo.Dataset, settings.IPv6Enabled, KeepDays(settings));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // DatasetInfo is a record, so a dataset read again with the same values is the same dataset
    private static bool SameInputs(GroupingInputs kept, GroupingInputs current) =>
        Equals(kept.Dataset, current.Dataset)
        && kept.IPv6Enabled == current.IPv6Enabled
        && kept.KeepDays == current.KeepDays;

    /// <summary>
    /// Groups when there has been no grouping, when the inputs changed, or when the last one ended in a failure that
    /// can heal on its own: a list that couldn't be read now, a lookup that failed or a dataset that changed. The others
    /// heal only when an input changes (no dataset) or through a download or Clear list, which group again themselves;
    /// rerunning them on every show would read the whole feed each time. A grouping refused for the clock groups again
    /// when a status read finds the clock put right.
    /// </summary>
    private void GroupIfStale(IAbuseIpDbService abuse)
    {
        if (ReadGroupingInputs() is not { } current)
            return;

        bool stale = _groupingInputs is not { } kept
            || !SameInputs(kept, current)
            || (!IsGrouping && _groupingOutcome is AbuseNetworksFailure.StorageError or AbuseNetworksFailure.LookupFailed
                or AbuseNetworksFailure.DatasetChanged);
        if (stale)
            StartGrouping(abuse, current);
    }

    // After a download or Clear list: the list changed or may have, so whatever the inputs, the grouping is stale
    private void Regroup(IAbuseIpDbService abuse)
    {
        if (ReadGroupingInputs() is { } current)
            StartGrouping(abuse, current);
    }

    /// <summary>
    /// Supersedes the running grouping, if any, and starts one on these inputs. The rows stay until its answer, so a
    /// rerun doesn't empty the list and fill it again.
    /// </summary>
    private void StartGrouping(IAbuseIpDbService abuse, GroupingInputs inputs)
    {
        // Linking to a disposed lifetime's token throws
        if (_disposed)
            return;

        // The new run is the newest before the old one is cancelled, so the old one's end, even inline, sets nothing
        CancellationTokenSource? superseded = _groupingCts;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        int version = ++_groupingVersion;
        _groupingCts = cts;
        _groupingInputs = inputs;
        superseded?.Cancel();

        IsGrouping = true;
        GroupingProgress = 0;
        SummaryText = AppText.GroupingNetworks(null);

        // Rows that exist stay until the answer; with none, the last answer's line no longer applies while this one runs
        if (Rows.Count == 0)
            EmptyText = AppText.AbuseNetworksEmpty;
        PendingGrouping = GroupAsync(abuse, version, cts, inputs);
    }

    private async Task GroupAsync(IAbuseIpDbService abuse, int version, CancellationTokenSource cts, GroupingInputs inputs)
    {
        try
        {
            AbuseNetworksResult result;
            try
            {
                IProgress<GroupingProgress> progress = ProgressFactory(report => OnGroupingProgress(version, report));
                result = await abuse.FindOffendingNetworksAsync(progress, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Superseded or the page closed: the newer run, or nothing, owns the list now
                return;
            }
            catch (Exception)
            {
                result = new AbuseNetworksResult(AbuseNetworksFailure.LookupFailed, [], 0, [], 0, 0, 0,
                    inputs.IPv6Enabled, inputs.KeepDays, null, null, 0);
            }

            if (_disposed || version != _groupingVersion)
                return;

            ShowNetworks(result, inputs);
        }
        finally
        {
            if (ReferenceEquals(_groupingCts, cts))
            {
                _groupingCts = null;
                if (!_disposed)
                    IsGrouping = false;
            }

            cts.Dispose();
        }
    }

    private void OnGroupingProgress(int version, GroupingProgress report)
    {
        // A late report, from a superseded grouping or after the answer, must not overwrite what is shown
        if (_disposed || version != _groupingVersion || !IsGrouping)
            return;

        SummaryText = AppText.GroupingNetworks(report);
        if (report.Total > 0)
            GroupingProgress = Math.Max(GroupingProgress, Math.Clamp((double)report.Done / report.Total, 0, 1));
    }

    private void ShowNetworks(AbuseNetworksResult result, GroupingInputs inputs)
    {
        _groupingOutcome = result.Failure;
        Rows.Clear();
        if (result.Failure == AbuseNetworksFailure.None)
        {
            // The answer names what the service read, which can differ from what was current at the start
            _groupingInputs = new GroupingInputs(result.Dataset, result.IncludesIPv6, result.KeepDays);
            foreach (AbuseNetworkGroup group in result.Networks.Take(RowLimit))
                Rows.Add(OffendingNetworkRow.From(group));

            HasRows = Rows.Count > 0;
            SummaryText = AppText.AbuseNetworksSummary(result, Rows.Count, inputs.IPv6Enabled);

            // With no networks, either nothing was looked up or every address is in the no-ASN or not-in-IPinfo bucket
            EmptyText = HasRows ? AppText.AbuseNetworksEmpty
                : result.IPv4Addresses + result.IPv6Addresses == 0 ? AppText.AbuseNetworksNoAddresses
                : AppText.AbuseNetworksNoAsn;
            return;
        }

        HasRows = false;
        SummaryText = "";
        EmptyText = AppText.AbuseNetworksFailureLine(result.Failure);
    }

    /// <summary>
    /// Asks, holding the gate only while the dialog is open, so the service call that follows doesn't lock Lookup or
    /// the Settings page. Null when the dialog failed to show; the hold stops a second dialog opening meanwhile.
    /// Clear list and Empty feed test the gate before they ask, because a holder may show its own dialog later (Reset
    /// token holds it while the token is removed, then asks for a new one); the close's question doesn't, as the close
    /// must be asked.
    /// </summary>
    private async Task<bool?> ConfirmAsync(string title, string message, string confirmText)
    {
        using IDisposable hold = _gate.Hold();
        try
        {
            return await _ui.ConfirmAsync(title, message, confirmText);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
