using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class FeedHistorySearchTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly DbTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(T);

    public void Dispose() => _temp.Dispose();

    private string HistoryPath => _temp.FilePath("history.db");

    private FeedHistory NewHistory() => new(HistoryPath, _time);

    private static FeedFamilyChange IPv4(params string[] added) => new(false, null, FeedFileState.Read, added, []);

    private static FeedFamilyChange IPv6(params string[] added) => new(true, null, FeedFileState.Read, added, []);

    private static FeedChangeRecord Change(string feedKey, DateTimeOffset at, params FeedFamilyChange[] families) =>
        new(at, feedKey, FeedKind.Asn, FeedChangeCause.PageWrite, new DateOnly(2026, 10, 1), null, families);

    private FeedHistory Recorded(params string[] entries)
    {
        FeedHistory history = NewHistory();
        Assert.Equal(HistoryState.Usable, history.Record(Change("asn:15169", T,
            IPv4(entries.Where(e => !e.Contains(':')).ToArray()), IPv6(entries.Where(e => e.Contains(':')).ToArray()))));
        return history;
    }

    private static HistoryRead Search(FeedHistory history, string? match = null, string? feedKey = null,
                                      DateTimeOffset? since = null, int limit = 1000)
    {
        HistoryMatch? parsed = null;
        if (match is not null)
            Assert.True(HistoryMatch.TryParse(match, out parsed));
        return history.Search(new HistoryQuery(feedKey, since, parsed), limit, TestContext.Current.CancellationToken);
    }

    private static string[] Entries(HistoryRead read) => read.Rows.Select(r => r.Entry).ToArray();

    [Fact]
    public void A_match_finds_the_entries_that_contain_it_equal_it_or_lie_inside_it()
    {
        FeedHistory history = Recorded("10.0.0.0/8", "10.1.0.0/16", "10.1.2.0/24", "10.1.2.3", "10.2.0.0/16", "11.0.0.0/8",
                                       "2001:4860::/32", "2001:4860:1::/48", "2001:4860:1:2::/64", "2001:4861::/32");

        HistoryRead v4 = Search(history, "10.1.0.0/16");
        HistoryRead v6 = Search(history, "2001:4860:1::/48");

        Assert.Equal(HistoryState.Usable, v4.State);
        Assert.Equal(["10.0.0.0/8", "10.1.0.0/16", "10.1.2.0/24", "10.1.2.3"], Entries(v4));
        Assert.Equal(4, v4.Total);
        Assert.Equal(["2001:4860::/32", "2001:4860:1::/48", "2001:4860:1:2::/64"], Entries(v6));
    }

    [Theory]
    [InlineData("192.168.4.0", true)]
    [InlineData("192.168.7.255", true)]
    [InlineData("192.168.3.255", false)]
    [InlineData("192.168.8.0", false)]
    [InlineData("192.168.0.0/22", false)]
    [InlineData("192.168.8.0/22", false)]
    [InlineData("192.168.0.0/16", true)]
    [InlineData("2001:4860::", true)]
    [InlineData("2001:4860:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2001:485f:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2001:4861::", false)]
    [InlineData("2001:4800::/23", true)]
    public void A_stored_range_matches_from_its_first_address_to_its_last_and_not_one_past(string match, bool found)
    {
        FeedHistory history = Recorded("192.168.4.0/22", "2001:4860::/32");

        Assert.Equal(found ? 1 : 0, Search(history, match).Rows.Count);
    }

    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("8.8.8.0/24", "8.8.8.8")]
    [InlineData("8.8.8.9", null)]
    [InlineData("2001:4860::8888", "2001:4860::8888")]
    [InlineData("2001:4860::/112", "2001:4860::8888")]
    [InlineData("2001:4860::8889", null)]
    public void A_bare_address_entry_matches_its_own_address(string match, string? expected)
    {
        FeedHistory history = Recorded("8.8.8.8", "2001:4860::8888");

        Assert.Equal(expected is null ? [] : [expected], Entries(Search(history, match)));
    }

    [Theory]
    [InlineData("32.1.13.184", "32.1.13.184")]
    [InlineData("32.1.13.0/24", "32.1.13.184")]
    [InlineData("2001:db0::/28", "2001:db0::/28")]
    [InlineData("2001:db8::", "2001:db0::/28")]
    [InlineData("42.2.38.241", "42.2.38.241")]
    [InlineData("42.2.38.0/24", "42.2.38.241")]
    [InlineData("2a02:26f0::/29", "2a02:26f0::/29")]
    [InlineData("2a02:26f0::1", "2a02:26f0::/29")]
    public void An_entry_of_the_other_family_is_never_found(string match, string expected)
    {
        FeedHistory history = Recorded("32.1.13.184", "2001:db0::/28", "42.2.38.241", "2a02:26f0::/29");

        Assert.Equal([expected], Entries(Search(history, match)));
    }

    [Fact]
    public void An_entry_without_bytes_is_never_matched_and_is_listed_without_a_match()
    {
        FeedHistory history = Recorded("not an address", "8.8.8.8");

        Assert.Equal(["8.8.8.8"], Entries(Search(history, "0.0.0.0/0")));
        Assert.Empty(Search(history, "::/0").Rows);
        Assert.Equal(["not an address", "8.8.8.8"], Entries(Search(history)));
    }

    [Fact]
    public void The_feed_and_period_filters_hold_alone_and_with_a_match()
    {
        FeedHistory history = NewHistory();
        history.Record(Change("asn:15169", T, IPv4("8.8.8.0/24", "10.0.0.0/8")));
        history.Record(Change("google:cloud", T.AddDays(1), IPv4("8.8.8.0/24", "34.0.0.0/8")));
        history.Record(Change("asn:15169", T.AddDays(2), IPv4("8.8.8.0/24", "9.9.9.0/24")));

        Assert.Equal(["8.8.8.0/24", "9.9.9.0/24", "8.8.8.0/24", "10.0.0.0/8"], Entries(Search(history, feedKey: "asn:15169")));
        Assert.Empty(Search(history, feedKey: "ASN:15169").Rows);
        Assert.Equal(["8.8.8.0/24", "9.9.9.0/24", "8.8.8.0/24", "34.0.0.0/8"], Entries(Search(history, since: T.AddDays(1))));
        Assert.Equal(["8.8.8.0/24", "9.9.9.0/24"], Entries(Search(history, feedKey: "asn:15169", since: T.AddDays(1))));
        Assert.Equal(["8.8.8.0/24", "8.8.8.0/24", "8.8.8.0/24"], Entries(Search(history, "8.8.8.8")));
        Assert.Equal(["8.8.8.0/24", "8.8.8.0/24"], Entries(Search(history, "8.8.8.8", feedKey: "asn:15169")));
        Assert.Equal(["8.8.8.0/24", "8.8.8.0/24"], Entries(Search(history, "8.8.8.8", since: T.AddDays(1))));

        HistoryRead all = Search(history, "8.8.8.8", feedKey: "google:cloud", since: T.AddDays(1));
        Assert.Equal(["8.8.8.0/24"], Entries(all));
        Assert.Equal("google:cloud", all.Rows[0].FeedKey);
        Assert.Equal(T.AddDays(1), all.Rows[0].AtUtc);
    }

    [Fact]
    public void Rows_come_newest_change_first_then_in_entry_order()
    {
        FeedHistory history = NewHistory();
        // The second change is stamped earlier, so only the change order puts it first
        history.Record(new FeedChangeRecord(T.AddHours(2), "asn:1", FeedKind.Asn, FeedChangeCause.PageWrite, null, null,
            [new FeedFamilyChange(false, null, FeedFileState.Read, ["9.0.0.0/8", "1.0.0.0/8"],
                                  [new FeedRemoval("5.0.0.0/8", FeedRemovalReason.SourceDropped)]),
             new FeedFamilyChange(true, null, FeedFileState.Read, ["2001:4860::/32"], [])]));
        history.Record(Change("asn:1", T.AddHours(1), IPv4("3.0.0.0/8", "2.0.0.0/8")));

        HistoryRead read = Search(history);

        Assert.Equal(["3.0.0.0/8", "2.0.0.0/8", "9.0.0.0/8", "1.0.0.0/8", "5.0.0.0/8", "2001:4860::/32"], Entries(read));
        Assert.Equal([true, true, true, true, false, true], read.Rows.Select(r => r.Added).ToArray());
        Assert.Equal(FeedRemovalReason.SourceDropped, read.Rows[4].Reason);
        Assert.Equal(6, read.Total);

        HistoryRead matched = Search(history, "0.0.0.0/0");
        Assert.Equal(["3.0.0.0/8", "2.0.0.0/8", "9.0.0.0/8", "1.0.0.0/8", "5.0.0.0/8"], Entries(matched));
    }

    [Fact]
    public void The_limit_caps_the_rows_while_the_total_counts_every_match()
    {
        FeedHistory history = Recorded("8.0.0.0/8", "8.8.0.0/16", "8.8.8.0/24", "8.8.8.8", "9.9.9.9");

        HistoryRead capped = Search(history, "8.8.8.8", limit: 2);
        HistoryRead unmatched = Search(history, limit: 1);

        Assert.Equal(["8.0.0.0/8", "8.8.0.0/16"], Entries(capped));
        Assert.Equal(4, capped.Total);
        Assert.Equal(["8.0.0.0/8"], Entries(unmatched));
        Assert.Equal(5, unmatched.Total);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            history.Search(new HistoryQuery(null, null, null), 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_feed_keys_are_every_distinct_key_in_order_whatever_the_filters()
    {
        FeedHistory history = NewHistory();
        history.Record(Change("google:cloud", T, IPv4("34.0.0.0/8")));
        history.Record(Change("asn:15169", T.AddDays(1), IPv4("8.8.8.0/24")));
        history.Record(Change("google:cloud", T.AddDays(2), IPv4("35.0.0.0/8")));
        history.Record(Change("abuseipdb", T.AddDays(3), IPv4("9.9.9.9")));

        string[] expected = ["abuseipdb", "asn:15169", "google:cloud"];
        Assert.Equal(expected, Search(history).FeedKeys);
        Assert.Equal(expected, Search(history, "1.1.1.1", feedKey: "asn:15169", since: T.AddDays(9)).FeedKeys);
    }

    [Fact]
    public void A_row_reads_back_as_it_was_recorded()
    {
        FeedHistory history = NewHistory();
        DateTimeOffset downloaded = new(2026, 10, 29, 23, 59, 59, TimeSpan.Zero);
        history.Record(new FeedChangeRecord(T, "abuseipdb", FeedKind.AbuseIpDbRolling, FeedChangeCause.RefreshAll,
            new DateOnly(2026, 10, 2), downloaded,
            [new FeedFamilyChange(true, FeedType.ModSecurity, FeedFileState.Unreadable, [],
                                  [new FeedRemoval("2001:4860::8888", FeedRemovalReason.LeftTheList)])]));

        HistoryRow row = Assert.Single(Search(history, "2001:4860::8888").Rows);

        Assert.Equal(new HistoryRow(T, "abuseipdb", FeedKind.AbuseIpDbRolling, FeedChangeCause.RefreshAll, true,
                                    FeedType.ModSecurity, false, "2001:4860::8888", FeedRemovalReason.LeftTheList,
                                    FeedFileState.Unreadable, new DateOnly(2026, 10, 2), downloaded), row);
        Assert.Equal(TimeSpan.Zero, row.AtUtc.Offset);
    }

    [Theory]
    [InlineData("UPDATE changes SET kind = 'Cloudflare' WHERE change_id = 2;")]
    [InlineData("UPDATE changes SET kind = '1' WHERE change_id = 2;")]
    [InlineData("UPDATE changes SET cause = 'Bogus' WHERE change_id = 2;")]
    [InlineData("UPDATE changes SET at_utc = 'soon' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET old_file = 'Gone' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET copy = 'Nginx' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET copy = 'fortigate' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET start_ip = x'0808' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET end_ip = x'00000000000000000000000000000008' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET start_ip = x'08080809' WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET start_ip = NULL WHERE change_id = 2;")]
    [InlineData("UPDATE entries SET start_ip = 'text' WHERE change_id = 2;")]
    public void A_stored_row_this_build_cant_read_is_left_out_and_still_counted(string damage)
    {
        FeedHistory history = NewHistory();
        history.Record(Change("asn:1", T, IPv4("8.8.8.0/24")));
        history.Record(Change("asn:1", T.AddHours(1), IPv4("8.8.8.8")));
        Execute(damage);

        HistoryRead listed = Search(NewHistory());

        Assert.Equal(HistoryState.Usable, listed.State);
        Assert.Equal(["8.8.8.0/24"], Entries(listed));
        Assert.Equal(2, listed.Total);
    }

    [Fact]
    public void An_unknown_reason_reads_as_not_told_and_a_bad_date_as_none()
    {
        NewHistory().Record(Change("asn:1", T, IPv4("8.8.8.8")));
        Execute("UPDATE entries SET reason = 'Whim'; UPDATE changes SET dataset_date = '2026-13-01', list_downloaded_utc = 'x';");

        HistoryRow row = Assert.Single(Search(NewHistory()).Rows);

        Assert.Equal(FeedRemovalReason.NotTold, row.Reason);
        Assert.Null(row.DatasetDate);
        Assert.Null(row.ListDownloadedUtc);
    }

    [Fact]
    public void A_missing_file_reads_as_missing_and_is_not_created()
    {
        HistoryRead read = Search(NewHistory(), "8.8.8.8");

        Assert.Equal(HistoryState.Missing, read.State);
        Assert.Empty(read.Rows);
        Assert.Empty(read.FeedKeys);
        Assert.Equal(0, read.Total);
        Assert.False(File.Exists(HistoryPath));
        Assert.Empty(Directory.GetFiles(_temp.Folder));
    }

    [Fact]
    public void An_empty_file_reads_as_usable_with_nothing_in_it()
    {
        File.WriteAllBytes(HistoryPath, []);

        HistoryRead read = Search(NewHistory());

        Assert.Equal(HistoryState.Usable, read.State);
        Assert.Empty(read.Rows);
        Assert.Equal(0, read.Total);
        Assert.Equal(0, new FileInfo(HistoryPath).Length);
    }

    [Theory]
    [InlineData("UPDATE metadata SET value = '2' WHERE key = 'schema_version';", HistoryState.Newer)]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON changes BEGIN SELECT 1; END;", HistoryState.Unreadable)]
    [InlineData("DROP TABLE entries;", HistoryState.Unreadable)]
    public void A_newer_or_damaged_file_reads_nothing_and_is_left_as_it_was(string change, HistoryState expected)
    {
        NewHistory().Record(Change("asn:1", T, IPv4("8.8.8.8")));
        Execute(change);
        string hash = Sha(HistoryPath);

        HistoryRead read = Search(NewHistory(), "8.8.8.8");

        Assert.Equal(expected, read.State);
        Assert.Empty(read.Rows);
        Assert.Empty(read.FeedKeys);
        Assert.Equal(hash, Sha(HistoryPath));
    }

    [Fact]
    public void A_file_that_is_not_sqlite_is_unreadable_and_left_as_it_was()
    {
        File.WriteAllText(HistoryPath, new string('x', 4096));
        string hash = Sha(HistoryPath);

        Assert.Equal(HistoryState.Unreadable, Search(NewHistory()).State);
        Assert.Equal(hash, Sha(HistoryPath));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("10.20.0.0/16")]
    [InlineData("2001:4860::8888")]
    [InlineData("2001:4860:1::/48")]
    public void The_match_seeks_the_range_index_in_both_halves_and_never_scans_the_entries(string match)
    {
        FeedHistory history = NewHistory();
        string[] v4 = Enumerable.Range(0, 3000).Select(i => $"10.{i / 256}.{i % 256}.0/24").ToArray();
        string[] v6 = Enumerable.Range(0, 3000).Select(i => $"2001:4860:{i:x}::/48").ToArray();
        history.Record(Change("asn:1", T, IPv4(v4), IPv6(v6)));
        Assert.True(HistoryMatch.TryParse(match, out HistoryMatch? parsed));

        foreach (HistoryQuery query in new[] { new HistoryQuery(null, null, parsed), new HistoryQuery("asn:1", T, parsed) })
        {
            foreach (bool count in new[] { false, true })
            {
                string[] plan = Plan(history, query, count);
                string all = string.Join(" | ", plan);
                Assert.Contains(plan, line => line.Contains("idx_entries_range (start_ip>? AND start_ip<?)", StringComparison.Ordinal));
                Assert.Contains(plan, line => line.Contains("idx_entries_range (start_ip=? AND end_ip>?)", StringComparison.Ordinal));
                Assert.DoesNotContain(plan, line => line.StartsWith("SCAN e", StringComparison.Ordinal)
                                                    || line.StartsWith("SCAN entries", StringComparison.Ordinal));
                Assert.False(all.Contains("SCAN c", StringComparison.Ordinal) || all.Contains("SCAN changes", StringComparison.Ordinal), all);
            }
        }
    }

    [Fact]
    public void The_total_stops_at_the_cap_and_says_more_matched()
    {
        FeedHistory history = NewHistory();
        string[] cap = Enumerable.Range(0, FeedHistory.CountCap).Select(i => $"10.{i >> 16}.{(i >> 8) & 255}.{i & 255}").ToArray();
        Assert.Equal(HistoryState.Usable, history.Record(Change("asn:1", T, IPv4(cap))));
        Assert.Equal(HistoryState.Usable, history.Record(Change("asn:2", T.AddDays(1), IPv4("10.200.0.0", "11.0.0.0/8"))));

        HistoryRead exact = Search(history, feedKey: "asn:1", limit: 10);
        HistoryRead exactMatched = Search(history, "10.0.0.0/9", limit: 10);
        HistoryRead under = Search(history, "10.0.0.0/16", feedKey: "asn:1", limit: 10);
        HistoryRead over = Search(history, "10.0.0.0/8", limit: 10);
        HistoryRead overUnmatched = Search(history, limit: 10);

        Assert.Equal((FeedHistory.CountCap, false), (exact.Total, exact.TotalCapped));
        Assert.Equal((FeedHistory.CountCap, false), (exactMatched.Total, exactMatched.TotalCapped));
        Assert.Equal((65_536, false), (under.Total, under.TotalCapped));
        Assert.Equal((FeedHistory.CountCap, true), (over.Total, over.TotalCapped));
        Assert.Equal((FeedHistory.CountCap, true), (overUnmatched.Total, overUnmatched.TotalCapped));
        Assert.Equal(["10.200.0.0", "10.0.0.0", "10.0.0.1"], Entries(Search(history, "10.0.0.0/8", limit: 3)));
        Assert.False(Search(history, "11.0.0.0/8").TotalCapped);
    }

    [Fact]
    public void Both_row_forms_give_the_same_rows_for_any_match_and_filters()
    {
        FeedHistory history = NewHistory();
        var random = new Random(11);
        string[] feeds = ["asn:1", "asn:2", "google:cloud"];
        for (int c = 0; c < 30; c++)
        {
            string[] v4 = Enumerable.Range(0, 60).Select(_ => random.Next(4) switch
            {
                0 => $"{random.Next(256)}.0.0.0/8",
                1 => $"{random.Next(256)}.{random.Next(256)}.0.0/16",
                2 => $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.0/24",
                _ => $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}",
            }).Concat(["0.0.0.0/0", "8.0.0.0/8", "8.8.8.0/24", "8.8.8.8", "32.1.13.184", "32.1.13.0/24", "128.0.0.0/1"]).ToArray();
            string[] v6 = Enumerable.Range(0, 20).Select(_ => $"2001:{random.Next(0x10000):x}::/32")
                .Concat(["::/0", "2001:4860::/32", "2001:4860::8888", "2001:db8::/32", "2001:db0::/28"]).ToArray();
            Assert.Equal(HistoryState.Usable, history.Record(Change(feeds[c % 3], T.AddHours(c % 7), IPv4(v4), IPv6(v6))));
        }

        string?[] matches = [null, "8.8.8.8", "8.0.0.0/8", "0.0.0.0/1", "128.0.0.0/1", "0.0.0.0/0", "32.1.13.0/24", "32.1.13.184",
                             "2001:4860::8888", "2001:db0::/28", "2001:db8::/32", "2000::/3", "::/0"];
        foreach (string? match in matches)
        {
            HistoryMatch? parsed = null;
            if (match is not null)
                Assert.True(HistoryMatch.TryParse(match, out parsed));
            foreach (HistoryQuery query in new[] { new HistoryQuery(null, null, parsed), new HistoryQuery("asn:2", null, parsed),
                                                   new HistoryQuery(null, T.AddHours(3), parsed), new HistoryQuery("asn:1", T.AddHours(2), parsed) })
            {
                CancellationToken ct = TestContext.Current.CancellationToken;
                HistoryRead seek = history.Search(query, 100_000, changeOrdered: false, ct);
                HistoryRead ordered = history.Search(query, 100_000, changeOrdered: true, ct);
                Assert.Equal(seek.Total, ordered.Total);
                Assert.Equal(seek.Total, seek.Rows.Count);
                Assert.True(seek.Rows.SequenceEqual(ordered.Rows), $"{match} {query.FeedKey} {query.SinceUtc}");
            }
        }
    }

    [Theory]
    [InlineData(FeedHistory.SortCap, false)]
    [InlineData(FeedHistory.SortCap + 1, true)]
    public void Past_the_sort_cap_a_match_reads_change_ordered_and_otherwise_seeks(int matching, bool changeOrdered)
    {
        FeedHistory history = NewHistory();
        string[] v4 = Enumerable.Range(0, matching).Select(i => $"10.0.{i >> 8}.{i & 255}").Append("11.0.0.0/8").ToArray();
        history.Record(Change("asn:1", T, IPv4(v4), IPv6("2001:4860::/32")));
        history.Record(Change("asn:2", T.AddDays(1), IPv4("12.0.0.0/8")));
        Assert.True(HistoryMatch.TryParse("10.0.0.0/8", out HistoryMatch? parsed));

        foreach (HistoryQuery query in new[] { new HistoryQuery(null, null, parsed), new HistoryQuery("asn:1", T, parsed) })
        {
            HistoryRead read = history.Search(query, 5, TestContext.Current.CancellationToken);
            Assert.Equal(matching, read.Total);
            string sql = FeedHistory.RowsSql(query, read.Total);
            string[] plan = Plan(history, query, sql);
            string all = string.Join(" | ", plan);
            Assert.False(all.Contains("TEMP B-TREE", StringComparison.Ordinal) == changeOrdered, all);
            Assert.Equal(changeOrdered, plan.Any(line => line.StartsWith("SCAN c", StringComparison.Ordinal)));
            Assert.Equal(changeOrdered, all.Contains("idx_entries_change", StringComparison.Ordinal));
            Assert.Equal(!changeOrdered, all.Contains("idx_entries_range (start_ip=? AND end_ip>?)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_cancelled_search_throws_and_changes_nothing()
    {
        FeedHistory history = Recorded("8.8.8.8", "9.9.9.9");
        string hash = Sha(HistoryPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => history.Search(new HistoryQuery(null, null, null), 10, cts.Token));

        Assert.Equal(hash, Sha(HistoryPath));
        Assert.Equal(2, Search(history).Rows.Count);
    }

    private static string[] Plan(FeedHistory history, HistoryQuery query, bool count) =>
        Plan(history, query, FeedHistory.SearchSql(query, count));

    private static string[] Plan(FeedHistory history, HistoryQuery query, string sql)
    {
        using SqliteConnection conn = history.Open(SqliteOpenMode.ReadOnly);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        FeedHistory.BindSearch(cmd, query);
        var lines = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            lines.Add(reader.GetString(3));
        return lines.ToArray();
    }

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = HistoryPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    private void Execute(string sql)
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
