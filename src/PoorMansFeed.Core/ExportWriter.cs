using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core;

/// <summary>
/// The Fortigate feed files written, and what exceptions did to them: networks cut into smaller ranges and networks
/// removed whole. OldFilesLeft lists the feed files from an earlier export that were not replaced because exceptions
/// removed every network they would hold; FortiGate still reads them.
/// </summary>
public sealed record FortigateFeedWrite(IReadOnlyList<string> Files, int ExceptionCuts, int ExceptionRemovals,
                                        IReadOnlyList<string> OldFilesLeft);

/// <summary>
/// One export file couldn't be written: FilePath names it, and WrittenBefore lists the files of the same call written
/// before it. OldFilesLeft lists the files from an earlier export still in place: every feed the exceptions emptied,
/// then the failed file when an earlier copy of it is still there, whole, because each file is replaced by a move, then
/// each feed of the same call after it that was never tried and has an earlier copy, in write order.
/// </summary>
internal sealed class ExportFileException(string filePath, IReadOnlyList<string> writtenBefore,
                                          IReadOnlyList<string> oldFilesLeft, Exception innerException)
    : IOException(innerException.Message, innerException)
{
    public string FilePath { get; } = filePath;

    public IReadOnlyList<string> WrittenBefore { get; } = writtenBefore;

    public IReadOnlyList<string> OldFilesLeft { get; } = oldFilesLeft;
}

/// <summary>
/// Writes the ASN report, the Fortigate threat feeds and the clipboard text. Nothing here filters large ranges or
/// prompts; the caller passes the rows to write.
/// </summary>
public static class ExportWriter
{
    public const string FortigateFolderName = "fortigate";

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
            throw new ExportFileException(path, [], File.Exists(path) ? [path] : [], ex);
        }

        return path;
    }

    /// <summary>
    /// Writes the Fortigate feeds into {folder}/fortigate/, with every exception cut out of them. Files is empty when
    /// the feed is disabled or no network qualifies. A network no exception touches is written as the dataset spells
    /// it; the pieces of a cut network are written in canonical form, in its place. There is no overload without the
    /// list, and an unreadable list throws InvalidOperationException rather than cutting nothing, so no caller can
    /// skip the cut. Callers refuse an unreadable list first with their own typed failure; with the feed disabled
    /// nothing is cut, so the list is not checked.
    /// </summary>
    public static FortigateFeedWrite WriteFortigateFeeds(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows, UserSettings settings, DateTimeOffset utcNow, ExceptionList exceptions)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        string digits = ValidateAsn(asn);
        var written = new List<string>();
        if (!settings.FortigateEnabled)
            return new FortigateFeedWrite(written, 0, 0, []);

        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        int ipv4Wanted = 0;
        int ipv6Wanted = 0;
        var tally = new ExceptionTally();
        foreach (NetworkRecord row in rows)
        {
            // Each network becomes a whole feed line, so only a valid address or CIDR is written
            if (!TryFeedEntry(row.Network, out bool isIpv6, out byte[] start, out byte[] end))
                continue;

            if (!IsWanted(isIpv6, settings))
                continue;

            if (isIpv6) ipv6Wanted++; else ipv4Wanted++;
            List<string> family = isIpv6 ? ipv6 : ipv4;
            IReadOnlyList<string>? pieces = exceptions.Cut(start, end);
            tally.Count(isIpv6, pieces);
            if (pieces is null)
                family.Add(row.Network);
            else
                family.AddRange(pieces);
        }

        string dir = Path.Combine(folder, FortigateFolderName);
        string timestamp = FormatTimestamp(utcNow);
        var oldFilesLeft = new List<string>();

        // A file whose every network an exception removed is not written, as for a family with no networks at all. A
        // copy from an earlier export is left in place, so the caller is told: FortiGate keeps reading it.
        void NoteOldFile(string path)
        {
            if (File.Exists(path))
                oldFilesLeft.Add(path);
        }

        // The feeds to write, in order. A family with nothing left to write is noted before any write, so a failed write
        // names it whichever file it was
        var plan = new List<(string Path, List<string> Networks, string Family, string? ExceptionsLine)>();
        if (settings.FortigateSplitByFamily)
        {
            string ipv4Path = FeedPath(dir, digits, "_ipv4");
            string ipv6Path = FeedPath(dir, digits, "_ipv6");
            if (settings.FortigateIncludeIPv4 && ipv4.Count > 0)
                plan.Add((ipv4Path, ipv4, "IPv4", tally.HeaderLine(includesIpv4: true, includesIpv6: false)));
            else if (ipv4Wanted > 0)
                NoteOldFile(ipv4Path);

            if (settings.FortigateIncludeIPv6 && ipv6.Count > 0)
                plan.Add((ipv6Path, ipv6, "IPv6", tally.HeaderLine(includesIpv4: false, includesIpv6: true)));
            else if (ipv6Wanted > 0)
                NoteOldFile(ipv6Path);
        }
        else
        {
            var all = new List<string>();
            if (settings.FortigateIncludeIPv4) all.AddRange(ipv4);
            if (settings.FortigateIncludeIPv6) all.AddRange(ipv6);

            string path = FeedPath(dir, digits, "");
            if (all.Count > 0)
                plan.Add((path, all, settings.IPv6Enabled ? "IPv4/IPv6" : "IPv4", tally.HeaderLine(includesIpv4: true, includesIpv6: true)));
            else if (ipv4Wanted + ipv6Wanted > 0)
                NoteOldFile(path);
        }

        // A failed write names its file, the feeds written before it and the old files left, for the caller's line. The
        // failed file's earlier copy, when there is one, is still whole: the move that would replace it never ran. A feed
        // after it is never tried, so its earlier copy is left too, and FortiGate keeps reading it
        for (int i = 0; i < plan.Count; i++)
        {
            try
            {
                written.Add(WriteFeed(plan[i].Path, digits, asName, timestamp, plan[i].Networks, plan[i].Family, plan[i].ExceptionsLine));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                for (int untried = i; untried < plan.Count; untried++)
                    NoteOldFile(plan[untried].Path);
                throw new ExportFileException(plan[i].Path, [.. written], [.. oldFilesLeft], ex);
            }
        }

        return new FortigateFeedWrite(written, tally.Cut, tally.Removed, oldFilesLeft);
    }

    /// <summary>
    /// The feed files <see cref="WriteFortigateFeeds"/> would write or note for the same rows and settings that exist as
    /// files, in write order: each family with a wanted network, in the current layout, and none with the feed off. A
    /// failed report names them, because no feed is tried after it and FortiGate keeps reading them.
    /// </summary>
    internal static IReadOnlyList<string> ExistingFeedFiles(string folder, string asn, IReadOnlyList<NetworkRecord> rows, UserSettings settings)
    {
        string digits = ValidateAsn(asn);
        if (!settings.FortigateEnabled)
            return [];

        bool ipv4Wanted = false;
        bool ipv6Wanted = false;
        foreach (NetworkRecord row in rows)
        {
            if (!TryFeedEntry(row.Network, out bool isIpv6, out _, out _) || !IsWanted(isIpv6, settings))
                continue;
            if (isIpv6) ipv6Wanted = true; else ipv4Wanted = true;
        }

        string dir = Path.Combine(folder, FortigateFolderName);
        var paths = new List<string>();
        if (settings.FortigateSplitByFamily)
        {
            if (ipv4Wanted)
                paths.Add(FeedPath(dir, digits, "_ipv4"));
            if (ipv6Wanted)
                paths.Add(FeedPath(dir, digits, "_ipv6"));
        }
        else if (ipv4Wanted || ipv6Wanted)
        {
            paths.Add(FeedPath(dir, digits, ""));
        }

        return paths.Where(File.Exists).ToList();
    }

    private static string FeedPath(string dir, string digits, string suffix) => Path.Combine(dir, $"AS{digits}{suffix}.txt");

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

    private static string WriteFeed(string path, string digits, string asName, string timestamp, List<string> networks, string family,
                                    string? exceptionsLine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("# PoorMansFeed - Fortigate Threat Feed").Append(nl);
        sb.Append("# ASN: AS").Append(digits).Append(" - ").Append(SingleLine(asName)).Append(nl);
        sb.Append("# Address Family: ").Append(family).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(networks.Count.ToString(CultureInfo.InvariantCulture)).Append(nl);
        if (exceptionsLine is not null)
            sb.Append("# ").Append(exceptionsLine).Append(nl);
        sb.Append("# ").Append(IpinfoAttribution).Append(nl);
        foreach (string network in networks)
            sb.Append(network).Append(nl);

        ReplaceFile(path, sb.ToString());
        return path;
    }

    // Writes the text to {path}.tmp and moves it over the file, so a write that fails leaves an earlier copy whole and
    // no reader sees a file cut short. The .tmp is made new, so a link planted there is never written through. A .tmp
    // left by the failure is deleted.
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
    /// True for a single address or CIDR that Cidr.Parse reads, written with hex digits, '.', ':' and '/' only, whose
    /// family matches its text. The dataset is not trusted to hold only networks. Cidr.Parse already takes only plain
    /// address text; the character check keeps a line break or any other character out of the feed even so.
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
