using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

public sealed class FeedHistoryTests : IDisposable
{
    private static readonly DateTimeOffset T = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly DbTempFolder _temp = new();
    // Each test moves the clock forward to the times it records at, since a fake clock can't go back
    private readonly FakeTimeProvider _time = new(T.AddDays(-1000));

    public void Dispose() => _temp.Dispose();

    private string HistoryPath => _temp.FilePath("history.db");

    private FeedHistory NewHistory() => new(HistoryPath, _time);

    private static FeedChangeRecord Change(DateTimeOffset at, params FeedFamilyChange[] families) =>
        new(at, "asn:15169", FeedKind.Asn, FeedChangeCause.PageWrite, new DateOnly(2026, 10, 1), null, families);

    private static FeedFamilyChange IPv4(params string[] added) =>
        new(false, null, FeedFileState.Read, added, []);

    private FeedHistory Recorded(int changes = 1)
    {
        FeedHistory history = NewHistory();
        for (int i = 0; i < changes; i++)
            Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("8.8.8.8"))));
        return history;
    }

    [Fact]
    public void A_missing_file_is_created_by_the_first_record_with_the_version_1_layout()
    {
        Assert.False(File.Exists(HistoryPath));

        Assert.Equal(HistoryState.Usable, NewHistory().Record(Change(T, IPv4("8.8.8.8"))));

        Assert.Equal("1", Scalar("SELECT value FROM metadata WHERE key = 'schema_version';"));
        var objects = new List<string>();
        using (SqliteConnection conn = OpenRaw())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type || ':' || name || ':' || tbl_name FROM sqlite_master ORDER BY name;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                objects.Add(reader.GetString(0));
        }
        Assert.Equal(["table:changes:changes", "table:entries:entries", "index:idx_changes_at_utc:changes",
                      "index:idx_entries_change:entries", "index:idx_entries_range:entries", "table:metadata:metadata",
                      "index:sqlite_autoindex_metadata_1:metadata"],
                     objects);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM changes;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM entries;"));
    }

    [Fact]
    public void Every_connection_runs_with_trusted_schema_and_foreign_keys_off()
    {
        FeedHistory history = NewHistory();
        using (SqliteConnection created = history.Open(SqliteOpenMode.ReadWriteCreate))
        {
            Assert.Equal(0L, Pragma(created, "trusted_schema"));
            Assert.Equal(0L, Pragma(created, "foreign_keys"));
        }

        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("8.8.8.8"))));
        using (SqliteConnection existing = history.Open(SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(0L, Pragma(existing, "trusted_schema"));
            Assert.Equal(0L, Pragma(existing, "foreign_keys"));
        }
    }

    [Fact]
    public void A_newer_version_is_never_written_even_with_a_layout_this_version_refuses()
    {
        Recorded();
        Execute("UPDATE metadata SET value = '2' WHERE key = 'schema_version';"
                + "CREATE TRIGGER t AFTER INSERT ON changes BEGIN SELECT 1; END;"
                + "CREATE TABLE later (x INTEGER);");
        string hash = Sha(HistoryPath);

        Assert.Equal(HistoryState.Newer, NewHistory().Record(Change(T, IPv4("9.9.9.9"))));

        Assert.Equal(hash, Sha(HistoryPath));
    }

    [Theory]
    [InlineData("DROP TABLE entries;")]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON changes BEGIN SELECT 1; END;")]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON metadata BEGIN SELECT 1; END;")]
    [InlineData("CREATE VIEW v AS SELECT 1;")]
    [InlineData("ALTER TABLE changes ADD COLUMN extra TEXT NULL;")]
    [InlineData("DROP INDEX idx_entries_range; CREATE UNIQUE INDEX idx_entries_range ON entries (start_ip, end_ip);")]
    [InlineData("CREATE INDEX idx_extra ON entries (text);")]
    [InlineData("INSERT INTO metadata (key, value) VALUES ('other', x'01');")]
    [InlineData("UPDATE metadata SET value = 'one' WHERE key = 'schema_version';")]
    [InlineData("UPDATE metadata SET value = '0' WHERE key = 'schema_version';")]
    public void A_damaged_layout_is_unreadable_and_never_written(string damage)
    {
        Recorded();
        Execute(damage);
        string hash = Sha(HistoryPath);

        Assert.Equal(HistoryState.Unreadable, NewHistory().Record(Change(T, IPv4("9.9.9.9"))));

        Assert.Equal(hash, Sha(HistoryPath));
    }

    [Fact]
    public void A_file_that_is_not_sqlite_is_unreadable_and_never_written()
    {
        File.WriteAllText(HistoryPath, new string('x', 4096));
        string hash = Sha(HistoryPath);

        Assert.Equal(HistoryState.Unreadable, NewHistory().Record(Change(T, IPv4("9.9.9.9"))));

        Assert.Equal(hash, Sha(HistoryPath));
    }

    [Fact]
    public void A_damaged_page_of_the_entries_fails_the_quick_check_and_is_never_written()
    {
        string[] many = Enumerable.Range(0, 2000).Select(i => $"10.{i / 256}.{i % 256}.0/24").ToArray();
        Assert.Equal(HistoryState.Usable, NewHistory().Record(Change(T, IPv4(many))));
        long rootPage = (long)Scalar("SELECT rootpage FROM sqlite_master WHERE name = 'entries';");
        long pageSize = (long)Scalar("PRAGMA page_size;");
        Assert.True(rootPage > 1);
        using (var file = new FileStream(HistoryPath, FileMode.Open, FileAccess.Write))
        {
            file.Position = (rootPage - 1) * pageSize;
            file.Write(Enumerable.Repeat((byte)0xFF, 64).ToArray());
        }
        string hash = Sha(HistoryPath);

        FeedHistory history = NewHistory();
        Assert.Equal(HistoryState.Unreadable, history.Record(Change(T, IPv4("9.9.9.9"))));

        Assert.Equal(hash, Sha(HistoryPath));
        Assert.Equal(1, history.QuickChecksRun);
    }

    [Fact]
    public void The_quick_check_runs_once_per_instance_and_again_after_clear()
    {
        Recorded();

        FeedHistory history = NewHistory();
        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("9.9.9.9"))));
        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("1.1.1.1"))));
        Assert.Equal(1, history.QuickChecksRun);

        FeedHistory other = NewHistory();
        Assert.Equal(HistoryState.Usable, other.Record(Change(T, IPv4("1.0.0.1"))));
        Assert.Equal(1, other.QuickChecksRun);

        Assert.Equal(HistoryState.Missing, history.Clear());
        Assert.Equal(HistoryState.Usable, other.Record(Change(T, IPv4("8.8.4.4"))));
        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("8.8.8.8"))));
        Assert.Equal(2, history.QuickChecksRun);
    }

    [Fact]
    public void A_folder_where_the_file_cannot_be_made_is_a_storage_error()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(_temp.Folder);
        File.SetUnixFileMode(_temp.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(HistoryState.StorageError, NewHistory().Record(Change(T, IPv4("8.8.8.8"))));
        }
        finally
        {
            File.SetUnixFileMode(_temp.Folder, mode);
        }
        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public void A_write_that_fails_before_its_commit_records_nothing_and_leaves_the_file_usable()
    {
        FeedHistory history = Recorded();
        history.BeforeCommit = () => throw new SqliteException("test", 13);

        Assert.Equal(HistoryState.StorageError,
                     history.Record(Change(T, IPv4("9.9.9.9"), new(true, null, FeedFileState.Read, ["2001:4860::8888"], []))));

        Assert.Equal(1L, Scalar("SELECT count(*) FROM changes;"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM entries;"));
        history.BeforeCommit = null;
        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("9.9.9.9"))));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM changes;"));
    }

    [Fact]
    public void A_record_trims_changes_more_than_the_keep_days_behind_the_newest()
    {
        FeedHistory history = NewHistory();
        RecordEvery30Days(history, T.AddDays(-420), T);
        RecordAt(history, T, "1.0.0.1");
        Assert.Equal(T.AddDays(-360), ChangeTimes()[0]);
        Assert.Equal(14, ChangeTimes().Count);

        // A clock set back adds no age, so nothing more is trimmed
        var back = new FeedHistory(HistoryPath, new FakeTimeProvider(T.AddDays(-200)));
        Assert.Equal(HistoryState.Usable, back.Record(Change(T.AddDays(-200), IPv4("9.9.9.9"))));
        Assert.Equal(T.AddDays(-360), ChangeTimes()[0]);
        Assert.Equal(15, ChangeTimes().Count);

        // A clock a year ahead counts that step as a month, so it trims at most a month early
        RecordAt(history, T.AddDays(365), "8.8.4.4");
        Assert.Equal(T.AddDays(-330), ChangeTimes()[0]);
        Assert.Equal((long)ChangeTimes().Count, Scalar("SELECT count(*) FROM entries;"));
    }

    [Fact]
    public void Records_under_a_clock_set_ahead_and_after_it_comes_back_keep_every_real_change()
    {
        FeedHistory history = NewHistory();
        RecordAt(history, T.AddDays(-100), "8.8.8.8");
        RecordAt(history, T, "9.9.9.9");

        RecordAt(history, T.AddDays(730), "1.1.1.1");
        RecordAt(history, T.AddDays(730), "1.0.0.1");
        Assert.Equal([T.AddDays(-100), T, T.AddDays(730), T.AddDays(730)], ChangeTimes());

        var back = new FeedHistory(HistoryPath, new FakeTimeProvider(T.AddDays(1)));
        Assert.Equal(HistoryState.Usable, back.Record(Change(T.AddDays(1), IPv4("8.8.4.4"))));
        Assert.Equal([T.AddDays(-100), T, T.AddDays(1), T.AddDays(730), T.AddDays(730)], ChangeTimes());
        Assert.Equal(5L, Scalar("SELECT count(*) FROM entries;"));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(365)]
    public void Records_under_a_clock_set_ahead_by_up_to_a_year_trim_at_most_a_month_early(int aheadDays)
    {
        FeedHistory history = NewHistory();
        RecordEvery30Days(history, T.AddDays(-360), T);

        RecordAt(history, T.AddDays(aheadDays), "1.1.1.1");
        RecordAt(history, T.AddDays(aheadDays), "1.0.0.1");

        Assert.Equal(T.AddDays(-330), ChangeTimes()[0]);
        Assert.Equal(14, ChangeTimes().Count);
    }

    [Fact]
    public void A_clock_that_keeps_jumping_to_the_same_wrong_time_counts_that_month_once()
    {
        FeedHistory history = NewHistory();
        RecordEvery30Days(history, T.AddDays(-360), T);

        for (int day = 1; day <= 3; day++)
        {
            RecordUnderItsOwnClock(T.AddDays(400), "1.1.1.1");
            RecordUnderItsOwnClock(T.AddDays(day), "1.0.0.1");
        }

        Assert.Equal(T.AddDays(-330), ChangeTimes()[0]);
    }

    [Fact]
    public void A_clock_reset_to_a_past_date_at_each_start_counts_only_the_time_that_passed()
    {
        FeedHistory history = NewHistory();
        RecordEvery30Days(history, T.AddDays(-360), T);
        DateTimeOffset reset = T.AddYears(-25);

        for (int day = 1; day <= 3; day++)
        {
            RecordUnderItsOwnClock(reset, "1.1.1.1");
            RecordUnderItsOwnClock(T.AddDays(day), "1.0.0.1");
        }
        Assert.Equal(T.AddDays(-360), ChangeTimes().First(at => at > reset));

        RecordUnderItsOwnClock(T.AddDays(6), "8.8.4.4");
        Assert.Equal(T.AddDays(-330), ChangeTimes().First(at => at > reset));
    }

    [Fact]
    public void Stamps_at_the_ends_of_the_integer_range_neither_overflow_nor_stop_the_trim()
    {
        FeedHistory history = NewHistory();
        RecordAt(history, T.AddDays(-360), "8.8.8.8");
        Execute("""
            INSERT INTO changes (at_utc, feed_key, kind, cause) VALUES (-9223372036854775807 - 1, 'asn:15169', 'Asn', 'PageWrite');
            INSERT INTO changes (at_utc, feed_key, kind, cause) VALUES (-9223372036854775803, 'asn:15169', 'Asn', 'PageWrite');
            INSERT INTO changes (at_utc, feed_key, kind, cause) VALUES (9223372036854775807, 'asn:15169', 'Asn', 'PageWrite');
            """);

        RecordEvery30Days(history, T.AddDays(-330), T.AddDays(30));

        Assert.Equal(T.AddDays(-330).ToUnixTimeSeconds(), Scalar("SELECT min(at_utc) FROM changes;"));
        Assert.Equal(long.MaxValue, Scalar("SELECT max(at_utc) FROM changes;"));
    }

    [Fact]
    public void A_gap_without_writes_counts_as_a_month_so_it_delays_the_trim()
    {
        FeedHistory history = NewHistory();
        RecordAt(history, T.AddDays(-1000), "8.8.8.8");
        RecordAt(history, T.AddDays(-900), "9.9.9.9");
        RecordAt(history, T, "1.1.1.1");
        RecordAt(history, T, "1.0.0.1");
        Assert.Equal([T.AddDays(-1000), T.AddDays(-900), T, T], ChangeTimes());

        RecordEvery30Days(history, T.AddDays(30), T.AddDays(300));
        Assert.Equal(T.AddDays(-1000), ChangeTimes()[0]);

        RecordAt(history, T.AddDays(330), "8.8.4.4");
        Assert.Equal(T.AddDays(-900), ChangeTimes()[0]);
    }

    [Fact]
    public void A_change_exactly_the_keep_days_behind_is_kept()
    {
        FeedHistory history = NewHistory();
        RecordEvery30Days(history, T.AddDays(-360), T);

        RecordAt(history, T.AddDays(5), "1.1.1.1");
        Assert.Equal(T.AddDays(-360), ChangeTimes()[0]);

        RecordAt(history, T.AddDays(6), "1.0.0.1");
        Assert.Equal(T.AddDays(-330), ChangeTimes()[0]);
    }

    [Fact]
    public void A_text_stamp_neither_stops_the_trim_nor_is_trimmed()
    {
        FeedHistory history = NewHistory();
        RecordAt(history, T.AddDays(-360), "8.8.8.8");
        Execute("""
            INSERT INTO changes (at_utc, feed_key, kind, cause) VALUES ('soon', 'asn:15169', 'Asn', 'PageWrite');
            INSERT INTO entries (change_id, is_ipv6, copy, old_file, added, text, start_ip, end_ip, reason)
            VALUES (last_insert_rowid(), 0, NULL, 'Read', 1, '7.7.7.7', NULL, NULL, 'NotTold');
            """);

        RecordEvery30Days(history, T.AddDays(-359), T.AddDays(1));
        Assert.Equal(T.AddDays(-360).ToUnixTimeSeconds(),
                     Scalar("SELECT min(at_utc) FROM changes WHERE typeof(at_utc) = 'integer';"));

        RecordAt(history, T.AddDays(7), "1.0.0.1");

        Assert.Equal(T.AddDays(-329).ToUnixTimeSeconds(),
                     Scalar("SELECT min(at_utc) FROM changes WHERE typeof(at_utc) = 'integer';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM changes WHERE typeof(at_utc) = 'text';"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM entries WHERE text = '7.7.7.7';"));
    }

    [Fact]
    public void With_nothing_recorded_nothing_is_trimmed()
    {
        _time.SetUtcNow(T);
        Assert.Equal(HistoryState.Usable, NewHistory().Record(Change(T.AddDays(-1000), IPv4("8.8.8.8"))));

        Assert.Equal([T.AddDays(-1000)], ChangeTimes());
    }

    [Fact]
    public void Entries_are_stored_with_their_family_copy_state_reason_and_bytes()
    {
        var change = new FeedChangeRecord(
            T, "cloud:aws", FeedKind.Aws, FeedChangeCause.Retire, new DateOnly(2026, 10, 1), T.AddHours(-2),
            [
                new FeedFamilyChange(false, FeedType.ModSecurity, FeedFileState.Unreadable, ["8.8.8.8", "not an address"],
                                     [new FeedRemoval("10.0.0.0/8", FeedRemovalReason.Exception)]),
                new FeedFamilyChange(true, null, FeedFileState.Missing, ["2001:4860::8888"],
                                     [new FeedRemoval("2001:db8::/32", FeedRemovalReason.SourceDropped)]),
                new FeedFamilyChange(false, FeedType.Fortigate, FeedFileState.Read, [], []),
            ]);

        Assert.Equal(HistoryState.Usable, NewHistory().Record(change));

        using SqliteConnection conn = OpenRaw();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT at_utc, feed_key, kind, cause, dataset_date, list_downloaded_utc FROM changes;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(T.ToUnixTimeSeconds(), reader.GetInt64(0));
            Assert.Equal("cloud:aws", reader.GetString(1));
            Assert.Equal("Aws", reader.GetString(2));
            Assert.Equal("Retire", reader.GetString(3));
            Assert.Equal("2026-10-01", reader.GetString(4));
            Assert.Equal(T.AddHours(-2).ToUnixTimeSeconds(), reader.GetInt64(5));
            Assert.False(reader.Read());
        }

        var rows = new List<string>();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT is_ipv6, coalesce(copy, '-'), old_file, added, text, "
                            + "CASE WHEN start_ip IS NULL THEN '-' ELSE hex(start_ip) END, CASE WHEN end_ip IS NULL THEN '-' ELSE hex(end_ip) END, reason, "
                            + "typeof(start_ip), change_id = (SELECT change_id FROM changes) FROM entries ORDER BY rowid;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, 10).Select(i => Convert.ToString(reader.GetValue(i),
                                                                                    System.Globalization.CultureInfo.InvariantCulture))));
        }
        Assert.Equal(
            [
                "0|ModSecurity|Unreadable|1|8.8.8.8|08080808|08080808|NotTold|blob|1",
                "0|ModSecurity|Unreadable|1|not an address|-|-|NotTold|null|1",
                "0|ModSecurity|Unreadable|0|10.0.0.0/8|0A000000|0AFFFFFF|Exception|blob|1",
                "1|-|Missing|1|2001:4860::8888|20014860000000000000000000008888|20014860000000000000000000008888|NotTold|blob|1",
                "1|-|Missing|0|2001:db8::/32|20010DB8000000000000000000000000|20010DB8FFFFFFFFFFFFFFFFFFFFFFFF|SourceDropped|blob|1",
            ],
            rows);
    }

    [Fact]
    public void A_record_with_no_entry_throws_and_makes_no_file()
    {
        FeedHistory history = NewHistory();

        Assert.Throws<ArgumentException>(() => history.Record(Change(T)));
        Assert.Throws<ArgumentException>(() => history.Record(Change(T, IPv4(), new(true, null, FeedFileState.Missing, [], []))));

        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public void Clear_deletes_the_database_and_its_journal_and_the_next_record_starts_afresh()
    {
        FeedHistory history = Recorded(changes: 2);
        File.WriteAllText(HistoryPath + "-journal", "");

        Assert.Equal(HistoryState.Missing, history.Clear());

        Assert.False(File.Exists(HistoryPath));
        Assert.False(File.Exists(HistoryPath + "-journal"));
        Assert.Equal(HistoryState.Missing, history.Clear());
        Assert.Equal(HistoryState.Usable, history.Record(Change(T, IPv4("9.9.9.9"))));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM changes;"));
    }

    [Fact]
    public void Clear_leaves_the_journal_when_the_database_cannot_be_deleted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        FeedHistory history = Recorded();
        File.WriteAllText(HistoryPath + "-journal", "");
        UnixFileMode mode = File.GetUnixFileMode(_temp.Folder);
        File.SetUnixFileMode(_temp.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(HistoryState.StorageError, history.Clear());
        }
        finally
        {
            File.SetUnixFileMode(_temp.Folder, mode);
        }

        Assert.True(File.Exists(HistoryPath));
        Assert.True(File.Exists(HistoryPath + "-journal"));
    }

    // ---- The size cap ----

    private const string CapKey = "cap_trimmed_before_utc";

    private static readonly HistoryQuery Everything = new(null, null, null);

    [Fact]
    public void Past_the_cap_a_record_trims_the_oldest_whole_changes_to_nine_tenths_and_keeps_the_new_one()
    {
        RecordMany(NewHistory(), 0, 8, 500);
        long cap = BytesInUse();
        var capped = new FeedHistory(HistoryPath, _time, cap);

        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(8, 500)));

        List<long> ids = ChangeIds();
        Assert.True(BytesInUse() <= cap / 10 * 9, "The pages in use stay over nine tenths of the cap.");
        Assert.Equal(9L, ids[^1]);
        Assert.InRange(ids.Count, 5, 7);
        Assert.Equal(Enumerable.Range((int)ids[0], ids.Count).Select(id => (long)id), ids);
        Assert.All(EntriesPerChange(), pair => Assert.Equal(500L, pair.Value));
        Assert.Equal(ids, EntriesPerChange().Keys.Order());
    }

    [Fact]
    public void An_entry_whose_change_id_was_edited_to_a_real_number_doesnt_stop_the_trim()
    {
        RecordMany(NewHistory(), 0, 8, 500);
        long cap = BytesInUse();
        Execute("UPDATE entries SET change_id = 2.5 WHERE change_id = 2;");
        var capped = new FeedHistory(HistoryPath, _time, cap);

        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(8, 500)));

        Assert.True(BytesInUse() <= cap / 10 * 9, "The pages in use stay over nine tenths of the cap.");
        Assert.Equal(9L, ChangeIds()[^1]);
    }

    [Fact]
    public void A_single_change_larger_than_the_cap_is_kept_alone()
    {
        var capped = new FeedHistory(HistoryPath, _time, 64 * 1024);
        RecordMany(capped, 0, 2, 10);
        Assert.Equal([1L, 2L], ChangeIds());

        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(2, 5000)));

        Assert.Equal([3L], ChangeIds());
        Assert.Equal(5000L, Scalar("SELECT count(*) FROM entries WHERE change_id = 3;"));
        Assert.Equal(5000L, Scalar("SELECT count(*) FROM entries;"));
        Assert.True(BytesInUse() > 64 * 1024);
    }

    [Fact]
    public void The_cap_records_the_latest_change_it_trimmed_keeps_the_later_mark_and_clear_removes_it()
    {
        RecordMany(NewHistory(), 0, 8, 500);
        var capped = new FeedHistory(HistoryPath, _time, BytesInUse());
        Assert.Null(capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);

        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(8, 500)));

        // Change n is recorded n - 1 hours after T, so the last trimmed is the one just before the first kept
        DateTimeOffset lastTrimmed = T.AddHours(ChangeIds()[0] - 2);
        Assert.Equal(lastTrimmed.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                     Scalar("SELECT value FROM metadata WHERE key = '" + CapKey + "';"));
        Assert.Equal(lastTrimmed, capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);
        HistoryRead none = capped.Search(new HistoryQuery("asn:1", null, null), 10, default);
        Assert.Empty(none.Rows);
        Assert.Equal(lastTrimmed, none.CapTrimmedBeforeUtc);

        // A later mark already there stays; an earlier one is replaced
        string later = T.AddDays(100).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Execute("UPDATE metadata SET value = '" + later + "' WHERE key = '" + CapKey + "';");
        int next = RecordUntilTrimmed(capped, 9);
        Assert.Equal(later, Scalar("SELECT value FROM metadata WHERE key = '" + CapKey + "';"));

        Execute("UPDATE metadata SET value = '1' WHERE key = '" + CapKey + "';");
        RecordUntilTrimmed(capped, next);
        Assert.Equal(T.AddHours(ChangeIds()[0] - 2), capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);

        Execute("UPDATE metadata SET value = 'soon' WHERE key = '" + CapKey + "';");
        HistoryRead unparsed = capped.Search(Everything, 10, default);
        Assert.Equal(HistoryState.Usable, unparsed.State);
        Assert.Null(unparsed.CapTrimmedBeforeUtc);

        Assert.Equal(HistoryState.Missing, capped.Clear());
        Assert.Null(capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);
        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(0, 10)));
        Assert.Null(capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM metadata WHERE key = '" + CapKey + "';"));
    }

    [Fact]
    public void Inside_a_write_transaction_the_pages_in_use_count_its_own_inserts_and_deletes()
    {
        Recorded();
        using SqliteConnection conn = OpenRaw();
        long committed = FeedHistory.BytesInUse(conn, null);
        Assert.Equal((PragmaIn(conn, null, "page_count") - PragmaIn(conn, null, "freelist_count"))
                     * PragmaIn(conn, null, "page_size"), committed);

        using SqliteTransaction tx = conn.BeginTransaction();
        Run(conn, tx, "WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 5000) "
                    + "INSERT INTO entries (change_id, is_ipv6, copy, old_file, added, text, start_ip, end_ip, reason) "
                    + "SELECT 1, 0, NULL, 'Read', 1, printf('8.8.%d.%d', value / 256, value % 256), NULL, NULL, 'NotTold' "
                    + "FROM n;");
        long pagesAfterInsert = PragmaIn(conn, tx, "page_count");
        long afterInsert = FeedHistory.BytesInUse(conn, tx);
        Assert.True(afterInsert > committed + 10 * 4096, "page_count inside the transaction doesn't count its own pages.");

        Run(conn, tx, "DELETE FROM entries WHERE change_id = 1 AND text <> '8.8.8.8';");
        Assert.Equal(pagesAfterInsert, PragmaIn(conn, tx, "page_count"));
        Assert.True(FeedHistory.BytesInUse(conn, tx) < afterInsert / 2, "freelist_count doesn't count the transaction's frees.");

        tx.Rollback();
        Assert.Equal(committed, FeedHistory.BytesInUse(conn, null));
    }

    [Fact]
    public void Below_the_cap_a_record_trims_nothing_even_past_nine_tenths_of_it()
    {
        FeedHistory history = NewHistory();
        RecordMany(history, 0, 8, 500);
        long eight = BytesInUse();
        RecordMany(history, 8, 1, 500);
        long nine = BytesInUse();
        // The tenth change adds about what the ninth did, so it lands near 96 % of this cap
        long cap = (nine + (nine - eight)) * 25 / 24;
        var capped = new FeedHistory(HistoryPath, _time, cap);

        Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(9, 500)));

        Assert.InRange(BytesInUse(), cap / 10 * 9 + 1, cap);
        Assert.Equal(Enumerable.Range(1, 10).Select(id => (long)id), ChangeIds());
        Assert.Equal(5000L, Scalar("SELECT count(*) FROM entries;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM metadata WHERE key = '" + CapKey + "';"));
        Assert.Null(capped.Search(Everything, 10, default).CapTrimmedBeforeUtc);
    }

    [Fact]
    public void A_record_that_fails_after_the_cap_trimmed_leaves_every_change_and_no_mark()
    {
        RecordMany(NewHistory(), 0, 8, 500);
        var capped = new FeedHistory(HistoryPath, _time, BytesInUse());
        string hash = Sha(HistoryPath);
        capped.BeforeCommit = () => throw new SqliteException("test", 13);

        Assert.Equal(HistoryState.StorageError, capped.Record(ManyChange(8, 500)));

        Assert.Equal(hash, Sha(HistoryPath));
        Assert.Equal(Enumerable.Range(1, 8).Select(id => (long)id), ChangeIds());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM metadata WHERE key = '" + CapKey + "';"));
    }

    private static string Address(int i) =>
        string.Join('.', 8, (i >> 16) & 255, (i >> 8) & 255, i & 255);

    // Change n (from 0) is recorded n hours after T, with entries no other change holds
    private static FeedChangeRecord ManyChange(int n, int entries) =>
        Change(T.AddHours(n), new FeedFamilyChange(false, null, FeedFileState.Read,
                                                   Enumerable.Range(n * 5000, entries).Select(Address).ToList(), []));

    private static void RecordMany(FeedHistory history, int from, int changes, int entries)
    {
        for (int n = from; n < from + changes; n++)
            Assert.Equal(HistoryState.Usable, history.Record(ManyChange(n, entries)));
    }

    private int RecordUntilTrimmed(FeedHistory capped, int n)
    {
        long first = ChangeIds()[0];
        for (int limit = n + 8; n < limit; n++)
        {
            Assert.Equal(HistoryState.Usable, capped.Record(ManyChange(n, 500)));
            if (ChangeIds()[0] > first)
                return n + 1;
        }
        Assert.Fail("Eight more changes past the cap trimmed nothing.");
        return n;
    }

    private long BytesInUse()
    {
        using SqliteConnection conn = OpenRaw();
        return FeedHistory.BytesInUse(conn, null);
    }

    private List<long> ChangeIds()
    {
        var ids = new List<long>();
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT change_id FROM changes ORDER BY change_id;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    private Dictionary<long, long> EntriesPerChange()
    {
        var counts = new Dictionary<long, long>();
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT change_id, count(*) FROM entries GROUP BY change_id;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetInt64(0)] = reader.GetInt64(1);
        return counts;
    }

    private static long PragmaIn(SqliteConnection conn, SqliteTransaction? tx, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA " + name + ";";
        return Assert.IsType<long>(cmd.ExecuteScalar());
    }

    private static void Run(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void RecordAt(FeedHistory history, DateTimeOffset at, string entry)
    {
        _time.SetUtcNow(at);
        Assert.Equal(HistoryState.Usable, history.Record(Change(at, IPv4(entry))));
    }

    // The shared fake clock can't go back, so a clock set back records through a store with its own
    private void RecordUnderItsOwnClock(DateTimeOffset at, string entry)
    {
        var history = new FeedHistory(HistoryPath, new FakeTimeProvider(at));
        Assert.Equal(HistoryState.Usable, history.Record(Change(at, IPv4(entry))));
    }

    private void RecordEvery30Days(FeedHistory history, DateTimeOffset from, DateTimeOffset to)
    {
        for (DateTimeOffset at = from; at <= to; at = at.AddDays(30))
            RecordAt(history, at, "8.8.8.8");
    }

    private List<DateTimeOffset> ChangeTimes()
    {
        var times = new List<DateTimeOffset>();
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT at_utc FROM changes ORDER BY at_utc;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            times.Add(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)));
        return times;
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

    private object Scalar(string sql)
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Pragma(SqliteConnection conn, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA " + name + ";";
        return Assert.IsType<long>(cmd.ExecuteScalar());
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
