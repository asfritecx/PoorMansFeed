using System.Diagnostics;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.CloudFeeds;

internal static class CloudFeedWriter
{
    /// <summary>Where the feed file of one type and family goes.</summary>
    public static string PathFor(string exportFolder, FeedType type, CloudFeedCheck check, bool ipv6) =>
        Path.Combine(exportFolder, FeedTypes.FolderName(type), $"{check.Description.FileStem}_{(ipv6 ? "ipv6" : "ipv4")}.txt");

    /// <summary>
    /// The files one write has made so far, in write order, and the one it is writing; after a failed write, Attempting
    /// names the file that failed, its type folder included, because that folder is made inside the file's write.
    /// </summary>
    public sealed class Progress
    {
        public List<string> Written { get; } = [];

        public List<string> ModSecurityWritten { get; } = [];

        public string? Attempting { get; set; }
    }

    /// <summary>Writes each ticked type's copy of each family that has lines, type by type, IPv4 before IPv6.</summary>
    public static IReadOnlyList<string> Write(string exportFolder, IReadOnlyList<FeedType> types, CloudFeedCheck check,
                                              IReadOnlyList<FeedLine> lines, ExceptionTally tally, int ipv4Whole, int ipv6Whole,
                                              DateTimeOffset utcNow, Progress progress)
    {
        List<FeedLine> ipv4 = lines.Where(line => !line.IsIpv6).ToList();
        List<FeedLine> ipv6 = lines.Where(line => line.IsIpv6).ToList();

        // Each family is rendered once without its first line, so the copies can differ only in that line
        string? ipv4Text = ipv4.Count > 0
            ? BodyAfterFirstLine(check, ipv4, "IPv4", tally.HeaderLine(includesIpv4: true, includesIpv6: false),
                                 ExportWriter.WholeRangesLine(ipv4Whole, 0), utcNow)
            : null;
        string? ipv6Text = ipv6.Count > 0
            ? BodyAfterFirstLine(check, ipv6, "IPv6", tally.HeaderLine(includesIpv4: false, includesIpv6: true),
                                 ExportWriter.WholeRangesLine(0, ipv6Whole), utcNow)
            : null;

        foreach ((FeedType type, bool isIpv6) in WriteOrder(types))
        {
            string? text = isIpv6 ? ipv6Text : ipv4Text;
            if (text is not null)
                WriteTracked(progress, type, PathFor(exportFolder, type, check, isIpv6), FeedTypes.FirstLine(type) + FeedText.LineEnd + text);
        }

        return progress.Written;
    }

    // Files are written type by type, IPv4 before IPv6, over the ticked types only
    internal static IEnumerable<(FeedType Type, bool Ipv6)> WriteOrder(IReadOnlyList<FeedType> types) =>
        types.SelectMany(type => new[] { (type, false), (type, true) });

    // Attempting stays set when the folder or the file fails, so the caller can name the file that failed
    private static void WriteTracked(Progress progress, FeedType type, string path, string body)
    {
        progress.Attempting = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        progress.Written.Add(WriteFamily(path, body));
        if (type == FeedType.ModSecurity)
            progress.ModSecurityWritten.Add(path);
        progress.Attempting = null;
    }

    private static string WriteFamily(string path, string body)
    {
        string tmp = path + ".tmp";
        try
        {
            TemporaryFile.WriteNew(tmp, body);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static string BodyAfterFirstLine(CloudFeedCheck check, IReadOnlyList<FeedLine> lines, string familyLabel,
                                             string? exceptionsLine, string? wholeRangesLine, DateTimeOffset utcNow)
    {
        int confirmed = 0;
        foreach (FeedLine line in lines)
        {
            if (line.Confirmed)
                confirmed++;
        }

        int unconfirmed = lines.Count - confirmed;
        string timestamp = utcNow.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string datasetDate = check.Dataset.DownloadedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string nl = FeedText.LineEnd;
        var sb = new StringBuilder();
        foreach (string line in check.Description.HeaderLines)
            sb.Append("# ").Append(line).Append(nl);
        sb.Append("# Address Family: ").Append(familyLabel).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(lines.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(confirmed.ToString(CultureInfo.InvariantCulture)).Append(" confirmed by IPinfo, ")
            .Append(unconfirmed.ToString(CultureInfo.InvariantCulture)).Append(" added without confirmation)").Append(nl);
        if (exceptionsLine is not null)
            sb.Append("# ").Append(exceptionsLine).Append(nl);
        if (wholeRangesLine is not null)
            sb.Append("# ").Append(wholeRangesLine).Append(nl);
        sb.Append("# Confirmed against: IPinfo Lite dataset of ").Append(datasetDate).Append(nl);
        sb.Append("# ").Append(ExportWriter.IpinfoAttribution).Append(nl);
        foreach (FeedLine line in lines)
            sb.Append(FeedText.Entry(line.Text)).Append(nl);

        return sb.ToString();
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
            Trace.TraceWarning("Could not delete a temporary feed file.");
        }
    }
}
