using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed;

/// <summary>A feed choice of the history's filter; a null key is every feed.</summary>
public sealed record HistoryFeedChoice(string? Key, string Text);

/// <summary>A period choice of the history's filter; a null span is any time.</summary>
public sealed record HistoryPeriodChoice(TimeSpan? Span, string Text);

/// <summary>
/// The Saved feeds page. Its runs keep their own IsWorking, not the work gate, so an update never disables it;
/// only its confirmations hold the gate.
/// </summary>
public sealed class SavedFeedsViewModel : ObservableObject, IDisposable, ICloseParticipant
{
    /// <summary>The most changes shown; the summary names the rest.</summary>
    public const int HistoryRowLimit = 300;

    private static readonly HistoryFeedChoice AllFeeds = new(null, AppText.HistoryAllFeeds);

    private readonly ISavedFeedsService? _feeds;
    private readonly IUiServices _ui;
    private readonly IWorkGate _gate;
    private readonly TimeProvider _time;
    private readonly RefreshAllRunner? _refreshAll;
    private readonly IRefreshSchedule? _schedule;
    private readonly CancellationTokenSource _lifetime = new();

    // A history row names a saved feed by its saved name; a key no longer saved shows itself
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    // The last AS names read, so a read shows them at once and the dataset's answer only brings changes
    private IReadOnlyDictionary<uint, string> _asNames = new Dictionary<uint, string>();

    private int _historyVersion;
    private int _feedsVersion;
    private bool _disposed;
    private bool _isWorking;
    private IDisposable? _pageWork;
    private Task? _activation;
    private bool _activateWhenStarted;
    private bool _refillingChoices;
    private bool _filterChangedDuringRun;
    private HistoryMatch? _match;

    // The close: the service call out right now, the window's holds on the runs, and whether the stop has begun
    private Task? _call;
    private bool _callOut;
    private int _closeHolds;
    private bool _closing;

    private string _searchText = "";
    private HistoryFeedChoice? _selectedFeed = AllFeeds;
    private HistoryPeriodChoice _selectedPeriod;
    private bool _hasFeeds;
    private bool _hasHistoryRows;
    private string _feedsSummaryText = "";
    private string _feedsEmptyText;
    private string _historySummaryText = "";
    private string _historyEmptyText = AppText.HistoryEmpty(HistoryState.Missing, filtered: false);
    private string _searchProblemText = "";
    private string _actionText = "";
    private bool _isActionWarning;
    private bool _refreshWasRunning;
    private (string Text, bool Warning) _lineBeforeRun;
    private RefreshRunResult? _refreshShown;

    // Each feed as the first read after a run saw it; a result shows only while its feed is still that
    private readonly Dictionary<string, (DateTimeOffset LastWriteUtc, bool Retired)> _refreshNotes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshNotePending;

    internal SavedFeedsViewModel(ISavedFeedsService? feeds, IUiServices ui, IWorkGate gate, TimeProvider time,
                                 RefreshAllRunner? refreshAll = null, IRefreshSchedule? schedule = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _feeds = feeds;
        _refreshAll = refreshAll;
        _schedule = schedule;
        _refreshShown = refreshAll?.LastRun;

        PeriodChoices =
        [
            new HistoryPeriodChoice(null, AppText.HistoryAnyTime),
            new HistoryPeriodChoice(TimeSpan.FromHours(24), AppText.HistoryLast24Hours),
            new HistoryPeriodChoice(TimeSpan.FromDays(7), AppText.HistoryLast7Days),
            new HistoryPeriodChoice(TimeSpan.FromDays(30), AppText.HistoryLast30Days),
        ];
        _selectedPeriod = PeriodChoices[0];
        FeedChoices.Add(AllFeeds);
        _feedsEmptyText = feeds is null ? AppText.SavedFeedsUnavailable : AppText.SavedFeedsEmpty;
        if (feeds is null)
            _historyEmptyText = AppText.HistoryUnavailable;

        SearchCommand = new AsyncRelayCommand(SearchAsync, CanRun);
        RetireCommand = new AsyncRelayCommand<SavedFeedRow>(RetireAsync, row => row is not null && CanWriteNow());
        RemoveCommand = new AsyncRelayCommand<SavedFeedRow>(RemoveAsync, row => row is not null && CanWriteNow());
        ClearHistoryCommand = new AsyncRelayCommand(ClearHistoryAsync, CanWriteNow);
        RefreshAllCommand = new RelayCommand(PressRefreshAll, CanPressRefreshAll);

        // IsStarted flips once, when start-up opens the service; until then nothing the page shows is the saved state
        _gate.Changed += GateChanged;
        if (_refreshAll is not null)
            _refreshAll.Changed += RefreshAllChanged;
        if (_schedule is not null)
            _schedule.Changed += ScheduleChanged;
    }

    public bool IsAvailable => _feeds is not null;

    /// <summary>A Retire, Remove or Clear history is running, from the press to the end.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set
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
        }
    }

    /// <summary>Refilled, never replaced, so the window binds each once.</summary>
    public ObservableCollection<SavedFeedRow> Feeds { get; } = [];

    /// <summary>At most <see cref="HistoryRowLimit"/>, newest change first.</summary>
    public ObservableCollection<HistoryChangeRow> HistoryRows { get; } = [];

    /// <summary>All feeds, then every feed key that has history, in the history's order.</summary>
    public ObservableCollection<HistoryFeedChoice> FeedChoices { get; } = [];

    public IReadOnlyList<HistoryPeriodChoice> PeriodChoices { get; }

    /// <summary>Read only by Search, so typing sends nothing.</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? "");
    }

    /// <summary>
    /// A null from the view (its context changed) keeps the last choice. A refill's own null is let through, so the
    /// choice it sets after it reaches the view even when it equals the old one.
    /// </summary>
    public HistoryFeedChoice? SelectedFeed
    {
        get => _selectedFeed;
        set
        {
            if (value is null && !_refillingChoices)
                return;

            if (SetProperty(ref _selectedFeed, value) && !_refillingChoices)
                QueryForFilter();
        }
    }

    public HistoryPeriodChoice SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            if (SetProperty(ref _selectedPeriod, value ?? PeriodChoices[0]))
                QueryForFilter();
        }
    }

    public bool HasFeeds
    {
        get => _hasFeeds;
        private set => SetProperty(ref _hasFeeds, value);
    }

    public bool HasHistoryRows
    {
        get => _hasHistoryRows;
        private set => SetProperty(ref _hasHistoryRows, value);
    }

    public string FeedsSummaryText
    {
        get => _feedsSummaryText;
        private set => SetProperty(ref _feedsSummaryText, value);
    }

    public string FeedsEmptyText
    {
        get => _feedsEmptyText;
        private set => SetProperty(ref _feedsEmptyText, value);
    }

    public string HistorySummaryText
    {
        get => _historySummaryText;
        private set => SetProperty(ref _historySummaryText, value);
    }

    public string HistoryEmptyText
    {
        get => _historyEmptyText;
        private set => SetProperty(ref _historyEmptyText, value);
    }

    public string SearchProblemText
    {
        get => _searchProblemText;
        private set => SetProperty(ref _searchProblemText, value);
    }

    public string RefreshAllText => RefreshRunning ? AppText.StopRefreshButton : AppText.RefreshAllButton;

    public string RefreshAllTip => RefreshRunning ? AppText.StopRefreshTip : AppText.RefreshAllTip;

    private bool RefreshRunning => _refreshAll is { IsRunning: true };

    /// <summary>The one line for Retire, Remove, Clear history and Refresh all.</summary>
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

    public bool IsActionWarning
    {
        get => _isActionWarning;
        private set => SetProperty(ref _isActionWarning, value);
    }

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand<SavedFeedRow> RetireCommand { get; }
    public IAsyncRelayCommand<SavedFeedRow> RemoveCommand { get; }
    public IAsyncRelayCommand ClearHistoryCommand { get; }

    /// <summary>Starts Refresh all, or while a run is out asks it to stop.</summary>
    public IRelayCommand RefreshAllCommand { get; }

    /// <summary>The read of the rows after a Refresh all ended, for tests to await.</summary>
    internal Task? PendingRefreshRead { get; private set; }

    /// <summary>The newest history query started by a filter change, for tests to await.</summary>
    internal Task? PendingQuery { get; private set; }

    /// <summary>The newest read of the AS names, for tests to await.</summary>
    internal Task? PendingAsNames { get; private set; }

    /// <summary>A Retire, Remove or Clear history is with the service; not while its confirmation is open.</summary>
    public bool AsksBeforeClose => _callOut && !_disposed;

    bool ICloseParticipant.HoldsClose => AsksBeforeClose;

    public async Task<bool> AskBeforeCloseAsync()
    {
        if (!AsksBeforeClose)
            return true;

        return await ConfirmAsync(AppText.CloseSavedFeedsWorkTitle, AppText.CloseSavedFeedsWorkBody,
            AppText.CloseFeedWriteButton) == true;
    }

    /// <summary>
    /// A run whose call ends starts nothing more. Cancels nothing, because a retire is never stopped mid-file; returns
    /// once the call with the service has ended. Never throws.
    /// </summary>
    public async Task StopForCloseAsync()
    {
        _closing = true;
        if (!AsksBeforeClose || _call is not { } call)
            return;

        try
        {
            await call;
        }
        catch (Exception)
        {
            // The call's outcome no longer matters: the window is closing
        }
    }

    /// <summary>Turns the run commands off until disposed. Holds nest; a second Dispose does nothing.</summary>
    public IDisposable HoldRunsForClose()
    {
        _closeHolds++;
        RefreshCommands();
        return new RunsHold(this);
    }

    /// <summary>A show while an earlier show's read is out joins that read.</summary>
    public Task ActivateAsync()
    {
        if (_feeds is not { } feeds || _disposed)
            return Task.CompletedTask;

        // Before start the service holds nothing saved yet, so the ask waits for the gate to start instead
        if (!_gate.IsStarted)
        {
            _activateWhenStarted = true;
            return Task.CompletedTask;
        }

        // A run reads again itself when it ends
        if (IsWorking)
            return Task.CompletedTask;

        if (_activation is { IsCompleted: false } running)
            return running;

        _activation = ReadAllAsync(feeds);
        return _activation;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _gate.Changed -= GateChanged;
        if (_refreshAll is not null)
            _refreshAll.Changed -= RefreshAllChanged;
        if (_schedule is not null)
            _schedule.Changed -= ScheduleChanged;
        ReleasePageWork();
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }

    // Never on the gate's CanStart: an update or another page's dialog must not disable the page's own runs
    private bool CanRun() => IsAvailable && _gate.IsStarted && !IsWorking && !_disposed && _closeHolds == 0;

    // Retire, Remove and Clear history only: Search and the history filter only read
    private bool CanWriteNow() => CanRun() && !_gate.IsRefreshing;

    // A gate hold, an update or a page call raises nothing that would enable the button again, so a press gets a line
    private bool CanPressRefreshAll() =>
        _refreshAll is { } runner && IsAvailable && _gate.IsStarted && !_disposed && _closeHolds == 0
        && !(runner.IsRunning && runner.CancelRequested);

    private void PressRefreshAll()
    {
        if (_refreshAll is not { } runner || !CanPressRefreshAll())
            return;

        if (runner.IsRunning)
        {
            runner.Cancel();
            return;
        }

        if (runner.TryStart() is RefreshStartRefusal.GateBusy or RefreshStartRefusal.PageWorking)
            SetAction(AppText.RefreshAllBusy, warning: true);
    }

    private void RefreshAllChanged(object? sender, EventArgs e)
    {
        if (_refreshAll is not { } runner || _disposed)
            return;

        OnPropertyChanged(nameof(RefreshAllText));
        OnPropertyChanged(nameof(RefreshAllTip));
        RefreshCommands();
        if (runner.IsRunning)
        {
            if (!_refreshWasRunning)
            {
                _refreshNotes.Clear();
                _lineBeforeRun = (ActionText, IsActionWarning);
            }
            _refreshWasRunning = true;
            SetAction(AppText.RefreshAllProgressLine(runner.CancelRequested, runner.Progress), warning: false);
            return;
        }

        if (!_refreshWasRunning)
            return;

        // A run that threw leaves the last run's result in place, so only a new result is a summary
        _refreshWasRunning = false;
        if (runner.LastRun is not { } run || ReferenceEquals(run, _refreshShown))
        {
            SetAction(AppText.Generic, warning: true);
            return;
        }

        _refreshShown = run;
        _refreshNotePending = true;
        if (runner.LastRunTrigger != RefreshTrigger.Timer)
            SetAction(AppText.RefreshAllSummary(run), AppText.RefreshAllSummaryIsWarning(run));
        // Nobody pressed it, so a timed run that found nothing to refresh puts back the line it found
        else if (!run.SavedFeedsAvailable || (run.Feeds.Count == 0 && !run.Cancelled))
            SetAction(_lineBeforeRun.Text, _lineBeforeRun.Warning);
        else
            SetAction(AppText.RefreshAllOnSchedule(AppText.RefreshAllSummary(run)), AppText.RefreshAllSummaryIsWarning(run));
        if (_feeds is { } feeds && !IsWorking)
            PendingRefreshRead = ReadAllAsync(feeds);
    }

    private void ReleasePageWork()
    {
        _pageWork?.Dispose();
        _pageWork = null;
    }

    // A change while a run is out waits for the run to end, so a query the run sends after it can't hide it
    private void QueryForFilter()
    {
        if (_feeds is not { } feeds || _disposed || !_gate.IsStarted)
            return;

        if (IsWorking)
            _filterChangedDuringRun = true;
        else
            PendingQuery = QueryHistoryAsync(feeds);
    }

    // The AS names come from the dataset, whose queries take turns, so neither the rows, the history nor a press waits
    // for them
    private async Task ReadAllAsync(ISavedFeedsService feeds)
    {
        SavedFeedsRead read = ReadFeeds(feeds);
        PendingAsNames = ShowAsNamesAsync(feeds, read, _feedsVersion);
        await QueryHistoryAsync(feeds);
    }

    private SavedFeedsRead ReadFeeds(ISavedFeedsService feeds)
    {
        SavedFeedsRead read;
        try
        {
            read = feeds.ReadSavedFeeds();
        }
        catch (Exception)
        {
            read = new SavedFeedsRead(false, []);
        }

        _feedsVersion++;
        Feeds.Clear();
        _names.Clear();
        if (read.Available)
        {
            bool noting = _refreshNotePending;
            _refreshNotePending = false;
            foreach (SavedFeed feed in read.Feeds)
            {
                FeedRefreshResult? result = null;
                if (_refreshAll?.LastResults.TryGetValue(feed.Key, out result) == true)
                {
                    if (noting)
                        _refreshNotes[feed.Key] = (feed.LastWriteUtc, feed.Retired);
                    else if (!_refreshNotes.TryGetValue(feed.Key, out var noted) || noted != (feed.LastWriteUtc, feed.Retired))
                        result = null;
                }
                SavedFeedRow row = SavedFeedRow.From(feed, result, AsNameOf(feed, _asNames));
                Feeds.Add(row);
                _names.TryAdd(row.Key, row.NameText);
            }
        }

        HasFeeds = Feeds.Count > 0;
        ShowFeedsSummary();
        FeedsEmptyText = read.Available ? AppText.SavedFeedsEmpty : AppText.SavedFeedsReadFailed;
        return read;
    }

    // A read of the rows that a later one overtook while the names were out leaves the rows to the later one
    private async Task ShowAsNamesAsync(ISavedFeedsService feeds, SavedFeedsRead read, int version)
    {
        uint[] asns = read.Available ? read.Feeds.Select(AsNumberOf).OfType<uint>().Distinct().ToArray() : [];
        if (asns.Length == 0)
            return;

        IReadOnlyDictionary<uint, string> asNames;
        try
        {
            asNames = await feeds.FindAsNamesAsync(asns, _lifetime.Token);
        }
        catch (Exception)
        {
            asNames = new Dictionary<uint, string>();
        }

        if (_disposed || version != _feedsVersion)
            return;

        _asNames = asNames;
        for (int i = 0; i < Feeds.Count; i++)
        {
            SavedFeedRow row = Feeds[i];
            string? asName = AsNameOf(row.Feed, asNames);
            if (row.AsNameText != (asName is null ? "" : AppText.SavedFeedAsName(asName)))
                Feeds[i] = row.WithAsName(asName);
        }
    }

    private static string? AsNameOf(SavedFeed feed, IReadOnlyDictionary<uint, string> asNames) =>
        AsNumberOf(feed) is { } asn && asNames.TryGetValue(asn, out string? asName) ? asName : null;

    private static uint? AsNumberOf(SavedFeed feed) =>
        feed.Kind == FeedKind.Asn
        && uint.TryParse(feed.Pick.Asn, NumberStyles.None, CultureInfo.InvariantCulture, out uint asn)
            ? asn
            : null;

    private void ScheduleChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
            ShowFeedsSummary();
    }

    private void ShowFeedsSummary() =>
        FeedsSummaryText = HasFeeds
            ? AppText.SavedFeedsSummary(Feeds.Count, _time.GetUtcNow(), _schedule?.NextDueUtc, _schedule?.IsWaiting == true)
            : "";

    // The period counts back from when the query runs, so a page left open never shows a stale window
    private async Task QueryHistoryAsync(ISavedFeedsService feeds)
    {
        if (_disposed)
            return;

        int version = ++_historyVersion;
        _filterChangedDuringRun = false;
        DateTimeOffset? since = SelectedPeriod.Span is { } span ? _time.GetUtcNow() - span : null;
        var query = new HistoryQuery(SelectedFeed?.Key, since, _match);
        bool filtered = query.FeedKey is not null || since is not null || _match is not null;
        try
        {
            HistoryRead read = await feeds.SearchHistoryAsync(query, HistoryRowLimit, _lifetime.Token);
            if (_disposed || version != _historyVersion)
                return;

            ShowHistory(read, filtered);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && version == _historyVersion)
            {
                HistoryRows.Clear();
                HasHistoryRows = false;
                HistorySummaryText = "";
                HistoryEmptyText = AppText.HistoryReadFailed;
            }
        }
    }

    // A fallback's query is sent only once this answer is shown, so an answer that comes at once isn't overwritten
    private void ShowHistory(HistoryRead read, bool filtered)
    {
        bool fellBack = !FillChoices(read.FeedKeys);
        HistoryRows.Clear();
        foreach (HistoryRow row in read.Rows.Take(HistoryRowLimit))
            HistoryRows.Add(HistoryChangeRow.From(row, FeedName(row.FeedKey)));

        HasHistoryRows = HistoryRows.Count > 0;
        HistorySummaryText = AppText.HistorySummary(HistoryRows.Count, read.Total, read.TotalCapped, read.CapTrimmedBeforeUtc);
        HistoryEmptyText = AppText.HistoryEmpty(read.State, filtered);
        if (fellBack)
            QueryForFilter();
    }

    // False when the chosen key is gone and the choice fell back to All feeds, whose rows the caller must then ask for
    private bool FillChoices(IReadOnlyList<string> keys)
    {
        string? selected = SelectedFeed?.Key;
        HistoryFeedChoice? kept;
        _refillingChoices = true;
        try
        {
            FeedChoices.Clear();
            FeedChoices.Add(AllFeeds);
            foreach (string key in keys)
                FeedChoices.Add(new HistoryFeedChoice(key, FeedName(key)));

            kept = FeedChoices.FirstOrDefault(choice => string.Equals(choice.Key, selected, StringComparison.Ordinal));
            SelectedFeed = kept ?? AllFeeds;
        }
        finally
        {
            _refillingChoices = false;
        }

        return kept is not null;
    }

    private string FeedName(string key) => _names.TryGetValue(key, out string? name) ? name : key;

    private async Task SearchAsync()
    {
        if (_feeds is not { } feeds || !CanRun())
            return;

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _match = null;
        }
        else if (HistoryMatch.TryParse(SearchText, out HistoryMatch? match))
        {
            _match = match;
        }
        else
        {
            SearchProblemText = AppText.HistorySearchInvalid;
            return;
        }

        SearchProblemText = "";
        await QueryHistoryAsync(feeds);
    }

    private async Task RetireAsync(SavedFeedRow? row)
    {
        if (_feeds is not { } feeds || row is null || !CanWriteNow())
            return;

        string name = row.NameText;
        await PressAsync(AppText.RetireSavedFeedBusy,
            () => ConfirmAsync(AppText.RetireSavedFeedTitle(name), AppText.RetireSavedFeedBody(name, row.Kind),
                AppText.RetireSavedFeedButton),
            async () =>
            {
                SetAction(AppText.RetiringSavedFeed(name), warning: false);
                RetireResult result;
                try
                {
                    // The row's own kind and key, always together: the service refuses a pair it hasn't saved
                    result = await CallServiceAsync(ct => feeds.RetireAsync(row.Kind, row.Key, ct));
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception)
                {
                    result = new RetireResult(RetireFailure.WriteFailed, "", [], null, [], null);
                }

                return (AppText.SavedFeedRetireLine(result, name), result.Failure != RetireFailure.None);
            });
    }

    private async Task RemoveAsync(SavedFeedRow? row)
    {
        if (_feeds is not { } feeds || row is null || !CanWriteNow())
            return;

        string name = row.NameText;
        await PressAsync(AppText.RemoveSavedFeedBusy,
            () => ConfirmAsync(AppText.RemoveSavedFeedTitle(name), AppText.RemoveSavedFeedBody(row.Kind),
                AppText.RemoveSavedFeedButton),
            async () =>
            {
                SetAction("", warning: false);
                SavedFeedsFailure failure;
                try
                {
                    failure = await CallServiceAsync(ct => feeds.RemoveAsync(row.Key, ct));
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception)
                {
                    failure = SavedFeedsFailure.StorageError;
                }

                return (AppText.SavedFeedRemoveLine(failure, name), failure != SavedFeedsFailure.None);
            });
    }

    private async Task ClearHistoryAsync()
    {
        if (_feeds is not { } feeds || !CanWriteNow())
            return;

        await PressAsync(AppText.ClearHistoryBusy,
            () => ConfirmAsync(AppText.ClearHistoryTitle, AppText.ClearHistoryBody, AppText.ClearHistoryButton),
            async () =>
            {
                SetAction("", warning: false);
                HistoryState state;
                try
                {
                    state = await CallServiceAsync(feeds.ClearHistoryAsync);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception)
                {
                    state = HistoryState.StorageError;
                }

                return (AppText.HistoryClearLine(state), state != HistoryState.Missing);
            });
    }

    /// <summary>
    /// Working from the press to the end, the confirmation included. The call gives its result line, or null when the
    /// page's lifetime cancelled it; the page then reads the feeds and the current query again.
    /// </summary>
    private async Task PressAsync(string busyLine, Func<Task<bool?>> confirm,
                                  Func<Task<(string Line, bool Warning)?>> call)
    {
        if (_feeds is not { } feeds)
            return;

        IsWorking = true;
        try
        {
            if (_gate.IsHeld)
            {
                SetAction(busyLine, warning: true);
                return;
            }

            bool? confirmed = await confirm();
            if (_disposed)
                return;
            if (confirmed is null)
            {
                SetAction(AppText.Generic, warning: true);
                return;
            }

            if (confirmed == false)
                return;

            (string Line, bool Warning)? result = await call();
            if (result is not { } line || _disposed || _closing)
                return;

            SetAction(line.Line, line.Warning);
            await ReadAllAsync(feeds);
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
            if (_filterChangedDuringRun && !_closing)
                QueryForFilter();
        }
    }

    private void SetAction(string text, bool warning)
    {
        ActionText = text;
        IsActionWarning = warning;
    }

    // Marks the service call as out for the close question, from just before the call to its end, and keeps the call
    // itself for the stop to wait on
    private async Task<T> CallServiceAsync<T>(Func<CancellationToken, Task<T>> call)
    {
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

    /// <summary>
    /// Holds the gate only while the dialog is open, so the service call that follows doesn't lock Lookup or Settings.
    /// Null when the dialog failed to show.
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

    private sealed class RunsHold(SavedFeedsViewModel page) : IDisposable
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
        SearchCommand.NotifyCanExecuteChanged();
        RetireCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        ClearHistoryCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
    }
}
