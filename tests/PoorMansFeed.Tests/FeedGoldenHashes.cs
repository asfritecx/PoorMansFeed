using System.Security.Cryptography;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>Pins the SHA-256 of every feed file a writer made, so no byte change passes a saved proof.</summary>
internal static class FeedGoldenHashes
{
    internal static void AssertPinned(string writer, string exportFolder, IReadOnlyDictionary<string, string> expected)
    {
        Assert.Equal(1, SavedFeedWriteProof.RenderVersion);
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (FeedType type in FeedTypes.All)
        {
            string folder = Path.Combine(exportFolder, FeedTypes.FolderName(type));
            if (!Directory.Exists(folder))
                continue;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                actual[FeedTypes.FolderName(type) + "/" + Path.GetFileName(file)] =
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
            }
        }

        bool same = actual.Count == expected.Count
                    && actual.All(pair => expected.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
        Assert.True(same,
            $"The {writer} writer's bytes changed: raise SavedFeedWriteProof.RenderVersion and these pins together, so a "
            + "refresh rewrites the files an older build left. Now written:"
            + string.Concat(actual.Select(pair => $"\n[\"{pair.Key}\"] = \"{pair.Value}\",")));
    }
}
