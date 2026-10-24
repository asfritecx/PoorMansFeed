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
/// A report's selection, with no exception applied; its own type so no <see cref="AbuseFeedBody"/>, all a file write
/// takes, can be made from it. A feed family with addresses comes only from <see cref="AbuseIpDbFeedWriter.Select"/>.
/// </summary>
internal sealed record AbuseReportFamily(bool IsIpv6, IReadOnlyList<PublicAddress> Addresses, int LeftOut);

/// <summary>
/// A family's file below its first line. Only <see cref="Of"/> makes one, from a feed family, so a report's selection
/// never reaches a file write.
/// </summary>
internal sealed class AbuseFeedBody
{
    private AbuseFeedBody(AbuseFeedFamily family, string text)
    {
        Family = family;
        Text = text;
    }

    public AbuseFeedFamily Family { get; }

    public string Text { get; }

    public static AbuseFeedBody Of(AbuseFeedFamily family, AbuseFeedHeader header)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(header);
        return new AbuseFeedBody(family, AbuseIpDbFeedWriter.BodyAfterFirstLine(family, header));
    }
}

/// <summary>
/// Null keep days mean a file emptied by hand (Empty feed), whose header says so in their place. The list's times are
/// null when the list doesn't know them.
/// </summary>
internal sealed record AbuseFeedHeader(int? KeepDays, DateTimeOffset WrittenAt, DateTimeOffset? LastDownloadAt,
                                       DateTimeOffset? ListGeneratedAt);

/// <summary>
/// Chooses and writes the AbuseIPDB rolling feed: one file per family and feed type, of bare addresses. Nothing in it
/// comes from IPinfo, so it carries no IPinfo line, and no country is written.
/// </summary>
internal static class AbuseIpDbFeedWriter
{
    /// <summary>The start of both file names.</summary>
    public const string FileStem = "AbuseIPDB_rolling";

    /// <summary>The family's file in the type's folder, as in <c>fortigate/AbuseIPDB_rolling_ipv4.txt</c>.</summary>
    public static string PathFor(string exportFolder, FeedType type, bool ipv6)
    {
        ArgumentNullException.ThrowIfNull(exportFolder);
        return Path.Combine(exportFolder, FeedTypes.FolderName(type), $"{FileStem}_{(ipv6 ? "ipv6" : "ipv4")}.txt");
    }

    /// <summary>
    /// Over <paramref name="cap"/>, keeps the most recently listed (<see cref="ByRecency"/>), the same in any input
    /// order. Throws when the exceptions are unreadable, so callers check <see cref="ExceptionList.IsReadable"/> first.
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
    /// <see cref="Select"/> with no exceptions, for the offending-networks report: exceptions apply to feeds only, and
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

    /// <summary>One type's copy of a family, written as <see cref="WriteFile"/> writes it.</summary>
    public static string WriteFamily(string exportFolder, FeedType type, AbuseFeedFamily family, AbuseFeedHeader header)
    {
        ArgumentNullException.ThrowIfNull(family);
        string path = PathFor(exportFolder, type, family.IsIpv6);
        WriteFile(path, type, AbuseFeedBody.Of(family, header));
        return path;
    }

    /// <summary>
    /// Writes the type's first line, then <paramref name="body"/>, making the type's folder first. The .tmp is made
    /// new, so a link there is never written through; IO failures escape with the .tmp deleted.
    /// </summary>
    public static void WriteFile(string path, FeedType type, AbuseFeedBody body)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(body);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        try
        {
            TemporaryFile.WriteNew(tmp, FeedTypes.FirstLine(type) + FeedText.LineEnd + body.Text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    /// <summary>A family's file below its first line, the one line that differs between the types' copies.</summary>
    public static string BodyAfterFirstLine(AbuseFeedFamily family, AbuseFeedHeader header)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(header);

        string nl = FeedText.LineEnd;
        var text = new StringBuilder();
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
