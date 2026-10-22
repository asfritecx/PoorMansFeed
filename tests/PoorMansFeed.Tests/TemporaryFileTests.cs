using System.Runtime.CompilerServices;
using System.Text;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// The feed writers' temporary file. It is made new, so a link or a stale file at its path is never written through, and
/// a feed writer makes it only through TemporaryFile.WriteNew, so a later writer can't bring back a write that follows a
/// link. The export folder may be one another account can write, which is why this matters there.
/// </summary>
public sealed class TemporaryFileTests
{
    // Calls that open or write a file at a path, following a link there
    private static readonly string[] FollowingWrites =
    [
        "File.WriteAll", "File.AppendAll", "File.Create(", "File.Open(", "File.OpenWrite(", "new FileStream(",
        "new StreamWriter(",
    ];

    // The modes that open or truncate a file that is already there, through a link at its path
    private static readonly string[] FollowingModes =
        ["FileMode.Create,", "FileMode.Create)", "FileMode.OpenOrCreate", "FileMode.Open,", "FileMode.Truncate", "FileMode.Append"];

    [Fact]
    public void WriteNew_replaces_a_stale_file_with_the_text_in_UTF8_and_no_byte_order_mark()
    {
        using var temp = new TempFolder();
        string tmp = temp.File("feed.txt.tmp");
        File.WriteAllText(tmp, "a stale file, longer than the text that replaces it");

        TemporaryFile.WriteNew(tmp, "# caf\u00e9\n8.8.8.8\n");

        Assert.Equal(new UTF8Encoding(false).GetBytes("# caf\u00e9\n8.8.8.8\n"), File.ReadAllBytes(tmp));
    }

    [Fact]
    public void WriteNew_makes_its_file_new_and_never_opens_one_that_is_there()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "PoorMansFeed.Core", "TemporaryFile.cs"));

        List<string> following = FollowingModes.Where(mode => source.Contains(mode, StringComparison.Ordinal)).ToList();

        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Empty(following);
        Assert.DoesNotContain("File.WriteAll", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_feed_writer_makes_its_temporary_file_through_WriteNew()
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        List<string> writers = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) is "ExportWriter.cs"
                || Path.GetFileName(file).EndsWith("FeedWriter.cs", StringComparison.Ordinal))
            .ToList();

        // The ASN export, the cloud feeds and the AbuseIPDB rolling feed at least
        Assert.True(writers.Count >= 3, $"Only {writers.Count} feed writers were found.");
        foreach (string writer in writers)
        {
            string source = File.ReadAllText(writer);
            List<string> following = FollowingWrites
                .Where(call => source.Contains(call, StringComparison.Ordinal))
                .Select(call => $"{Path.GetFileName(writer)}: {call}")
                .ToList();

            Assert.Contains("TemporaryFile.WriteNew(", source, StringComparison.Ordinal);
            Assert.Empty(following);
        }
    }

    private static string RepositoryRoot([CallerFilePath] string testFile = "")
    {
        for (DirectoryInfo? directory = new FileInfo(testFile).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoorMansFeed.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
