using System.Security.Cryptography;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class SavedFeedWriteProofTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>Each change's hash is of the bytes now on disk, and the proof names exactly those files.</summary>
    internal static void AssertProofOfDisk(string exportFolder, IReadOnlyList<FeedFileChange> changes, SavedFeedWriteProof? proof,
                                           FeedOptions options)
    {
        Assert.NotEmpty(changes);
        Assert.All(changes, change =>
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(change.Path))), change.WrittenSha256));
        Assert.NotNull(proof);
        Assert.Equal(SavedFeedWriteProof.FingerprintOf(options), proof.SettingsFingerprint);
        Assert.Equal(changes.Select(change => Path.GetRelativePath(exportFolder, change.Path).Replace('\\', '/'))
                            .Order(StringComparer.Ordinal),
                     proof.FileSha256.Keys.Order(StringComparer.Ordinal));
        Assert.All(changes, change =>
            Assert.Equal(change.WrittenSha256, proof.FileSha256[Path.GetRelativePath(exportFolder, change.Path).Replace('\\', '/')]));
    }

    private static readonly FeedOptions Every = new([FeedType.Fortigate, FeedType.ModSecurity], true, true, true, true);

    [Fact]
    public void The_fingerprint_is_fixed_text_of_the_render_version_and_every_switch_in_type_order()
    {
        Assert.Equal(1, SavedFeedWriteProof.RenderVersion);
        Assert.Equal("render=1;types=fortigate,modsecurity;ipv4=1;ipv6=1;split=1;ipv6enabled=1",
                     SavedFeedWriteProof.FingerprintOf(Every));
        Assert.Equal("render=1;types=fortigate,modsecurity;ipv4=1;ipv6=1;split=1;ipv6enabled=1",
                     SavedFeedWriteProof.FingerprintOf(Every with { Types = [FeedType.ModSecurity, FeedType.Fortigate] }));
        Assert.Equal("render=1;types=;ipv4=0;ipv6=0;split=0;ipv6enabled=0",
                     SavedFeedWriteProof.FingerprintOf(new FeedOptions([], false, false, false, false)));
    }

    [Fact]
    public void Each_switch_changes_the_fingerprint()
    {
        FeedOptions[] options =
        [
            Every, Every with { Types = [FeedType.Fortigate] }, Every with { Types = [FeedType.ModSecurity] },
            Every with { IncludeIPv4 = false }, Every with { IncludeIPv6 = false }, Every with { SplitByFamily = false },
            Every with { IPv6Enabled = false },
        ];

        Assert.Equal(options.Length, options.Select(SavedFeedWriteProof.FingerprintOf).Distinct(StringComparer.Ordinal).Count());
    }

    private FeedFileChange Change(string relative, FeedType type, string? hash = "ab") =>
        new(Path.Combine(_temp.Path, relative), type, FeedFamilies.IPv4, FeedEntryChanges.None, FeedEntryChanges.None,
            FeedFileState.Missing, hash is null ? null : string.Concat(Enumerable.Repeat(hash, 32)));

    [Fact]
    public void A_proof_names_each_file_under_the_export_folder_with_its_hash()
    {
        SavedFeedWriteProof? proof = SavedFeedWriteProof.Of(_temp.Path, Every,
            [Change(Path.Combine("modsecurity", "AS1_ipv4.txt"), FeedType.ModSecurity, "cd"),
             Change(Path.Combine("fortigate", "AS1_ipv4.txt"), FeedType.Fortigate)]);

        Assert.NotNull(proof);
        Assert.Equal(SavedFeedWriteProof.FingerprintOf(Every), proof.SettingsFingerprint);
        Assert.Equal(["fortigate/AS1_ipv4.txt", "modsecurity/AS1_ipv4.txt"], proof.FileSha256.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(string.Concat(Enumerable.Repeat("cd", 32)), proof.FileSha256["modsecurity/AS1_ipv4.txt"]);
    }

    [Fact]
    public void No_changes_a_missing_hash_or_a_file_outside_the_folder_gives_no_proof()
    {
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every, null));
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every, []));
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every,
            [Change(Path.Combine("fortigate", "AS1_ipv4.txt"), FeedType.Fortigate),
             Change(Path.Combine("modsecurity", "AS1_ipv4.txt"), FeedType.ModSecurity, null)]));
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every, [Change(Path.Combine("..", "fortigate", "AS1.txt"), FeedType.Fortigate)]));
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every, [Change("AS1.txt", FeedType.Fortigate)]));
        Assert.Null(SavedFeedWriteProof.Of(_temp.Path, Every,
            Enumerable.Range(0, 17).Select(i => Change(Path.Combine("fortigate", $"F{i}.txt"), FeedType.Fortigate)).ToList()));
    }

    [Fact]
    public void The_retired_writer_hashes_the_bytes_it_wrote()
    {
        string path = Path.Combine(_temp.Path, "fortigate", "AS1_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# old\n8.8.8.0/24\n");

        FeedFileChange change = RetiredFeedWriter.WriteTracked(path, FeedType.Fortigate, FeedFamilies.IPv4, "# Retired \u00e9\n# Entries: 0\n");

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), change.WrittenSha256);
        Assert.Equal(["8.8.8.0/24"], change.IPv4.Removed);
    }
}
