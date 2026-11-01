using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PoorMansFeed.Core;

/// <summary>Two lines have the same Key exactly when they name the same network (family, masked start, end).</summary>
internal readonly record struct FeedEntry(string Text, string Key, bool IsIPv6);

/// <summary>Sha256 is the lower-case hex SHA-256 of the bytes read; null unless a whole file was read.</summary>
internal readonly record struct FeedFileRead(FeedFileState State, IReadOnlyList<FeedEntry> Entries, string? Sha256 = null);

internal static class FeedFileEntries
{
    public const long MaxBytes = 64L * 1024 * 1024;

    // A written line that doesn't parse gets this before its text, which no parsed key starts with
    private const string UnparsedKeyMark = "!";

    // A feed line by ExportWriter.TryFeedEntry's rules; false for any other line
    public static bool TryParse(string line, out FeedEntry entry)
    {
        entry = default;
        if (!ExportWriter.TryFeedEntry(line, out _, out byte[] start, out byte[] end))
            return false;

        bool isIpv6 = start.Length == 16;
        string key = (isIpv6 ? "6:" : "4:") + Convert.ToHexString(start) + "-" + Convert.ToHexString(end);
        entry = new FeedEntry(line, key, isIpv6);
        return true;
    }

    // The entries in file order. A missing file or a folder is Missing; a link, an oversized file or a failed read is
    // Unreadable; each has none
    public static FeedFileRead Read(string path) => Read(path, MaxBytes);

    internal static FeedFileRead Read(string path, long maxBytes)
    {
        var missing = new FeedFileRead(FeedFileState.Missing, []);
        var unreadable = new FeedFileRead(FeedFileState.Unreadable, []);
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            // Not the ReparsePoint attribute: a OneDrive file is one in every state; only a link is refused
            var info = new FileInfo(path);
            if (!info.Exists)
                return missing;
            if (info.LinkTarget is not null)
                return unreadable;

            // A FIFO or a device reports no size, and opening a FIFO blocks
            if (info.Length == 0)
                return new FeedFileRead(FeedFileState.Read, []);

            if (info.Length > maxBytes)
            {
                Trace.TraceWarning("A feed file was too large to compare with, so it counts as empty.");
                return unreadable;
            }

            byte[]? bytes = ReadAtMost(path, maxBytes);
            if (bytes is null)
            {
                Trace.TraceWarning("A feed file grew too large to compare with, so it counts as empty.");
                return unreadable;
            }

            // A byte that isn't UTF-8 becomes U+FFFD, which no entry can hold, so it costs only its own line
            return new FeedFileRead(FeedFileState.Read, Parse(Encoding.UTF8.GetString(bytes)),
                                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"A feed file couldn't be read, so it counts as empty ({ex.GetType().Name}: {ex.Message}).");
            return unreadable;
        }
    }

    // Per family, distinct by Key: Added = written lines whose key the file didn't hold; Removed = the reverse
    public static FeedFileChange Compare(string path, FeedType type, FeedFamilies families, FeedFileRead before,
                                         IEnumerable<string> written)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(before.Entries);
        ArgumentNullException.ThrowIfNull(written);

        var beforeKeys = new HashSet<string>(before.Entries.Select(entry => entry.Key), StringComparer.Ordinal);
        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);
        var added4 = new List<string>();
        var added6 = new List<string>();
        foreach (string line in written)
        {
            (string key, bool isIpv6) = TryParse(line, out FeedEntry entry)
                ? (entry.Key, entry.IsIPv6)
                : (UnparsedKeyMark + line, line.Contains(':'));
            if (writtenKeys.Add(key) && !beforeKeys.Contains(key))
                (isIpv6 ? added6 : added4).Add(line);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var removed4 = new List<string>();
        var removed6 = new List<string>();
        foreach (FeedEntry entry in before.Entries)
        {
            if (seen.Add(entry.Key) && !writtenKeys.Contains(entry.Key))
                (entry.IsIPv6 ? removed6 : removed4).Add(entry.Text);
        }

        return new FeedFileChange(path, type, families, new FeedEntryChanges(added4, removed4),
                                  new FeedEntryChanges(added6, removed6), before.State);
    }

    // Null when the file holds more than maxBytes by the time it is read
    private static byte[]? ReadAtMost(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1 - total))) > 0)
        {
            buffer.Write(chunk, 0, read);
            total += read;
            if (total > maxBytes)
                return null;
        }

        return buffer.ToArray();
    }

    private static List<FeedEntry> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        var entries = new List<FeedEntry>();
        int junk = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
                continue;

            if (TryParse(line, out FeedEntry entry))
                entries.Add(entry);
            else
                junk++;
        }

        if (junk > 0)
            Trace.TraceWarning(FormattableString.Invariant($"A feed file had {junk} line(s) that aren't feed entries, which were skipped."));

        return entries;
    }
}
