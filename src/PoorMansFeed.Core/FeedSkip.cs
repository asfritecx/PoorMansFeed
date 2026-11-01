using System.Diagnostics;

namespace PoorMansFeed.Core;

/// <summary>Whether a saved feed's planned files are provably its last write and hold the planned entries.</summary>
internal static class FeedSkip
{
    /// <summary>
    /// True only when the proof's settings and files are exactly the plan's, and each file is still the whole regular
    /// file written with the planned entries. Never throws, and never creates, writes or deletes anything.
    /// </summary>
    internal static bool IsUnchanged(string exportFolder, IReadOnlyList<PlannedFeedFile> plan, string fingerprint,
                                     SavedFeedWriteProof? proof)
    {
        try
        {
            if (proof?.FileSha256 is not { } hashes || plan is null || plan.Count == 0
                || !string.Equals(proof.SettingsFingerprint, fingerprint, StringComparison.Ordinal))
                return false;

            var keys = new List<string>(plan.Count);
            foreach (PlannedFeedFile file in plan)
            {
                if (SavedFeedWriteProof.KeyOf(exportFolder, file.Path) is not { } key)
                    return false;
                keys.Add(key);
            }

            var planned = new HashSet<string>(keys, StringComparer.Ordinal);
            if (planned.Count != keys.Count || !planned.SetEquals(hashes.Keys) || hashes.Count != planned.Count)
                return false;

            for (int i = 0; i < plan.Count; i++)
            {
                PlannedFeedFile file = plan[i];
                // One read gives both the hash and the entries, so they describe the same bytes
                FeedFileRead read = FeedFileEntries.Read(file.Path);
                if (read.State != FeedFileState.Read || read.Sha256 is null
                    || !hashes.TryGetValue(keys[i], out string? hash)
                    || !string.Equals(read.Sha256, hash, StringComparison.Ordinal)
                    || !FeedFileEntries.Compare(file.Path, file.Type, file.Families, read, file.Lines).IsEmpty)
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"A saved feed's files couldn't be checked, so the feed is written ({ex.GetType().Name}).");
            return false;
        }
    }
}
