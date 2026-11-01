namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// The AbuseIPDB check and the blacklist download the UI calls. Every failure comes back as a result value.
/// </summary>
public interface IAbuseIpDbService
{
    /// <summary>Whether a key is saved. Looks at the marker only and never reads the store.</summary>
    bool HasKey { get; }

    /// <summary>Saves the key after trimming it. Text that cannot be a key gives NotAKey without touching the store.</summary>
    Task<AbuseKeySave> SaveKeyAsync(string key);

    /// <summary>
    /// Removes the saved key. True when none is saved afterwards; a failed removal keeps HasKey true. Touches only the
    /// key's store: the rolling list and the download accounting are kept.
    /// </summary>
    Task<bool> ClearKeyAsync();

    /// <summary>Checks the address of an address search. Only a public address is ever sent.</summary>
    Task<AbuseCheckResult> CheckAsync(SearchQuery query, CancellationToken ct);

    /// <summary>
    /// The rate limit, today's count and the refusal come from config.db, so they show even beside a list that can't
    /// be used. Sends nothing and never reads the key.
    /// </summary>
    Task<AbuseListStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// Reads neither the key, the settings nor the exceptions, sends and writes nothing, and doesn't wait on the
    /// download gate (the list serialises its own calls). Only the caller's cancellation escapes.
    /// </summary>
    Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct);

    /// <summary>
    /// Every local refusal comes before the key is read or anything is sent. Once a 2xx answer's headers are in, the
    /// download is spent: its body is read and merged even if the caller cancels, and Counted is true.
    /// </summary>
    Task<AbuseDownloadResult> DownloadBlacklistAsync(AbuseDownloadPolicy? policy, CancellationToken ct);

    /// <summary>Removes the rolling list. None, or StorageError when it couldn't be removed.</summary>
    Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct);

    /// <summary>
    /// A family left with no address has its existing file replaced by a header-only one, so FortiGate follows it
    /// down. Refused while the exceptions can't be read; never reads the key. Waits for a download or clear running.
    /// </summary>
    Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct);

    /// <summary>
    /// Reads only the settings, not the list, the exceptions or the key, so it works while they can't be read. Makes no
    /// folder or file and leaves the list as it is. Waits for a download, clear or write still running.
    /// </summary>
    Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct);

    /// <summary>
    /// A report, like the ASN report: no exceptions apply, so above the cap or where one covers an address it differs
    /// from the written feed. Doesn't wait for a download, clear or write; it reads the list as it is before or after.
    /// </summary>
    Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct);

    /// <summary>Rises each time a download merged into the list or Clear list removed it.</summary>
    long ListVersion { get; }

    /// <summary>
    /// Raised after the download gate is free, on the caller's thread, so two calls can deliver theirs out of order:
    /// a listener compares the change's Version with the last it saw, or reads ListVersion. Listeners marshal.
    /// </summary>
    event EventHandler<AbuseListChange>? ListChanged;

    /// <summary>
    /// A snapshot of the call holding the download gate, or null while none does. Set first inside each hold and
    /// cleared once the gate is free, so after a clear it may already name the next call's work.
    /// </summary>
    AbuseWork? WorkUnderWay { get; }

    /// <summary>Raised as <see cref="WorkUnderWay"/> is set and cleared, the clear once the gate is free.</summary>
    event EventHandler? WorkChanged;

    /// <summary>Writes the saved rolling feed again from the list, with no download; takes the download gate.</summary>
    internal Task<RollingFeedRefresh> RefreshFeedAsync(CancellationToken ct) =>
        Task.FromResult(new RollingFeedRefresh(RollingRefreshOutcome.SavedFeedsUnavailable, null, null));
}
