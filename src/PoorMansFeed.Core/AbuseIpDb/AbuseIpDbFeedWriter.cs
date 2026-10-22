using System.Diagnostics;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// One family of the rolling feed, as it will be written: the addresses in byte order, how many an exception removed,
/// and how many were left out over the cap.
/// </summary>
internal sealed record AbuseFeedFamily(bool IsIpv6, IReadOnlyList<PublicAddress> Addresses, int ExceptionRemovals, int LeftOut);

/// <summary>
/// One family as a report selects it (the offending networks), not a feed: the addresses in byte order and how many
/// were left out over the cap, with no exception applied. It can't be written, because it is its own type:
/// <see cref="AbuseIpDbFeedWriter.WriteFamily"/> takes only an <see cref="AbuseFeedFamily"/>. Build an
/// <see cref="AbuseFeedFamily"/> with addresses only through <see cref="AbuseIpDbFeedWriter.Select"/>, which applies the
/// exceptions: one built from a report's addresses would write a feed with none cut.
/// </summary>
internal sealed record AbuseReportFamily(bool IsIpv6, IReadOnlyList<PublicAddress> Addresses, int LeftOut);

/// <summary>
/// What a rolling feed's header says: the keep days, when the feed was written, and when the list it was written from
/// was downloaded and generated (null when the list doesn't know). Null keep days mean a file emptied by hand (Empty
/// feed): its header says so in place of the keep days.
/// </summary>
internal sealed record AbuseFeedHeader(int? KeepDays, DateTimeOffset WrittenAt, DateTimeOffset? LastDownloadAt,
                                       DateTimeOffset? ListGeneratedAt);

/// <summary>
/// Chooses and writes the AbuseIPDB rolling feed: one Fortigate threat feed file per family, of bare addresses. Nothing
/// in it comes from IPinfo, so it carries no IPinfo line, and no country is written.
/// </summary>
internal static class AbuseIpDbFeedWriter
{
    /// <summary>The start of both file names.</summary>
    public const string FileStem = "AbuseIPDB_rolling";

    /// <summary>The family's feed file: <c>{exportFolder}/fortigate/AbuseIPDB_rolling_ipv4.txt</c> or <c>_ipv6.txt</c>.</summary>
    public static string PathFor(string exportFolder, bool ipv6)
    {
        ArgumentNullException.ThrowIfNull(exportFolder);
        return Path.Combine(exportFolder, ExportWriter.FortigateFolderName, $"{FileStem}_{(ipv6 ? "ipv6" : "ipv4")}.txt");
    }

    /// <summary>
    /// One family's feed: the entries of that family, less those an exception covers, so a covered address takes no
    /// place under the cap. When more than <paramref name="cap"/> remain, the most recently listed are kept: last seen
    /// latest first, then the higher score, then the later last report (none last), then the lower address bytes, so
    /// the choice is the same whatever the input order. What is kept is then sorted by address bytes. Throws when the
    /// exceptions are unreadable, so callers check <see cref="ExceptionList.IsReadable"/> first.
    /// </summary>
    public static AbuseFeedFamily Select(IEnumerable<AbuseListEntry> entries, ExceptionList exceptions, bool ipv6,
                                         int cap = AbuseIpDbLimits.MaxFeedEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        List<Candidate> kept = OfFamily(entries, ipv6);
        // Before the cap, so a covered address takes no place under it
        int removed = kept.RemoveAll(candidate => exceptions.Covers(candidate.Bytes));
        int leftOut = Rank(kept, cap);
        return new AbuseFeedFamily(ipv6, kept.Select(candidate => candidate.Entry.Address).ToList(), removed, leftOut);
    }

    /// <summary>
    /// <see cref="Select"/> with no exceptions, for the offending-networks report: the entries of that family, at most
    /// <paramref name="cap"/> of them by the same ranking, sorted by address bytes. Exceptions apply to feeds only, and
    /// the <see cref="AbuseReportFamily"/> it returns can't be written as one.
    /// </summary>
    public static AbuseReportFamily SelectForReport(IEnumerable<AbuseListEntry> entries, bool ipv6,
                                                    int cap = AbuseIpDbLimits.MaxFeedEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);

        List<Candidate> kept = OfFamily(entries, ipv6);
        int leftOut = Rank(kept, cap);
        return new AbuseReportFamily(ipv6, kept.Select(candidate => candidate.Entry.Address).ToList(), leftOut);
    }

    /// <summary>The entries of one family, in input order, with their bytes.</summary>
    private static List<Candidate> OfFamily(IEnumerable<AbuseListEntry> entries, bool ipv6)
    {
        int length = ipv6 ? 16 : 4;
        var kept = new List<Candidate>();
        foreach (AbuseListEntry entry in entries)
        {
            // PublicAddress.Bytes is a new copy on each read, so it is read once here
            byte[] bytes = entry.Address.Bytes;
            if (bytes.Length == length)
                kept.Add(new Candidate(entry, bytes));
        }
        return kept;
    }

    /// <summary>
    /// The ranking both selections share: over <paramref name="cap"/>, keeps the most recently listed
    /// (<see cref="ByRecency"/>), then sorts what is kept by bytes. Returns how many were left out.
    /// </summary>
    private static int Rank(List<Candidate> kept, int cap)
    {
        int leftOut = 0;
        if (kept.Count > cap)
        {
            kept.Sort(ByRecency);
            leftOut = kept.Count - cap;
            kept.RemoveRange(cap, leftOut);
        }

        kept.Sort(ByBytes);
        return leftOut;
    }

    /// <summary>
    /// Writes one family to its file through <c>{path}.tmp</c> and a move, and returns the path. A family with no
    /// address gives a header-only file (<c># Entries: 0</c>). The <c>fortigate</c> folder must exist. IOException and
    /// UnauthorizedAccessException escape, with the <c>.tmp</c> file deleted. The <c>.tmp</c> is made new, so a link
    /// planted there is never written through.
    /// </summary>
    public static string WriteFamily(string exportFolder, AbuseFeedFamily family, AbuseFeedHeader header)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(header);

        string path = PathFor(exportFolder, family.IsIpv6);
        string tmp = path + ".tmp";
        try
        {
            TemporaryFile.WriteNew(tmp, Body(family, header));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static string Body(AbuseFeedFamily family, AbuseFeedHeader header)
    {
        string nl = Environment.NewLine;
        var text = new StringBuilder();
        text.Append("# PoorMansFeed - Fortigate Threat Feed").Append(nl);
        text.Append("# Source: AbuseIPDB blacklist (https://www.abuseipdb.com), rolling list").Append(nl);
        text.Append("# For your own firewalls only: AbuseIPDB's terms don't allow this data to be republished.").Append(nl);
        if (header.KeepDays is int keepDays)
        {
            text.Append("# Kept: addresses listed within ").Append(Number(keepDays))
                .Append(keepDays == 1 ? " day" : " days").Append(" of the last download").Append(nl);
        }
        else
        {
            text.Append("# Emptied by hand: no address is listed until the feed is written again").Append(nl);
        }
        text.Append("# Address Family: ").Append(family.IsIpv6 ? "IPv6" : "IPv4").Append(nl);
        text.Append("# Generated: ").Append(Utc(header.WrittenAt)).Append(nl);
        if (header.LastDownloadAt is DateTimeOffset downloaded)
        {
            text.Append("# Last download: ").Append(Utc(downloaded));
            if (header.ListGeneratedAt is DateTimeOffset generated)
                text.Append(" (list generated ").Append(Utc(generated)).Append(')');
            text.Append(nl);
        }
        text.Append("# Entries: ").Append(Number(family.Addresses.Count)).Append(nl);
        if (family.ExceptionRemovals > 0)
        {
            text.Append("# Exceptions: ").Append(Number(family.ExceptionRemovals))
                .Append(family.ExceptionRemovals == 1 ? " address removed" : " addresses removed").Append(nl);
        }
        if (family.LeftOut > 0)
        {
            text.Append("# Left out: ").Append(Number(family.LeftOut))
                .Append(family.LeftOut == 1 ? " address" : " addresses")
                .Append(" over FortiOS's ").Append(Number(AbuseIpDbLimits.MaxFeedEntries)).Append(" limit").Append(nl);
        }

        foreach (PublicAddress address in family.Addresses)
            text.Append(address.Text).Append(nl);
        return text.ToString();
    }

    private static string Utc(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Last seen latest first, then the higher score, then the later last report (none last), then the lower bytes.</summary>
    private static int ByRecency(Candidate a, Candidate b)
    {
        int order = b.Entry.LastSeen.CompareTo(a.Entry.LastSeen);
        if (order != 0)
            return order;
        order = b.Entry.Score.CompareTo(a.Entry.Score);
        if (order != 0)
            return order;
        order = (a.Entry.LastReportedAt, b.Entry.LastReportedAt) switch
        {
            (DateTimeOffset x, DateTimeOffset y) => y.CompareTo(x),
            (DateTimeOffset, null) => -1,
            (null, DateTimeOffset) => 1,
            _ => 0,
        };
        return order != 0 ? order : ByBytes(a, b);
    }

    /// <summary>Big-endian bytes of one length compare as numbers.</summary>
    private static int ByBytes(Candidate a, Candidate b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes);

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete a temporary feed file.");
        }
    }

    private readonly record struct Candidate(AbuseListEntry Entry, byte[] Bytes);
}
