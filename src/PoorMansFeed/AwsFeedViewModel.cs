using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed;

/// <summary>One service in the Service picker.</summary>
public sealed record AwsServiceOption(string Name, string Text);

/// <summary>One entry in the Region picker. Region is null for "All regions".</summary>
public sealed record AwsRegionOption(string? Region, string Text);

/// <summary>
/// The AWS feed tool: shows the saved ip-ranges.json, looks for a newer one, and checks one service in one region
/// (or in every region). The list, the commands and the write flow are in <see cref="CloudFeedPageViewModel"/>.
/// </summary>
public sealed class AwsFeedViewModel : CloudFeedPageViewModel
{
    private const string DefaultService = "EC2";
    private const string GlobalRegion = "GLOBAL";

    private readonly IAwsFeedService _aws;
    private readonly IIpInfoService _ipinfo;
    private readonly TimeProvider _time;

    private AwsRangeCatalogue? _catalogue;
    private int _activated;
    private string _statusText = "";
    private AwsServiceOption? _selectedService;
    private AwsRegionOption? _selectedRegion;
    private IReadOnlyList<AwsServiceOption> _services = [];
    private IReadOnlyList<AwsRegionOption> _regions = [];

    public AwsFeedViewModel(IAwsFeedService feeds, IIpInfoService ipinfo, IUiServices ui, TimeProvider time,
                            string? defaultExportFolder = null)
        : base(feeds, ipinfo, ui, defaultExportFolder, AppText.AwsLoading)
    {
        _aws = feeds;
        _ipinfo = ipinfo;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsDisposed && !IsWorking);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IReadOnlyList<AwsServiceOption> Services => _services;

    public IReadOnlyList<AwsRegionOption> Regions => _regions;

    /// <summary>Which file the list comes from, and when it was last checked. Empty until there is something to say.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // A picker that loses its items writes null back to these. Only the view model empties a pick, so null is ignored.
    public AwsServiceOption? SelectedService
    {
        get => _selectedService;
        set
        {
            if (value is null || (_selectedService is not null && _selectedService.Name == value.Name))
                return;

            _selectedService = value;
            OnPropertyChanged();
            ShowRegions(value, null);
            ClearRows();
        }
    }

    public AwsRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            // The "All regions" option has a null Region, so compare the options and not just the ids
            if (value is null || (_selectedRegion is not null && _selectedRegion.Region == value.Region))
                return;

            _selectedRegion = value;
            OnPropertyChanged();
            ClearRows();
        }
    }

    protected override bool CanStartCheck => _catalogue is not null && _selectedService is not null && _selectedRegion is not null;

    protected override string? EmptyTextWhenReady => _catalogue is not null ? AppText.AwsEmptyInitial : null;

    protected override string NothingSelectedText => AppText.AwsNothingSelected;

    public Task ActivateAsync()
    {
        if (Interlocked.Exchange(ref _activated, 1) == 1)
            return Task.CompletedTask;

        return UpdateAsync(loadSavedFirst: true);
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

    protected override Task<Func<CancellationToken, Task<CheckResult>>?> PrepareCheckAsync()
    {
        if (IsDisposed || _catalogue is not { } catalogue || _selectedService is not { } service || _selectedRegion is not { } region)
            return Task.FromResult<Func<CancellationToken, Task<CheckResult>>?>(null);

        var selection = new AwsSelection(service.Name, region.Region);
        return Task.FromResult<Func<CancellationToken, Task<CheckResult>>?>(ct => _aws.CheckAsync(catalogue, selection, ct));
    }

    private async Task CheckForUpdatesAsync()
    {
        if (IsDisposed || IsWorking)
            return;

        Interlocked.Exchange(ref _activated, 1);
        await UpdateAsync(loadSavedFirst: false);
    }

    private async Task UpdateAsync(bool loadSavedFirst)
    {
        if (IsDisposed)
            return;

        IsWorking = true;
        try
        {
            if (loadSavedFirst)
            {
                EmptyText = AppText.AwsLoading;
                AwsRangeCatalogue? saved = await _aws.LoadCachedAsync(Lifetime);
                if (IsDisposed)
                    return;

                if (saved is not null)
                    ApplyCatalogue(saved);
            }

            StatusText = AppText.CheckingForUpdates;
            AwsLoadResult result = await _aws.CheckForUpdateAsync(_catalogue, Lifetime);
            if (IsDisposed)
                return;

            ApplyUpdate(result);
        }
        catch (OperationCanceledException) when (IsLifetimeCancelled)
        {
        }
        catch (Exception)
        {
            if (!IsDisposed)
                ApplyUpdate(new AwsLoadResult(
                    _catalogue is null ? AwsUpdateStatus.Unavailable : AwsUpdateStatus.CheckFailed,
                    AwsSourceFailure.ServiceError,
                    _catalogue,
                    _time.GetUtcNow()));
        }
        finally
        {
            if (!IsDisposed)
                IsWorking = false;
        }
    }

    private void ApplyUpdate(AwsLoadResult result)
    {
        switch (result.Status)
        {
            case AwsUpdateStatus.Updated when result.Catalogue is { } updated:
                ApplyCatalogue(updated);
                StatusText = AppText.AwsStatusUpdated(updated.Source.CreatedUtc, LocalTime(result.CheckedAt));
                break;
            case AwsUpdateStatus.UpToDate when result.Catalogue is { } current:
                // An up-to-date answer can carry a newer record for the same ranges (a new ETag), which changes nothing the page shows
                _catalogue = current;
                StatusText = AppText.AwsStatusUpToDate(current.Source.CreatedUtc, LocalTime(result.CheckedAt));
                break;
            case AwsUpdateStatus.CheckFailed when _catalogue is { } kept:
                StatusText = AppText.AwsStatusCheckFailed(kept.Source.CreatedUtc);
                break;
            default:
                ShowUnavailable(result.Failure);
                break;
        }
    }

    private void ApplyCatalogue(AwsRangeCatalogue catalogue)
    {
        string? previousRegion = _selectedRegion?.Region;
        string? previousService = _selectedService?.Name;
        _catalogue = catalogue;
        _services = catalogue.Services.Select(service => new AwsServiceOption(service.Name, service.Name)).ToList();
        OnPropertyChanged(nameof(Services));

        // Keep the service if the new file still has it, else EC2, else the first one
        AwsServiceOption? service = previousService is null ? null : _services.FirstOrDefault(option => option.Name == previousService);
        service ??= _services.FirstOrDefault(option => option.Name == DefaultService) ?? _services.FirstOrDefault();
        bool sameService = service is not null && service.Name == previousService;
        _selectedService = service;
        OnPropertyChanged(nameof(SelectedService));
        ShowRegions(service, sameService ? previousRegion : null);
        ClearRows();
    }

    private void ShowUnavailable(AwsSourceFailure failure)
    {
        _catalogue = null;
        _services = [];
        OnPropertyChanged(nameof(Services));
        _selectedService = null;
        OnPropertyChanged(nameof(SelectedService));
        ShowRegions(null, null);
        StatusText = AppText.AwsStatusUnavailable;
        ClearRows();
        ShowNoRows(failure switch
        {
            AwsSourceFailure.Offline => AppText.AwsLoadOffline,
            AwsSourceFailure.FileUnreadable => AppText.AwsLoadUnreadable,
            _ => AppText.AwsLoadFailed,
        });
    }

    // The picker writes null back while its items change. The setters ignore it, and the selection is announced after the list, so the picker ends on the right item.
    private void ShowRegions(AwsServiceOption? service, string? preferredRegion)
    {
        _regions = service is null ? [] : BuildRegions(service.Name);
        OnPropertyChanged(nameof(Regions));
        _selectedRegion = (preferredRegion is null ? null : _regions.FirstOrDefault(region => region.Region == preferredRegion))
            ?? _regions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedRegion));
    }

    private IReadOnlyList<AwsRegionOption> BuildRegions(string serviceName)
    {
        if (_catalogue?.Find(serviceName) is not { } service)
            return [];

        bool includeIPv6 = _ipinfo.Settings.IPv6Enabled;
        var options = new List<AwsRegionOption>(service.Regions.Count + 1)
        {
            new(null, AppText.RegionChoice(AppText.AwsAllRegions, service.CountFor(includeIPv6))),
        };
        foreach (AwsRegionRanges region in service.Regions)
        {
            string label = region.Region == GlobalRegion ? AppText.AwsGlobalRegion : region.Region;
            options.Add(new AwsRegionOption(region.Region, AppText.RegionChoice(label, region.CountFor(includeIPv6))));
        }

        return options;
    }

    private TimeOnly LocalTime(DateTimeOffset moment) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, _time.LocalTimeZone).DateTime);
}
