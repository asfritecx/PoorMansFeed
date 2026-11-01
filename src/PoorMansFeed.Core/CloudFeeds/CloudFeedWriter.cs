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

        public List<string> Emptied { get; } = [];

        public string? Attempting { get; set; }

        public List<FeedFileChange> Changes { get; } = [];
    }

    /// <summary>
    /// The files a write moves, in write order: each family the settings write that has lines, and the existing file of
    /// one with none left, whatever emptied it, made header-only. IPv6 only while the options and the check take it.
    /// </summary>
    public static IReadOnlyList<PlannedFeedFile> Plan(string exportFolder, FeedOptions options, CloudFeedCheck check,
                                                      IReadOnlyList<FeedLine> lines, ExceptionTally tally, int ipv4Whole,
                                                      int ipv6Whole, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<FeedLine> ipv4 = lines.Where(line => !line.IsIpv6).ToList();
        List<FeedLine> ipv6 = lines.Where(line => line.IsIpv6).ToList();
        // The body appends these same strings, so a change is always counted against exactly what was written
        string[] ipv4Entries = ipv4.Select(line => FeedText.Entry(line.Text)).ToArray();
        string[] ipv6Entries = ipv6.Select(line => FeedText.Entry(line.Text)).ToArray();

        // Each family is rendered once without its first line, so the copies can differ only in that line
        string ipv4Text = BodyAfterFirstLine(check, ipv4, ipv4Entries, "IPv4", tally.HeaderLine(includesIpv4: true, includesIpv6: false),
                                             ExportWriter.WholeRangesLine(ipv4Whole, 0), utcNow);
        string ipv6Text = BodyAfterFirstLine(check, ipv6, ipv6Entries, "IPv6", tally.HeaderLine(includesIpv4: false, includesIpv6: true),
                                             ExportWriter.WholeRangesLine(0, ipv6Whole), utcNow);

        var plan = new List<PlannedFeedFile>();
        foreach ((FeedType type, bool isIpv6) in WriteOrder(options.Types))
        {
            if (isIpv6 && !(options.WritesIPv6 && check.IncludesIPv6))
                continue;

            FeedFamilies families = isIpv6 ? FeedFamilies.IPv6 : FeedFamilies.IPv4;
            string path = PathFor(exportFolder, type, check, isIpv6);
            string[] entries = isIpv6 ? ipv6Entries : ipv4Entries;
            if (entries.Length > 0)
            {
                plan.Add(new PlannedFeedFile(path, type, families, isIpv6 ? ipv6Text : ipv4Text, entries, Emptied: false));
                continue;
            }

            // A dataset without IPv6 reads every IPv6 range as not in it, which isn't the source dropping them
            if (isIpv6 && !check.Dataset.IncludesIPv6)
                continue;

            // A family with no file gets none, and one the user deleted stays deleted; never Path.Exists
            if (File.Exists(path))
                plan.Add(new PlannedFeedFile(path, type, families, isIpv6 ? ipv6Text : ipv4Text, [], Emptied: true));
        }

        return plan;
    }

    /// <summary>Writes the plan in order, naming each header-only file in <see cref="Progress.Emptied"/>.</summary>
    public static IReadOnlyList<string> Write(IReadOnlyList<PlannedFeedFile> plan, Progress progress)
    {
        foreach (PlannedFeedFile file in plan)
        {
            WriteTracked(progress, file.Type, file.Families, file.Path, file.Text, file.Lines);
            if (file.Emptied)
                progress.Emptied.Add(file.Path);
        }

        return progress.Written;
    }

    // Files are written type by type, IPv4 before IPv6, over the ticked types only
    internal static IEnumerable<(FeedType Type, bool Ipv6)> WriteOrder(IReadOnlyList<FeedType> types) =>
        types.SelectMany(type => new[] { (type, false), (type, true) });

    // Attempting stays set when the folder or the file fails, so the caller can name the file that failed
    private static void WriteTracked(Progress progress, FeedType type, FeedFamilies families, string path, string body,
                                     IReadOnlyList<string> entries)
    {
        progress.Attempting = path;
        FeedFileRead before = FeedFileEntries.Read(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        progress.Written.Add(WriteFamily(path, body));
        progress.Changes.Add(FeedFileEntries.Compare(path, type, families, before, entries)
                             with { WrittenSha256 = SavedFeedWriteProof.HashOf(body) });
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

    private static string BodyAfterFirstLine(CloudFeedCheck check, IReadOnlyList<FeedLine> lines, IReadOnlyList<string> entries,
                                             string familyLabel, string? exceptionsLine, string? wholeRangesLine, DateTimeOffset utcNow)
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
        foreach (string entry in entries)
            sb.Append(entry).Append(nl);

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
