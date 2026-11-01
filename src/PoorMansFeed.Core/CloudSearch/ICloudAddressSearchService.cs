namespace PoorMansFeed.Core.CloudSearch;

/// <summary>Searches the saved Google Cloud, Azure and AWS ranges for one address; sends and writes nothing.</summary>
public interface ICloudAddressSearchService
{
    /// <summary>Every failure is an outcome; only the caller's own cancellation escapes.</summary>
    Task<CloudAddressSearchResult> SearchAsync(string? input, CancellationToken ct);

    /// <summary>Each provider's saved state, read as a search reads it; a failure gives none.</summary>
    Task<IReadOnlyList<SavedRangesStatus>> ReadSourcesAsync(CancellationToken ct);
}
