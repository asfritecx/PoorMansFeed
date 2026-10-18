using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed;

/// <summary>One service in the Service picker: every tag that shares a base name.</summary>
public sealed record AzureServiceOption(AzureServiceGroup Group, string Text)
{
    public string BaseName => Group.BaseName;
}

/// <summary>One service tag in the Region picker, with the text the picker shows for it.</summary>
public sealed record AzureTagOption(string TagName, string Text);

/// <summary>
/// The Azure feed tool: shows the saved service tags file, looks for a newer one, and checks one service tag. The
/// list, the commands and the write flow are in <see cref="CloudFeedPageViewModel"/>.
/// </summary>
public sealed class AzureFeedViewModel : CloudFeedPageViewModel
{
    private readonly IAzureFeedService _azure;
    private readonly IIpInfoService _ipinfo;
    private readonly TimeProvider _time;

    private AzureTagCatalogue? _catalogue;
    private int _activated;
    private string _statusText = "";
    private AzureServiceOption? _selectedService;
    private AzureTagOption? _selectedRegion;
    private IReadOnlyList<AzureServiceOption> _services = [];
    private IReadOnlyList<AzureTagOption> _regions = [];

    public AzureFeedViewModel(IAzureFeedService feeds, IIpInfoService ipinfo, IUiServices ui, TimeProvider time,
                              string? defaultExportFolder = null)
        : base(feeds, ipinfo, ui, defaultExportFolder, AppText.AzureLoading)
    {
        _azure = feeds;
        _ipinfo = ipinfo;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsDisposed && !IsWorking);
    }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IReadOnlyList<AzureServiceOption> Services => _services;

    public IReadOnlyList<AzureTagOption> Regions => _regions;

    /// <summary>Which file the list comes from, and when it was last checked. Empty until there is something to say.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // A picker that loses its items writes null back to these. Only the view model empties a pick, so null is ignored.
    public AzureServiceOption? SelectedService
    {
        get => _selectedService;
        set
        {
            if (value is null || _selectedService?.BaseName == value.BaseName)
                return;

            _selectedService = value;
            OnPropertyChanged();
            ShowRegions(value, null);
            ClearRows();
        }
    }

    public AzureTagOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (value is null || _selectedRegion?.TagName == value.TagName)
                return;

            _selectedRegion = value;
            OnPropertyChanged();
            ClearRows();
        }
    }

    protected override bool CanStartCheck => _catalogue is not null && _selectedRegion is not null;

    protected override string? EmptyTextWhenReady => _catalogue is not null ? AppText.AzureEmptyInitial : null;

    protected override string NothingSelectedText => AppText.AzureNothingSelected;

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
        if (IsDisposed || _catalogue is not { } catalogue || _selectedRegion is not { } region)
            return Task.FromResult<Func<CancellationToken, Task<CheckResult>>?>(null);

        var selection = new AzureTagSelection(region.TagName);
        return Task.FromResult<Func<CancellationToken, Task<CheckResult>>?>(ct => _azure.CheckAsync(catalogue, selection, ct));
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
                EmptyText = AppText.AzureLoading;
                AzureTagCatalogue? saved = await _azure.LoadCachedAsync(Lifetime);
                if (IsDisposed)
                    return;

                if (saved is not null)
                    ApplyCatalogue(saved);
            }

            StatusText = AppText.CheckingForUpdates;
            AzureLoadResult result = await _azure.CheckForUpdateAsync(_catalogue, Lifetime);
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
                ApplyUpdate(new AzureLoadResult(
                    _catalogue is null ? AzureUpdateStatus.Unavailable : AzureUpdateStatus.CheckFailed,
                    AzureSourceFailure.ServiceError,
                    _catalogue,
                    _time.GetUtcNow()));
        }
        finally
        {
            if (!IsDisposed)
                IsWorking = false;
        }
    }

    private void ApplyUpdate(AzureLoadResult result)
    {
        switch (result.Status)
        {
            case AzureUpdateStatus.Updated when result.Catalogue is { } updated:
                ApplyCatalogue(updated);
                StatusText = AppText.AzureStatusUpdated(updated.Source.FileDate, updated.Source.ChangeNumber, LocalTime(result.CheckedAt));
                break;
            case AzureUpdateStatus.UpToDate when _catalogue is { } current:
                StatusText = AppText.AzureStatusUpToDate(current.Source.FileDate, current.Source.ChangeNumber, LocalTime(result.CheckedAt));
                break;
            case AzureUpdateStatus.CheckFailed when _catalogue is { } kept:
                StatusText = AppText.AzureStatusCheckFailed(kept.Source.FileDate, kept.Source.ChangeNumber);
                break;
            default:
                ShowUnavailable(result.Failure);
                break;
        }
    }

    private void ApplyCatalogue(AzureTagCatalogue catalogue)
    {
        string? previousTag = _selectedRegion?.TagName;
        string? previousService = _selectedService?.BaseName;
        _catalogue = catalogue;
        _services = catalogue.Services.Select(group => new AzureServiceOption(group, group.BaseName)).ToList();
        OnPropertyChanged(nameof(Services));

        // Keep the tag if the new file still has it, else the same service, else the first one
        AzureServiceOption? service = null;
        if (previousTag is not null && catalogue.Find(previousTag) is { } previous)
            service = _services.FirstOrDefault(option => option.Group.Tags.Contains(previous));

        service ??= previousService is null
            ? null
            : _services.FirstOrDefault(option => string.Equals(option.BaseName, previousService, StringComparison.OrdinalIgnoreCase));
        service ??= _services.FirstOrDefault();
        _selectedService = service;
        OnPropertyChanged(nameof(SelectedService));
        ShowRegions(service, previousTag);
        ClearRows();
    }

    private void ShowUnavailable(AzureSourceFailure failure)
    {
        _catalogue = null;
        _services = [];
        OnPropertyChanged(nameof(Services));
        _selectedService = null;
        OnPropertyChanged(nameof(SelectedService));
        ShowRegions(null, null);
        StatusText = AppText.AzureStatusUnavailable;
        ClearRows();
        ShowNoRows(failure switch
        {
            AzureSourceFailure.Offline => AppText.AzureLoadOffline,
            AzureSourceFailure.PageUnreadable or AzureSourceFailure.FileUnreadable => AppText.AzureLoadUnreadable,
            _ => AppText.AzureLoadFailed,
        });
    }

    // The picker writes null back while its items change. The setters ignore it, and the selection is announced after the list, so the picker ends on the right item.
    private void ShowRegions(AzureServiceOption? service, string? preferredTag)
    {
        _regions = service is null ? [] : BuildRegions(service.Group);
        OnPropertyChanged(nameof(Regions));
        _selectedRegion = (preferredTag is null ? null : _regions.FirstOrDefault(region => region.TagName == preferredTag))
            ?? _regions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedRegion));
    }

    private IReadOnlyList<AzureTagOption> BuildRegions(AzureServiceGroup group)
    {
        bool includeIPv6 = _ipinfo.Settings.IPv6Enabled;
        var labels = group.Tags
            .Select(tag => tag.IsBase ? AppText.AzureAllRegions : tag.Region.Length > 0 ? tag.Region : tag.Suffix)
            .ToList();
        var shared = labels
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Where(sameLabel => sameLabel.Count() > 1)
            .Select(sameLabel => sameLabel.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var options = new List<AzureTagOption>(group.Tags.Count);
        for (int i = 0; i < group.Tags.Count; i++)
        {
            AzureServiceTag tag = group.Tags[i];
            string label = !tag.IsBase && shared.Contains(labels[i]) ? $"{labels[i]} ({tag.Suffix})" : labels[i];
            options.Add(new AzureTagOption(tag.Name, AppText.RegionChoice(label, tag.CountFor(includeIPv6))));
        }

        return options;
    }

    private TimeOnly LocalTime(DateTimeOffset moment) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, _time.LocalTimeZone).DateTime);
}
