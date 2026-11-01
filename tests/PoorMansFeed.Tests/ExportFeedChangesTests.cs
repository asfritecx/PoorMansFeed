using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

public sealed class ExportFeedChangesTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly NetworkRecord Range4 = new(
        "8.8.8.0/24", "US", "United States", "NA", "AS15169", "Google LLC", "google.com");

    private static readonly NetworkRecord Host4 = Range4 with { Network = "8.8.4.4/32" };

    private static readonly NetworkRecord Other4 = Range4 with { Network = "1.1.1.0/24" };

    private static readonly NetworkRecord Range6 = Range4 with { Network = "2a02:26f0::/29" };

    private static readonly NetworkRecord Host6 = Range4 with { Network = "2001:4860::8888/128" };

    private static readonly NetworkRecord Whole6 = Range4 with { Network = "::/0" };

    private static readonly List<NetworkRecord> Rows = [Range4, Host4, Range6, Host6];

    private static FeedOptions Options(bool fortigate = true, bool modSecurity = false, bool split = true) =>
        FeedOptions.ForAsnExport(new UserSettings
        {
            FortigateEnabled = true,
            WriteFortigateFeeds = fortigate,
            WriteModSecurityLists = modSecurity,
            FortigateSplitByFamily = split,
        });

    private static FeedWrite Write(string folder, IReadOnlyList<NetworkRecord> rows, FeedOptions? options = null,
                                   ExceptionList? exceptions = null, DateTimeOffset? at = null) =>
        ExportWriter.WriteFeeds(folder, "AS15169", "Google LLC", rows, options ?? Options(), at ?? FixedUtc,
            ReadableExceptions.WrapForFeed(exceptions ?? ExceptionList.Empty)!);

    private static string In(string folder, string type, string name) => Path.Combine(folder, type, name);

    private static void AssertChange(FeedEntryChanges actual, string[] added, string[] removed)
    {
        Assert.Equal(added, actual.Added);
        Assert.Equal(removed, actual.Removed);
    }

    [Fact]
    public void A_first_write_adds_every_entry_as_written_in_its_own_family_and_file_order()
    {
        using var temp = new ExportTempFolder();

        FeedWrite write = Write(temp.Folder, Rows);

        Assert.Equal(write.Files, write.Changes.Select(change => change.Path));
        Assert.Equal(2, write.Changes.Count);
        AssertChange(write.Changes[0].IPv4, ["8.8.8.0/24", "8.8.4.4"], []);
        AssertChange(write.Changes[0].IPv6, [], []);
        AssertChange(write.Changes[1].IPv4, [], []);
        AssertChange(write.Changes[1].IPv6, ["2a02:26f0::/29", "2001:4860::8888"], []);
    }

    [Fact]
    public void Writing_the_same_rows_again_changes_nothing_and_no_byte()
    {
        using var temp = new ExportTempFolder();
        FeedWrite first = Write(temp.Folder, Rows);
        byte[][] before = first.Files.Select(File.ReadAllBytes).ToArray();

        FeedWrite again = Write(temp.Folder, Rows);

        Assert.Equal(first.Files, again.Changes.Select(change => change.Path));
        Assert.All(again.Changes, change => Assert.True(change.IsEmpty));
        Assert.Equal(before, again.Files.Select(File.ReadAllBytes).ToArray());
    }

    [Fact]
    public void A_dropped_row_and_an_added_row_are_Removed_and_Added_in_their_family()
    {
        using var temp = new ExportTempFolder();
        Write(temp.Folder, Rows);

        FeedWrite write = Write(temp.Folder, [Range4, Other4, Range6]);

        AssertChange(write.Changes[0].IPv4, ["1.1.1.0/24"], ["8.8.4.4"]);
        AssertChange(write.Changes[0].IPv6, [], []);
        AssertChange(write.Changes[1].IPv4, [], []);
        AssertChange(write.Changes[1].IPv6, [], ["2001:4860::8888"]);
    }

    [Fact]
    public void An_old_file_spelling_an_entry_another_way_counts_no_change_for_it()
    {
        using var temp = new ExportTempFolder();
        IReadOnlyList<string> files = Write(temp.Folder, Rows).Files;
        // A byte order mark, CRLF, a /32 suffix, and upper-case or longer IPv6
        File.WriteAllBytes(files[0], [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# old\r\n8.8.8.0/24\r\n8.8.4.4/32\r\n")]);
        File.WriteAllText(files[1], "# old\n2A02:26F0::/29\n2001:4860:0:0::8888/128\n");

        FeedWrite write = Write(temp.Folder, Rows);

        Assert.Equal(2, write.Changes.Count);
        Assert.All(write.Changes, change => Assert.True(change.IsEmpty));
    }

    [Fact]
    public void An_unsplit_file_fills_both_families()
    {
        using var temp = new ExportTempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Folder, "fortigate"));
        File.WriteAllText(In(temp.Folder, "fortigate", "AS15169.txt"), "1.1.1.0/24\n2001:4860::8888\n");

        FeedWrite write = Write(temp.Folder, [Range4, Range6, Host6], Options(split: false));

        FeedFileChange change = Assert.Single(write.Changes);
        Assert.Equal(In(temp.Folder, "fortigate", "AS15169.txt"), change.Path);
        AssertChange(change.IPv4, ["8.8.8.0/24"], ["1.1.1.0/24"]);
        AssertChange(change.IPv6, ["2a02:26f0::/29"], []);
    }

    [Fact]
    public void With_both_types_ticked_FortiGate_comes_first_and_each_copy_is_compared_with_its_own_old_file()
    {
        using var temp = new ExportTempFolder();
        string fortigateV4 = In(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        string modSecurityV4 = In(temp.Folder, "modsecurity", "AS15169_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(fortigateV4)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modSecurityV4)!);
        File.WriteAllText(fortigateV4, "# old\n8.8.8.0/24\n");
        File.WriteAllText(modSecurityV4, "# old\n1.1.1.0/24\n");

        FeedWrite write = Write(temp.Folder, Rows, Options(modSecurity: true));

        Assert.Equal(
            [fortigateV4, In(temp.Folder, "fortigate", "AS15169_ipv6.txt"), modSecurityV4, In(temp.Folder, "modsecurity", "AS15169_ipv6.txt")],
            write.Changes.Select(change => change.Path));
        Assert.Equal(write.Files, write.Changes.Select(change => change.Path));
        AssertChange(write.Changes[0].IPv4, ["8.8.4.4"], []);
        AssertChange(write.Changes[2].IPv4, ["8.8.8.0/24", "8.8.4.4"], ["1.1.1.0/24"]);
        AssertChange(write.Changes[1].IPv6, ["2a02:26f0::/29", "2001:4860::8888"], []);
        AssertChange(write.Changes[3].IPv6, ["2a02:26f0::/29", "2001:4860::8888"], []);
    }

    [Fact]
    public void A_family_the_exceptions_or_the_whole_range_rule_empty_gives_its_old_entries_as_removed()
    {
        using var temp = new ExportTempFolder();
        IReadOnlyList<string> files = Write(temp.Folder, Rows).Files;
        FeedWrite cut = Write(temp.Folder, Rows, exceptions: RangeSubtractionTests.List("2a02:26f0::/29", "2001:4860::8888"));
        Write(temp.Folder, Rows);
        FeedWrite whole = Write(temp.Folder, [Range4, Host4, Whole6]);

        foreach (FeedWrite write in new[] { cut, whole })
        {
            Assert.Equal(files, write.Changes.Select(change => change.Path));
            Assert.True(write.Changes[0].IsEmpty);
            AssertChange(write.Changes[1].IPv6, [], ["2a02:26f0::/29", "2001:4860::8888"]);
            Assert.Equal([files[1]], write.EmptiedFiles);
        }
    }

    [Fact]
    public void An_exception_cut_gives_its_pieces_as_Added_and_the_whole_range_as_Removed()
    {
        using var temp = new ExportTempFolder();
        Write(temp.Folder, [Range4]);

        FeedWrite write = Write(temp.Folder, [Range4], exceptions: RangeSubtractionTests.List("8.8.8.0/25"));

        AssertChange(Assert.Single(write.Changes).IPv4, ["8.8.8.128/25"], ["8.8.8.0/24"]);
    }

    [Fact]
    public void A_link_at_a_feed_path_counts_as_no_old_file()
    {
        using var temp = new ExportTempFolder();
        using var elsewhere = new ExportTempFolder();
        string target = Path.Combine(elsewhere.Folder, "list.txt");
        File.WriteAllText(target, "8.8.8.0/24\n8.8.4.4\n");
        string feed = In(temp.Folder, "fortigate", "AS15169_ipv4.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(feed)!);
        try
        {
            File.CreateSymbolicLink(feed, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account can't make a symbolic link, so none can be planted at the feed path.");
        }

        FeedWrite write = Write(temp.Folder, [Range4, Host4]);

        FeedFileChange change = Assert.Single(write.Changes);
        AssertChange(change.IPv4, ["8.8.8.0/24", "8.8.4.4"], []);
        Assert.Equal(FeedFileState.Unreadable, change.OldFile);
        Assert.Equal("8.8.8.0/24\n8.8.4.4\n", File.ReadAllText(target));
    }

    [Fact]
    public void A_failure_on_the_second_file_carries_only_the_first_files_change()
    {
        using var temp = new ExportTempFolder();
        IReadOnlyList<string> files = Write(temp.Folder, Rows).Files;
        // A folder where the feed's temporary file should go makes that one write fail
        Directory.CreateDirectory(files[1] + ".tmp");

        var ex = Assert.Throws<ExportFileException>(() => Write(temp.Folder, [.. Rows, Other4], at: FixedUtc.AddHours(1)));

        Assert.Equal(files[1], ex.FilePath);
        FeedFileChange change = Assert.Single(ex.ChangesBefore);
        Assert.Equal(files[0], change.Path);
        AssertChange(change.IPv4, ["1.1.1.0/24"], []);
        AssertChange(change.IPv6, [], []);
    }

    [Fact]
    public void Each_split_file_carries_its_type_and_family_and_whether_an_old_file_was_read()
    {
        using var temp = new ExportTempFolder();

        FeedWrite first = Write(temp.Folder, Rows, Options(modSecurity: true));
        FeedWrite second = Write(temp.Folder, Rows, Options(modSecurity: true));

        foreach (FeedWrite write in new[] { first, second })
        {
            Assert.Equal([FeedType.Fortigate, FeedType.Fortigate, FeedType.ModSecurity, FeedType.ModSecurity],
                         write.Changes.Select(change => change.Type));
            Assert.Equal([FeedFamilies.IPv4, FeedFamilies.IPv6, FeedFamilies.IPv4, FeedFamilies.IPv6],
                         write.Changes.Select(change => change.Families));
        }
        Assert.All(first.Changes, change => Assert.Equal(FeedFileState.Missing, change.OldFile));
        Assert.All(second.Changes, change => Assert.Equal(FeedFileState.Read, change.OldFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_unsplit_file_holds_both_families_whatever_the_IPv6_switch_says(bool ipv6Enabled)
    {
        using var temp = new ExportTempFolder();
        FeedOptions options = FeedOptions.ForAsnExport(new UserSettings
        {
            FortigateEnabled = true,
            WriteFortigateFeeds = true,
            WriteModSecurityLists = true,
            FortigateSplitByFamily = false,
            IPv6Enabled = ipv6Enabled,
        });

        FeedWrite first = Write(temp.Folder, Rows, options);
        FeedWrite second = Write(temp.Folder, Rows, options);

        Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], first.Changes.Select(change => change.Type));
        Assert.Equal([FeedType.Fortigate, FeedType.ModSecurity], second.Changes.Select(change => change.Type));
        Assert.All(first.Changes.Concat(second.Changes), change => Assert.Equal(FeedFamilies.Both, change.Families));
        Assert.All(first.Changes, change => Assert.Equal(FeedFileState.Missing, change.OldFile));
        Assert.All(second.Changes, change => Assert.Equal(FeedFileState.Read, change.OldFile));
    }

    [Fact]
    public void The_report_is_never_a_change_and_no_type_gives_none()
    {
        using var temp = new ExportTempFolder();
        string report = ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", Rows, FixedUtc);

        FeedWrite write = Write(temp.Folder, Rows);
        FeedWrite none = Write(temp.Folder, Rows, Options(fortigate: false));

        Assert.DoesNotContain(report, write.Changes.Select(change => change.Path));
        Assert.Empty(none.Changes);
        Directory.CreateDirectory(report + ".tmp");
        var ex = Assert.Throws<ExportFileException>(() =>
            ExportWriter.WriteAsnReport(temp.Folder, "AS15169", "Google LLC", Rows, FixedUtc));
        Assert.Empty(ex.ChangesBefore);
    }
}
