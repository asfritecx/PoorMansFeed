using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core;

/// <summary>
/// Files lists every feed file written, FortiGate's first, and Changes each one's entries against the file it replaced;
/// EmptiedFiles, those rewritten header-only as exceptions, the whole-range rule or left-out large ranges emptied them.
/// </summary>
public sealed record FeedWrite(IReadOnlyList<string> Files, int ExceptionCuts, int ExceptionRemovals,
                               IReadOnlyList<string> EmptiedFiles, IReadOnlyList<string> ModSecurityFiles,
                               int WholeRangesLeftOut, IReadOnlyList<FeedFileChange> Changes);

/// <summary>
/// OldFilesLeft lists earlier copies still in place, in write order: the failed file (whole, as only a move replaces
/// it), then each feed of the call after it that was never tried.
/// </summary>
internal sealed class ExportFileException(string filePath, IReadOnlyList<string> writtenBefore,
                                          IReadOnlyList<string> modSecurityWrittenBefore,
                                          IReadOnlyList<string> oldFilesLeft, IReadOnlyList<FeedFileChange> changesBefore,
                                          Exception innerException)
    : IOException(innerException.Message, innerException)
{
    public string FilePath { get; } = filePath;

    public IReadOnlyList<string> WrittenBefore { get; } = writtenBefore;

    public IReadOnlyList<string> ModSecurityWrittenBefore { get; } = modSecurityWrittenBefore;

    public IReadOnlyList<string> OldFilesLeft { get; } = oldFilesLeft;

    public IReadOnlyList<FeedFileChange> ChangesBefore { get; } = changesBefore;
}

/// <summary>
/// Writes the ASN report, the feeds of each ticked type and the clipboard text. Nothing here filters large ranges or
/// prompts; the caller passes the rows to write.
/// </summary>
public static class ExportWriter
{
    public const string FortigateFolderName = "fortigate";
    public const string ModSecurityFolderName = "modsecurity";

    internal const string IpinfoAttribution = "IP address data powered by IPinfo (https://ipinfo.io)";
    private const string Timestamp = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Writes {folder}/AS{digits}.txt, creating the folder and replacing the file by a move. Returns the full path.
    /// </summary>
    public static string WriteAsnReport(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows, DateTimeOffset utcNow)
    {
        string digits = ValidateAsn(asn);
        string path = Path.Combine(folder, $"AS{digits}.txt");

        string nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("ASN:       AS").Append(digits).Append(nl);
        sb.Append("AS Name:   ").Append(SingleLine(asName)).Append(nl);
        sb.Append("Generated: ").Append(FormatTimestamp(utcNow)).Append(" UTC").Append(nl);
        sb.Append("Networks:  ").Append(rows.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(nl);
        sb.Append(new string('-', 80)).Append(nl);
        sb.Append(nl);

        string[] headers = ["Network", "Country", "CC", "Continent", "AS Domain"];
        var cells = rows.Select(r => new[]
        {
            SingleLine(r.Network),
            SingleLine(r.Country),
            SingleLine(r.CountryCode),
            SingleLine(r.Continent),
            SingleLine(r.AsDomain)
        }).ToList();

        int[] widths = headers.Select(h => h.Length).ToArray();
        foreach (string[] row in cells)
        {
            for (int i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                if (row[i].Length > widths[i])
                    widths[i] = row[i].Length;
            }
        }

        string separator = "+-" + string.Join("-+-", widths.Select(w => new string('-', w))) + "-+";
        string FormatRow(string[] line) =>
            "| " + string.Join(" | ", line.Select((c, i) => i < widths.Length ? c.PadRight(widths[i]) : c)) + " |";

        sb.Append(separator).Append(nl);
        sb.Append(FormatRow(headers)).Append(nl);
        sb.Append(separator).Append(nl);
        foreach (string[] row in cells)
            sb.Append(FormatRow(row)).Append(nl);
        sb.Append(separator).Append(nl);
        sb.Append("  ").Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(" row(s)").Append(nl);

        sb.Append(nl);
        sb.Append(IpinfoAttribution).Append(nl);

        try
        {
            Directory.CreateDirectory(folder);
            ReplaceFile(path, sb.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportFileException(path, [], [], File.Exists(path) ? [path] : [], [], ex);
        }

        return path;
    }

    /// <summary>
    /// Only a readable list can be passed, so no write skips the cut. Each feed file's old entries are read just before
    /// its write and compared after its move; with no type ticked nothing is read or written.
    /// </summary>
    internal static FeedWrite WriteFeeds(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows,
                                         FeedOptions options, DateTimeOffset utcNow, ReadableExceptions exceptions) =>
        WritePlan(PlanFeeds(folder, asn, asName, rows, options, utcNow, exceptions, datasetHasIPv6: true));

    /// <summary>The files <see cref="WriteFeeds"/> moves, in write order, with the counts its result carries.</summary>
    internal static AsnFeedPlan PlanFeeds(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows,
                                          FeedOptions options, DateTimeOffset utcNow, ReadableExceptions exceptions,
                                          bool datasetHasIPv6)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exceptions);
        string digits = ValidateAsn(asn);
        IReadOnlyList<FeedType> types = options.Types;
        if (types.Count == 0)
            return AsnFeedPlan.Empty;

        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        int ipv4Whole = 0;
        int ipv6Whole = 0;
        var tally = new ExceptionTally();
        foreach (NetworkRecord row in rows)
        {
            // Each network becomes a whole feed line, so only a valid address or CIDR is written
            if (!TryFeedEntry(row.Network, out bool isIpv6, out byte[] start, out byte[] end))
                continue;

            if (!IsWanted(isIpv6, options))
                continue;

            // A range covering every address would block everything, so no feed carries it
            if (CoversEveryAddress(start, end))
            {
                if (isIpv6) ipv6Whole++; else ipv4Whole++;
                continue;
            }

            List<string> family = isIpv6 ? ipv6 : ipv4;
            IReadOnlyList<string>? pieces = exceptions.Cut(start, end);
            tally.Count(isIpv6, pieces);
            if (pieces is null)
                family.Add(row.Network);
            else
                family.AddRange(pieces);
        }

        string timestamp = FormatTimestamp(utcNow);

        // Each file's text is made once, without its first line, so the copies of one feed differ only in that line.
        // Its entry lines are kept with it, so the lines compared with the old file are the lines written.
        var bodies = new List<(string Name, FeedFamilies Families, string Body, string[] Lines)>();
        if (options.SplitByFamily)
        {
            if (options.IncludeIPv4)
            {
                string[] lines = EntryLines(ipv4);
                bodies.Add((FeedName(digits, "_ipv4"), FeedFamilies.IPv4, FeedBody(digits, asName, timestamp, lines, "IPv4",
                    tally.HeaderLine(includesIpv4: true, includesIpv6: false), WholeRangesLine(ipv4Whole, 0)), lines));
            }

            // A dataset built without IPv6 can't say an AS dropped its IPv6 networks, so its old file stays
            if (options.WritesIPv6 && (ipv6.Count > 0 || datasetHasIPv6))
            {
                string[] lines = EntryLines(ipv6);
                bodies.Add((FeedName(digits, "_ipv6"), FeedFamilies.IPv6, FeedBody(digits, asName, timestamp, lines, "IPv6",
                    tally.HeaderLine(includesIpv4: false, includesIpv6: true), WholeRangesLine(0, ipv6Whole)), lines));
            }
        }
        // With IPv4 off the file holds only IPv6, so a dataset built without IPv6 leaves it as it was
        else if (options.IncludeIPv4 || (options.WritesIPv6 && (ipv6.Count > 0 || datasetHasIPv6)))
        {
            var all = new List<string>();
            if (options.IncludeIPv4) all.AddRange(ipv4);
            if (options.IncludeIPv6) all.AddRange(ipv6);

            string[] lines = EntryLines(all);
            // One file for both families whatever the IPv6 switch says, since an old one may hold IPv6 lines
            bodies.Add((FeedName(digits, ""), FeedFamilies.Both,
                FeedBody(digits, asName, timestamp, lines, options.IPv6Enabled ? "IPv4/IPv6" : "IPv4",
                tally.HeaderLine(includesIpv4: true, includesIpv6: true), WholeRangesLine(ipv4Whole, ipv6Whole)), lines));
        }

        // A family the settings write but nothing is left in, whatever emptied it, rewrites its existing file
        // header-only, as FortiGate keeps reading a file left in place; one with no file gets none. Planned first.
        var plan = new List<PlannedFeedFile>();
        foreach (FeedType type in types)
        {
            string dir = Path.Combine(folder, FeedTypes.FolderName(type));
            foreach ((string name, FeedFamilies families, string body, string[] lines) in bodies)
            {
                string path = Path.Combine(dir, name);
                bool isEmptied = lines.Length == 0;
                if (!isEmptied || File.Exists(path))
                    plan.Add(new PlannedFeedFile(path, type, families, body, lines, isEmptied));
            }
        }

        return new AsnFeedPlan(plan, tally.Cut, tally.Removed, ipv4Whole + ipv6Whole);
    }

    /// <summary>The planned files that exist, in write order: the copies a failed write leaves in place.</summary>
    internal static IReadOnlyList<string> ExistingFiles(AsnFeedPlan plan) =>
        plan.Files.Select(file => file.Path).Where(File.Exists).ToList();

    /// <summary>Writes the plan in order; a failed file throws <see cref="ExportFileException"/>.</summary>
    internal static FeedWrite WritePlan(AsnFeedPlan feeds)
    {
        IReadOnlyList<PlannedFeedFile> plan = feeds.Files;

        var written = new List<string>();
        var modSecurityWritten = new List<string>();
        var emptiedWritten = new List<string>();
        var changes = new List<FeedFileChange>();
        var oldFilesLeft = new List<string>();

        // The failed file's earlier copy is still whole, since its move never ran, and each feed after it is never
        // tried, so its earlier copy is left too and its reader keeps using it.
        for (int i = 0; i < plan.Count; i++)
        {
            FeedFileRead before = FeedFileEntries.Read(plan[i].Path);
            string text = plan[i].Text;
            try
            {
                WriteFeed(plan[i].Path, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                for (int untried = i; untried < plan.Count; untried++)
                {
                    if (File.Exists(plan[untried].Path))
                        oldFilesLeft.Add(plan[untried].Path);
                }
                throw new ExportFileException(plan[i].Path, [.. written], [.. modSecurityWritten], [.. oldFilesLeft], [.. changes], ex);
            }

            written.Add(plan[i].Path);
            changes.Add(FeedFileEntries.Compare(plan[i].Path, plan[i].Type, plan[i].Families, before, plan[i].Lines)
                        with { WrittenSha256 = SavedFeedWriteProof.HashOf(text) });
            if (plan[i].Type == FeedType.ModSecurity)
                modSecurityWritten.Add(plan[i].Path);
            if (plan[i].Emptied)
                emptiedWritten.Add(plan[i].Path);
        }

        return new FeedWrite(written, feeds.ExceptionCuts, feeds.ExceptionRemovals, emptiedWritten, modSecurityWritten,
                             feeds.WholeRangesLeftOut, changes);
    }

    /// <summary>The header text for the ranges covering every address a file's family left out; null when none was.</summary>
    internal static string? WholeRangesLine(int ipv4, int ipv6)
    {
        int count = ipv4 + ipv6;
        if (count == 0)
            return null;

        string ranges = ipv4 > 0 && ipv6 > 0 ? "0.0.0.0/0 and ::/0" : ipv4 > 0 ? "0.0.0.0/0" : "::/0";
        return "Left out: " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " range" : " ranges")
               + " covering every address: " + ranges;
    }

    internal static bool CoversEveryAddress(byte[] start, byte[] end) =>
        start.All(b => b == 0x00) && end.All(b => b == 0xFF);

    private static string FeedName(string digits, string suffix) => FeedStem(digits) + suffix + ".txt";

    /// <summary>The name each file of an ASN's feed starts with, and the key its saved feed and lock go by.</summary>
    internal static string FeedStem(string asn) => "AS" + ValidateAsn(asn);

    /// <summary>Whether the key is exactly what <see cref="FeedStem"/> gives: "AS" then ASCII digits.</summary>
    internal static bool IsFeedStem([NotNullWhen(true)] string? key) =>
        key is not null && key.StartsWith("AS", StringComparison.Ordinal) && IsAsnDigits(key[2..]);

    // With IPv6 turned off in Settings no IPv6 network is written, even if the rows still hold one
    private static bool IsWanted(bool isIpv6, FeedOptions options) =>
        isIpv6 ? options.WritesIPv6 : options.IncludeIPv4;

    /// <summary>
    /// Tab-separated text for the clipboard: a header line, then one line per row. CRLF line ends, null fields as "".
    /// </summary>
    public static string BuildClipboardText(IReadOnlyList<NetworkRecord> rows)
    {
        var sb = new StringBuilder();
        sb.Append("Network\tCountry code\tCountry\tASN\tAS name\tAS domain\r\n");
        foreach (NetworkRecord row in rows)
        {
            sb.Append(string.Join('\t',
                row.Network ?? "",
                row.CountryCode ?? "",
                row.Country ?? "",
                row.Asn ?? "",
                row.AsName ?? "",
                row.AsDomain ?? ""));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string[] EntryLines(List<string> networks) => networks.Select(network => FeedText.Entry(network)).ToArray();

    private static string FeedBody(string digits, string asName, string timestamp, string[] lines, string family,
                                    string? exceptionsLine, string? wholeRangesLine)
    {
        string nl = FeedText.LineEnd;
        var sb = new StringBuilder();
        sb.Append("# ASN: AS").Append(digits).Append(" - ").Append(SingleLine(asName)).Append(nl);
        sb.Append("# Address Family: ").Append(family).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(lines.Length.ToString(CultureInfo.InvariantCulture)).Append(nl);
        if (exceptionsLine is not null)
            sb.Append("# ").Append(exceptionsLine).Append(nl);
        if (wholeRangesLine is not null)
            sb.Append("# ").Append(wholeRangesLine).Append(nl);
        sb.Append("# ").Append(IpinfoAttribution).Append(nl);
        foreach (string line in lines)
            sb.Append(line).Append(nl);
        return sb.ToString();
    }

    private static void WriteFeed(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ReplaceFile(path, text);
    }

    // Moving {path}.tmp over the file means a failed write leaves an earlier copy whole and no reader sees a file cut
    // short. The .tmp is made new, so a link planted there is never written through.
    private static void ReplaceFile(string path, string text)
    {
        string tmp = path + ".tmp";
        try
        {
            TemporaryFile.WriteNew(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete a temporary export file.");
        }
    }

    /// <summary>Strips an optional AS prefix and checks the rest is ASCII digits (path safety).</summary>
    private static string ValidateAsn(string asn)
    {
        string digits = asn.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? asn[2..] : asn;
        if (!IsAsnDigits(digits))
            throw new ArgumentException($"Invalid ASN for export: '{asn}'.", nameof(asn));
        return digits;
    }

    private static bool IsAsnDigits(string digits) => digits.Length > 0 && digits.All(c => c is >= '0' and <= '9');

    /// <summary>
    /// The dataset is not trusted to hold only networks: the entry must parse with Cidr.Parse, use only hex digits,
    /// '.', ':' and '/', and match its family, so no line break or other character reaches a feed.
    /// </summary>
    internal static bool TryFeedEntry([NotNullWhen(true)] string? network, out bool isIpv6, out byte[] start, out byte[] end)
    {
        isIpv6 = false;
        start = [];
        end = [];
        if (string.IsNullOrEmpty(network) || network.Any(c => !char.IsAsciiHexDigit(c) && c is not ('.' or ':' or '/')))
            return false;

        try
        {
            (start, end) = Cidr.Parse(network);
        }
        catch (FormatException)
        {
            return false;
        }

        isIpv6 = start.Length == 16;
        return isIpv6 == network.Contains(':');
    }

    /// <summary>
    /// The text with every control character and Unicode line or paragraph separator replaced by a space. Dataset
    /// values are written into single lines (feed headers, report cells), so a line break in one must not start a line.
    /// </summary>
    private static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = (char.IsControl(c) || c is '\u2028' or '\u2029') ? ' ' : c;
            }
        });
    }

    internal static string FormatTimestamp(DateTimeOffset utcNow) =>
        utcNow.ToUniversalTime().ToString(Timestamp, CultureInfo.InvariantCulture);
}
