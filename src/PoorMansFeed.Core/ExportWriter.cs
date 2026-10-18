using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace PoorMansFeed.Core;

/// <summary>
/// Writes the ASN report, the Fortigate threat feeds and the clipboard text. Nothing here filters large ranges or
/// prompts; the caller passes the rows to write.
/// </summary>
public static class ExportWriter
{
    public const string FortigateFolderName = "fortigate";

    internal const string IpinfoAttribution = "IP address data powered by IPinfo (https://ipinfo.io)";
    private const string Timestamp = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Writes {folder}/AS{digits}.txt, creating the folder and overwriting. Returns the full path.</summary>
    public static string WriteAsnReport(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows, DateTimeOffset utcNow)
    {
        string digits = ValidateAsn(asn);
        Directory.CreateDirectory(folder);
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

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Writes the Fortigate feeds into {folder}/fortigate/. Returns the files written: empty when the feed is disabled
    /// or no network qualifies.
    /// </summary>
    public static IReadOnlyList<string> WriteFortigateFeeds(string folder, string asn, string asName, IReadOnlyList<NetworkRecord> rows, UserSettings settings, DateTimeOffset utcNow)
    {
        string digits = ValidateAsn(asn);
        var written = new List<string>();
        if (!settings.FortigateEnabled)
            return written;

        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        foreach (NetworkRecord row in rows)
        {
            // Each network becomes a whole feed line, so only a valid address or CIDR is written
            if (!TryFeedEntry(row.Network, out bool isIpv6))
                continue;

            // With IPv6 turned off in Settings no IPv6 network is written, even if the rows still hold one
            if (isIpv6 && settings.IPv6Enabled && settings.FortigateIncludeIPv6)
                ipv6.Add(row.Network);
            else if (!isIpv6 && settings.FortigateIncludeIPv4)
                ipv4.Add(row.Network);
        }

        string dir = Path.Combine(folder, FortigateFolderName);
        string timestamp = FormatTimestamp(utcNow);

        if (settings.FortigateSplitByFamily)
        {
            if (settings.FortigateIncludeIPv4 && ipv4.Count > 0)
                written.Add(WriteFeed(Path.Combine(dir, $"AS{digits}_ipv4.txt"), digits, asName, timestamp, ipv4, "IPv4"));

            if (settings.FortigateIncludeIPv6 && ipv6.Count > 0)
                written.Add(WriteFeed(Path.Combine(dir, $"AS{digits}_ipv6.txt"), digits, asName, timestamp, ipv6, "IPv6"));
        }
        else
        {
            var all = new List<string>();
            if (settings.FortigateIncludeIPv4) all.AddRange(ipv4);
            if (settings.FortigateIncludeIPv6) all.AddRange(ipv6);

            if (all.Count > 0)
                written.Add(WriteFeed(Path.Combine(dir, $"AS{digits}.txt"), digits, asName, timestamp, all,
                    settings.IPv6Enabled ? "IPv4/IPv6" : "IPv4"));
        }

        return written;
    }

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

    private static string WriteFeed(string path, string digits, string asName, string timestamp, List<string> networks, string family)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("# PoorMansFeed - Fortigate Threat Feed").Append(nl);
        sb.Append("# ASN: AS").Append(digits).Append(" - ").Append(SingleLine(asName)).Append(nl);
        sb.Append("# Address Family: ").Append(family).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(networks.Count.ToString(CultureInfo.InvariantCulture)).Append(nl);
        sb.Append("# ").Append(IpinfoAttribution).Append(nl);
        foreach (string network in networks)
            sb.Append(network).Append(nl);

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
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
    private static bool TryFeedEntry([NotNullWhen(true)] string? network, out bool isIpv6)
    {
        isIpv6 = false;
        if (string.IsNullOrEmpty(network) || network.Any(c => !char.IsAsciiHexDigit(c) && c is not ('.' or ':' or '/')))
            return false;

        byte[] start;
        try
        {
            (start, _) = Cidr.Parse(network);
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
