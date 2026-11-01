using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.CloudSearch;

namespace PoorMansFeed;

/// <summary>
/// Cloud Address Lookup: one typed address searched in the saved Google Cloud, Azure and AWS ranges. It doesn't use
/// the work gate, because it reads only the saved files and writes nothing.
/// </summary>
public sealed class CloudAddressSearchViewModel : ObservableObject, IDisposable
{
    private readonly ICloudAddressSearchService _search;
    private readonly IUiServices _ui;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _disposed;
    private bool _isWorking;
    private bool _hasSearched;
    private string? _lastReadInput;
    private int _searchesStarted;
    private string _query = "";
    private IReadOnlyList<CloudAddressRow> _rows = [];
    private string _summaryText = AppText.CloudSearchInitial;
    private string _googleSourceText = "";
    private string _azureSourceText = "";
    private string _awsSourceText = "";
    private string _actionText = "";

    public CloudAddressSearchViewModel(ICloudAddressSearchService search, IUiServices ui)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        // A copy changes nothing a search reads, so it stays on while one runs
        CopyNetworkCommand = new AsyncRelayCommand<CloudAddressRow>(CopyNetworkAsync, row => row is not null && !_disposed);
    }

    public IAsyncRelayCommand SearchCommand { get; }

    public IAsyncRelayCommand<CloudAddressRow> CopyNetworkCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? ""))
                SearchCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<CloudAddressRow> Rows
    {
        get => _rows;
        private set
        {
            if (!SetProperty(ref _rows, value))
                return;

            OnPropertyChanged(nameof(HasRows));
        }
    }

    public bool HasRows => _rows.Count > 0;

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
                RefreshCommands();
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>The empty list's line: one before the first answer, another once an answer had no rows.</summary>
    public string EmptyText => _hasSearched ? AppText.CloudSearchEmptyNone : AppText.CloudSearchEmptyInitial;

    public string GoogleSourceText
    {
        get => _googleSourceText;
        private set
        {
            if (SetProperty(ref _googleSourceText, value))
                OnPropertyChanged(nameof(HasSourceTexts));
        }
    }

    public string AzureSourceText
    {
        get => _azureSourceText;
        private set => SetProperty(ref _azureSourceText, value);
    }

    public string AwsSourceText
    {
        get => _awsSourceText;
        private set => SetProperty(ref _awsSourceText, value);
    }

    /// <summary>False until the saved ranges have been read, by a show or a search; the three lines are set together.</summary>
    public bool HasSourceTexts => _googleSourceText.Length > 0;

    public string ActionText
    {
        get => _actionText;
        private set
        {
            if (SetProperty(ref _actionText, value))
                OnPropertyChanged(nameof(HasActionText));
        }
    }

    public bool HasActionText => _actionText.Length > 0;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }

    /// <summary>Searches the last answer's address again when that answer read the saved files, else reads only the
    /// source lines, because a provider page may have saved new ranges since; the service reuses its parse meanwhile.</summary>
    public Task RefreshAsync()
    {
        if (_disposed || IsWorking)
            return Task.CompletedTask;
        return _lastReadInput is { } input ? RunAsync(input) : ReadSourcesAsync();
    }

    // Reads without IsWorking, so the summary and the commands stay. A search pressed meanwhile keeps its own lines,
    // but one that read no files (a bad input) leaves none, so this read still fills them
    private async Task ReadSourcesAsync()
    {
        int started = _searchesStarted;
        IReadOnlyList<SavedRangesStatus> sources;
        try
        {
            sources = await _search.ReadSourcesAsync(_lifetime.Token);
        }
        catch (Exception)
        {
            return;
        }

        if (!_disposed && (started == _searchesStarted || !HasSourceTexts))
            ShowSources(sources);
    }

    private bool CanSearch() => !_disposed && !IsWorking && !string.IsNullOrWhiteSpace(_query);

    private void RefreshCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        CopyNetworkCommand.NotifyCanExecuteChanged();
    }

    private Task SearchAsync() => CanSearch() ? RunAsync(_query) : Task.CompletedTask;

    // The old rows and source lines stay until the answer, so nothing changes height while it runs
    private async Task RunAsync(string input)
    {
        _searchesStarted++;
        IsWorking = true;
        SummaryText = AppText.Searching;
        ActionText = "";
        try
        {
            CloudAddressSearchResult result;
            try
            {
                result = await _search.SearchAsync(input, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (!_disposed)
                    Apply(input, new CloudAddressSearchResult(CloudAddressSearchOutcome.Failed, null, [], []));
                return;
            }

            if (!_disposed)
                Apply(input, result);
        }
        finally
        {
            if (!_disposed)
                IsWorking = false;
        }
    }

    private void Apply(string input, CloudAddressSearchResult result)
    {
        _lastReadInput = result.Sources.Count > 0 ? input : null;
        string address = result.Query?.Text ?? "";
        List<CloudAddressRow> rows = result.Outcome == CloudAddressSearchOutcome.Found
            ? result.Matches.Take(MainViewModel.ResultLimit).Select(CloudAddressRow.From).ToList()
            : [];

        _hasSearched = true;
        Rows = rows;
        OnPropertyChanged(nameof(EmptyText));
        SummaryText = result.Outcome switch
        {
            CloudAddressSearchOutcome.Found => AppText.CloudSearchFound(rows.Count, result.Matches.Count, address),
            CloudAddressSearchOutcome.NoMatches => AppText.CloudSearchNoMatches(address, Searched(result.Sources)),
            CloudAddressSearchOutcome.InvalidInput => AppText.CloudSearchInvalidInput,
            CloudAddressSearchOutcome.NotAnAddress => AppText.CloudSearchNotAnAddress,
            CloudAddressSearchOutcome.NoSavedRanges when result.Sources.Any(source => source.State == SavedRangesState.Unusable)
                => AppText.CloudSearchNoReadableRanges,
            CloudAddressSearchOutcome.NoSavedRanges => AppText.CloudSearchNoSavedRanges,
            _ => AppText.CloudSearchFailed,
        };

        ShowSources(result.Sources);
    }

    // No sources (a bad input or a failure) keeps the last read's lines, each true when it was read
    private void ShowSources(IReadOnlyList<SavedRangesStatus> sources)
    {
        foreach (SavedRangesStatus source in sources)
        {
            string line = AppText.CloudSearchSource(source);
            switch (source.Provider)
            {
                case CloudProvider.GoogleCloud:
                    GoogleSourceText = line;
                    break;
                case CloudProvider.Azure:
                    AzureSourceText = line;
                    break;
                case CloudProvider.Aws:
                    AwsSourceText = line;
                    break;
            }
        }
    }

    private static List<CloudProvider> Searched(IReadOnlyList<SavedRangesStatus> sources) =>
        sources.Where(source => source.State == SavedRangesState.Read).Select(source => source.Provider).ToList();

    // No success line, because a paste shows it; a success clears an earlier failure's line
    private async Task CopyNetworkAsync(CloudAddressRow? row)
    {
        if (row is null || _disposed)
            return;

        try
        {
            await _ui.CopyTextAsync(row.NetworkText);
        }
        catch (Exception)
        {
            if (!_disposed)
                ActionText = AppText.NetworkCopyFailed;
            return;
        }

        if (!_disposed)
            ActionText = "";
    }
}
