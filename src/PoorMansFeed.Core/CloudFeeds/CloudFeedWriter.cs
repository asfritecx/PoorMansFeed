using System.Diagnostics;
using System.Globalization;
using System.Text;
using PoorMansFeed.Core;

namespace PoorMansFeed.Core.CloudFeeds;

internal static class CloudFeedWriter
{
    public static IReadOnlyList<string> Write(string exportFolder, CloudFeedCheck check, IReadOnlyList<CheckedRange> ranges, DateTimeOffset utcNow)
    {
        var written = new List<string>();
        string directory = Path.Combine(exportFolder, ExportWriter.FortigateFolderName);
        Directory.CreateDirectory(directory);

        List<CheckedRange> ipv4 = ranges.Where(range => !range.Prefix.Block.IsIpv6).ToList();
        List<CheckedRange> ipv6 = ranges.Where(range => range.Prefix.Block.IsIpv6).ToList();
        if (ipv4.Count > 0)
            written.Add(WriteFamily(directory, check, ipv4, "ipv4", "IPv4", utcNow));
        if (ipv6.Count > 0)
            written.Add(WriteFamily(directory, check, ipv6, "ipv6", "IPv6", utcNow));

        return written;
    }

    private static string WriteFamily(string directory, CloudFeedCheck check, IReadOnlyList<CheckedRange> ranges,
                                      string familyFile, string familyLabel, DateTimeOffset utcNow)
    {
        string path = Path.Combine(directory, $"{check.Description.FileStem}_{familyFile}.txt");
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, Body(check, ranges, familyLabel, utcNow), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch
        {
            DeleteQuietly(tmp);
            throw;
        }
    }

    private static string Body(CloudFeedCheck check, IReadOnlyList<CheckedRange> ranges, string familyLabel, DateTimeOffset utcNow)
    {
        int confirmed = 0;
        foreach (CheckedRange range in ranges)
        {
            if (range.Status == RangeConfirmation.Confirmed)
                confirmed++;
        }

        int unconfirmed = ranges.Count - confirmed;
        string timestamp = utcNow.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string datasetDate = check.Dataset.DownloadedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("# PoorMansFeed - Fortigate Threat Feed").Append(nl);
        foreach (string line in check.Description.HeaderLines)
            sb.Append("# ").Append(line).Append(nl);
        sb.Append("# Address Family: ").Append(familyLabel).Append(nl);
        sb.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        sb.Append("# Entries: ").Append(ranges.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(confirmed.ToString(CultureInfo.InvariantCulture)).Append(" confirmed by IPinfo, ")
            .Append(unconfirmed.ToString(CultureInfo.InvariantCulture)).Append(" added without confirmation)").Append(nl);
        sb.Append("# Confirmed against: IPinfo Lite dataset of ").Append(datasetDate).Append(nl);
        sb.Append("# ").Append(ExportWriter.IpinfoAttribution).Append(nl);
        foreach (CheckedRange range in ranges)
            sb.Append(range.Prefix.Block.Text).Append(nl);

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
