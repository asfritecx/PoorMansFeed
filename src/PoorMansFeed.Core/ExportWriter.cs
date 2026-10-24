using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core;

/// <summary>
/// Files lists every feed file written, FortiGate's first; ModSecurityFiles the ones in its folder. OldFilesLeft lists
/// earlier copies left in place because exceptions or the whole-range rule emptied their family; readers still use them.
/// </summary>
public sealed record FeedWrite(IReadOnlyList<string> Files, int ExceptionCuts, int ExceptionRemovals,
                               IReadOnlyList<string> OldFilesLeft, IReadOnlyList<string> ModSecurityFiles,
                               int WholeRangesLeftOut);

/// <summary>
/// OldFilesLeft lists earlier copies still in place, in write order: the feeds the exceptions emptied, the failed file
/// (whole, as only a move replaces it), then each feed of the call after it that was never tried.
/// </summary>
internal sealed class ExportFileException(string filePath, IReadOnlyList<string> writtenBefore,
                                          IReadOnlyList<string> modSecurityWrittenBefore,
                                          IReadOnlyList<string> oldFilesLeft, Exception innerException)
    : IOException(innerException.Message, innerException)
{
    public string FilePath { get; } = filePath;

    public IReadOnlyList<string> WrittenBefore { get; } = writtenBefore;

    public IReadOnlyList<string> ModSecurityWrittenBefore { get; } = modSecurityWrittenBefore;

    public IReadOnlyList<string> OldFilesLeft { get; } = oldFilesLeft;
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
            throw new ExportFileException(path, [], [], File.Exists(path) ? [path] : [], ex);
        }

        return path;
    }

    /// <summary>
    /// An unreadable list throws InvalidOperationException rather than cutting nothing, so no caller can skip the cut;
    /// callers refuse it first with their own typed failure. With the feed off or no type ticked the list is not checked.
    /// </summary>
    public static FeedWrite WriteFeeds(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows, UserSettings settings, DateTimeOffset utcNow, ExceptionList exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        string digits = ValidateAsn(asn);
        IReadOnlyList<FeedType> types = TypesToWrite(settings);
        if (types.Count == 0)
            return new FeedWrite([], 0, 0, [], [], 0);

        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        int ipv4Wanted = 0;
        int ipv6Wanted = 0;
        int ipv4Whole = 0;
        int ipv6Whole = 0;
        var tally = new ExceptionTally();
        foreach (NetworkRecord row in rows)
        {
            // Each network becomes a whole feed line, so only a valid address or CIDR is written
            if (!TryFeedEntry(row.Network, out bool isIpv6, out byte[] start, out byte[] end))
                continue;

            if (!IsWanted(isIpv6, settings))
                continue;

            if (isIpv6) ipv6Wanted++; else ipv4Wanted++;

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

        // Each file's text is made once, without its first line, so the copies of one feed differ only in that line
        var bodies = new List<(string Name, string Body)>();
        var emptied = new List<string>();
        if (settings.FortigateSplitByFamily)
        {
            if (settings.FortigateIncludeIPv4 && ipv4.Count > 0)
                bodies.Add((FeedName(digits, "_ipv4"), FeedBody(digits, asName, timestamp, ipv4, "IPv4",
                    tally.HeaderLine(includesIpv4: true, includesIpv6: false), WholeRangesLine(ipv4Whole, 0))));
            else if (ipv4Wanted > 0)
                emptied.Add(FeedName(digits, "_ipv4"));

            if (settings.FortigateIncludeIPv6 && ipv6.Count > 0)
                bodies.Add((FeedName(digits, "_ipv6"), FeedBody(digits, asName, timestamp, ipv6, "IPv6",
                    tally.HeaderLine(includesIpv4: false, includesIpv6: true), WholeRangesLine(0, ipv6Whole))));
            else if (ipv6Wanted > 0)
                emptied.Add(FeedName(digits, "_ipv6"));
        }
        else
        {
            var all = new List<string>();
            if (settings.FortigateIncludeIPv4) all.AddRange(ipv4);
            if (settings.FortigateIncludeIPv6) all.AddRange(ipv6);

            if (all.Count > 0)
                bodies.Add((FeedName(digits, ""), FeedBody(digits, asName, timestamp, all, settings.IPv6Enabled ? "IPv4/IPv6" : "IPv4",
                    tally.HeaderLine(includesIpv4: true, includesIpv6: true), WholeRangesLine(ipv4Whole, ipv6Whole))));
            else if (ipv4Wanted + ipv6Wanted > 0)
                emptied.Add(FeedName(digits, ""));
        }

        var oldFilesLeft = new List<string>();

        // A family with nothing left to write gets no file, as for one with no networks at all. A copy from an earlier
        // export is left in place, so the caller is told: its reader keeps using it.
        void NoteOldFile(string path)
        {
            if (File.Exists(path))
                oldFilesLeft.Add(path);
        }

        // The emptied families are noted before any write, so a failed write names them whichever file it was
        var plan = new List<(string Path, FeedType Type, string Body)>();
        foreach (FeedType type in types)
        {
            string dir = Path.Combine(folder, FeedTypes.FolderName(type));
            foreach (string name in emptied)
                NoteOldFile(Path.Combine(dir, name));
            foreach ((string name, string body) in bodies)
                plan.Add((Path.Combine(dir, name), type, body));
        }

        var written = new List<string>();
        var modSecurityWritten = new List<string>();

        // The failed file's earlier copy is still whole, since its move never ran, and each feed after it is never
        // tried, so its earlier copy is left too and its reader keeps using it.
        for (int i = 0; i < plan.Count; i++)
        {
            try
            {
                WriteFeed(plan[i].Path, FeedTypes.FirstLine(plan[i].Type) + FeedText.LineEnd + plan[i].Body);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                for (int untried = i; untried < plan.Count; untried++)
                    NoteOldFile(plan[untried].Path);
                throw new ExportFileException(plan[i].Path, [.. written], [.. modSecurityWritten], [.. oldFilesLeft], ex);
            }

            written.Add(plan[i].Path);
            if (plan[i].Type == FeedType.ModSecurity)
                modSecurityWritten.Add(plan[i].Path);
        }

        return new FeedWrite(written, tally.Cut, tally.Removed, oldFilesLeft, modSecurityWritten, ipv4Whole + ipv6Whole);
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

    /// <summary>
    /// The feed files <see cref="WriteFeeds"/> would write or note that exist, in write order (none with the feed off or
    /// no type ticked). A failed report names them, because no feed is tried after it and their readers keep using them.
    /// </summary>
    internal static IReadOnlyList<string> ExistingFeedFiles(string folder, string asn, IReadOnlyList<NetworkRecord> rows, UserSettings settings)
    {
        string digits = ValidateAsn(asn);
        IReadOnlyList<FeedType> types = TypesToWrite(settings);
        if (types.Count == 0)
            return [];

        bool ipv4Wanted = false;
        bool ipv6Wanted = false;
        foreach (NetworkRecord row in rows)
        {
            if (!TryFeedEntry(row.Network, out bool isIpv6, out _, out _) || !IsWanted(isIpv6, settings))
                continue;
            if (isIpv6) ipv6Wanted = true; else ipv4Wanted = true;
        }

        var names = new List<string>();
        if (settings.FortigateSplitByFamily)
        {
            if (ipv4Wanted)
                names.Add(FeedName(digits, "_ipv4"));
            if (ipv6Wanted)
                names.Add(FeedName(digits, "_ipv6"));
        }
        else if (ipv4Wanted || ipv6Wanted)
        {
            names.Add(FeedName(digits, ""));
        }

        return types.SelectMany(type => names.Select(name => Path.Combine(folder, FeedTypes.FolderName(type), name)))
                    .Where(File.Exists)
                    .ToList();
    }

    private static IReadOnlyList<FeedType> TypesToWrite(UserSettings settings) =>
        settings.FortigateEnabled ? FeedTypes.Ticked(settings) : [];

    private static string FeedName(string digits, string suffix) => $"AS{digits}{suffix}.txt";

    // With IPv6 turned off in Settings no IPv6 network is written, even if the rows still hold one
    private static bool IsWanted(bool isIpv6, UserSettings settings) =>
        isIpv6 ? settings.IPv6Enabled && settings.FortigateIncludeIPv6 : settings.FortigateIncludeIPv4;

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

    private static string FeedBody(string digits, string asName, string timestamp, List<string> networks, string family,
                                    string? exceptionsLine, string? wholeRangesLine)
    {
        string nl = FeedText.LineEnd;
        var sb = new StringBuilder();
        sb.Append("# ASN: AS").Append(digits).Append(" - ").Append(SingleLine(asName)).Append(nl);
        sb.Append("# Address Family: ").Append(family).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(networks.Count.ToString(CultureInfo.InvariantCulture)).Append(nl);
        if (exceptionsLine is not null)
            sb.Append("# ").Append(exceptionsLine).Append(nl);
        if (wholeRangesLine is not null)
            sb.Append("# ").Append(wholeRangesLine).Append(nl);
        sb.Append("# ").Append(IpinfoAttribution).Append(nl);
        foreach (string network in networks)
            sb.Append(FeedText.Entry(network)).Append(nl);
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
        if (digits.Length == 0 || !digits.All(c => c is >= '0' and <= '9'))
            throw new ArgumentException($"Invalid ASN for export: '{asn}'.", nameof(asn));
        return digits;
    }

    /// <summary>
    /// The dataset is not trusted to hold only networks: the entry must parse with Cidr.Parse, use only hex digits,
    /// '.', ':' and '/', and match its family, so no line break or other character reaches a feed.
    /// </summary>
    private static bool TryFeedEntry([NotNullWhen(true)] string? network, out bool isIpv6, out byte[] start, out byte[] end)
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

    private static string FormatTimestamp(DateTimeOffset utcNow) =>
        utcNow.ToUniversalTime().ToString(Timestamp, CultureInfo.InvariantCulture);
}
