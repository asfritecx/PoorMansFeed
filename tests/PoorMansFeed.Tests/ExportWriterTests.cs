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
        WriteFortigateFeeds = true,
        FortigateSplitByFamily = split,
        FortigateIncludeIPv4 = v4,
        FortigateIncludeIPv6 = v6,
    };

    private static FeedOptions Options(UserSettings settings) => FeedOptions.ForAsnExport(settings);

    private static ReadableExceptions Readable(ExceptionList list) => ReadableExceptions.WrapForFeed(list)!;

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

        IReadOnlyList<string> files = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;

        string v4Path = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        string v6Path = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt");
        Assert.Equal([v4Path, v6Path], files);

        string v4 = File.ReadAllText(v4Path);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.0/24\n" +
            "1.1.1.0/24\n", v4);

        string v6 = File.ReadAllText(v6Path);
        Assert.Contains("# Address Family: IPv6\n", v6);
        Assert.Contains("# Entries: 1\n", v6);
        Assert.EndsWith("2a02:26f0::/29\n", v6);
    }

    [Fact]
    public void FortigateCombinedWritesIPv4BeforeIPv6()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GermanV6, GoogleV4 };

        IReadOnlyList<string> files = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

        string path = Path.Combine(temp.Folder, "fortigate", "AS15169.txt");
        Assert.Equal([path], files);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4/IPv6\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.0/24\n" +
            "2a02:26f0::/29\n", File.ReadAllText(path));
    }

    [Fact]
    public void FortigateIPv4OnlyWritesNoIPv6File()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };

        IReadOnlyList<string> files = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(v6: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

        Assert.Equal([Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt")], files);
        Assert.False(File.Exists(Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt")));
    }

    [Fact]
    public void FortigateDisabledWritesNothing()
    {
        using var temp = new ExportTempFolder();

        IReadOnlyList<string> files = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4], Options(Settings(enabled: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

        Assert.Empty(files);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder));
    }

    [Fact]
    public void ALineBreakInTheAsNameStaysInItsHeaderLine()
    {
        using var temp = new ExportTempFolder();
        const string name = "Evil\r\n9.9.9.0/24\nCorp\u2028Ltd";

        IReadOnlyList<string> feeds = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", name, [GoogleV4], Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", name, [GoogleV4], FixedUtc);

        string[] feedLines = File.ReadAllLines(Assert.Single(feeds));
        Assert.Contains("# ASN: AS15169 - Evil  9.9.9.0/24 Corp Ltd", feedLines);
        Assert.Equal(["8.8.8.0/24"], feedLines.Where(line => !line.StartsWith('#')));
        FeedFormat.AssertWellFormed(File.ReadAllBytes(feeds[0]));
        Assert.Contains("AS Name:   Evil  9.9.9.0/24 Corp Ltd", File.ReadAllLines(report));
    }

    [Fact]
    public void ANonAsciiAsNameIsWrittenUnchanged()
    {
        using var temp = new ExportTempFolder();
        const string name = "Telef\u00F3nica de Espa\u00F1a";

        IReadOnlyList<string> feeds = ExportWriter.WriteFeeds(
            temp.Folder, "AS3352", name, [GoogleV4], Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

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

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(exceptions));

        Assert.Equal(2, written.ExceptionCuts);
        Assert.Equal(1, written.ExceptionRemovals);
        Assert.Empty(written.EmptiedFiles);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 2\n" +
            "# Exceptions: 1 range cut, 1 removed\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n" +
            "8.8.8.128/25\n" +
            "1.1.1.0/24\n", File.ReadAllText(written.Files[0]));
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

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(exceptions));

        string[] lines = File.ReadAllLines(Assert.Single(written.Files));
        Assert.Contains("# Entries: 4", lines);
        Assert.Contains("# Exceptions: 1 range cut, 1 removed", lines);
        Assert.Equal(["8.8.8.0/24", "2a02:26f1::/32", "2a02:26f2::/31", "2a02:26f4::/30"], lines.Where(line => !line.StartsWith('#')));
    }

    private static readonly List<NetworkRecord> HostRows =
    [
        GoogleV4 with { Network = "8.8.8.8/32" },
        GermanV6 with { Network = "2001:4860::8888/128" },
        GermanV6 with { Network = "2001:db8::/32" },
        GoogleV4 with { Network = "9.9.9.9" },
    ];

    [Fact]
    public void ASingleHostIsWrittenBareInEachFamilysFeed()
    {
        using var temp = new ExportTempFolder();

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", HostRows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty));

        Assert.Equal(2, written.Files.Count);
        string[] v4 = File.ReadAllLines(written.Files[0]);
        Assert.Contains("# Entries: 2", v4);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], v4.Where(line => !line.StartsWith('#')));
        string[] v6 = File.ReadAllLines(written.Files[1]);
        Assert.Contains("# Entries: 2", v6);
        Assert.Equal(["2001:4860::8888", "2001:db8::/32"], v6.Where(line => !line.StartsWith('#')));
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
    }

    [Fact]
    public void ASingleHostIsWrittenBareInTheCombinedFeed()
    {
        using var temp = new ExportTempFolder();

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", HostRows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty));

        string file = Assert.Single(written.Files);
        string[] lines = File.ReadAllLines(file);
        Assert.Contains("# Entries: 4", lines);
        Assert.Equal(
            ["8.8.8.8", "9.9.9.9", "2001:4860::8888", "2001:db8::/32"],
            lines.Where(line => !line.StartsWith('#')));
        FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
    }

    [Fact]
    public void AnExceptionCutThatLeavesOneHostWritesItBare()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord>
        {
            GoogleV4 with { Network = "8.8.8.0/30" },
            GermanV6 with { Network = "2001:4860::/127" },
        };
        ExceptionList exceptions = RangeSubtractionTests.List("8.8.8.0/31", "8.8.8.3/32", "2001:4860::1/128");

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(exceptions));

        Assert.Equal(2, written.Files.Count);
        string[] v4 = File.ReadAllLines(written.Files[0]);
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", v4);
        Assert.Equal(["8.8.8.2"], v4.Where(line => !line.StartsWith('#')));
        string[] v6 = File.ReadAllLines(written.Files[1]);
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", v6);
        Assert.Equal(["2001:4860::"], v6.Where(line => !line.StartsWith('#')));
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
    }

    [Fact]
    public void TheAsnReportKeepsTheDatasetsTextForASingleHost()
    {
        using var temp = new ExportTempFolder();

        string path = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", HostRows, FixedUtc);

        string text = File.ReadAllText(path);
        Assert.Contains("| 8.8.8.8/32 ", text);
        Assert.Contains("| 2001:4860::8888/128 ", text);
        Assert.Contains("  4 row(s)" + Environment.NewLine, text);
        Assert.EndsWith("(https://ipinfo.io)" + Environment.NewLine, text);
    }

    [Fact]
    public void ExceptionsThatTouchNothingWriteTheFeedAsBefore()
    {
        using var temp = new ExportTempFolder();
        using var before = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6, OtherV4 };

        IReadOnlyList<string> plain = ExportWriter.WriteFeeds(before.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(RangeSubtractionTests.List("8.8.9.0/24", "8.8.7.255", "::808:800/120")));

        Assert.Equal(0, written.ExceptionCuts + written.ExceptionRemovals);
        Assert.Equal(File.ReadAllBytes(plain[0]), File.ReadAllBytes(written.Files[0]));
        Assert.Equal(File.ReadAllBytes(plain[1]), File.ReadAllBytes(written.Files[1]));
    }

    [Fact]
    public void AFamilyExceptionsEmptiedRewritesItsFileInEachFolderHeaderOnlyAndNamesIt()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        UserSettings both = Settings() with { WriteModSecurityLists = true };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(both), FixedUtc, Readable(ExceptionList.Empty)).Files;
        string[] fortigate = [first[0], first[1]];
        string[] modSecurity = [first[2], first[3]];

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(both), FixedUtc, Readable(RangeSubtractionTests.List("2a02:26f0::/29")));

        // Each emptied file takes the slot its family's file would, so FortiGate's copies still come first
        Assert.Equal([fortigate[0], fortigate[1], modSecurity[0], modSecurity[1]], written.Files);
        Assert.Equal([fortigate[1], modSecurity[1]], written.EmptiedFiles);
        Assert.Equal([modSecurity[0], modSecurity[1]], written.ModSecurityFiles);
        Assert.Equal(1, written.ExceptionRemovals);
        string body =
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv6\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 0\n" +
            "# Exceptions: 0 ranges cut, 1 removed\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n";
        Assert.Equal("# PoorMansFeed - Fortigate Threat Feed\n" + body, File.ReadAllText(fortigate[1]));
        Assert.EndsWith(body, File.ReadAllText(modSecurity[1]), StringComparison.Ordinal);
        foreach (string file in written.Files)
            FeedFormat.AssertWellFormed(File.ReadAllBytes(file));
        // Each emptied file's old entry is a removal, and the family with lines changed nothing
        FeedFileChange[] emptied = written.Changes.Where(c => written.EmptiedFiles.Contains(c.Path)).ToArray();
        Assert.Equal(2, emptied.Length);
        Assert.All(emptied, c => Assert.Equal(["2a02:26f0::/29"], c.IPv6.Removed));
        Assert.All(written.Changes.Except(emptied), c => Assert.True(c.IsEmpty));
    }

    [Fact]
    public void AnEmptiedFamilyWithNoFileGetsNoneAndADeletedOneStaysAbsent()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        string ipv6 = Path.Combine(temp.Folder, ExportWriter.FortigateFolderName, "AS15169_ipv6.txt");

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(RangeSubtractionTests.List("2a02:26f0::/29")));

        Assert.Equal([first[0]], written.Files);
        Assert.Empty(written.EmptiedFiles);
        Assert.False(File.Exists(ipv6));

        // An emptied family's file the user deleted, and the one of a type folder never written, stay absent
        File.Delete(first[0]);
        FeedWrite deleted = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows,
            Options(Settings() with { WriteModSecurityLists = true }), FixedUtc, Readable(RangeSubtractionTests.List("0.0.0.0/0", "::/0")));
        Assert.Empty(deleted.Files);
        Assert.Empty(deleted.EmptiedFiles);
        Assert.Empty(Directory.GetFiles(temp.Folder, "AS15169_*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFamilyHoldingOnlyTheWholeRangeIsEmptiedWithItsLeftOutLine()
    {
        using var temp = new ExportTempFolder();
        NetworkRecord wholeV4 = GoogleV4 with { Network = "0.0.0.0/0" };
        NetworkRecord wholeV6 = GermanV6 with { Network = "::/0" };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, wholeV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty));

        Assert.Equal(first, written.Files);
        Assert.Equal([first[1]], written.EmptiedFiles);
        Assert.Equal(1, written.WholeRangesLeftOut);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv6\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 0\n" +
            "# Left out: 1 range covering every address: ::/0\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n",
            File.ReadAllText(first[1]));
        Assert.Equal(["2a02:26f0::/29"], written.Changes[1].IPv6.Removed);

        FeedWrite onlyWhole = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [wholeV4], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty));
        // The IPv6 family has no network at all now, so its existing file is emptied as well
        Assert.Equal(first, onlyWhole.EmptiedFiles);
        Assert.Contains("# Left out: 1 range covering every address: 0.0.0.0/0\n", File.ReadAllText(first[0]));
        Assert.Equal(["8.8.8.0/24"], onlyWhole.Changes[0].IPv4.Removed);
    }

    [Fact]
    public void AFamilyWhoseOnlyRangeWasLeftOutAsLargeIsEmptiedAndTheOtherWrittenAsBefore()
    {
        using var temp = new ExportTempFolder();
        using var plain = new ExportTempFolder();
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        IReadOnlyList<string> ipv6Only = ExportWriter.WriteFeeds(plain.Folder, "AS15169", "Google LLC", [GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;

        FeedWrite written = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty));

        Assert.Equal(first, written.Files);
        Assert.Equal([first[0]], written.EmptiedFiles);
        Assert.Equal(0, written.WholeRangesLeftOut + written.ExceptionCuts + written.ExceptionRemovals);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 0\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n",
            File.ReadAllText(first[0]));
        Assert.Equal(["8.8.8.0/24"], written.Changes[0].IPv4.Removed);
        Assert.Equal(File.ReadAllBytes(ipv6Only[0]), File.ReadAllBytes(first[1]));

        // A left-out range whose family a setting switches off empties nothing, and with no old file none is made
        File.Delete(first[1]);
        FeedWrite none = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [], Options(Settings(v4: false)), FixedUtc, Readable(ExceptionList.Empty));
        Assert.Empty(none.Files);
        Assert.False(File.Exists(first[1]));
    }

    [Fact]
    public void ACombinedFeedTheExceptionsEmptiedIsRewrittenHeaderOnly()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

        FeedWrite combined = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(RangeSubtractionTests.List("0.0.0.0/0", "::/0")));

        Assert.Equal(first, combined.Files);
        Assert.Equal(first, combined.EmptiedFiles);
        Assert.Equal(
            "# PoorMansFeed - Fortigate Threat Feed\n" +
            "# ASN: AS15169 - Google LLC\n" +
            "# Address Family: IPv4/IPv6\n" +
            "# Generated: 2026-03-04 05:06:07 UTC\n" +
            "# Entries: 0\n" +
            "# Exceptions: 0 ranges cut, 2 removed\n" +
            "# IP address data powered by IPinfo (https://ipinfo.io)\n",
            File.ReadAllText(first[0]));
        Assert.Equal(["8.8.8.0/24"], combined.Changes[0].IPv4.Removed);
        Assert.Equal(["2a02:26f0::/29"], combined.Changes[0].IPv6.Removed);
    }

    [Fact]
    public void ASuccessfulExportLeavesNoTemporaryFile()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", rows, FixedUtc);
        IReadOnlyList<string> split = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        IReadOnlyList<string> combined = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

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
        IReadOnlyList<string> plainFeeds = ExportWriter.WriteFeeds(
            plain.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
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
        IReadOnlyList<string> writtenFeeds = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;

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
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        byte[] oldV6 = File.ReadAllBytes(first[1]);
        // A folder where the feed's temporary file should go makes that one write fail
        Directory.CreateDirectory(first[1] + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc.AddHours(1), Readable(ExceptionList.Empty)));

        Assert.Equal(first[1], ex.FilePath);
        Assert.Equal([first[0]], ex.WrittenBefore);
        Assert.Equal([first[1]], ex.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(first[1]));
        Assert.Contains("# Generated: 2026-03-04 06:06:07 UTC", File.ReadAllText(first[0]));

        // A family the exceptions emptied is rewritten in its slot before the failed file, so it is written, not left
        File.WriteAllText(first[0], "8.8.8.0/24\n");
        var cut = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(RangeSubtractionTests.List("8.8.8.0/24"))));
        Assert.Equal([first[0]], cut.WrittenBefore);
        Assert.Equal([first[1]], cut.OldFilesLeft);
        Assert.Contains("# Entries: 0\n", File.ReadAllText(first[0]));
        Assert.Equal(["8.8.8.0/24"], Assert.Single(cut.ChangesBefore).IPv4.Removed);
    }

    [Fact]
    public void AFailedWriteNamesAFamilyTheExceptionsEmptiedAsUntriedWhenThatFamilyComesLater()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        string ipv4 = first[0];
        string ipv6 = first[1];
        byte[] oldV4 = File.ReadAllBytes(ipv4);
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // The IPv4 write fails before the IPv6 family, which the exceptions emptied, is tried, so its old file is left
        Directory.CreateDirectory(ipv4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc.AddHours(1), Readable(RangeSubtractionTests.List("2a02:26f0::/29"))));

        Assert.Equal(ipv4, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Equal([ipv4, ipv6], ex.OldFilesLeft);
        Assert.Equal(oldV4, File.ReadAllBytes(ipv4));
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));
    }

    [Fact]
    public void AFailedIpv4WriteNamesTheOldIpv6FeedItNeverTried()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> first = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        string ipv4 = first[0];
        string ipv6 = first[1];
        byte[] oldV6 = File.ReadAllBytes(ipv6);
        // The IPv4 write fails, so the IPv6 feed is never tried and its earlier copy stays for FortiGate to read
        Directory.CreateDirectory(ipv4 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc.AddHours(1), Readable(ExceptionList.Empty)));

        Assert.Equal(ipv4, ex.FilePath);
        Assert.Empty(ex.WrittenBefore);
        Assert.Equal([ipv4, ipv6], ex.OldFilesLeft);
        Assert.Equal(oldV6, File.ReadAllBytes(ipv6));

        // With no earlier IPv6 feed only the failed file is named
        File.Delete(ipv6);
        var noV6 = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)));
        Assert.Equal([ipv4], noV6.OldFilesLeft);
    }

    [Fact]
    public void ThePlannedFilesThatExistAreTheFeedsAnExportWouldWriteOrNote()
    {
        using var temp = new ExportTempFolder();
        var rows = new List<NetworkRecord> { GoogleV4, GermanV6 };
        IReadOnlyList<string> split = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", rows, Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;
        IReadOnlyList<string> combined = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

        Assert.Equal(split, Existing(temp.Folder, rows, Options(Settings())));
        Assert.Equal(combined, Existing(temp.Folder, rows, Options(Settings(split: false))));
        // A family with no network left still names its file; the feed turned off, or IPv6 turned off, names none of it
        Assert.Equal(split, Existing(temp.Folder, [GoogleV4], Options(Settings())));
        Assert.Equal([split[0]], Existing(temp.Folder, rows, Options(Settings() with { IPv6Enabled = false })));
        Assert.Empty(Existing(temp.Folder, rows, Options(Settings() with { FortigateEnabled = false })));
        // A file that isn't there is not named
        File.Delete(split[0]);
        Assert.Equal([split[1]], Existing(temp.Folder, rows, Options(Settings())));
    }

    [Fact]
    public void ThePlannedFilesThatExistNameTheFileOfAFamilyWithNoNetworkLeft()
    {
        using var temp = new ExportTempFolder();
        IReadOnlyList<string> split = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files;

        // The IPv6 family has no network left (declined as large or gone), so its old file is emptied and named
        Assert.Equal(split, Existing(temp.Folder, [GoogleV4], Options(Settings())));
        File.Delete(split[1]);
        Assert.Equal([split[0]], Existing(temp.Folder, [GoogleV4], Options(Settings())));
    }

    [Fact]
    public void AFeedWriteThatFailsWithNoEarlierCopyNamesNoOldFile()
    {
        using var temp = new ExportTempFolder();
        string ipv6 = Path.Combine(temp.Folder, ExportWriter.FortigateFolderName, "AS15169_ipv6.txt");
        Directory.CreateDirectory(ipv6 + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)));

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
    public void EveryFeedWriteTakesReadableExceptions()
    {
        // Without the list an overload would skip the cut; over an ExceptionList it could be given an unreadable one
        var overloads = typeof(ExportWriter).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(method => method.Name == nameof(ExportWriter.WriteFeeds))
            .ToList();

        Assert.NotEmpty(overloads);
        Assert.All(overloads, method =>
        {
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(ReadableExceptions));
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(ExceptionList));
        });
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

        IReadOnlyList<string> files = ExportWriter.WriteFeeds(
            temp.Folder, "AS15169", "Google LLC", rows, Options(Settings(split: false)), FixedUtc, Readable(ExceptionList.Empty)).Files;

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
            ExportWriter.WriteFeeds(temp.Folder, asn, "Google LLC", [GoogleV4], Options(Settings()), FixedUtc, Readable(ExceptionList.Empty)).Files);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAsnFeedBytesArePinnedWithTheRenderVersion(bool split)
    {
        using var temp = new ExportTempFolder();
        UserSettings settings = Settings(split: split) with { WriteModSecurityLists = true };
        ExceptionList exceptions = ExceptionList.Create([ExceptionEntryTests.Entry("8.8.8.128/25", "Pinned")]);

        ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6, OtherV4], Options(settings),
                                FixedUtc, Readable(exceptions));

        FeedGoldenHashes.AssertPinned(split ? "ASN split" : "ASN combined", temp.Folder, split
            ? new Dictionary<string, string>
            {
                ["fortigate/AS15169_ipv4.txt"] = "08d8a4cf58d31f2a5fa162c8478f9cfc74ae2f27ed6a61e2f52f881c99d835cf",
                ["fortigate/AS15169_ipv6.txt"] = "8e5ab3166c3c964589957d602a3f9df0fab9b93c77b23589b3a5d3c4acb7d39c",
                ["modsecurity/AS15169_ipv4.txt"] = "4d31fb926b189917e45c197934611ad6a233021d1292d6ab02bf62f6c9b5e313",
                ["modsecurity/AS15169_ipv6.txt"] = "fa462e13245a3ecb73db9ba11f2bcbc21f915d73e823dbacd29f339e90816c85",
            }
            : new Dictionary<string, string>
            {
                ["fortigate/AS15169.txt"] = "d5c52c5728b8c4b94c1572d14bf8066f3f8e23d581e5b65aaef46e66caf8cb7d",
                ["modsecurity/AS15169.txt"] = "c95a9abea9281a8d184e2aed13286b390e06e2b5048e9c812af0277eebb944c0",
            });
    }

    [Fact]
    public void TheAsnHeaderOnlyExceptionsAndLeftOutBytesArePinnedWithTheRenderVersion()
    {
        using var split = new ExportTempFolder();
        UserSettings settings = Settings() with { WriteModSecurityLists = true };
        var whole = new NetworkRecord("0.0.0.0/0", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");
        ExceptionList exceptions = ExceptionList.Create(
            [ExceptionEntryTests.Entry("8.8.8.128/25", "Pinned"), ExceptionEntryTests.Entry("2a02:26f0::/29", "Pinned")]);
        foreach (FeedType type in FeedTypes.All)
            WriteOld(Path.Combine(split.Folder, FeedTypes.FolderName(type), "AS15169_ipv6.txt"));

        ExportWriter.WriteFeeds(split.Folder, "AS15169", "Google LLC", [GoogleV4, whole, GermanV6], Options(settings),
                                FixedUtc, Readable(exceptions));

        string[] ipv4 = File.ReadAllLines(Path.Combine(split.Folder, "fortigate", "AS15169_ipv4.txt"));
        Assert.Contains("# Exceptions: 1 range cut, 0 removed", ipv4);
        Assert.Contains("# Left out: 1 range covering every address: 0.0.0.0/0", ipv4);
        string[] ipv6 = File.ReadAllLines(Path.Combine(split.Folder, "fortigate", "AS15169_ipv6.txt"));
        Assert.Contains("# Entries: 0", ipv6);
        Assert.Contains("# Exceptions: 0 ranges cut, 1 removed", ipv6);
        FeedGoldenHashes.AssertPinned("ASN split, header-only", split.Folder, new Dictionary<string, string>
        {
            ["fortigate/AS15169_ipv4.txt"] = "8c515b0228b480788fea4eaa785f94107254d12478c36f43a80834c57536b80d",
            ["fortigate/AS15169_ipv6.txt"] = "777f8366e71d6446df766f11394fb911eb45904b37cea0be341f7df771e82282",
            ["modsecurity/AS15169_ipv4.txt"] = "cdb960b1a88c169a01e95f0f8c827b64f66a4badb0fb672afef32e94be6c71bc",
            ["modsecurity/AS15169_ipv6.txt"] = "697c7b9cb33260f36725a7e664a6c33c30e451aeb2380dff8a78b3e5fe15f0e7",
        });

        using var combined = new ExportTempFolder();
        foreach (FeedType type in FeedTypes.All)
            WriteOld(Path.Combine(combined.Folder, FeedTypes.FolderName(type), "AS15169.txt"));

        ExportWriter.WriteFeeds(combined.Folder, "AS15169", "Google LLC", [], Options(settings with { FortigateSplitByFamily = false }),
                                FixedUtc, Readable(ExceptionList.Empty));

        Assert.Contains("# Entries: 0", File.ReadAllLines(Path.Combine(combined.Folder, "fortigate", "AS15169.txt")));
        FeedGoldenHashes.AssertPinned("ASN combined, header-only", combined.Folder, new Dictionary<string, string>
        {
            ["fortigate/AS15169.txt"] = "014dba7e0a85fe7226c895b4fbad626b7451c086e2e390a992c16051a9e988ec",
            ["modsecurity/AS15169.txt"] = "8504867084f2a61a6dcd490382b64abd5c23a0dac5cfad85f494ca44fb38c42a",
        });
    }

    [Fact]
    public void A_family_with_no_networks_left_rewrites_its_existing_files_header_only()
    {
        using var temp = new ExportTempFolder();
        FeedOptions options = Options(Settings() with { WriteModSecurityLists = true });
        string[] old = FeedTypes.All.Select(type => Path.Combine(temp.Folder, FeedTypes.FolderName(type), "AS15169_ipv6.txt")).ToArray();
        foreach (string path in old)
            WriteOld(path, "2a02:26f0::/29");

        FeedWrite write = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], options, FixedUtc,
                                                  Readable(ExceptionList.Empty));

        Assert.Equal(old, write.EmptiedFiles);
        foreach (string path in old)
        {
            string[] lines = File.ReadAllLines(path);
            Assert.Contains("# Entries: 0", lines);
            Assert.All(lines, line => Assert.StartsWith("#", line));
        }
    }

    [Fact]
    public void A_combined_feed_with_every_network_declined_still_empties_its_file()
    {
        using var temp = new ExportTempFolder();
        string path = Path.Combine(temp.Folder, "fortigate", "AS15169.txt");
        WriteOld(path);

        FeedWrite write = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [], Options(Settings(split: false)),
                                                  FixedUtc, Readable(ExceptionList.Empty));

        Assert.Equal([path], write.EmptiedFiles);
        Assert.Contains("# Entries: 0", File.ReadAllLines(path));
    }

    [Fact]
    public void A_family_switched_off_leaves_its_old_file_untouched()
    {
        using var temp = new ExportTempFolder();
        string path = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt");
        WriteOld(path, "2a02:26f0::/29");
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);

        FeedWrite write = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6],
                                                  Options(Settings(v6: false)), FixedUtc, Readable(ExceptionList.Empty));

        Assert.Empty(write.EmptiedFiles);
        Assert.DoesNotContain(path, write.Files);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void A_family_with_no_networks_and_no_file_gets_none()
    {
        using var temp = new ExportTempFolder();

        FeedWrite write = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], Options(Settings()), FixedUtc,
                                                  Readable(ExceptionList.Empty));

        Assert.Equal([Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt")], write.Files);
        Assert.False(File.Exists(Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt")));
        Assert.Empty(write.EmptiedFiles);
    }

    [Fact]
    public void A_combined_feed_with_both_families_switched_off_leaves_its_old_file_untouched()
    {
        using var temp = new ExportTempFolder();
        string path = Path.Combine(temp.Folder, "fortigate", "AS15169.txt");
        WriteOld(path);
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        byte[] before = File.ReadAllBytes(path);
        FeedOptions options = Options(Settings(split: false, v4: false, v6: false));

        AsnFeedPlan plan = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], options, FixedUtc,
                                                  Readable(ExceptionList.Empty), datasetHasIPv6: true);
        FeedWrite write = ExportWriter.WriteFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], options, FixedUtc,
                                                  Readable(ExceptionList.Empty));

        Assert.Empty(plan.Files);
        Assert.Empty(write.Files);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void An_IPv6_family_the_dataset_does_not_cover_leaves_its_old_file_and_one_with_lines_is_written()
    {
        using var temp = new ExportTempFolder();
        string ipv6 = Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt");
        WriteOld(ipv6, "2a02:26f0::/29");

        AsnFeedPlan uncovered = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], Options(Settings()),
                                                       FixedUtc, Readable(ExceptionList.Empty), datasetHasIPv6: false);
        AsnFeedPlan withLines = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6],
                                                       Options(Settings()), FixedUtc, Readable(ExceptionList.Empty),
                                                       datasetHasIPv6: false);
        AsnFeedPlan covered = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], Options(Settings()),
                                                     FixedUtc, Readable(ExceptionList.Empty), datasetHasIPv6: true);

        Assert.Equal([Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt")], uncovered.Files.Select(file => file.Path));
        Assert.Contains(withLines.Files, file => file.Path == ipv6 && !file.Emptied);
        Assert.Contains(covered.Files, file => file.Path == ipv6 && file.Emptied);
    }

    [Fact]
    public void A_combined_IPv6_only_feed_the_dataset_does_not_cover_leaves_its_old_file_and_one_with_lines_is_written()
    {
        using var temp = new ExportTempFolder();
        string path = Path.Combine(temp.Folder, "fortigate", "AS15169.txt");
        WriteOld(path, "2a02:26f0::/29");
        FeedOptions options = Options(Settings(split: false, v4: false, v6: true));

        AsnFeedPlan uncovered = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], options, FixedUtc,
                                                       Readable(ExceptionList.Empty), datasetHasIPv6: false);
        AsnFeedPlan withLines = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4, GermanV6], options,
                                                       FixedUtc, Readable(ExceptionList.Empty), datasetHasIPv6: false);
        AsnFeedPlan covered = ExportWriter.PlanFeeds(temp.Folder, "AS15169", "Google LLC", [GoogleV4], options, FixedUtc,
                                                     Readable(ExceptionList.Empty), datasetHasIPv6: true);

        Assert.Empty(uncovered.Files);
        Assert.Contains(withLines.Files, file => file.Path == path && !file.Emptied);
        Assert.Contains(covered.Files, file => file.Path == path && file.Emptied);
    }

    private static void WriteOld(string path, string entry = "9.9.9.0/24")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# old\n" + entry + "\n");
    }

    private static IReadOnlyList<string> Existing(string folder, IReadOnlyList<NetworkRecord> rows, FeedOptions options) =>
        ExportWriter.ExistingFiles(ExportWriter.PlanFeeds(folder, "AS15169", "Google LLC", rows, options, FixedUtc,
                                                          Readable(ExceptionList.Empty), datasetHasIPv6: true));
}
