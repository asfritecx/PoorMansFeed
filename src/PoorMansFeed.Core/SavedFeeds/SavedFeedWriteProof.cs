using System.Security.Cryptography;
using System.Text;

namespace PoorMansFeed.Core;

/// <summary>
/// What a page write left: the settings that planned its files, and each file's SHA-256 as written, keyed by its path
/// under the export folder with '/'.
/// </summary>
public sealed record SavedFeedWriteProof(string SettingsFingerprint, IReadOnlyDictionary<string, string> FileSha256)
{
    /// <summary>Raised when a feed writer writes other bytes for the same entries, so refreshes rewrite.</summary>
    internal const int RenderVersion = 1;
    internal const int MaxFiles = 16;
    internal const int MaxKeyLength = 260;
    internal const int MaxFingerprintLength = 200;

    /// <summary>Every switch that changes which files a write plans or what they hold, as fixed text.</summary>
    internal static string FingerprintOf(FeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Types);
        string types = string.Join(',', FeedTypes.All.Where(options.Types.Contains).Select(FeedTypes.FolderName));
        return $"render={RenderVersion};types={types};ipv4={Flag(options.IncludeIPv4)};ipv6={Flag(options.IncludeIPv6)};" +
               $"split={Flag(options.SplitByFamily)};ipv6enabled={Flag(options.IPv6Enabled)}";
    }

    private static char Flag(bool on) => on ? '1' : '0';

    /// <summary>Null unless every change has its hash and is a file in a type folder of the export folder.</summary>
    internal static SavedFeedWriteProof? Of(string exportFolder, FeedOptions options, IReadOnlyList<FeedFileChange>? changes)
    {
        ArgumentNullException.ThrowIfNull(exportFolder);
        if (changes is null || changes.Count is 0 or > MaxFiles)
            return null;

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FeedFileChange change in changes)
        {
            if (change?.WrittenSha256 is not { } hash || !IsHash(hash) || change.Path is null)
                return null;
            if (KeyOf(exportFolder, change.Path) is not { } key || !files.TryAdd(key, hash))
                return null;
        }

        return new SavedFeedWriteProof(FingerprintOf(options), files);
    }

    /// <summary>A file's key: its path under the export folder with '/'; null outside a type folder of it.</summary>
    internal static string? KeyOf(string exportFolder, string path)
    {
        ArgumentNullException.ThrowIfNull(exportFolder);
        ArgumentNullException.ThrowIfNull(path);
        string relative = Path.GetRelativePath(Path.GetFullPath(exportFolder), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative))
            return null;
        string key = relative.Replace(Path.DirectorySeparatorChar, '/');
        return IsFileKey(key) ? key : null;
    }

    /// <summary>The SHA-256 of a feed file's text in the bytes TemporaryFile.WriteNew writes for it.</summary>
    internal static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text)));

    internal static bool IsValid(SavedFeedWriteProof? proof) =>
        proof is { SettingsFingerprint: { } fingerprint, FileSha256: { } files } && IsFingerprint(fingerprint)
        && files.Count is >= 1 and <= MaxFiles && files.All(pair => IsFileKey(pair.Key) && IsHash(pair.Value));

    internal static bool IsFingerprint(string? text) =>
        text is { Length: <= MaxFingerprintLength } && text.All(c => c is >= ' ' and <= '~');

    internal static bool IsHash(string? text) =>
        text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A type folder's name, '/', and a file name of letters, digits, '.', '_' and '-' ending .txt.</summary>
    internal static bool IsFileKey(string? key)
    {
        if (key is not { Length: <= MaxKeyLength })
            return false;
        int slash = key.IndexOf('/');
        if (slash < 0 || !FeedTypes.All.Any(type => string.Equals(FeedTypes.FolderName(type), key[..slash], StringComparison.Ordinal)))
            return false;
        string name = key[(slash + 1)..];
        return name.Length > ".txt".Length && name.EndsWith(".txt", StringComparison.Ordinal)
               && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }
}
