using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;

namespace PoorMansFeed;

/// <summary>
/// The Google Cloud feed tool: loads cloud.json, offers its regions and checks one of them. The list, the commands
/// and the write flow are in <see cref="CloudFeedPageViewModel"/>.
/// </summary>
public sealed class GoogleCloudFeedViewModel : CloudFeedPageViewModel
{
    private readonly IGoogleCloudFeedService _google;
    private readonly IIpInfoService _ipinfo;

    private CloudRangeCatalogue? _catalogue;
    private int _activated;
    private bool _includeGlobal;
    private RegionOption? _selectedRegion;
    private IReadOnlyList<RegionOption> _regions = [];

    public GoogleCloudFeedViewModel(IGoogleCloudFeedService feeds, IIpInfoService ipinfo, IUiServices ui, string? defaultExportFolder = null)
        : base(feeds, ipinfo, ui, defaultExportFolder, AppText.GoogleCloudEmptyInitial)
    {
        _google = feeds;
        _ipinfo = ipinfo;
        ReloadRangesCommand = new AsyncRelayCommand(ReloadAsync, () => !IsDisposed && !IsWorking);
    }

    public IAsyncRelayCommand ReloadRangesCommand { get; }

    public IReadOnlyList<RegionOption> Regions => _regions;

    public RegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SameRegion(_selectedRegion, value))
                return;

            if (SetProperty(ref _selectedRegion, value))
            {
                OnPropertyChanged(nameof(CanIncludeGlobal));
                ClearRows();
            }
        }
    }

    public bool IncludeGlobal
    {
        get => _includeGlobal;
        set
        {
            if (SetProperty(ref _includeGlobal, value))
                ClearRows();
        }
    }

    public bool CanIncludeGlobal => _selectedRegion is not { Scope.IsGlobal: true };

    protected override bool CanStartCheck => _selectedRegion is not null || _catalogue is null;

    protected override string? EmptyTextWhenReady => _catalogue is not null ? AppText.GoogleCloudEmptyInitial : null;

    protected override string NothingSelectedText => AppText.GoogleCloudNothingSelected;

    public Task ActivateAsync()
    {
        if (Interlocked.Exchange(ref _activated, 1) == 1)
            return Task.CompletedTask;

        return LoadCatalogueAsync();
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        ReloadRangesCommand.NotifyCanExecuteChanged();
    }

    protected override async Task<Func<CancellationToken, Task<CheckResult>>?> PrepareCheckAsync()
    {
        if (_catalogue is null)
            await LoadCatalogueAsync();

        if (IsDisposed || _catalogue is not { } catalogue || _selectedRegion is not { } region)
            return null;

        bool includeGlobal = _includeGlobal;
        return ct => _google.CheckAsync(catalogue, new RegionSelection(region.Scope, includeGlobal), ct);
    }

    private async Task ReloadAsync()
    {
        if (IsDisposed || IsWorking)
            return;

        Interlocked.Exchange(ref _activated, 1);
        await LoadCatalogueAsync();
    }

    private async Task LoadCatalogueAsync()
    {
        if (IsDisposed)
            return;

        IsWorking = true;
        EmptyText = AppText.GoogleCloudLoading;
        try
        {
            CatalogueResult result = await _google.LoadCatalogueAsync(Lifetime);
            if (IsDisposed)
                return;

            if (result.Failure != CatalogueFailure.None || result.Catalogue is null)
            {
                FailLoad(result.Failure);
                return;
            }

            ApplyCatalogue(result.Catalogue);
        }
        catch (OperationCanceledException) when (IsLifetimeCancelled)
        {
        }
        catch (Exception)
        {
            if (!IsDisposed)
                FailLoad(CatalogueFailure.ServiceError);
        }
        finally
        {
            if (!IsDisposed)
                IsWorking = false;
        }
    }

    private void ApplyCatalogue(CloudRangeCatalogue catalogue)
    {
        _catalogue = catalogue;
        bool includeIPv6 = _ipinfo.Settings.IPv6Enabled;
        string? previous = _selectedRegion?.Scope.Id;
        _regions = catalogue.Regions
            .Select(region => new RegionOption(region.Scope, AppText.RegionChoice(region.Scope.Id, region.CountFor(includeIPv6))))
            .ToList();
        OnPropertyChanged(nameof(Regions));

        RegionOption? match = previous is null
            ? null
            : _regions.FirstOrDefault(region => region.Scope.Id == previous);
        _selectedRegion = match ?? (_regions.Count > 0 ? _regions[0] : null);
        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(CanIncludeGlobal));
        ClearRows();
        EmptyText = AppText.GoogleCloudEmptyInitial;
    }

    private void FailLoad(CatalogueFailure failure)
    {
        _catalogue = null;
        _regions = [];
        OnPropertyChanged(nameof(Regions));
        _selectedRegion = null;
        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(CanIncludeGlobal));
        ShowNoRows(failure switch
        {
            CatalogueFailure.Offline => AppText.GoogleCloudLoadOffline,
            CatalogueFailure.Unreadable => AppText.GoogleCloudLoadUnreadable,
            _ => AppText.GoogleCloudLoadFailed,
        });
    }

    private static bool SameRegion(RegionOption? left, RegionOption? right) =>
        left == right || (left is not null && right is not null && left.Scope == right.Scope);
}
