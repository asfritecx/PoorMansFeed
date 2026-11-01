using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed;

/// <summary>
/// What every cloud feed page shares, including the guard that drops the answer of a check the operator has moved on
/// from. A page supplies how its pick is loaded and checked. It never reads or sets MainViewModel.IsBusy.
/// </summary>
public abstract class CloudFeedPageViewModel : ObservableObject, IDisposable
{
    private readonly ICloudFeedService _feeds;
    private readonly IIpInfoService _ipinfo;
    private readonly IUiServices _ui;
    private readonly IWorkGate _gate;
    private readonly IUnsavedSettings? _unsaved;
    private readonly string _defaultExportFolder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ModSecurityRulesPress _rulesPress;
    private readonly ModSecurityRulesFiles _rulesFiles = new();

    private CloudFeedCheck? _check;
    private string? _savedTicksText;
    private HashSet<string>? _writtenTicks;
    private int _checkVersion;
    private bool _disposed;
    private bool _isWorking;
    private IDisposable? _pageWork;
    private Func<Task>? _waitingActivation;
    private SourceUpdate? _pendingUpdate;
    private Dictionary<string, RowCarry>? _queuedCarry;
    private bool _queuedHeld;
    private bool _hasRows;
    private bool _hasActionText;
    private string _summaryText = AppText.CloudSummaryInitial;
    private string _emptyText;
    private string _actionText = "";

    protected CloudFeedPageViewModel(ICloudFeedService feeds, IIpInfoService ipinfo, IUiServices ui,
                                     string? defaultExportFolder, string initialEmptyText, IWorkGate gate,
                                     IUnsavedSettings? unsaved)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _unsaved = unsaved;
        _rulesPress = new ModSecurityRulesPress(ipinfo, ui, gate);
        _rulesFiles.ShowsChanged += RulesFilesShowsChanged;
        _defaultExportFolder = defaultExportFolder ?? Path.Combine(ipinfo.DataFolder, "exports");
        _emptyText = initialEmptyText ?? throw new ArgumentNullException(nameof(initialEmptyText));
        Rows = [];

        CheckCommand = new AsyncRelayCommand(() => CheckAsync(carry: null), () => CanWriteNow() && CanStartCheck);
        WriteFeedCommand = new AsyncRelayCommand(WriteAsync, CanWrite);
        OpenFeedFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => !_disposed);
        TickAllCommand = new RelayCommand(() => SetEveryTick(true), CanSetTicks);
        UntickAllCommand = new RelayCommand(() => SetEveryTick(false), CanSetTicks);
        ModSecurityRulesCommand = new AsyncRelayCommand(ShowModSecurityRulesAsync,
            () => !_disposed && !IsWorking && ShowsModSecurityRules);
        // A refresh's start and end reach the page only through the gate
        _gate.Changed += GateChanged;
        // Neither waits on the work gate or IsWorking: a copy changes nothing the page's work reads
        CopyNetworkCommand = new AsyncRelayCommand<CloudRangeRow>(CopyNetworkAsync, row => row is not null && !_disposed);
        CopyAsNumberCommand = new AsyncRelayCommand<CloudRangeRow>(CopyAsNumberAsync,
            row => row is { SingleAsnText: not null } && !_disposed);
    }

    public IAsyncRelayCommand CheckCommand { get; }

    public IAsyncRelayCommand WriteFeedCommand { get; }

    public IAsyncRelayCommand OpenFeedFolderCommand { get; }

    public IRelayCommand TickAllCommand { get; }

    public IRelayCommand UntickAllCommand { get; }

    public IAsyncRelayCommand ModSecurityRulesCommand { get; }

    public IAsyncRelayCommand<CloudRangeRow> CopyNetworkCommand { get; }

    public IAsyncRelayCommand<CloudRangeRow> CopyAsNumberCommand { get; }

    /// <summary>The last write succeeded and wrote ModSecurity copies.</summary>
    public bool ShowsModSecurityRules => _rulesFiles.Shows;

    public bool IsWorking
    {
        get => _isWorking;
        protected set
        {
            if (value == _isWorking)
                return;

            // Marked before the change is raised, so a listener already sees the page's call in the gate
            if (value && !_disposed)
                _pageWork ??= _gate.EnterPageWork();
            else if (!value)
                ReleasePageWork();
            SetProperty(ref _isWorking, value);
            RefreshCommands();
            if (!value)
                TakeWaitingWork();
        }
    }

    public ObservableCollection<CloudRangeRow> Rows { get; }

    public bool HasRows
    {
        get => _hasRows;
        private set
        {
            if (SetProperty(ref _hasRows, value))
            {
                WriteFeedCommand.NotifyCanExecuteChanged();
                TickAllCommand.NotifyCanExecuteChanged();
                UntickAllCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        protected set => SetProperty(ref _emptyText, value);
    }

    public string ActionText
    {
        get => _actionText;
        protected set
        {
            if (SetProperty(ref _actionText, value))
                HasActionText = value.Length > 0;
        }
    }

    public bool HasActionText
    {
        get => _hasActionText;
        private set => SetProperty(ref _hasActionText, value);
    }

    /// <summary>
    /// Ticked rows from a Check that no Write feed has written since, or a tick changed after the write that leaves a
    /// range ticked. A timed Refresh all waits while a page holds one. UI thread only.
    /// </summary>
    public bool HoldsUnwrittenCheck =>
        !_disposed && _check is not null && HasRows
        && (_writtenTicks is { } written ? TicksDifferFrom(written) : Rows.Any(row => row.IsIncluded));

    protected bool IsDisposed => _disposed;

    protected bool IsRefreshing => _gate.IsRefreshing;

    /// <summary>Cancelled when the page is disposed. Every await a page starts uses it.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    protected bool IsLifetimeCancelled => _lifetime.IsCancellationRequested;

    /// <summary>False while the page has nothing it could check (for example, no pick is available).</summary>
    protected abstract bool CanStartCheck { get; }

    /// <summary>
    /// The empty-list text once the page has what it needs to start a check, or null while it has not (a loading or
    /// failure text stays on show).
    /// </summary>
    protected abstract string? EmptyTextWhenReady { get; }

    protected abstract string NothingSelectedText { get; }

    /// <summary>
    /// Loads whatever is missing, then captures the current pick. Returns the check to run, or null when there is
    /// nothing to check. The pick is captured here, before Working starts, so a later change cannot alter it.
    /// </summary>
    protected abstract Task<Func<CancellationToken, Task<CheckResult>>?> PrepareCheckAsync();

    public virtual void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gate.Changed -= GateChanged;
        _waitingActivation = null;
        _pendingUpdate = null;
        _queuedCarry = null;
        ReleasePageWork();
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }

    private void ReleasePageWork()
    {
        _pageWork?.Dispose();
        _pageWork = null;
    }

    private void GateChanged(object? sender, EventArgs e)
    {
        RefreshCommands();
        if (_waitingActivation is { } activate && !_disposed && !IsRefreshing)
        {
            // Cleared before it runs: its own page work flips the gate and raises this again
            _waitingActivation = null;
            _ = activate();
            return;
        }

        RunQueuedRecheck();
    }

    private void TakeWaitingWork()
    {
        if (_disposed)
            return;

        if (_pendingUpdate is { } update)
        {
            _pendingUpdate = null;
            TakeSourceUpdate(update);
        }

        RunQueuedRecheck();
    }

    /// <summary>
    /// Takes a newer provider file on the UI thread. <paramref name="ignored"/>: never activated, or the file is held;
    /// <paramref name="apply"/> takes it and returns whether the pick the rows were checked for is still in it.
    /// </summary>
    protected void PostSourceUpdate(Func<bool> ignored, Func<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(ignored);
        ArgumentNullException.ThrowIfNull(apply);
        _ui.PostToUiThread(() => TakeSourceUpdate(new SourceUpdate(ignored, apply)));
    }

    // Kept while working, newest first: the page's own load has set the file by the time it is taken, so it is ignored
    private void TakeSourceUpdate(SourceUpdate update)
    {
        if (_disposed)
            return;

        if (IsWorking)
        {
            _pendingUpdate = update;
            return;
        }

        if (_waitingActivation is not null || update.Ignored())
            return;

        // A re-check already queued keeps waiting, and checks the newer file
        if (!HasRows)
        {
            Dictionary<string, RowCarry>? queued = _queuedCarry;
            update.Apply();
            _queuedCarry = queued;
            return;
        }

        // Taken before the file is: taking it can clear the rows, and with them the hold
        bool held = HoldsUnwrittenCheck;
        var carry = new Dictionary<string, RowCarry>(StringComparer.Ordinal);
        foreach (CloudRangeRow row in Rows)
            carry[row.Block.Text] = new RowCarry(row.IsIncluded, row.Status);

        if (!update.Apply())
        {
            ClearRows();
            // Kept where a saved-ticks line is kept, so the next Check clears it
            ActionText = AppText.CloudSourcePickGone;
            _savedTicksText = ActionText;
            return;
        }

        _queuedCarry = carry;
        _queuedHeld = held;
        if (IsRefreshing)
            EmptyText = AppText.CloudWaitsForRefresh;
        RunQueuedRecheck();
    }

    private void RunQueuedRecheck()
    {
        if (_queuedCarry is not { } carry || _disposed || IsWorking || IsRefreshing)
            return;

        _queuedCarry = null;
        _ = CheckAsync(carry, _queuedHeld);
    }

    /// <summary>Runs the first load, or keeps it until a running refresh ends: both save the provider's file.</summary>
    protected Task ActivateWhenFreeAsync(Func<Task> activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        if (!IsRefreshing)
            return activate();

        _waitingActivation = activate;
        EmptyText = AppText.CloudWaitsForRefresh;
        return Task.CompletedTask;
    }

    /// <summary>Forgets the checked ranges. Anything still checking is told to stand down by the version bump.</summary>
    protected void ClearRows()
    {
        _checkVersion++;
        _check = null;
        _writtenTicks = null;
        _queuedCarry = null;
        _rulesFiles.Clear();
        Rows.Clear();
        HasRows = false;
        SummaryText = AppText.CloudSummaryInitial;
        ActionText = "";
        if (EmptyTextWhenReady is { } ready)
            EmptyText = ready;

        RefreshCommands();
    }

    /// <summary>Empties the list and says why in its place, without touching the action line.</summary>
    protected void ShowNoRows(string emptyText)
    {
        _check = null;
        _writtenTicks = null;
        _rulesFiles.Clear();
        Rows.Clear();
        HasRows = false;
        SummaryText = AppText.CloudSummaryInitial;
        EmptyText = emptyText;
        RefreshCommands();
    }

    protected virtual void RefreshCommands()
    {
        CheckCommand.NotifyCanExecuteChanged();
        WriteFeedCommand.NotifyCanExecuteChanged();
        OpenFeedFolderCommand.NotifyCanExecuteChanged();
        TickAllCommand.NotifyCanExecuteChanged();
        UntickAllCommand.NotifyCanExecuteChanged();
        ModSecurityRulesCommand.NotifyCanExecuteChanged();
        CopyNetworkCommand.NotifyCanExecuteChanged();
        CopyAsNumberCommand.NotifyCanExecuteChanged();
    }

    private void RulesFilesShowsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShowsModSecurityRules));
        RefreshCommands();
    }

    // Working for the whole press: that turns off Check, Write feed, the pickers and the page's update or reload
    private async Task ShowModSecurityRulesAsync()
    {
        if (_disposed || IsWorking || _rulesFiles.Files is not { Count: > 0 } files)
            return;

        IsWorking = true;
        try
        {
            string? line = await _rulesPress.RunAsync(files, () => !_disposed, _lifetime.Token);
            if (line is not null && !_disposed)
                ActionText = line;
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    private Task CopyNetworkAsync(CloudRangeRow? row) =>
        row is not null && !_disposed ? CopyRowTextAsync(row.NetworkText, AppText.NetworkCopyFailed) : Task.CompletedTask;

    private Task CopyAsNumberAsync(CloudRangeRow? row) =>
        row is { SingleAsnText: { } asn } && !_disposed ? CopyRowTextAsync(asn, AppText.AsNumberCopyFailed) : Task.CompletedTask;

    // No success line, so a failed write's old-files line stays; a success clears only a copy's own failure line
    private async Task CopyRowTextAsync(string text, string failedLine)
    {
        try
        {
            await _ui.CopyTextAsync(text);
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = failedLine;
            return;
        }

        if (!_disposed && ActionText is AppText.NetworkCopyFailed or AppText.AsNumberCopyFailed)
            ActionText = "";
    }

    private bool CanSetTicks() => !_disposed && !IsWorking && HasRows;

    // Large ranges too: the large-range question at Write starts from the saved feed's choice, not from these ticks
    private void SetEveryTick(bool ticked)
    {
        if (!CanSetTicks())
            return;

        foreach (CloudRangeRow row in Rows)
            row.IsIncluded = ticked;
    }

    // Writes and source checks only: the ticks and the rules button read nothing a refresh writes
    private bool CanWriteNow() => !_disposed && !IsWorking && !_gate.IsRefreshing;

    private bool CanWrite() => CanWriteNow() && HasRows && _check is not null;

    private async Task CheckAsync(Dictionary<string, RowCarry>? carry, bool carryHeld = false)
    {
        if (!CanWriteNow())
            return;

        _rulesFiles.Clear();
        Func<CancellationToken, Task<CheckResult>>? start = await PrepareCheckAsync();
        if (_disposed || start is null || _gate.IsRefreshing)
            return;

        int version = _checkVersion;
        IsWorking = true;
        try
        {
            CheckResult result = await start(_lifetime.Token);
            if (_disposed || version != _checkVersion)
                return;

            ShowCheck(result, carry, carryHeld);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && version == _checkVersion)
                ShowNoRows(AppText.CloudCheckFailed);
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    private async Task WriteAsync()
    {
        if (!CanWriteNow() || _check is null)
            return;

        int rulesMark = _rulesFiles.Clear();

        if (WriteRefusal() is { } refusal)
        {
            ActionText = refusal;
            return;
        }

        var ticked = new List<CidrBlock>();
        foreach (CloudRangeRow row in Rows)
        {
            if (row.IsIncluded)
                ticked.Add(row.Block);
        }

        CloudFeedCheck check = _check;
        CloudFeedPlan plan = _feeds.PlanWrite(check, ticked);
        // Working from the press to the end, the large-range question included
        IsWorking = true;
        try
        {
            if (!_ipinfo.Settings.TicksAnyFeedType())
            {
                await AskToConfigureFeedsAsync();
                return;
            }

            IReadOnlyCollection<CidrBlock> large = [];
            if (plan.LargeRanges.Count > 0)
            {
                var choices = plan.LargeRanges.Select(LargeRangeChoice.From).ToList();
                var prompt = new LargeRangePrompt(
                    AppText.CloudLargeRangesTitle,
                    AppText.CloudLargeRangesMessage(choices.Count, _ipinfo.Settings.IPv4MinPrefix, _ipinfo.Settings.IPv6MinPrefix),
                    choices);
                IReadOnlyList<string>? chosen = await ChooseLargeRangesAsync(prompt);
                if (chosen is null || _disposed || _check is null)
                    return;

                if (WriteRefusal() is { } lateRefusal)
                {
                    ActionText = lateRefusal;
                    return;
                }

                var chosenText = new HashSet<string>(chosen, StringComparer.Ordinal);
                var includedLarge = new List<CidrBlock>();
                foreach (CheckedRange range in plan.Included)
                {
                    if (chosenText.Contains(range.Prefix.Block.Text))
                        includedLarge.Add(range.Prefix.Block);
                }

                large = includedLarge;
            }

            // Read once, just before the write, so the note says what the write read; a save during it doesn't change it
            bool unsaved = _unsaved?.HasUnsavedChanges == true;
            CloudFeedWriteResult written = await _feeds.WriteAsync(plan, large, _lifetime.Token);
            if (!_disposed)
            {
                if (written.Failure == CloudFeedWriteFailure.None)
                {
                    _rulesFiles.Set(rulesMark, written.ModSecurityFiles);
                    if (ReferenceEquals(_check, check))
                        _writtenTicks = TickedSet(ticked);
                }
                ActionText = WriteMessage(written, unsaved);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = AppText.CloudWriteFailed;
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    // The ranges' statuses were found in the dataset of the check, so a newer dataset needs a new check
    private string? WriteRefusal() =>
        _check is null ? null
        : !_check.MatchesWriteSettings(_ipinfo.Settings) ? AppText.CloudSettingsChanged
        : !Equals(_ipinfo.Dataset, _check.Dataset) ? AppText.CloudDatasetChanged
        : null;

    // Not asked while something else holds the gate, because that holder may show its own dialog later (Reset token
    // holds it while the token is removed, then asks for a new one). Write feed itself doesn't wait on the gate
    private async Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt)
    {
        if (_gate.IsHeld)
        {
            ActionText = AppText.CloudLargeRangesBusy;
            return null;
        }

        using IDisposable hold = _gate.Hold();
        try
        {
            return await _ui.ChooseLargeRangesAsync(prompt);
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = AppText.Generic;
            return null;
        }
    }

    // Not shown while something else holds the gate, for the large-range question's reason; Close leaves the line
    // as it is
    private async Task AskToConfigureFeedsAsync()
    {
        if (_gate.IsHeld)
        {
            ActionText = AppText.ConfigureFeedsBusy;
            return;
        }

        ConfigureFeedsChoice choice;
        using (IDisposable hold = _gate.Hold())
        {
            try
            {
                choice = await _ui.AskToConfigureFeedsAsync();
            }
            catch (Exception)
            {
                if (!_disposed)
                    ActionText = AppText.Generic;
                return;
            }
        }

        if (choice == ConfigureFeedsChoice.OpenSettings && !_disposed)
            _ui.ShowSettingsPage();
    }

    private async Task OpenFolderAsync()
    {
        if (_disposed)
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

    private void ShowCheck(CheckResult result, Dictionary<string, RowCarry>? carry, bool carryHeld)
    {
        if (result.Failure != CheckFailure.None || result.Check is null)
        {
            ShowNoRows(result.Failure switch
            {
                CheckFailure.NoDataset => AppText.CloudNoDataset,
                CheckFailure.NothingSelected => NothingSelectedText,
                CheckFailure.PickGone => AppText.CloudPickGone,
                _ => AppText.CloudCheckFailed,
            });
            return;
        }

        // A check again after a refresh saved the provider's file is the page's own: written rows stay written
        bool keepsWritten = carry is not null && !carryHeld;
        _check = result.Check;
        _writtenTicks = null;
        Rows.Clear();
        foreach (CheckedRange range in result.Check.Ranges)
            Rows.Add(CloudRangeRow.From(range));

        HasRows = Rows.Count > 0;
        // A line an earlier Check left goes, so it never speaks for saved ticks this Check didn't apply
        string? applied = SavedTicksLine(result.Check);
        if (applied is not null)
            ActionText = applied;
        else if (_savedTicksText is not null && ActionText == _savedTicksText)
            ActionText = "";
        _savedTicksText = applied;
        if (carry is not null && HasRows)
        {
            int kept = CarryTicks(carry);
            // No saved-ticks clause here: the one-line action box can't hold both, and the rows show the ticks
            ActionText = kept > 0 ? AppText.CloudSourceRechecked + " " + AppText.CloudSourceTicksKept(kept)
                : AppText.CloudSourceRechecked;
            _savedTicksText = ActionText;
        }

        if (keepsWritten)
            _writtenTicks = TickedSet(Rows.Where(row => row.IsIncluded).Select(row => row.Block));

        SummaryText = AppText.CloudSummary(
            result.Check.Ranges.Count,
            result.Check.Count(RangeConfirmation.Confirmed),
            result.Check.Count(RangeConfirmation.Mixed),
            result.Check.Count(RangeConfirmation.NotInDatabase));
        EmptyText = HasRows ? EmptyTextWhenReady ?? EmptyText : NothingSelectedText;
        RefreshCommands();
    }

    // Found as the write finds the saved feed it updates (file stem ignoring case, and kind), so a retired one counts
    // too; entries that match no row are ignored, and nothing is said unless a tick moved from its default
    private string? SavedTicksLine(CloudFeedCheck check)
    {
        SavedFeedsRead read = _ipinfo.Records.ReadSavedFeeds();
        if (!read.Available)
            return null;

        string key = check.Description.FileStem;
        SavedFeedPick? pick = read.Feeds.FirstOrDefault(feed => feed.Kind == check.Pick.Kind
            && string.Equals(feed.Key, key, StringComparison.OrdinalIgnoreCase))?.Pick;
        if (pick is null)
            return null;

        var picked = new HashSet<string>(RefreshPick.Apply(check, pick).Ticked.Select(block => block.Text),
                                         StringComparer.Ordinal);
        int ticked = 0, unticked = 0;
        foreach (CloudRangeRow row in Rows)
        {
            row.IsIncluded = picked.Contains(row.Block.Text);

            if (row.IsIncluded != row.IsConfirmed)
            {
                if (row.IsIncluded)
                    ticked++;
                else
                    unticked++;
            }
        }

        return ticked + unticked > 0 ? AppText.CloudSavedTicksApplied(ticked, unticked) : null;
    }

    private static HashSet<string> TickedSet(IEnumerable<CidrBlock> ticked) =>
        new(ticked.Select(block => block.Text), StringComparer.Ordinal);

    // Unticking everything leaves nothing a write would add, so it holds nothing
    private bool TicksDifferFrom(HashSet<string> written)
    {
        bool anyTicked = false;
        bool differs = false;
        foreach (CloudRangeRow row in Rows)
        {
            anyTicked |= row.IsIncluded;
            differs |= row.IsIncluded != written.Contains(row.Block.Text);
        }

        return anyTicked && differs;
    }

    // Only where the range's status is unchanged: a changed status is the reason the user's tick may no longer hold
    private int CarryTicks(Dictionary<string, RowCarry> carry)
    {
        int changed = 0;
        foreach (CloudRangeRow row in Rows)
        {
            if (carry.TryGetValue(row.Block.Text, out RowCarry was) && was.Status == row.Status
                && was.Included != row.IsIncluded)
            {
                row.IsIncluded = was.Included;
                changed++;
            }
        }

        return changed;
    }

    private static string WriteMessage(CloudFeedWriteResult result, bool unsaved)
    {
        switch (result.Failure)
        {
            case CloudFeedWriteFailure.FolderUnavailable:
                return AppText.ExportFolderUnavailable;
            case CloudFeedWriteFailure.WriteFailed:
                return AppText.CloudWriteFailedLine(result.FailedFile, result.Files, result.OldFilesLeft, unsaved, result.Record);
            case CloudFeedWriteFailure.SettingsChanged:
                return AppText.CloudSettingsChanged;
            case CloudFeedWriteFailure.ExceptionsUnreadable:
                return AppText.ExceptionsUnreadable;
            case CloudFeedWriteFailure.NoFeedType:
                return AppText.NothingWrittenNoFeedType + (unsaved ? AppText.UnsavedSettingsNote : "");
            case CloudFeedWriteFailure.NothingToWrite:
                // The exceptions decided this line, so it names the unsaved changes as a written feed's line does
                return AppText.CloudNothingWritten(WhyNothingWritten(result))
                    + (unsaved && result.ExceptionRemovals > 0 ? AppText.UnsavedSettingsNote : "");
        }

        if (result.RangesWritten == 0 && result.EmptiedFiles is { Count: > 0 })
        {
            return AppText.FeedRecordLead(result.Record) + AppText.Emptied(result.Files.Count, WhyNothingWritten(result))
                + (unsaved ? AppText.UnsavedSettingsNote : "");
        }

        string text = AppText.CloudFeedWritten(result.RangesWritten, result.Files.Count);
        if (result.LargeRangesLeftOut > 0)
            text += AppText.LargeRangesLeftOut(result.LargeRangesLeftOut);

        if (result.WholeRangesLeftOut > 0)
            text += AppText.CloudWholeRangesLeftOut(result.WholeRangesLeftOut);

        if (result.UnconfirmedWritten > 0)
            text += AppText.CloudUnconfirmed(result.UnconfirmedWritten);

        return AppText.FeedRecordLead(result.Record) + text
            + AppText.ExceptionsApplied(result.ExceptionCuts, result.ExceptionRemovals, "range", "ranges")
            + AppText.FilesEmptied(result.EmptiedFiles) + (unsaved ? AppText.UnsavedSettingsNote : "");
    }

    private static string WhyNothingWritten(CloudFeedWriteResult result) =>
        (result.LargeRangesLeftOut > 0, result.WholeRangesLeftOut > 0, result.ExceptionRemovals > 0) switch
        {
            (true, true, true) => AppText.CloudWhyAllLeftOutWholeOrExcepted,
            (true, true, false) => AppText.CloudWhyAllLeftOutOrWhole,
            (true, false, true) => AppText.CloudWhyAllLeftOutOrExcepted,
            (true, false, false) => AppText.CloudWhyAllLeftOut,
            (false, true, true) => AppText.CloudWhyAllWholeOrExcepted,
            (false, true, false) => AppText.CloudWhyAllWhole,
            (false, false, true) => AppText.CloudWhyAllExcepted,
            _ => AppText.CloudWhyNothingTicked,
        };

    private sealed record SourceUpdate(Func<bool> Ignored, Func<bool> Apply);

    private readonly record struct RowCarry(bool Included, RangeConfirmation Status);
}
