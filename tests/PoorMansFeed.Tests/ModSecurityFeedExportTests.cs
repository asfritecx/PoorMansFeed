using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

public sealed class ModSecurityFeedExportTests
{
    private const string FortigateFirstLine = "# PoorMansFeed - Fortigate Threat Feed";
    private const string ModSecurityFirstLine = "# PoorMansFeed - ModSecurity IP list";
    private static readonly DateTimeOffset FixedUtc = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly NetworkRecord GoogleV4 = new(
        "8.8.8.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly NetworkRecord OtherV4 = new(
        "1.1.1.0/24", "AU", "Australia", "OC", "AS15169", "Google LLC", "one.one");

    private static readonly NetworkRecord GoogleV6 = new(
        "2001:4860::/32", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly NetworkRecord WholeV4 = new(
        "0.0.0.0/0", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly NetworkRecord WholeV6 = new(
        "::/0", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly List<NetworkRecord> Rows = [GoogleV4, GoogleV6, OtherV4];

    private static UserSettings Types(bool fortigate, bool modSecurity, bool split = true) => new()
    {
        FortigateEnabled = true,
        WriteFortigateFeeds = fortigate,
        WriteModSecurityLists = modSecurity,
        FortigateSplitByFamily = split,
    };

    private static FeedWrite Write(string folder, IReadOnlyList<NetworkRecord> rows, UserSettings settings, ExceptionList? exceptions = null,
                                   DateTimeOffset? at = null) =>
        ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", rows, settings, at ?? FixedUtc, exceptions ?? ExceptionList.Empty);

    private static string In(string folder, string type, string name) => Path.Combine(folder, type, name);

    private static string AfterFirstLine(string text) => text[(text.IndexOf('\n') + 1)..];

    private static string[] Names(bool split) => split ? ["AS15169_ipv4.txt", "AS15169_ipv6.txt"] : ["AS15169.txt"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_ticked_type_gets_the_same_files_and_only_the_first_line_differs(bool split)
    {
        using var reference = new ExportTempFolder();
        using var fortigateOnly = new ExportTempFolder();
        using var modSecurityOnly = new ExportTempFolder();
        using var both = new ExportTempFolder();
        string[] names = Names(split);

        // A FortiGate-only write in a folder of its own is the reference every other write here is compared with
        IReadOnlyList<string> expectedFortigate = ExportWriter.WriteFeeds(
            reference.Folder, "AS15169", "Google LLC", Rows, Types(true, false, split), FixedUtc, ExceptionList.Empty).Files;
        FeedWrite fortigate = Write(fortigateOnly.Folder, Rows, Types(true, false, split));
        Assert.Equal(names.Select(n => In(fortigateOnly.Folder, "fortigate", n)), fortigate.Files);
        Assert.Empty(fortigate.ModSecurityFiles);
        Assert.False(Directory.Exists(Path.Combine(fortigateOnly.Folder, "modsecurity")));
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(File.ReadAllBytes(expectedFortigate[i]), File.ReadAllBytes(fortigate.Files[i]));
        Assert.StartsWith(FortigateFirstLine + "\n", File.ReadAllText(fortigate.Files[0]));

        // ModSecurity only: the same names in its own folder, and an unticked type's old file is never touched or named
        string oldFortigate = In(modSecurityOnly.Folder, "fortigate", "AS15169_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(oldFortigate)!);
        File.WriteAllText(oldFortigate, "old");
        DateTime oldTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(oldFortigate, oldTime);
        FeedWrite modSecurity = Write(modSecurityOnly.Folder, Rows, Types(false, true, split), RangeSubtractionTests.List("8.8.8.0/24"));
        FeedWrite modSecurityPlain = Write(modSecurityOnly.Folder, Rows, Types(false, true, split));
        Assert.Equal(names.Select(n => In(modSecurityOnly.Folder, "modsecurity", n)), modSecurityPlain.Files);
        Assert.Equal(modSecurityPlain.Files, modSecurityPlain.ModSecurityFiles);
        foreach (FeedWrite write in new[] { modSecurity, modSecurityPlain })
        {
            Assert.DoesNotContain(oldFortigate, write.Files);
            Assert.DoesNotContain(oldFortigate, write.ModSecurityFiles);
            Assert.DoesNotContain(oldFortigate, write.OldFilesLeft);
        }
        Assert.Equal("old", File.ReadAllText(oldFortigate));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(oldFortigate));
        Assert.Single(Directory.GetFileSystemEntries(Path.GetDirectoryName(oldFortigate)!));
        for (int i = 0; i < names.Length; i++)
        {
            string expected = ModSecurityFirstLine + "\n" + AfterFirstLine(File.ReadAllText(expectedFortigate[i]));
            Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(modSecurityPlain.Files[i]));
        }

        // Both: every FortiGate file in order, then every ModSecurity file in the same order
        FeedWrite all = Write(both.Folder, Rows, Types(true, true, split));
        string[] modSecurityFiles = names.Select(n => In(both.Folder, "modsecurity", n)).ToArray();
        Assert.Equal([.. names.Select(n => In(both.Folder, "fortigate", n)), .. modSecurityFiles], all.Files);
        Assert.Equal(modSecurityFiles, all.ModSecurityFiles);
        Assert.Equal(0, all.WholeRangesLeftOut);
        foreach (string file in all.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
        Assert.All(all.ModSecurityFiles, f => Assert.True(ModSecurityRules.IsFileName(Path.GetFileName(f)), f));
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(File.ReadAllBytes(expectedFortigate[i]), File.ReadAllBytes(all.Files[i]));
            Assert.Equal(AfterFirstLine(File.ReadAllText(all.Files[i])), AfterFirstLine(File.ReadAllText(modSecurityFiles[i])));
        }
    }

    [Fact]
    public void Exceptions_cut_both_copies_alike_and_an_emptied_family_names_its_old_file_in_each_folder()
    {
        using var temp = new ExportTempFolder();
        Write(temp.Folder, Rows, Types(true, true));
        ExceptionList exceptions = RangeSubtractionTests.List("8.8.8.0/25", "1.1.1.0/24", "2001:4860::/32");

        FeedWrite write = Write(temp.Folder, Rows, Types(true, true), exceptions, FixedUtc.AddHours(1));

        string fortigateV4 = In(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        string modSecurityV4 = In(temp.Folder, "modsecurity", "AS15169_ipv4.txt");
        Assert.Equal([fortigateV4, modSecurityV4], write.Files);
        Assert.Equal([modSecurityV4], write.ModSecurityFiles);
        Assert.Equal(1, write.ExceptionCuts);
        Assert.Equal(2, write.ExceptionRemovals);
        Assert.Equal([In(temp.Folder, "fortigate", "AS15169_ipv6.txt"), In(temp.Folder, "modsecurity", "AS15169_ipv6.txt")],
                     write.OldFilesLeft);
        foreach (string file in write.Files)
        {
            string text = File.ReadAllText(file);
            Assert.Contains("# Exceptions: 1 range cut, 1 removed\n", text);
            Assert.EndsWith("\n8.8.8.128/25\n", text);
        }
        Assert.Equal(AfterFirstLine(File.ReadAllText(fortigateV4)), AfterFirstLine(File.ReadAllText(modSecurityV4)));
    }

    [Fact]
    public void A_failed_ModSecurity_write_names_its_file_and_everything_written_before_it()
    {
        using var temp = new ExportTempFolder();
        FeedWrite first = Write(temp.Folder, Rows, Types(true, true));
        string[] fortigate = [first.Files[0], first.Files[1]];
        string[] modSecurity = [first.Files[2], first.Files[3]];
        byte[] oldV4 = File.ReadAllBytes(modSecurity[0]);
        Directory.CreateDirectory(modSecurity[0] + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => Write(temp.Folder, Rows, Types(true, true), at: FixedUtc.AddHours(1)));

        Assert.Equal(modSecurity[0], ex.FilePath);
        Assert.Equal(fortigate, ex.WrittenBefore);
        Assert.Empty(ex.ModSecurityWrittenBefore);
        Assert.Equal(modSecurity, ex.OldFilesLeft);
        Assert.Equal(oldV4, File.ReadAllBytes(modSecurity[0]));
        Assert.All(fortigate, f => Assert.Contains("# Generated: 2026-03-04 06:06:07 UTC", File.ReadAllText(f)));

        // The second ModSecurity file fails after the first was written
        Directory.Delete(modSecurity[0] + ".tmp");
        Directory.CreateDirectory(modSecurity[1] + ".tmp");
        var second = Assert.Throws<ExportFileException>(() => Write(temp.Folder, Rows, Types(true, true)));
        Assert.Equal(modSecurity[1], second.FilePath);
        Assert.Equal([.. fortigate, modSecurity[0]], second.WrittenBefore);
        Assert.Equal([modSecurity[0]], second.ModSecurityWrittenBefore);
        Assert.Equal([modSecurity[1]], second.OldFilesLeft);

        // A plain file where the modsecurity folder goes fails its first file, after every FortiGate file is written
        using var blocked = new ExportTempFolder();
        File.WriteAllText(Path.Combine(blocked.Folder, "modsecurity"), "");
        var folder = Assert.Throws<ExportFileException>(() => Write(blocked.Folder, Rows, Types(true, true)));
        Assert.Equal(In(blocked.Folder, "modsecurity", "AS15169_ipv4.txt"), folder.FilePath);
        Assert.Equal([In(blocked.Folder, "fortigate", "AS15169_ipv4.txt"), In(blocked.Folder, "fortigate", "AS15169_ipv6.txt")],
                     folder.WrittenBefore);
        Assert.All(folder.WrittenBefore, f => Assert.True(File.Exists(f)));
        Assert.Empty(folder.ModSecurityWrittenBefore);
        Assert.Empty(folder.OldFilesLeft);
    }

    [Fact]
    public void A_failed_FortiGate_file_names_every_untried_old_file_of_both_types_in_write_order()
    {
        using var temp = new ExportTempFolder();
        string fortigateV4 = In(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        string[] old = [In(temp.Folder, "fortigate", "AS15169_ipv6.txt"), In(temp.Folder, "modsecurity", "AS15169_ipv4.txt"),
                        In(temp.Folder, "modsecurity", "AS15169_ipv6.txt")];
        foreach (string file in old)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "old " + file + "\n");
        }
        Directory.CreateDirectory(fortigateV4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => Write(temp.Folder, Rows, Types(true, true)));

        Assert.Equal(fortigateV4, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Empty(ex.ModSecurityWrittenBefore);
        Assert.Equal(old, ex.OldFilesLeft);
        Assert.All(old, file => Assert.Equal("old " + file + "\n", File.ReadAllText(file)));
    }

    [Fact]
    public void A_failed_write_names_the_emptied_old_files_then_the_failed_file_then_the_untried_ones()
    {
        using var temp = new ExportTempFolder();
        Write(temp.Folder, Rows, Types(true, true));
        string fortigateV4 = In(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        Directory.CreateDirectory(fortigateV4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(
            () => Write(temp.Folder, Rows, Types(true, true), RangeSubtractionTests.List("2001:4860::/32")));

        Assert.Equal(fortigateV4, ex.FilePath);
        Assert.Equal(
            [
                In(temp.Folder, "fortigate", "AS15169_ipv6.txt"),
                In(temp.Folder, "modsecurity", "AS15169_ipv6.txt"),
                fortigateV4,
                In(temp.Folder, "modsecurity", "AS15169_ipv4.txt"),
            ],
            ex.OldFilesLeft);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(1, 0, "Left out: 1 range covering every address: 0.0.0.0/0")]
    [InlineData(0, 1, "Left out: 1 range covering every address: ::/0")]
    [InlineData(1, 1, "Left out: 2 ranges covering every address: 0.0.0.0/0 and ::/0")]
    [InlineData(2, 0, "Left out: 2 ranges covering every address: 0.0.0.0/0")]
    public void The_left_out_line_names_the_whole_ranges(int ipv4, int ipv6, string? expected)
    {
        Assert.Equal(expected, ExportWriter.WholeRangesLine(ipv4, ipv6));
    }

    [Fact]
    public void A_range_covering_every_address_is_never_written_and_counts_once()
    {
        using var temp = new ExportTempFolder();
        List<NetworkRecord> rows = [WholeV4, GoogleV4, WholeV6, GoogleV6];

        FeedWrite split = Write(temp.Folder, rows, Types(true, true));

        Assert.Equal(2, split.WholeRangesLeftOut);
        Assert.Equal(4, split.Files.Count);
        foreach (string file in split.Files)
        {
            string text = File.ReadAllText(file);
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
            Assert.DoesNotContain("\n0.0.0.0/0\n", text);
            Assert.DoesNotContain("\n::/0\n", text);
            bool v6 = file.EndsWith("_ipv6.txt", StringComparison.Ordinal);
            string leftOut = v6 ? "::/0" : "0.0.0.0/0";
            Assert.Contains("# Entries: 1\n# Left out: 1 range covering every address: " + leftOut
                            + "\n# IP address data powered by IPinfo (https://ipinfo.io)\n", text);
        }

        // Combined, the line names both after the exceptions line
        FeedWrite combined = Write(temp.Folder, rows, Types(true, true, split: false), RangeSubtractionTests.List("8.8.8.0/25"));
        Assert.Equal(2, combined.WholeRangesLeftOut);
        Assert.All(combined.Files, file => Assert.EndsWith(
            "# Entries: 2\n# Exceptions: 1 range cut, 0 removed\n# Left out: 2 ranges covering every address: 0.0.0.0/0 and ::/0\n"
            + "# IP address data powered by IPinfo (https://ipinfo.io)\n8.8.8.128/25\n2001:4860::/32\n", File.ReadAllText(file)));

        // An IPv6 family the settings leave out counts nothing and names nothing
        FeedWrite noV6 = Write(temp.Folder, [WholeV6, GoogleV4], Types(true, false) with { FortigateIncludeIPv6 = false });
        Assert.Equal(0, noV6.WholeRangesLeftOut);
        Assert.Empty(noV6.OldFilesLeft);
        Assert.DoesNotContain("Left out", File.ReadAllText(noV6.Files.Single()));

        // A family of only a whole range gets no file, and its earlier copy is named in each folder
        FeedWrite onlyWhole = Write(temp.Folder, [GoogleV4, WholeV6], Types(true, true));
        Assert.Equal(1, onlyWhole.WholeRangesLeftOut);
        Assert.Equal([In(temp.Folder, "fortigate", "AS15169_ipv4.txt"), In(temp.Folder, "modsecurity", "AS15169_ipv4.txt")],
                     onlyWhole.Files);
        Assert.Equal([In(temp.Folder, "fortigate", "AS15169_ipv6.txt"), In(temp.Folder, "modsecurity", "AS15169_ipv6.txt")],
                     onlyWhole.OldFilesLeft);

        // The report keeps the row
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", rows, FixedUtc);
        Assert.Contains("| 0.0.0.0/0 ", File.ReadAllText(report));
        Assert.Contains("| ::/0 ", File.ReadAllText(report));
    }

    [Fact]
    public void No_ticked_type_writes_nothing_checks_no_list_and_names_no_file()
    {
        using var temp = new ExportTempFolder();

        FeedWrite none = Write(temp.Folder, Rows, Types(false, false), ExceptionList.Unreadable);

        Assert.Empty(none.Files);
        Assert.Empty(none.ModSecurityFiles);
        Assert.Empty(none.OldFilesLeft);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder));

        FeedWrite both = Write(temp.Folder, Rows, Types(true, true));
        Assert.Empty(ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", Rows, Types(false, false)));
        Assert.Equal(both.Files, ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", Rows, Types(true, true)));
        Assert.Equal(both.ModSecurityFiles, ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", Rows, Types(false, true)));
    }
}
