using System.Diagnostics;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.CloudFeeds;

internal static class CloudFeedWriter
{
    /// <summary>Where the feed file of one family goes.</summary>
    public static string PathFor(string exportFolder, CloudFeedCheck check, bool ipv6) =>
        Path.Combine(exportFolder, ExportWriter.FortigateFolderName, $"{check.Description.FileStem}_{(ipv6 ? "ipv6" : "ipv4")}.txt");

    /// <summary>
    /// The files one write has made so far, and the one it is writing; after a failed write, Attempting names the file
    /// that failed, or is null when the folder failed before any file.
    /// </summary>
    public sealed class Progress
    {
        public List<string> Written { get; } = [];

        public string? Attempting { get; set; }
    }

    public static IReadOnlyList<string> Write(string exportFolder, CloudFeedCheck check, IReadOnlyList<FeedLine> lines,
                                              ExceptionTally tally, DateTimeOffset utcNow, Progress progress)
    {
        Directory.CreateDirectory(Path.Combine(exportFolder, ExportWriter.FortigateFolderName));

        List<FeedLine> ipv4 = lines.Where(line => !line.IsIpv6).ToList();
        List<FeedLine> ipv6 = lines.Where(line => line.IsIpv6).ToList();
        if (ipv4.Count > 0)
            WriteTracked(progress, PathFor(exportFolder, check, ipv6: false), check, ipv4, "IPv4", tally.HeaderLine(includesIpv4: true, includesIpv6: false), utcNow);
        if (ipv6.Count > 0)
            WriteTracked(progress, PathFor(exportFolder, check, ipv6: true), check, ipv6, "IPv6", tally.HeaderLine(includesIpv4: false, includesIpv6: true), utcNow);

        return progress.Written;
    }

    // Attempting stays set when WriteFamily throws, so the caller can name the file that failed
    private static void WriteTracked(Progress progress, string path, CloudFeedCheck check, IReadOnlyList<FeedLine> lines,
                                     string familyLabel, string? exceptionsLine, DateTimeOffset utcNow)
    {
        progress.Attempting = path;
        progress.Written.Add(WriteFamily(path, check, lines, familyLabel, exceptionsLine, utcNow));
        progress.Attempting = null;
    }

    private static string WriteFamily(string path, CloudFeedCheck check, IReadOnlyList<FeedLine> lines,
                                      string familyLabel, string? exceptionsLine, DateTimeOffset utcNow)
    {
        string tmp = path + ".tmp";
        try
        {
            TemporaryFile.WriteNew(tmp, Body(check, lines, familyLabel, exceptionsLine, utcNow));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static string Body(CloudFeedCheck check, IReadOnlyList<FeedLine> lines, string familyLabel, string? exceptionsLine,
                               DateTimeOffset utcNow)
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
        string nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("# PoorMansFeed - Fortigate Threat Feed").Append(nl);
        foreach (string line in check.Description.HeaderLines)
            sb.Append("# ").Append(line).Append(nl);
        sb.Append("# Address Family: ").Append(familyLabel).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(lines.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(confirmed.ToString(CultureInfo.InvariantCulture)).Append(" confirmed by IPinfo, ")
            .Append(unconfirmed.ToString(CultureInfo.InvariantCulture)).Append(" added without confirmation)").Append(nl);
        if (exceptionsLine is not null)
            sb.Append("# ").Append(exceptionsLine).Append(nl);
        sb.Append("# Confirmed against: IPinfo Lite dataset of ").Append(datasetDate).Append(nl);
        sb.Append("# ").Append(ExportWriter.IpinfoAttribution).Append(nl);
        foreach (FeedLine line in lines)
            sb.Append(line.Text).Append(nl);

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
