using System.Reflection;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

public sealed class ExportWriterTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly NetworkRecord GoogleV4 = new(
        "8.8.8.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly NetworkRecord GermanV6 = new(
        "2a02:26f0::/29", "DE", "Germany", "EU", "AS15169", "Google LLC", "example.de");

    private static readonly NetworkRecord OtherV4 = new(
        "1.1.1.0/24", "AU", "Australia", "OC", "AS15169", "Google LLC", "one.one");

    private static string Nl(string text) => text.Replace("\n", Environment.NewLine);

    private static UserSettings Settings(bool split = true, bool v4 = true, bool v6 = true, bool enabled = true) => new()
    {
        FortigateEnabled = enabled,
        FortigateSplitByFamily = split,
        FortigateIncludeIPv4 = v4,
        FortigateIncludeIPv6 = v6,
    };

    [Fact]
    public void AsnReportMatchesTheOldExportExactly()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };

        string path = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", rows, FixedUtc);

        string expected = Nl(
            "ASN:       AS15169\n" +
            "AS Name:   Google LLC\n" +
            "Generated: 2026-03-04 05:06:07 UTC\n" +
            "Networks:  2\n" +
            new string('-', 80) + "\n" +
            "\n" +
            "+----------------+---------------+----+-----------+------------+\n" +
            "| Network        | Country       | CC | Continent | AS Domain  |\n" +
            "+----------------+---------------+----+-----------+------------+\n" +
            "| 8.8.8.0/24     | United States | US | NA        | google.com |\n" +
            "| 2a02:26f0::/29 | Germany       | DE | EU        | example.de |\n" +
            "+----------------+---------------+----+-----------+------------+\n" +
            "  2 row(s)\n" +
            "\n" +
            "IP address data powered by IPinfo (https://ipinfo.io)\n");

        Assert.Equal(Path.Combine(temp.Folder, "AS15169.txt"), path);
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void AsnReportAcceptsBareNumberAndOverwrites()
    {
        using var temp = new ExportTempFolder();
        ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], FixedUtc);

        string path = ExportWriter.WriteAsnReport(temp.Folder, "15169", "Google LLC", [OtherV4], FixedUtc);

        Assert.Equal(Path.Combine(temp.Folder, "AS15169.txt"), path);
        string text = File.ReadAllText(path);
        Assert.Contains("  1 row(s)" + Environment.NewLine, text);
        Assert.DoesNotContain("8.8.8.0/24", text);
        Assert.Single(Directory.GetFiles(temp.Folder));
    }

    [Fact]
    public void FortigateSplitWritesOneFilePerFamily()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6, new("", null, null, null, null, null, null), OtherV4 };

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;

        string v4Path = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        string v6Path = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt");
        Assert.Equal([v4Path, v6Path], files);

        string v4 = File.ReadAllText(v4Path);
        Assert.Equal(Nl(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.0/24\n" +
            "1.1.1.0/24\n"), v4);

        string v6 = File.ReadAllText(v6Path);
        Assert.Contains("# Address Family: IPv6" + Environment.NewLine, v6);
        Assert.Contains("# Entries: 1" + Environment.NewLine, v6);
        Assert.EndsWith("2a02:26f0::/29" + Environment.NewLine, v6);
    }

    [Fact]
    public void FortigateCombinedWritesIPv4BeforeIPv6()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GermanV6, GoogleV4 };

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, ExceptionList.Empty).Files;

        string path = Path.Combine(temp.Folder, "fortigate", "AS15169.txt");
        Assert.Equal([path], files);
        Assert.Equal(Nl(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4/IPv6\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.0/24\n" +
            "2a02:26f0::/29\n"), File.ReadAllText(path));
    }

    [Fact]
    public void FortigateIPv4OnlyWritesNoIPv6File()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(v6: false), FixedUtc, ExceptionList.Empty).Files;

        Assert.Equal([Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt")], files);
        Assert.False(File.Exists(Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt")));
    }

    [Fact]
    public void FortigateDisabledWritesNothing()
    {
        using var temp = new ExportTempFolder();

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4], Settings(enabled: false), FixedUtc, ExceptionList.Empty).Files;

        Assert.Empty(files);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnreadableExceptionListThrowsRatherThanWritingAnUncutFeed(bool split)
    {
        using var temp = new ExportTempFolder();

        Assert.Throws<InvalidOperationException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Settings(split: split), FixedUtc, ExceptionList.Unreadable));

        string fortigate = Path.Combine(temp.Folder, "fortigate");
        Assert.True(!Directory.Exists(fortigate) || Directory.GetFileSystemEntries(fortigate).Length == 0);
    }

    [Fact]
    public void FortigateDisabledWithAnUnreadableExceptionListWritesNothingAndDoesNotThrow()
    {
        // The export still writes the ASN report alone while the list is unreadable, and passes the list here
        using var temp = new ExportTempFolder();

        FortigateFeedWrite write = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Settings(enabled: false), FixedUtc, ExceptionList.Unreadable);

        Assert.Empty(write.Files);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder));
    }

    [Fact]
    public void ALineBreakInTheAsNameStaysInItsHeaderLine()
    {
        using var temp = new ExportTempFolder();
        const string name = "Evil\r\n9.9.9.0/24\nCorp\u2028Ltd";

        IReadOnlyList<string> feeds = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", name, [GoogleV4], Settings(split: false), FixedUtc, ExceptionList.Empty).Files;
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", name, [GoogleV4], FixedUtc);

        string[] feedLines = File.ReadAllLines(Assert.Single(feeds));
        Assert.Contains("# ASN: AS15169 - Evil  9.9.9.0/24 Corp Ltd", feedLines);
        Assert.Equal(["8.8.8.0/24"], feedLines.Where(line => !line.StartsWith('#')));
        Assert.Contains("AS Name:   Evil  9.9.9.0/24 Corp Ltd", File.ReadAllLines(report));
    }

    [Fact]
    public void ANonAsciiAsNameIsWrittenUnchanged()
    {
        using var temp = new ExportTempFolder();
        const string name = "Telef\u00F3nica de Espa\u00F1a";

        IReadOnlyList<string> feeds = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS3352", name, [GoogleV4], Settings(split: false), FixedUtc, ExceptionList.Empty).Files;

        Assert.Contains("# ASN: AS3352 - " + name, File.ReadAllLines(Assert.Single(feeds)));
    }

    [Fact]
    public void ExceptionsRemoveABareAddressAndCutANetworkInPlace()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord>
        {
            GoogleV4 with { Network = "1.0.168.172" },
            GoogleV4,
            OtherV4,
            GermanV6 with { Network = "0a01:0200::/64" },
            GermanV6,
        };
        ExceptionList exceptions = RangeSubtractionTests.List("1.0.168.172", "8.8.8.0/25", "2a02:26f0::/30");

        FortigateFeedWrite written = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, exceptions);

        Assert.Equal(2, written.ExceptionCuts);
        Assert.Equal(1, written.ExceptionRemovals);
        Assert.Empty(written.OldFilesLeft);
        Assert.Equal(Nl(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# Exceptions: 1 range cut, 1 removed\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.128/25\n" +
            "1.1.1.0/24\n"), File.ReadAllText(written.Files[0]));
        // An untouched line keeps the dataset's spelling; the pieces of a cut one are canonical
        string[] v6 = File.ReadAllLines(written.Files[1]);
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", v6);
        Assert.Equal(["0a01:0200::/64", "2a02:26f4::/30"], v6.Where(line => !line.StartsWith('#')));
    }

    [Fact]
    public void ExceptionsInTheCombinedFeedAreCountedOnOneLine()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, OtherV4, GermanV6 };
        ExceptionList exceptions = RangeSubtractionTests.List("1.1.1.0/24", "2a02:26f0::/32");

        FortigateFeedWrite written = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, exceptions);

        string[] lines = File.ReadAllLines(Assert.Single(written.Files));
        Assert.Contains("# Entries: 4", lines);
        Assert.Contains("# Exceptions: 1 range cut, 1 removed", lines);
        Assert.Equal(["8.8.8.0/24", "2a02:26f1::/32", "2a02:26f2::/31", "2a02:26f4::/30"], lines.Where(line => !line.StartsWith('#')));
    }

    [Fact]
    public void ExceptionsThatTouchNothingWriteTheFeedAsBefore()
    {
        using var temp = new ExportTempFolder();
        using var before = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6, OtherV4 };

        IReadOnlyList<string> plain = ExportWriter.WriteFortigateFeeds(before.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        FortigateFeedWrite written = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, RangeSubtractionTests.List("8.8.9.0/24", "8.8.7.255", "::808:800/120"));

        Assert.Equal(0, written.ExceptionCuts + written.ExceptionRemovals);
        Assert.Equal(File.ReadAllBytes(plain[0]), File.ReadAllBytes(written.Files[0]));
        Assert.Equal(File.ReadAllBytes(plain[1]), File.ReadAllBytes(written.Files[1]));
    }

    [Fact]
    public void AFamilyExceptionsEmptiedLeavesItsOldFileAndNamesIt()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        byte[] oldV6 = File.ReadAllBytes(first[1]);

        FortigateFeedWrite written = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, RangeSubtractionTests.List("2a02:26f0::/29"));

        Assert.Equal([first[0]], written.Files);
        Assert.Equal([first[1]], written.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(first[1]));

        FortigateFeedWrite combined = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, RangeSubtractionTests.List("0.0.0.0/0", "::/0"));
        Assert.Empty(combined.Files);
        Assert.Empty(combined.OldFilesLeft);
    }

    [Fact]
    public void ASuccessfulExportLeavesNoTemporaryFile()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", rows, FixedUtc);
        IReadOnlyList<string> split = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        IReadOnlyList<string> combined = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, ExceptionList.Empty).Files;

        Assert.True(File.Exists(report));
        Assert.Equal(2, split.Count);
        Assert.Single(combined);
        Assert.Empty(Directory.GetFiles(temp.Folder, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(temp.Folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void ALinkAtATemporaryPathIsReplacedNotWrittenThrough()
    {
        using var plain = new ExportTempFolder();
        using var temp = new ExportTempFolder();
        using var outside = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        string plainReport = ExportWriter.WriteAsnReport(plain.Folder, "AS15169", "Google LLC", rows, FixedUtc);
        IReadOnlyList<string> plainFeeds = ExportWriter.WriteFortigateFeeds(
            plain.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        string report = Path.Combine(temp.Folder, Path.GetFileName(plainReport));
        string[] feeds = plainFeeds.Select(file => Path.Combine(temp.Folder, "fortigate", Path.GetFileName(file))).ToArray();
        Directory.CreateDirectory(Path.Combine(temp.Folder, "fortigate"));
        string victim = Path.Combine(outside.Folder, "victim.txt");
        File.WriteAllText(victim, "not the export's");
        byte[] victimBefore = File.ReadAllBytes(victim);
        string missing = Path.Combine(outside.Folder, "missing.txt");
        // The report's and the IPv4 feed's temporary paths point at a file outside the export folder, the IPv6 feed's
        // at nothing
        if (!TryLink(report + ".tmp", victim) || !TryLink(feeds[0] + ".tmp", victim) || !TryLink(feeds[1] + ".tmp", missing))
            return;

        string written = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", rows, FixedUtc);
        IReadOnlyList<string> writtenFeeds = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;

        Assert.Equal(report, written);
        Assert.Equal(feeds, writtenFeeds);
        Assert.Equal(victimBefore, File.ReadAllBytes(victim));
        Assert.False(File.Exists(missing));
        Assert.Null(new FileInfo(report).LinkTarget);
        Assert.Equal(File.ReadAllBytes(plainReport), File.ReadAllBytes(report));
        for (int i = 0; i < feeds.Length; i++)
        {
            Assert.Null(new FileInfo(feeds[i]).LinkTarget);
            Assert.Equal(File.ReadAllBytes(plainFeeds[i]), File.ReadAllBytes(feeds[i]));
        }
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder, "*.tmp", SearchOption.AllDirectories));
    }

    // A symbolic link needs a privilege on Windows that a test run may not have
    private static bool TryLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link, so a link at the temporary path can't be planted.");
            return false;
        }
    }

    [Fact]
    public void AFeedWriteThatFailsKeepsItsEarlierCopyAndNamesIt()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        byte[] oldV6 = File.ReadAllBytes(first[1]);
        // A folder where the feed's temporary file should go makes that one write fail
        Directory.CreateDirectory(first[1] + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc.AddHours(1), ExceptionList.Empty));

        Assert.Equal(first[1], ex.FilePath);
        Assert.Equal([first[0]], ex.WrittenBefore);
        Assert.Equal([first[1]], ex.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(first[1]));
        Assert.Contains("# Generated: 2026-03-04 06:06:07 UTC", File.ReadAllText(first[0]));

        // A family the exceptions emptied is named before the failed file
        File.WriteAllText(first[0], "old");
        var cut = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, RangeSubtractionTests.List("8.8.8.0/24")));
        Assert.Empty(cut.WrittenBefore);
        Assert.Equal([first[0], first[1]], cut.OldFilesLeft);
        Assert.Equal("old", File.ReadAllText(first[0]));
    }

    [Fact]
    public void AFailedWriteNamesAFamilyTheExceptionsEmptiedEvenWhenThatFamilyComesLater()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        string ipv4 = first[0];
        string ipv6 = first[1];
        byte[] oldV4 = File.ReadAllBytes(ipv4);
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // The IPv4 write fails before the IPv6 family, which the exceptions emptied, is reached
        Directory.CreateDirectory(ipv4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc.AddHours(1), RangeSubtractionTests.List("2a02:26f0::/29")));

        Assert.Equal(ipv4, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Equal([ipv6, ipv4], ex.OldFilesLeft);
        Assert.Equal(oldV4, File.ReadAllBytes(ipv4));
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));
    }

    [Fact]
    public void AFailedIpv4WriteNamesTheOldIpv6FeedItNeverTried()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        string ipv4 = first[0];
        string ipv6 = first[1];
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // The IPv4 write fails, so the IPv6 feed is never tried and its earlier copy stays for FortiGate to read
        Directory.CreateDirectory(ipv4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc.AddHours(1), ExceptionList.Empty));

        Assert.Equal(ipv4, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Equal([ipv4, ipv6], ex.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));

        // With no earlier IPv6 feed only the failed file is named
        File.Delete(ipv6);
        var noV6 = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty));
        Assert.Equal([ipv4], noV6.OldFilesLeft);
    }

    [Fact]
    public void ExistingFeedFilesNamesTheFeedsAnExportWouldWriteOrNote()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> split = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc, ExceptionList.Empty).Files;
        IReadOnlyList<string> combined = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, ExceptionList.Empty).Files;

        Assert.Equal(split, ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", rows, Settings()));
        Assert.Equal(combined, ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", rows, Settings(split: false)));
        // A family with no wanted network, the feed turned off, or IPv6 turned off names no file of that family
        Assert.Equal([split[0]], ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", [GoogleV4], Settings()));
        Assert.Equal([split[0]], ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", rows, Settings() with { IPv6Enabled = false }));
        Assert.Empty(ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", rows, Settings() with { FortigateEnabled = false }));
        // A file that isn't there is not named
        File.Delete(split[0]);
        Assert.Equal([split[1]], ExportWriter.ExistingFeedFiles(temp.Folder, "AS15169", rows, Settings()));
    }

    [Fact]
    public void AFeedWriteThatFailsWithNoEarlierCopyNamesNoOldFile()
    {
        using var temp = new ExportTempFolder();
        string ipv6 = Path.Combine(temp.Folder, ExportWriter.FortigateFolderName, "AS15169_ipv6.txt");
        Directory.CreateDirectory(ipv6 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Settings(), FixedUtc, ExceptionList.Empty));

        Assert.Equal(ipv6, ex.FilePath);
        Assert.Equal([Path.Combine(temp.Folder, ExportWriter.FortigateFolderName, "AS15169_ipv4.txt")], ex.WrittenBefore);
        Assert.Empty(ex.OldFilesLeft);
        Assert.False(File.Exists(ipv6));
    }

    [Fact]
    public void AReportWriteThatFailsKeepsTheEarlierReport()
    {
        using var temp = new ExportTempFolder();
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", [GoogleV4], FixedUtc);
        byte[] oldReport = File.ReadAllBytes(report);
        Directory.CreateDirectory(report + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() =>
            ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", [GoogleV4, OtherV4], FixedUtc.AddHours(1)));

        Assert.Equal(report, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Equal([report], ex.OldFilesLeft);
        Assert.Equal(oldReport, File.ReadAllBytes(report));

        // With no earlier report there is no old file to name
        File.Delete(report);
        var none = Assert.Throws<ExportFileException>(() =>
            ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", [GoogleV4], FixedUtc));
        Assert.Empty(none.OldFilesLeft);
    }

    [Fact]
    public void EveryPublicFeedWriteTakesTheExceptions()
    {
        // An overload without the list would skip the cut. The refusal while the list is unreadable is the callers';
        // here an unreadable list only throws (AnUnreadableExceptionListThrowsRatherThanWritingAnUncutFeed).
        var overloads = typeof(ExportWriter).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(ExportWriter.WriteFortigateFeeds))
            .ToList();

        Assert.NotEmpty(overloads);
        Assert.All(overloads, method => Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(ExceptionList)));
    }

    [Fact]
    public void FeedsWriteOnlyValidAddressesAndNetworks()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord>
        {
            GoogleV4,
            GoogleV4 with { Network = "1.0.168.172" },
            GoogleV4 with { Network = "8.8.4.0/24\n9.9.9.0/24" },
            GoogleV4 with { Network = " 8.8.4.0/24" },
            GoogleV4 with { Network = "not a network" },
            GoogleV4 with { Network = "8.8.4.0/33" },
            GermanV6,
            GermanV6 with { Network = "2a02:26f0::1" },
            GermanV6 with { Network = "[2a02:26f0::1]:80" },
        };

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc, ExceptionList.Empty).Files;

        string[] lines = File.ReadAllLines(Assert.Single(files));
        Assert.Contains("# Entries: 4", lines);
        Assert.Equal(["8.8.8.0/24", "1.0.168.172", "2a02:26f0::/29", "2a02:26f0::1"], lines.Where(line => !line.StartsWith('#')));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AS")]
    [InlineData("AS15169x")]
    [InlineData("../15169")]
    [InlineData("15169/../../x")]
    [InlineData("AS1.5")]
    public void InvalidAsnThrows(string asn)
    {
        using var temp = new ExportTempFolder();

        Assert.Throws<ArgumentException>(() =>
            ExportWriter.WriteAsnReport(temp.Folder, asn, "Google LLC", [GoogleV4], FixedUtc));
        Assert.Throws<ArgumentException>(() =>
            ExportWriter.WriteFortigateFeeds(temp.Folder, asn, "Google LLC", [GoogleV4], Settings(), FixedUtc, ExceptionList.Empty).Files);
    }

    [Fact]
    public void ClipboardTextIsExactWithNullFields()
    {
        var rows = new List<NetworkRecord>
        {
            GoogleV4,
            new("2a02:26f0::/29", null, "Germany", "EU", "AS15169", "Google LLC", null),
        };

        string text = ExportWriter.BuildClipboardText(rows);

        Assert.Equal(
            "Network\tCountry code\tCountry\tASN\tAS name\tAS domain\r\n" +
            "8.8.8.0/24\tUS\tUnited States\tAS15169\tGoogle LLC\tgoogle.com\r\n" +
            "2a02:26f0::/29\t\tGermany\tAS15169\tGoogle LLC\t\r\n",
            text);
    }
}
