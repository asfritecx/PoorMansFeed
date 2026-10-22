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
    /// The rolling list now, and when a download may be sent again. The counts are of the addresses listed within the keep
    /// days of the earlier of now and the last download, so a clock set ahead changes nothing; while this computer's clock
    /// is more than a day behind the last download they are withheld as 0 with ClockBehind, which doesn't mean the list
    /// is empty, and the rest is kept. The rate limit, today's count and the refusal come from the download accounting in
    /// config.db, so they are given even beside a list that is newer, unreadable or can't be read now, whose counts, last
    /// download and generation are then empty. While no config store is open the accounting is unknown, so any other
    /// list gives StorageError with nothing filled, as a download would. Sends nothing and never reads the key.
    /// </summary>
    Task<AbuseListStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// The addresses the last download counted as new, as the merge kept them in the list, each row judged again.
    /// NoDownload while the list has had no download, and NotKnown when the set the list holds wasn't made by its last
    /// download. Never reads the key, the settings or the exceptions; sends nothing; writes nothing; doesn't wait on the
    /// download gate (the list serialises its own calls). Only the caller's cancellation escapes.
    /// </summary>
    Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct);

    /// <summary>
    /// Downloads the blacklist into the rolling list. Nothing is sent while the list can't be read or written
    /// (ListNewer, ListUnreadable, StorageError), the download accounting isn't available (StorageError), no downloads
    /// are left (LimitReached), this computer's clock is more than a day behind the last download (ClockBehind) or the
    /// data folder's disk is nearly full (DiskNearlyFull), and the key is read only when a request will be made. Only a
    /// 2xx answer counts as a download.
    /// Once a 2xx answer's headers are in, the download is spent: its body is read under the request's timeout alone and
    /// merged even if the caller cancels meanwhile, and the result's Counted is true whatever came after it.
    /// </summary>
    Task<AbuseDownloadResult> DownloadBlacklistAsync(CancellationToken ct);

    /// <summary>Removes the rolling list. None, or StorageError when it couldn't be removed.</summary>
    Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct);

    /// <summary>
    /// Writes the rolling feed into the export folder's <c>fortigate</c> folder, one file per family, from the addresses
    /// listed within the keep days of the earlier of now and the last download, less those an exception covers. A
    /// family left with no address replaces its existing file with a header-only one, and gets no file when it has none.
    /// Nothing is read or written while the exceptions can't be read, and no file or folder is touched while the list
    /// has had no download. Sends nothing and never reads the key. Waits for a download or clear still running.
    /// </summary>
    Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct);

    /// <summary>
    /// Replaces each rolling feed file that exists in the export folder's <c>fortigate</c> folder, IPv4 and IPv6 whether
    /// or not IPv6 is on, with a header-only one (<c># Entries: 0</c>), so FortiGate stops blocking on its next fetch.
    /// No folder is created and no file is made where none exists; NothingToWrite when neither file exists in the
    /// current export folder (a file in any other folder isn't looked for). Reads only
    /// the settings: not the list, the exceptions or the key, so it works while either can't be read. Sends nothing and
    /// leaves the list as it is, so a later Write feed writes its addresses again. Waits for a download, clear or write
    /// still running.
    /// </summary>
    Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct);

    /// <summary>
    /// Looks up the rolling list's addresses within the keep days (counted back from the earlier of now and the last
    /// download) and the IPv6 switch, the newest 131,072 per family by the feed's ranking, with no exceptions applied,
    /// because this is a report, like the ASN report: above the cap, or where exceptions cover listed addresses, it
    /// differs from the written feed. It looks them up in the local IPinfo dataset, a chunk at a time, and groups them
    /// by IPinfo's ASN, with the addresses IPinfo holds with no ASN and those it doesn't hold counted apart. The
    /// countries are IPinfo's. A change of the dataset or the IPv6 switch during the lookup starts it again once.
    /// Nothing is looked up while the list has had no download, or while this computer's clock is more than a day behind
    /// the last download (ClockWrong). Reads neither the exceptions nor the key, sends nothing and writes nothing. Doesn't
    /// wait for a download, clear or write still running: it reads the list as it is before or after one.
    /// </summary>
    Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct);
}
