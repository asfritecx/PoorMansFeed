using System.Diagnostics;
using System.Text;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>
/// Retires an ASN or cloud saved feed: each file of its stem in either type folder, ticked or not, is rewritten
/// header-only, so FortiGate and ModSecurity stop listing it until the feed is written again.
/// </summary>
internal static class RetiredFeedWriter
{
    internal const string RetiredLine = "# Retired by hand: no address is listed until the feed is written again";

    private sealed record PlannedFile(FeedType Type, FeedFamilies Families, string Path);

    public static RetireResult Retire(IIpInfoService ipinfo, FeedKind kind, string key, TimeProvider time,
                                      string defaultExportFolder)
    {
        ArgumentNullException.ThrowIfNull(ipinfo);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(defaultExportFolder);
        // The key becomes a file name, so a key no write could have saved touches nothing
        if (!IsRetirable(kind, key))
            return RetireResult.Of(RetireFailure.NotRetirable);

        IDisposable hold;
        try
        {
            hold = ipinfo.Records.EnterWrite(key);
        }
        catch (InvalidOperationException)
        {
            // The writes were stopped for the close
            return RetireResult.Of(RetireFailure.WriteFailed);
        }

        using (hold)
        {
            // Read once the lock is held, so a write that ran while this one waited is the one retired
            UserSettings settings = ipinfo.Settings;
            string folder = ExportFolders.Resolve(settings, defaultExportFolder);
            DateTimeOffset now = time.GetUtcNow();
            List<PlannedFile> plan = Plan(folder, kind, key);
            if (plan.Count == 0)
                return RetireResult.Of(RetireFailure.NothingToRetire, folder);

            return WritePlan(ipinfo, kind, key, folder, now, plan);
        }
    }

    private static bool IsRetirable(FeedKind kind, string? key) => kind switch
    {
        FeedKind.Asn => ExportWriter.IsFeedStem(key),
        FeedKind.GoogleCloud or FeedKind.Azure or FeedKind.Aws => FeedDescription.IsSafeStem(key),
        _ => false,
    };

    // Both types whatever is ticked. A folder at a feed's path counts, so it fails the retire rather than reading as
    // nothing; the report at the export folder's root has the combined feed's name, so the type folder is always named
    private static List<PlannedFile> Plan(string folder, FeedKind kind, string key)
    {
        (string Suffix, FeedFamilies Families)[] names = kind == FeedKind.Asn
            ? [("_ipv4", FeedFamilies.IPv4), ("_ipv6", FeedFamilies.IPv6), ("", FeedFamilies.Both)]
            : [("_ipv4", FeedFamilies.IPv4), ("_ipv6", FeedFamilies.IPv6)];
        var plan = new List<PlannedFile>();
        foreach (FeedType type in FeedTypes.All)
        {
            foreach ((string suffix, FeedFamilies families) in names)
            {
                string path = Path.Combine(folder, FeedTypes.FolderName(type), key + suffix + ".txt");
                if (Path.Exists(path))
                    plan.Add(new PlannedFile(type, families, path));
            }
        }
        return plan;
    }

    private static RetireResult WritePlan(IIpInfoService ipinfo, FeedKind kind, string key, string folder,
                                          DateTimeOffset now, IReadOnlyList<PlannedFile> plan)
    {
        string timestamp = ExportWriter.FormatTimestamp(now);
        var done = new List<string>();
        var changes = new List<FeedFileChange>();
        string? failed = null;
        List<string> left = [];
        try
        {
            foreach (PlannedFile file in plan)
            {
                FeedFileChange change = WriteTracked(file.Path, file.Type, file.Families, Text(file.Type, file.Families, timestamp));
                done.Add(file.Path);
                changes.Add(change);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"A retired feed file could not be written ({ex.GetType().FullName}).");
            // The failed file's earlier copy is whole, as the move never ran, and the readers keep reading it and
            // every later one never tried; a folder at a feed's path is not a file and isn't named
            failed = plan[done.Count].Path;
            left = plan.Skip(done.Count).Select(file => file.Path).Where(File.Exists).ToList();
        }

        FeedRecordResult? record = null;
        if (done.Count > 0)
        {
            // No dataset date and no reasons: a retired file holds nothing from IPinfo, and every entry went by hand
            var write = new FeedWriteRecord(key, kind, FeedChangeCause.Retire, now, FeedTypes.All, changes, null, null,
                                            null, new SavedFeedIntent.Retired());
            record = IpInfoService.RecordFeedWrite(ipinfo, write);
        }

        return new RetireResult(failed is null ? RetireFailure.None : RetireFailure.WriteFailed, folder, done, failed,
                                left, record);
    }

    // No attribution, AS name or dataset line: a retired file lists nothing from IPinfo
    private static string Text(FeedType type, FeedFamilies families, string timestamp)
    {
        string nl = FeedText.LineEnd;
        string family = families switch
        {
            FeedFamilies.IPv4 => "IPv4",
            FeedFamilies.IPv6 => "IPv6",
            _ => "IPv4/IPv6",
        };
        var text = new StringBuilder();
        text.Append(FeedTypes.FirstLine(type)).Append(nl);
        text.Append(RetiredLine).Append(nl);
        text.Append("# Address Family: ").Append(family).Append(nl);
        text.Append("# Generated: ").Append(timestamp).Append(" UTC").Append(nl);
        text.Append("# Entries: 0").Append(nl);
        return text.ToString();
    }

    /// <summary>Writes the header-only text over the file and gives the entries it removed.</summary>
    internal static FeedFileChange WriteTracked(string path, FeedType type, FeedFamilies families, string text)
    {
        FeedFileRead before = FeedFileEntries.Read(path);
        WriteFile(path, text);
        return FeedFileEntries.Compare(path, type, families, before, [])
               with { WrittenSha256 = SavedFeedWriteProof.HashOf(text) };
    }

    // The file exists, so its folder does. The .tmp is made new, so a link there is never written through
    private static void WriteFile(string path, string text)
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
            Trace.TraceWarning("Could not delete a temporary retired feed file.");
        }
    }
}
