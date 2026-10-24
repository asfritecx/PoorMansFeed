using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// The feed writers' temporary file is made new, so a link or a stale file at its path is never written through.
/// The export folder may be one another account can write, which is why this matters there.
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

    // A feed ends every line with LF only, on every OS; the report and Copy keep their own. On macOS
    // Environment.NewLine is LF, so only this sweep sees a writer go back to it.
    [Fact]
    public void Every_feed_writer_ends_its_lines_with_LF_only()
    {
        // A verbatim or raw string spanning lines takes the checkout's line ends, CRLF on a Windows checkout
        string[] newlineTokens =
        [
            "Environment.NewLine", "AppendLine(", "WriteLine(", "WriteAllLines(", "AppendAllLines(", "\\r", "@\"", "@$\"",
            "\"\"\"",
        ];
        string[] notFeeds = ["WriteAsnReport", "BuildClipboardText"];
        List<string> writers = FeedWriterFiles();

        Assert.True(writers.Count >= 3, $"Only {writers.Count} feed writers were found.");
        foreach (string writer in writers)
        {
            string name = Path.GetFileName(writer);
            string source = File.ReadAllText(writer);
            foreach (string method in notFeeds)
            {
                string? without = WithoutMethodBody(source, method);
                if (name == "ExportWriter.cs")
                    Assert.True(without is not null, $"{name} no longer declares {method} with a block body.");
                source = without ?? source;
            }

            List<string> found = newlineTokens
                .Where(token => source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{name}: {token}")
                .ToList();

            Assert.Empty(found);
            Assert.Contains("FeedText.LineEnd", source, StringComparison.Ordinal);
            // The rolling feed writes PublicAddress.Text, which is bare by construction
            if (name != "AbuseIpDbFeedWriter.cs")
                Assert.True(source.Contains("FeedText.Entry(", StringComparison.Ordinal), $"{name} doesn't write its entries through FeedText.Entry.");
        }
    }

    // A writer under another name is still swept when it makes its file through WriteNew
    private static List<string> FeedWriterFiles()
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) is "ExportWriter.cs"
                || Path.GetFileName(file).EndsWith("FeedWriter.cs", StringComparison.Ordinal)
                || File.ReadAllText(file).Contains("TemporaryFile.WriteNew(", StringComparison.Ordinal))
            .ToList();
    }

    // Null unless the method has a block body. Braces are counted as they come; the bodies swept hold none in a
    // string or char literal that is unbalanced.
    private static string? WithoutMethodBody(string source, string method)
    {
        Match declaration = Regex.Match(source, $@"\bstatic\s+[\w<>\[\]?]+\s+{method}\s*\(");
        if (!declaration.Success)
            return null;

        int open = source.IndexOf('{', declaration.Index);
        int arrow = source.IndexOf("=>", declaration.Index, StringComparison.Ordinal);
        int end = source.IndexOf(';', declaration.Index);
        if (open < 0 || (arrow >= 0 && arrow < open) || (end >= 0 && end < open))
            return null;

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}' && --depth == 0)
                return string.Concat(source.AsSpan(0, declaration.Index), source.AsSpan(i + 1));
        }

        return null;
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
