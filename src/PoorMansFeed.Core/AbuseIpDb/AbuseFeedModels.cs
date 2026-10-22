namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// Why the AbuseIPDB rolling feed wasn't written. <see cref="None"/> means it was. <see cref="ExceptionsUnreadable"/>:
/// the exceptions list can't be read, so nothing was read or written. <see cref="ListUnreadable"/>:
/// <c>abuseipdb.db</c> isn't a database, is damaged, or isn't this application's list. <see cref="ListNewer"/>: it was
/// written by a newer version. <see cref="StorageError"/>: it couldn't be read now. <see cref="NoDownload"/>: the list
/// has had no successful download (no file, a cleared list, or only failed attempts), so no file or folder was
/// touched and an existing feed keeps its bytes. <see cref="ClockWrong"/>: this computer's clock is more than
/// <see cref="AbuseIpDbLimits.MaxClockDifferenceHours"/> hours before the list's last download, so the header would
/// say the feed was written before its list was downloaded; nothing was written. <see cref="NothingToWrite"/>: the
/// list has a download, but neither family has an address to write (none within the keep days, every one removed by
/// an exception, or only IPv6 addresses while IPv6 is off) and neither family's feed file exists; from Empty feed,
/// neither rolling feed file exists in the current export folder's <c>fortigate</c> folder (a file in any other folder
/// isn't looked for). <see cref="FolderUnavailable"/>: the <c>fortigate</c> folder couldn't be created.
/// <see cref="WriteFailed"/>: a feed file couldn't be written, or something unexpected failed.
/// </summary>
public enum AbuseFeedWriteFailure
{
    None,
    ExceptionsUnreadable,
    ListUnreadable,
    ListNewer,
    StorageError,
    NoDownload,
    ClockWrong,
    NothingToWrite,
    FolderUnavailable,
    WriteFailed,
}

/// <summary>
/// One write of the AbuseIPDB rolling feed. <see cref="Files"/> are the feed files written, IPv4 first.
/// <see cref="LeftOut"/> counts the addresses left out over FortiOS's limit of <see cref="AbuseIpDbLimits.MaxFeedEntries"/>
/// a file, and <see cref="ExceptionRemovals"/> the addresses an exception removed, both over the two families.
/// <see cref="EmptiedFiles"/> names the files among <see cref="Files"/> written with no address: a family with nothing to
/// write whose file already exists gets a header-only file, so FortiGate follows the list down to empty, and a family
/// that never had a file gets none. A refusal before the addresses are chosen carries no files, zero counts and no
/// emptied files. Every outcome after they are chosen keeps both families' left-out and removal counts, written or
/// not: <see cref="AbuseFeedWriteFailure.NothingToWrite"/> and <see cref="AbuseFeedWriteFailure.FolderUnavailable"/>
/// with no files, and a <see cref="AbuseFeedWriteFailure.WriteFailed"/> from a file write naming the files written
/// before it with their address counts. Empty feed writes no address, so its counts are all zero; its files are the
/// ones emptied, named in both <see cref="Files"/> and <see cref="EmptiedFiles"/>. <see cref="FailedFile"/> is the feed
/// file whose write failed, null when no file's write failed, and <see cref="Files"/> then lists those done before it.
/// <see cref="OldFilesLeft"/> (null for none) names, on a failed write, the feed files left as they were, which FortiGate
/// still reads: the failed file when its earlier copy is still there, whole, because each file is replaced by a move,
/// then each file of the same write after it that was never tried and exists.
/// </summary>
public sealed record AbuseFeedWriteResult(AbuseFeedWriteFailure Failure, IReadOnlyList<string> Files,
                                          int IPv4Written, int IPv6Written, int LeftOut, int ExceptionRemovals,
                                          IReadOnlyList<string> EmptiedFiles, string? FailedFile = null,
                                          IReadOnlyList<string>? OldFilesLeft = null);
