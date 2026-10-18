using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed;

/// <summary>
/// What every cloud feed page shares: the list of checked ranges, the Check, Write feed and Open folder commands,
/// the large-range question, and the guard that drops the answer of a check the operator has moved on from. A page
/// supplies how its pick is loaded and checked. It never reads or sets MainViewModel.IsBusy.
/// </summary>
public abstract class CloudFeedPageViewModel : ObservableObject, IDisposable
{
    private readonly ICloudFeedService _feeds;
    private readonly IIpInfoService _ipinfo;
    private readonly IUiServices _ui;
    private readonly string _defaultExportFolder;
    private readonly CancellationTokenSource _lifetime = new();

    private CloudFeedCheck? _check;
    private int _checkVersion;
    private bool _disposed;
    private bool _isWorking;
    private bool _hasRows;
    private bool _hasActionText;
    private string _summaryText = AppText.CloudSummaryInitial;
    private string _emptyText;
    private string _actionText = "";

    protected CloudFeedPageViewModel(ICloudFeedService feeds, IIpInfoService ipinfo, IUiServices ui,
                                     string? defaultExportFolder, string initialEmptyText)
    {
        _feeds = feeds ?? throw new ArgumentNullException(nameof(feeds));
        _ipinfo = ipinfo ?? throw new ArgumentNullException(nameof(ipinfo));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _defaultExportFolder = defaultExportFolder ?? Path.Combine(ipinfo.DataFolder, "exports");
        _emptyText = initialEmptyText ?? throw new ArgumentNullException(nameof(initialEmptyText));
        Rows = [];

        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !_disposed && !IsWorking && CanStartCheck);
        WriteFeedCommand = new AsyncRelayCommand(WriteAsync, CanWrite);
        OpenFeedFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => !_disposed);
    }

    public IAsyncRelayCommand CheckCommand { get; }

    public IAsyncRelayCommand WriteFeedCommand { get; }

    public IAsyncRelayCommand OpenFeedFolderCommand { get; }

    public bool IsWorking
    {
        get => _isWorking;
        protected set
        {
            if (SetProperty(ref _isWorking, value))
                RefreshCommands();
        }
    }

    public ObservableCollection<CloudRangeRow> Rows { get; }

    public bool HasRows
    {
        get => _hasRows;
        private set
        {
            if (SetProperty(ref _hasRows, value))
                WriteFeedCommand.NotifyCanExecuteChanged();
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

    protected bool IsDisposed => _disposed;

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
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }

    /// <summary>Forgets the checked ranges. Anything still checking is told to stand down by the version bump.</summary>
    protected void ClearRows()
    {
        _checkVersion++;
        _check = null;
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
    }

    private bool CanWrite() => !_disposed && !IsWorking && HasRows && _check is not null;

    private async Task CheckAsync()
    {
        if (_disposed || IsWorking)
            return;

        Func<CancellationToken, Task<CheckResult>>? start = await PrepareCheckAsync();
        if (_disposed || start is null)
            return;

        int version = _checkVersion;
        IsWorking = true;
        try
        {
            CheckResult result = await start(_lifetime.Token);
            if (_disposed || version != _checkVersion)
                return;

            ShowCheck(result);
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
        if (_disposed || IsWorking || _check is null)
            return;

        if (!_check.MatchesWriteSettings(_ipinfo.Settings))
        {
            ActionText = AppText.CloudSettingsChanged;
            return;
        }

        var ticked = new List<CidrBlock>();
        foreach (CloudRangeRow row in Rows)
        {
            if (row.IsIncluded)
                ticked.Add(row.Block);
        }

        CloudFeedPlan plan = _feeds.PlanWrite(_check, ticked);
        IReadOnlyCollection<CidrBlock> large = [];
        if (plan.LargeRanges.Count > 0)
        {
            var choices = plan.LargeRanges.Select(LargeRangeChoice.From).ToList();
            var prompt = new LargeRangePrompt(
                AppText.CloudLargeRangesTitle,
                AppText.CloudLargeRangesMessage(choices.Count, _ipinfo.Settings.IPv4MinPrefix, _ipinfo.Settings.IPv6MinPrefix),
                choices);
            IReadOnlyList<string>? chosen = await _ui.ChooseLargeRangesAsync(prompt);
            if (chosen is null || _disposed || _check is null)
                return;

            if (!_check.MatchesWriteSettings(_ipinfo.Settings))
            {
                ActionText = AppText.CloudSettingsChanged;
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

        IsWorking = true;
        try
        {
            CloudFeedWriteResult written = await _feeds.WriteAsync(plan, large, _lifetime.Token);
            if (!_disposed)
                ActionText = WriteMessage(written);
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

    private void ShowCheck(CheckResult result)
    {
        if (result.Failure != CheckFailure.None || result.Check is null)
        {
            ShowNoRows(result.Failure switch
            {
                CheckFailure.NoDataset => AppText.CloudNoDataset,
                CheckFailure.NothingSelected => NothingSelectedText,
                _ => AppText.CloudCheckFailed,
            });
            return;
        }

        _check = result.Check;
        Rows.Clear();
        foreach (CheckedRange range in result.Check.Ranges)
            Rows.Add(CloudRangeRow.From(range));

        HasRows = Rows.Count > 0;
        SummaryText = AppText.CloudSummary(
            result.Check.Ranges.Count,
            result.Check.Count(RangeConfirmation.Confirmed),
            result.Check.Count(RangeConfirmation.Mixed),
            result.Check.Count(RangeConfirmation.NotInDatabase));
        EmptyText = HasRows ? EmptyTextWhenReady ?? EmptyText : NothingSelectedText;
        RefreshCommands();
    }

    private static string WriteMessage(CloudFeedWriteResult result)
    {
        switch (result.Failure)
        {
            case CloudFeedWriteFailure.FolderUnavailable:
                return AppText.ExportFolderUnavailable;
            case CloudFeedWriteFailure.WriteFailed:
                return AppText.CloudWriteFailed;
            case CloudFeedWriteFailure.SettingsChanged:
                return AppText.CloudSettingsChanged;
            case CloudFeedWriteFailure.NothingToWrite:
                return result.LargeRangesLeftOut > 0 ? AppText.CloudAllLeftOut : AppText.CloudNothingTicked;
        }

        string text = AppText.CloudFeedWritten(result.RangesWritten, result.Files.Count);
        if (result.LargeRangesLeftOut > 0)
            text += AppText.LargeRangesLeftOut(result.LargeRangesLeftOut);

        if (result.UnconfirmedWritten > 0)
            text += AppText.CloudUnconfirmed(result.UnconfirmedWritten);

        return text;
    }
}
