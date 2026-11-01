using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.CloudSearch;

public enum CloudProvider { GoogleCloud, Azure, Aws }

/// <summary>
/// One saved range that holds the address. Region is "" for an all-regions Azure tag; Scope is an AWS Local Zone's
/// border group. IsUmbrella marks the ranges that cover a whole cloud (AzureCloud, AWS's AMAZON).
/// </summary>
public sealed record CloudAddressMatch(CloudProvider Provider, string Service, string Region, string? Scope,
                                       CidrBlock Prefix, bool IsUmbrella);

public sealed record SavedRangesStatus(CloudProvider Provider, SavedRangesState State, DateTimeOffset? SavedUtc);

public enum CloudAddressSearchOutcome { Found, NoMatches, InvalidInput, NotAnAddress, NoSavedRanges, Failed }

/// <summary>Sources holds the three providers in provider order once the saved ranges were read, else none.</summary>
public sealed record CloudAddressSearchResult(CloudAddressSearchOutcome Outcome, SearchQuery? Query,
                                              IReadOnlyList<CloudAddressMatch> Matches,
                                              IReadOnlyList<SavedRangesStatus> Sources);
