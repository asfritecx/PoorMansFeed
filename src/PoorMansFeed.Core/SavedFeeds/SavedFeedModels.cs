using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>
/// What a saved feed writes again. Lists hold canonical CIDR text; the thresholds are the ASN feed's only, and a null
/// AwsRegion means every region. SeenUnconfirmed is a cloud page write's mixed and not-in-database ranges (null: none).
/// </summary>
public sealed record SavedFeedPick(string? Asn, string? GoogleScope, bool GoogleAddsGlobal, string? AzureTag,
                                   string? AwsService, string? AwsRegion,
                                   IReadOnlyList<string> HandTicked, IReadOnlyList<string> HandUnticked,
                                   IReadOnlyList<string> LargeIncluded, IReadOnlyList<string> LargeLeftOut,
                                   bool? LargeRangeCheckEnabled, int? IPv4MinPrefix, int? IPv6MinPrefix,
                                   IReadOnlyList<string>? SeenUnconfirmed = null);

/// <summary>One saved feed per set of files, kept by file stem; Proof is null unless its write was whole.</summary>
public sealed record SavedFeed(string Key, FeedKind Kind, SavedFeedPick Pick, DateTimeOffset LastWriteUtc,
                               DateTimeOffset? LastRefreshUtc, bool Retired, SavedFeedWriteProof? Proof = null);

public abstract record SavedFeedIntent
{
    private SavedFeedIntent() { }

    public sealed record Written(SavedFeedPick Pick, SavedFeedWriteProof? Proof = null) : SavedFeedIntent;

    public sealed record Retired : SavedFeedIntent;
}

public enum SavedFeedsFailure { None, NotFound, TooMany, Unavailable, StorageError }

/// <summary>Available is false while config.db holds saved feeds this build can't read.</summary>
public sealed record SavedFeedsRead(bool Available, IReadOnlyList<SavedFeed> Feeds);

/// <summary>
/// Applied only while the stored feed equals Expected and isn't retired. Null NewPick keeps the pick; null Proof clears
/// it (a skip passes Expected.Proof); null RefreshedUtc keeps LastRefreshUtc, as a write that failed after a move does.
/// </summary>
public sealed record SavedFeedStamp(SavedFeed Expected, SavedFeedPick? NewPick, SavedFeedWriteProof? Proof,
                                    DateTimeOffset? RefreshedUtc);

public enum SavedFeedStampOutcome { Saved, Unchanged, Superseded, Removed, TooMany, Unavailable, StorageError }
