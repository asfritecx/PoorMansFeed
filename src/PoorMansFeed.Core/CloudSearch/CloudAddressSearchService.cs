using System.Diagnostics;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.GoogleCloud;

namespace PoorMansFeed.Core.CloudSearch;

/// <summary>
/// Keeps each provider's last Read catalogue with the stamps of its saved file and its record, and loads again when
/// either stamp changes.
/// </summary>
public sealed class CloudAddressSearchService : ICloudAddressSearchService
{
    private readonly AppPaths? _paths;
    private readonly IGoogleCloudFeedService _google;
    private readonly IAzureFeedService _azure;
    private readonly IAwsFeedService _aws;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Slot<CloudRangeCatalogue> _googleSlot = new();
    private readonly Slot<AzureTagCatalogue> _azureSlot = new();
    private readonly Slot<AwsRangeCatalogue> _awsSlot = new();

    public CloudAddressSearchService(AppPaths? paths, IGoogleCloudFeedService google,
                                     IAzureFeedService azure, IAwsFeedService aws)
    {
        _paths = paths;
        _google = google ?? throw new ArgumentNullException(nameof(google));
        _azure = azure ?? throw new ArgumentNullException(nameof(azure));
        _aws = aws ?? throw new ArgumentNullException(nameof(aws));
    }

    public async Task<CloudAddressSearchResult> SearchAsync(string? input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!SearchQuery.TryParse(input, out SearchQuery? query))
            return new CloudAddressSearchResult(CloudAddressSearchOutcome.InvalidInput, null, [], []);
        if (query.Kind != SearchKind.IpAddress || query.IpBytes is null)
            return new CloudAddressSearchResult(CloudAddressSearchOutcome.NotAnAddress, query, [], []);

        try
        {
            (SavedRanges<CloudRangeCatalogue> google, SavedRanges<AzureTagCatalogue> azure, SavedRanges<AwsRangeCatalogue> aws) =
                await LoadAllAsync(ct).ConfigureAwait(false);
            SavedRangesStatus[] sources = SourcesOf(google, azure, aws);
            if (sources.All(source => source.State != SavedRangesState.Read))
                return new CloudAddressSearchResult(CloudAddressSearchOutcome.NoSavedRanges, query, [], sources);

            byte[] address = query.IpBytes;
            IReadOnlyList<CloudAddressMatch> matches = await Task.Run(
                () => CloudAddressIndex.Find(address, google.Catalogue, azure.Catalogue, aws.Catalogue), ct).ConfigureAwait(false);
            CloudAddressSearchOutcome outcome = matches.Count > 0 ? CloudAddressSearchOutcome.Found : CloudAddressSearchOutcome.NoMatches;
            return new CloudAddressSearchResult(outcome, query, matches, sources);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            Trace.TraceWarning("The saved cloud ranges could not be searched: " + ex.GetType().Name);
            return new CloudAddressSearchResult(CloudAddressSearchOutcome.Failed, query, [], []);
        }
    }

    public async Task<IReadOnlyList<SavedRangesStatus>> ReadSourcesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            (SavedRanges<CloudRangeCatalogue> google, SavedRanges<AzureTagCatalogue> azure, SavedRanges<AwsRangeCatalogue> aws) =
                await LoadAllAsync(ct).ConfigureAwait(false);
            return SourcesOf(google, azure, aws);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            Trace.TraceWarning("The saved cloud ranges could not be read: " + ex.GetType().Name);
            return [];
        }
    }

    private async Task<(SavedRanges<CloudRangeCatalogue>, SavedRanges<AzureTagCatalogue>, SavedRanges<AwsRangeCatalogue>)>
        LoadAllAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SavedRanges<CloudRangeCatalogue> google = await LoadAsync(_googleSlot, _paths?.GoogleCloudRangesPath,
                _paths?.GoogleCloudRangesSourcePath, _google.LoadSavedAsync, ct).ConfigureAwait(false);
            SavedRanges<AzureTagCatalogue> azure = await LoadAsync(_azureSlot, _paths?.AzureServiceTagsPath,
                _paths?.AzureServiceTagsSourcePath, _azure.LoadSavedAsync, ct).ConfigureAwait(false);
            SavedRanges<AwsRangeCatalogue> aws = await LoadAsync(_awsSlot, _paths?.AwsIpRangesPath,
                _paths?.AwsIpRangesSourcePath, _aws.LoadSavedAsync, ct).ConfigureAwait(false);
            return (google, azure, aws);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static SavedRangesStatus[] SourcesOf(SavedRanges<CloudRangeCatalogue> google, SavedRanges<AzureTagCatalogue> azure,
                                                 SavedRanges<AwsRangeCatalogue> aws) =>
    [
        new(CloudProvider.GoogleCloud, google.State, google.SavedUtc),
        new(CloudProvider.Azure, azure.State, azure.SavedUtc),
        new(CloudProvider.Aws, aws.State, aws.SavedUtc),
    ];

    private static async Task<SavedRanges<T>> LoadAsync<T>(Slot<T> slot, string? dataPath, string? recordPath,
                                                           Func<CancellationToken, Task<SavedRanges<T>>> load,
                                                           CancellationToken ct) where T : class
    {
        // The stamps are taken before the load, so a save landing during it makes the next search load again
        FileStamp? data = StampOf(dataPath);
        FileStamp? record = StampOf(recordPath);
        SavedStamp? stamp = data is { } d && record is { } r ? new SavedStamp(d, r) : null;
        if (stamp is { } now && slot.Held is { } held && held.Stamp.Data == now.Data && held.Stamp.Record == now.Record)
            return held.Saved;

        SavedRanges<T> saved = await load(ct).ConfigureAwait(false);
        if (saved.State == SavedRangesState.Read && saved.Catalogue is null)
            saved = new SavedRanges<T>(SavedRangesState.Unusable, null, null);

        slot.Held = stamp is { Data.Exists: true, Record.Exists: true } kept && saved.State == SavedRangesState.Read
            ? (kept, saved)
            : null;
        return saved;
    }

    private static FileStamp? StampOf(string? path)
    {
        if (path is null)
            return null;

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(true, info.Length, info.LastWriteTimeUtc) : new FileStamp(false, 0, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private readonly record struct FileStamp(bool Exists, long Length, DateTime LastWriteUtc);

    private readonly record struct SavedStamp(FileStamp Data, FileStamp Record);

    private sealed class Slot<T> where T : class
    {
        public (SavedStamp Stamp, SavedRanges<T> Saved)? Held { get; set; }
    }
}
