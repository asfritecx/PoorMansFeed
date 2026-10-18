using PoorMansFeed.Core;

namespace PoorMansFeed.Core.GoogleCloud;

/// <summary>
/// A Google Cloud scope from cloud.json: a region id such as "us-central1", or "global". Only lower-case ASCII
/// letters, digits and '-' are accepted, so a scope is safe in a filename.
/// </summary>
public readonly record struct GoogleCloudScope
{
    public const string GlobalId = "global";

    private GoogleCloudScope(string id) => Id = id;

    public string Id { get; }

    public bool IsGlobal => string.Equals(Id, GlobalId, StringComparison.Ordinal);

    public static bool TryCreate(string? id, out GoogleCloudScope scope)
    {
        scope = default;
        if (string.IsNullOrEmpty(id))
            return false;

        foreach (char character in id)
        {
            bool allowed = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
            if (!allowed)
                return false;
        }

        scope = new GoogleCloudScope(id);
        return true;
    }
}

/// <summary>One cloud.json entry: exactly one of ipv4Prefix / ipv6Prefix, already parsed.</summary>
public sealed record CloudPrefix(CidrBlock Block, GoogleCloudScope Scope);

/// <summary>The file's own identity, copied into the feed header verbatim.</summary>
public sealed record CloudRangeSource(Uri Url, string SyncToken, string CreationTime);

/// <summary>One picker entry. Counts per family so the picker can honour IPv6Enabled without reloading.</summary>
public sealed record CloudRegion(GoogleCloudScope Scope, int Ipv4Count, int Ipv6Count)
{
    public int CountFor(bool includeIPv6) => includeIPv6 ? Ipv4Count + Ipv6Count : Ipv4Count;
}

/// <summary>
/// Everything read from one download of cloud.json. Regions lists global first, then the rest by id.
/// SkippedEntries counts entries with no prefix, both prefixes, a bad prefix, a bad scope or another service.
/// </summary>
public sealed record CloudRangeCatalogue(CloudRangeSource Source, IReadOnlyList<CloudPrefix> Prefixes,
                                         IReadOnlyList<CloudRegion> Regions, int SkippedEntries);

public enum CatalogueFailure { None, Offline, ServiceError, Unreadable }

/// <summary>Catalogue is non-null exactly when Failure is None.</summary>
public sealed record CatalogueResult(CatalogueFailure Failure, CloudRangeCatalogue? Catalogue);

/// <summary>
/// The operator's pick. Selecting global already returns the global prefixes, so IncludeGlobal only widens a
/// regional pick. Membership is one predicate, never a concatenation of two lists.
/// </summary>
public sealed record RegionSelection(GoogleCloudScope Region, bool IncludeGlobal)
{
    public bool AddsGlobal => IncludeGlobal && !Region.IsGlobal;

    public bool Matches(GoogleCloudScope scope) =>
        scope == Region || (IncludeGlobal && !Region.IsGlobal && scope.IsGlobal);
}
