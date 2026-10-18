using PoorMansFeed.Core;

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

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(temp.Folder, "AS15169", "Google LLC", rows, Settings(), FixedUtc);

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
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc);

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
            temp.Folder, "AS15169", "Google LLC", rows, Settings(v6: false), FixedUtc);

        Assert.Equal([Path.Combine(temp.Folder, "fortigate", "AS15169_ipv4.txt")], files);
        Assert.False(File.Exists(Path.Combine(temp.Folder, "fortigate", "AS15169_ipv6.txt")));
    }

    [Fact]
    public void FortigateDisabledWritesNothing()
    {
        using var temp = new ExportTempFolder();

        IReadOnlyList<string> files = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", "Google LLC", [GoogleV4], Settings(enabled: false), FixedUtc);

        Assert.Empty(files);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Folder));
    }

    [Fact]
    public void ALineBreakInTheAsNameStaysInItsHeaderLine()
    {
        using var temp = new ExportTempFolder();
        const string name = "Evil\r\n9.9.9.0/24\nCorp\u2028Ltd";

        IReadOnlyList<string> feeds = ExportWriter.WriteFortigateFeeds(
            temp.Folder, "AS15169", name, [GoogleV4], Settings(split: false), FixedUtc);
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
            temp.Folder, "AS3352", name, [GoogleV4], Settings(split: false), FixedUtc);

        Assert.Contains("# ASN: AS3352 - " + name, File.ReadAllLines(Assert.Single(feeds)));
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
            temp.Folder, "AS15169", "Google LLC", rows, Settings(split: false), FixedUtc);

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
            ExportWriter.WriteFortigateFeeds(temp.Folder, asn, "Google LLC", [GoogleV4], Settings(), FixedUtc));
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
