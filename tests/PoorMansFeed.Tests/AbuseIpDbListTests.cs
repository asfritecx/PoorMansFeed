using System.Collections;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbListTests : IDisposable
{
    private static readonly DateTimeOffset Gen1 = new(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Gen1.AddDays(-30);

    private readonly DbTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ListPath => _temp.FilePath("abuseipdb.db");

    private AbuseIpDbList NewList() => new(ListPath);

    [Fact]
    public void Reads_of_a_missing_file_are_missing_and_empty_and_create_no_file()
    {
        AbuseIpDbList list = NewList();

        ListRead<ListFacts> facts = list.ReadFacts(LongAgo, TimeSpan.Zero);
        ListRead<IReadOnlyList<AbuseListEntry>> current = list.ReadCurrent(LongAgo, includeIPv6: true);

        Assert.Equal(AbuseListState.Missing, facts.State);
        Assert.Same(ListFacts.Empty, facts.Value);
        Assert.Equal(AbuseListState.Missing, current.State);
        Assert.NotNull(current.Value);
        Assert.Empty(current.Value);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Fact]
    public void Every_connection_runs_with_trusted_schema_and_foreign_keys_off()
    {
        AbuseIpDbList list = NewList();

        // A new path, as a first write opens it
        using (SqliteConnection created = list.Open(SqliteOpenMode.ReadWriteCreate))
        {
            Assert.Equal(0L, Pragma(created, "trusted_schema"));
            Assert.Equal(0L, Pragma(created, "foreign_keys"));
        }

        // An existing list, as a read opens it
        Assert.Equal(AbuseListState.Usable,
                     list.Merge(Listing(Gen1, Entry("8.8.8.8", 90)), Gen1, LongAgo, CancellationToken.None).State);
        using (SqliteConnection existing = list.Open(SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(0L, Pragma(existing, "trusted_schema"));
            Assert.Equal(0L, Pragma(existing, "foreign_keys"));
        }
    }

    private static long Pragma(SqliteConnection conn, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA " + name + ";";
        return Assert.IsType<long>(cmd.ExecuteScalar());
    }

    [Fact]
    public void The_first_merge_creates_the_file_and_stamps_rows_with_the_download_time()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset downloadedAt = Gen1.AddHours(3);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 90, "US"), Entry("1.1.1.1", 40),
                                                      Entry("2001:4860:4860::8888", 75)),
                                               downloadedAt, LongAgo, CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(new ListMerge(true, 3, 0, 0), merge.Value);
        Dictionary<string, string> meta = Metadata();
        Assert.Equal("1", meta["schema_version"]);
        Assert.Equal(Unix(downloadedAt), meta["last_download_at"]);
        Assert.Equal(Unix(Gen1), meta["last_generated_at"]);
        List<string[]> rows = Rows();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(Unix(downloadedAt), row[1]);
            Assert.Equal(Unix(downloadedAt), row[2]);
        });
        Assert.Equal(["abuseipdb.db"], FolderNames());
    }

    [Fact]
    public void A_newer_generation_moves_last_seen_and_keeps_what_the_new_entry_lacks()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset reported = Gen1.AddHours(-5);
        DateTimeOffset gen2 = Gen1.AddHours(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US", reported), Entry("1.1.1.1", 10)),
                   Gen1, LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(gen2, Entry("8.8.8.8", 70), Entry("9.9.9.9", 20, "DE")),
                                               gen2, LongAgo, CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        string[] google = Row("8.8.8.8");
        Assert.Equal([Hex("8.8.8.8"), Unix(Gen1), Unix(gen2), "70", "US", Unix(reported)], google);
        string[] cloudflare = Row("1.1.1.1");
        Assert.Equal([Hex("1.1.1.1"), Unix(Gen1), Unix(Gen1), "10", "", ""], cloudflare);
        string[] quad9 = Row("9.9.9.9");
        Assert.Equal([Hex("9.9.9.9"), Unix(gen2), Unix(gen2), "20", "DE", ""], quad9);
        Assert.Equal(Unix(gen2), Metadata()["last_generated_at"]);
    }

    [Fact]
    public void A_generation_older_than_the_cutoff_keeps_every_row_it_lists()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset downloadedAt = Gen1.AddDays(40);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("1.1.1.1", 10)),
                                               downloadedAt, cutoff: Gen1.AddDays(10), CancellationToken.None);

        Assert.Equal(new ListMerge(true, 2, 0, 0), merge.Value);
        Assert.Equal([Hex("1.1.1.1"), Hex("8.8.8.8")], Rows().Select(row => row[0]));
        Assert.All(Rows(), row => Assert.Equal(Unix(downloadedAt), row[2]));
    }

    [Fact]
    public void The_same_generation_downloaded_later_refreshes_last_seen_and_keeps_a_row_that_lapsed()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset first = Gen1.AddHours(1);
        DateTimeOffset later = Gen1.AddDays(5);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US"), Entry("1.1.1.1", 10)), first, LongAgo, CancellationToken.None);
        // A download in between keeps 8.8.8.8 within the cutoff, so it is refreshed, not listed again after lapsing
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US")), Gen1.AddDays(3), LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 99, "GB")), later,
                                               cutoff: Gen1.AddDays(2), CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        // 1.1.1.1 lapsed at the cutoff but is within the horizon, so it is hidden, not removed. It was within the keep
        // days of the last download, so it is counted as lapsed
        Assert.Equal(new ListMerge(false, 0, 1, 0), merge.Value);
        Assert.Equal([[Hex("1.1.1.1"), Unix(first), Unix(first), "10", "", ""],
                      [Hex("8.8.8.8"), Unix(first), Unix(later), "99", "GB", ""]], Rows());
        Assert.Equal(["8.8.8.8"], list.ReadCurrent(Gen1.AddDays(2), includeIPv6: true).Value!.Select(Text));
        Dictionary<string, string> meta = Metadata();
        Assert.Equal(Unix(later), meta["last_download_at"]);
        Assert.Equal(Unix(Gen1), meta["last_generated_at"]);
    }

    [Fact]
    public void An_older_generation_is_not_new_and_keeps_the_stored_generation()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1.AddSeconds(-1), Entry("9.9.9.9", 10)), later, LongAgo,
                                               CancellationToken.None);

        Assert.Equal(new ListMerge(false, 1, 0, 0), merge.Value);
        Assert.Equal([Hex("9.9.9.9"), Unix(later), Unix(later), "10", "", ""], Row("9.9.9.9"));
        Dictionary<string, string> meta = Metadata();
        Assert.Equal(Unix(later), meta["last_download_at"]);
        Assert.Equal(Unix(Gen1), meta["last_generated_at"]);
    }

    [Fact]
    public void Expiry_keeps_a_row_at_the_horizon_and_removes_one_a_second_older()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddSeconds(1);
        // The horizon of this download is gen2 exactly
        DateTimeOffset gen3 = gen2.AddDays(AbuseIpDbLimits.MaxKeepDays);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)), Gen1, LongAgo,
                   CancellationToken.None);
        list.Merge(Listing(gen2, Entry("1.1.1.1", 50), Entry("2606:4700:4700::1111", 50)), gen2, LongAgo,
                   CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(gen3, Entry("9.9.9.9", 50)), gen3, cutoff: gen3.AddDays(-30),
                                               CancellationToken.None);

        // All four were within the keep days of the last download (gen2) and lapsed; the two of Gen1 also expired
        Assert.Equal(new ListMerge(true, 1, 4, 2), merge.Value);
        Assert.Equal([Hex("1.1.1.1"), Hex("9.9.9.9"), Hex("2606:4700:4700::1111")], Rows().Select(row => row[0]));
    }

    [Fact]
    public void A_row_older_than_the_cutoff_but_within_the_horizon_is_hidden_kept_and_added_when_listed_again()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(40);
        DateTimeOffset again = Gen1.AddDays(41);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)), Gen1, LongAgo,
                   CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("9.9.9.9", 60), Entry("2606:4700:4700::1111", 60)),
                                               later, cutoff: later.AddDays(-30), CancellationToken.None);

        // Added counts what the cutoff shows, not every row the file holds; the two rows of Gen1 lapsed
        Assert.Equal(new ListMerge(true, 2, 2, 0), merge.Value);
        Assert.Equal(4, Rows().Count);
        Assert.Equal(["9.9.9.9", "2606:4700:4700::1111"],
                     list.ReadCurrent(later.AddDays(-30), includeIPv6: true).Value!.Select(Text));
        Assert.Equal(new ListFacts(1, 1, later, later), list.ReadFacts(later.AddDays(-30), TimeSpan.Zero).Value);
        // Raising the keep days brings the hidden rows back
        Assert.Equal(["8.8.8.8", "9.9.9.9", "2001:4860:4860::8888", "2606:4700:4700::1111"],
                     list.ReadCurrent(later.AddDays(-60), includeIPv6: true).Value!.Select(Text));
        Assert.Equal(new ListFacts(2, 2, later, later), list.ReadFacts(later.AddDays(-60), TimeSpan.Zero).Value);

        ListRead<ListMerge> relisted = list.Merge(Listing(again, Entry("8.8.8.8", 70), Entry("2001:4860:4860::8888", 70)),
                                                  again, cutoff: again.AddDays(-30), CancellationToken.None);

        // Still within the horizon, so listed again they are added and keep their first sighting
        Assert.Equal(new ListMerge(true, 2, 0, 0), relisted.Value);
        Assert.Equal([Hex("8.8.8.8"), Unix(Gen1), Unix(again), "70", "", ""], Row("8.8.8.8"));
        Assert.Equal([Hex("2001:4860:4860::8888"), Unix(Gen1), Unix(again), "70", "", ""], Row("2001:4860:4860::8888"));
        Assert.Equal([("8.8.8.8", Gen1), ("9.9.9.9", later), ("2001:4860:4860::8888", Gen1), ("2606:4700:4700::1111", later)],
                     list.ReadCurrent(again.AddDays(-30), includeIPv6: true).Value!.Select(e => (Text(e), e.FirstSeen)));
    }

    [Fact]
    public void A_row_last_listed_before_the_horizon_and_listed_again_starts_again_from_this_download()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays + 1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        // Gen1 is a day before this download's horizon; the upsert runs before the expiry, so the row is kept
        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("8.8.8.8", 60)), later, cutoff: later.AddDays(-30),
                                               CancellationToken.None);

        // It was within the keep days of the last download and is within this one's, so it isn't added
        Assert.Equal(new ListMerge(true, 0, 0, 0), merge.Value);
        Assert.Equal([[Hex("8.8.8.8"), Unix(later), Unix(later), "60", "", ""]], Rows());
        AbuseListEntry google = Assert.Single(list.ReadCurrent(later.AddDays(-30), includeIPv6: true).Value!);
        Assert.Equal(later, google.FirstSeen);
    }

    [Fact]
    public void A_row_last_listed_exactly_at_the_horizon_and_listed_again_keeps_its_first_seen()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddSeconds(1);
        // The horizon of this download is gen2 exactly
        DateTimeOffset later = gen2.AddDays(AbuseIpDbLimits.MaxKeepDays);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(gen2, Entry("8.8.8.8", 55)), gen2, LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("8.8.8.8", 60)), later, cutoff: later.AddDays(-30),
                                               CancellationToken.None);

        // Within the keep days of the last download (gen2) and of this one, so it isn't added
        Assert.Equal(new ListMerge(true, 0, 0, 0), merge.Value);
        Assert.Equal([[Hex("8.8.8.8"), Unix(Gen1), Unix(later), "60", "", ""]], Rows());
    }

    [Fact]
    public void A_row_last_seen_a_day_beyond_the_horizon_is_removed()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays + 1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)), Gen1, LongAgo,
                   CancellationToken.None);
        list.Merge(Listing(Gen1.AddDays(2), Entry("1.1.1.1", 50), Entry("2606:4700:4700::1111", 50)), Gen1.AddDays(2),
                   LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("9.9.9.9", 60)), later,
                                               cutoff: later.AddDays(-AbuseIpDbLimits.MaxKeepDays), CancellationToken.None);

        // The two rows of Gen1 were within the keep days of the last download and are past the horizon now
        Assert.Equal(new ListMerge(true, 1, 2, 2), merge.Value);
        Assert.Equal([Hex("1.1.1.1"), Hex("9.9.9.9"), Hex("2606:4700:4700::1111")], Rows().Select(row => row[0]));
    }

    [Fact]
    public void Merge_refuses_a_cutoff_earlier_than_the_horizon_before_it_opens_the_file()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset downloadedAt = Gen1.AddDays(10);
        DateTimeOffset horizon = downloadedAt.AddDays(-AbuseIpDbLimits.MaxKeepDays);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), downloadedAt, horizon.AddTicks(-1), CancellationToken.None));
        Assert.Empty(FolderNames());

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), downloadedAt, horizon, CancellationToken.None);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        string hash = Sha(ListPath);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            list.Merge(Listing(Gen1, Entry("9.9.9.9", 50)), downloadedAt, horizon.AddSeconds(-1), CancellationToken.None));
        Assert.Equal(hash, Sha(ListPath));
    }

    [Fact]
    public void RecordAttempt_on_a_missing_file_creates_an_empty_list_and_stores_the_time()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset at = Gen1.AddMinutes(3);

        Assert.Equal(AbuseListState.Usable, list.RecordAttempt(at));

        Assert.Equal(["abuseipdb.db"], FolderNames());
        ListRead<ListFacts> facts = list.ReadFacts(LongAgo, TimeSpan.Zero);
        Assert.Equal(AbuseListState.Usable, facts.State);
        Assert.Equal(ListFacts.Empty, facts.Value);
        Dictionary<string, string> meta = Metadata();
        Assert.Equal("1", meta["schema_version"]);
        Assert.Equal(Unix(at), meta["last_attempt_at"]);
        Assert.Empty(Rows());
    }

    [Fact]
    public void RecordAttempt_on_a_write_protected_list_is_a_storage_error_and_changes_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)), Gen1, LongAgo,
                   CancellationToken.None);
        string hash = Sha(ListPath);
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            // SQLite opens the file read-only without an error, and a read still works
            Assert.Equal(AbuseListState.Usable, list.ReadFacts(LongAgo, TimeSpan.Zero).State);
            Assert.Equal(AbuseListState.StorageError, list.RecordAttempt(Gen1.AddDays(1)));
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }

        Assert.Equal(hash, Sha(ListPath));
        Assert.Equal(["abuseipdb.db"], FolderNames());
        Assert.False(Metadata().ContainsKey("last_attempt_at"));
    }

    [Fact]
    public void A_failure_part_way_through_a_merge_changes_nothing()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US"), Entry("1.1.1.1", 10)), Gen1, LongAgo, CancellationToken.None);
        // SQLITE_FULL after every statement of the merge has run, as a disk filling up would fail it
        list.BeforeCommit = () => throw new SqliteException("test", 13);
        string before = Dump();

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1.AddHours(1), Entry("8.8.8.8", 70), Entry("1.1.1.1", 20),
                                                       Entry("9.9.9.9", 30), Entry("185.199.108.153", 40),
                                                       Entry("2001:4860:4860::8888", 50)),
                                               Gen1.AddHours(1), LongAgo, CancellationToken.None);

        Assert.Equal(AbuseListState.StorageError, merge.State);
        Assert.Null(merge.Value);
        Assert.Equal(before, Dump());
        Assert.False(File.Exists(ListPath + "-journal"));
    }

    [Fact]
    public void A_cancelled_merge_throws_and_changes_nothing()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US"), Entry("1.1.1.1", 10)), Gen1, LongAgo, CancellationToken.None);
        string before = Dump();
        using var cts = new CancellationTokenSource();
        var entries = new CancellingList(
            [Entry("8.8.8.8", 70), Entry("1.1.1.1", 20), Entry("9.9.9.9", 30), Entry("185.199.108.153", 40),
             Entry("2001:4860:4860::8888", 50)],
            // The first pass over the entries is the new-address set's, so the second read cancels in the upsert loop,
            // after some rows were upserted
            cancelAt: 3, cts, cancelOnRead: 2);

        Assert.Throws<OperationCanceledException>(() =>
            list.Merge(new Blacklist(Gen1.AddHours(1), entries, 0, 0, 0), Gen1.AddHours(1), LongAgo, cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(before, Dump());
        Assert.False(File.Exists(ListPath + "-journal"));
    }

    [Fact]
    public void A_file_from_a_newer_version_is_never_written()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute("UPDATE metadata SET value = '2' WHERE key = 'schema_version';");

        AssertEveryMethodGives(list, AbuseListState.Newer);

        Assert.True(list.Delete());
        Assert.False(File.Exists(ListPath));
    }

    public static TheoryData<string> UnreadableFiles => ["random bytes", "text", "other tables only", "no version", "version zero", "version not a number", "no addresses table",
                                                         "metadata without value", "metadata with an extra column", "addresses missing a column",
                                                         "addresses with an extra column", "addresses column of another type",
                                                         "metadata value null", "metadata key null", "metadata value not text",
                                                         "addresses without a primary key", "metadata without a primary key",
                                                         "addresses keyed by another column",
                                                         "trigger on addresses", "trigger on metadata", "trigger on new_addresses",
                                                         "trigger on another table", "a view", "view named new_addresses", "only a view",
                                                         "unique index on addresses", "index on metadata",
                                                         "index named new_addresses on addresses", "own index made unique",
                                                         "own index made partial", "own index on another column",
                                                         "addresses with a check", "addresses remade in place with a check",
                                                         "addresses with a generated column", "metadata made with a no-break space",
                                                         "trigger relabelled as a table", "unique index relabelled to another table"];

    [Theory]
    [MemberData(nameof(UnreadableFiles))]
    public void A_file_that_isnt_this_list_is_unreadable_and_never_written(string kind)
    {
        switch (kind)
        {
            case "random bytes":
                byte[] noise = new byte[8192];
                new Random(1234).NextBytes(noise);
                File.WriteAllBytes(ListPath, noise);
                break;
            case "text":
                File.WriteAllText(ListPath, "8.8.8.8\n1.1.1.1\n", Encoding.UTF8);
                break;
            case "other tables only":
                Execute("CREATE TABLE things (x INTEGER); INSERT INTO things VALUES (1);", create: true);
                break;
            case "only a view":
                Execute("CREATE VIEW things AS SELECT 1 AS x;", create: true);
                break;
            default:
                NewList().Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
                Execute(kind switch
                {
                    "no version" => "DELETE FROM metadata WHERE key = 'schema_version';",
                    "version zero" => "UPDATE metadata SET value = '0' WHERE key = 'schema_version';",
                    "no addresses table" => "DROP TABLE addresses;",
                    "metadata without value" => "ALTER TABLE metadata DROP COLUMN value;",
                    "metadata with an extra column" => "ALTER TABLE metadata ADD COLUMN note TEXT NULL;",
                    "addresses missing a column" => "ALTER TABLE addresses DROP COLUMN last_reported;",
                    "addresses with an extra column" => "ALTER TABLE addresses ADD COLUMN note TEXT NULL;",
                    "addresses column of another type" => """
                        CREATE TABLE addresses2 (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                          score TEXT NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL) WITHOUT ROWID;
                        INSERT INTO addresses2 SELECT * FROM addresses;
                        DROP TABLE addresses;
                        ALTER TABLE addresses2 RENAME TO addresses;
                        """,
                    "addresses without a primary key" => """
                        CREATE TABLE addresses2 (ip BLOB NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                          score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL);
                        INSERT INTO addresses2 SELECT * FROM addresses;
                        DROP TABLE addresses;
                        ALTER TABLE addresses2 RENAME TO addresses;
                        """,
                    "metadata without a primary key" => """
                        CREATE TABLE metadata2 (key TEXT NOT NULL, value TEXT NOT NULL);
                        INSERT INTO metadata2 SELECT key, value FROM metadata;
                        DROP TABLE metadata;
                        ALTER TABLE metadata2 RENAME TO metadata;
                        """,
                    "addresses keyed by another column" => """
                        CREATE TABLE addresses2 (ip BLOB NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER PRIMARY KEY,
                          score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL);
                        INSERT INTO addresses2 SELECT * FROM addresses;
                        DROP TABLE addresses;
                        ALTER TABLE addresses2 RENAME TO addresses;
                        """,
                    "trigger on addresses" => "CREATE TRIGGER noted AFTER INSERT ON addresses BEGIN SELECT 1; END;",
                    "trigger on metadata" => "CREATE TRIGGER noted AFTER UPDATE ON metadata BEGIN SELECT 1; END;",
                    "trigger on new_addresses" => "CREATE TRIGGER noted AFTER INSERT ON new_addresses BEGIN SELECT 1; END;",
                    "trigger on another table" => """
                        CREATE TABLE notes (x INTEGER);
                        CREATE TRIGGER noted AFTER INSERT ON notes BEGIN SELECT 1; END;
                        """,
                    "a view" => "CREATE VIEW recent AS SELECT ip FROM addresses;",
                    "view named new_addresses" => "DROP TABLE new_addresses; CREATE VIEW new_addresses AS SELECT ip FROM addresses;",
                    "unique index on addresses" => "CREATE UNIQUE INDEX addresses_score ON addresses (score);",
                    "index on metadata" => "CREATE INDEX metadata_value ON metadata (value);",
                    "index named new_addresses on addresses" => "DROP TABLE new_addresses; CREATE INDEX new_addresses ON addresses (score);",
                    "own index made unique" => """
                        DROP INDEX idx_addresses_last_seen;
                        CREATE UNIQUE INDEX idx_addresses_last_seen ON addresses (last_seen);
                        """,
                    "own index made partial" => """
                        DROP INDEX idx_addresses_last_seen;
                        CREATE INDEX idx_addresses_last_seen ON addresses (last_seen) WHERE score > 0;
                        """,
                    "own index on another column" => """
                        DROP INDEX idx_addresses_last_seen;
                        CREATE INDEX idx_addresses_last_seen ON addresses (score);
                        """,
                    // The rename stores the name quoted, so the text differs there too
                    "addresses with a check" => """
                        CREATE TABLE addresses2 (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                          score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL, CHECK (score >= 0)) WITHOUT ROWID;
                        INSERT INTO addresses2 SELECT * FROM addresses;
                        DROP TABLE addresses;
                        ALTER TABLE addresses2 RENAME TO addresses;
                        CREATE INDEX idx_addresses_last_seen ON addresses (last_seen);
                        """,
                    // Made under its own name with its own index, so the CHECK is all that differs
                    "addresses remade in place with a check" => """
                        ALTER TABLE addresses RENAME TO old;
                        CREATE TABLE addresses (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                          score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL, CHECK (score >= 0)) WITHOUT ROWID;
                        INSERT INTO addresses SELECT * FROM old;
                        DROP TABLE old;
                        CREATE INDEX idx_addresses_last_seen ON addresses (last_seen);
                        """,
                    // pragma_table_info doesn't list a generated column; only the stored text shows it
                    "addresses with a generated column" => "ALTER TABLE addresses ADD COLUMN extra INTEGER AS (score + 1);",
                    // SQLite reads a no-break space as part of a word, so value's type becomes "TEXT NOT NULL" with no
                    // constraint; only SQLite's own separators may be collapsed in the text compare
                    "metadata made with a no-break space" => "ALTER TABLE metadata RENAME TO old; "
                                                            + "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT\u00a0NULL); "
                                                            + "INSERT INTO metadata SELECT key, value FROM old; DROP TABLE old;",
                    // The classifier trusts each row's type and table; SQLite reads a row whose CREATE text says
                    // otherwise as a malformed schema, and these pin that
                    "trigger relabelled as a table" => """
                        CREATE TABLE other (x);
                        CREATE TRIGGER refuse BEFORE INSERT ON addresses BEGIN SELECT RAISE(ABORT, 'refused'); END;
                        PRAGMA writable_schema = ON;
                        UPDATE sqlite_master SET type = 'table', tbl_name = 'other' WHERE name = 'refuse';
                        PRAGMA writable_schema = OFF;
                        """,
                    "unique index relabelled to another table" => """
                        CREATE TABLE other (x);
                        CREATE UNIQUE INDEX one_score ON addresses (score);
                        PRAGMA writable_schema = ON;
                        UPDATE sqlite_master SET tbl_name = 'other' WHERE name = 'one_score';
                        PRAGMA writable_schema = OFF;
                        """,
                    "metadata value null" => LooseMetadata + "INSERT INTO metadata VALUES ('rl_limit', NULL);",
                    "metadata key null" => LooseMetadata + "INSERT INTO metadata VALUES (NULL, '5');",
                    "metadata value not text" => LooseMetadata + "INSERT INTO metadata VALUES ('rl_limit', X'35');",
                    _ => "UPDATE metadata SET value = 'one' WHERE key = 'schema_version';",
                });
                break;
        }

        AbuseIpDbList list = NewList();
        AssertEveryMethodGives(list, AbuseListState.Unreadable);

        Assert.True(list.Delete());
        Assert.False(File.Exists(ListPath));
    }

    [Theory]
    [InlineData("addresses")]
    [InlineData("idx_addresses_last_seen")]
    public void A_damaged_page_of_the_addresses_or_their_index_is_unreadable_and_never_written(string damaged)
    {
        NewList().Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute(InsertManySql(2000, Unix(Gen1)));
        long rootPage;
        long pageSize;
        using (SqliteConnection conn = OpenRaw())
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rootpage FROM sqlite_master WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", damaged);
            rootPage = (long)cmd.ExecuteScalar()!;
            pageSize = Scalar(conn, "PRAGMA page_size;");
        }
        Assert.True(rootPage > 1);
        using (var file = new FileStream(ListPath, FileMode.Open, FileAccess.Write))
        {
            file.Position = (rootPage - 1) * pageSize;
            file.Write(Enumerable.Repeat((byte)0xFF, 64).ToArray());
        }
        using (SqliteConnection conn = OpenRaw())
        {
            if (damaged == "addresses")
            {
                // count(*) would read the smaller last_seen index, so sum the score to read the table itself
                var error = Assert.Throws<SqliteException>(() => Scalar(conn, "SELECT sum(score) FROM addresses;"));
                Assert.Equal(11, error.SqliteErrorCode);
            }
            else
            {
                // The table itself still reads; only the index is damaged
                Assert.Equal(2001L * 50, Scalar(conn, "SELECT sum(score) FROM addresses;"));
            }
        }

        AbuseIpDbList list = NewList();
        AssertEveryMethodGives(list, AbuseListState.Unreadable);

        Assert.True(list.Delete());
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public void A_hot_journal_is_rolled_back_at_the_next_read()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The copy of a locked file uses cp here.");
            return;
        }

        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("1.1.1.1", 10), Entry("2001:4860:4860::8888", 75)),
                   Gen1, LongAgo, CancellationToken.None);
        string before = Sha(ListPath);
        string copy = _temp.FilePath("hot.db");
        using (SqliteConnection conn = OpenRaw())
        {
            Execute(conn, "PRAGMA cache_size = 1;");
            using SqliteTransaction tx = conn.BeginTransaction();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = InsertManySql(20000, Unix(Gen1));
                cmd.ExecuteNonQuery();
            }
            // File.Copy's own lock conflicts with SQLite's on macOS, so copy as a crash would leave the files
            Run("/bin/cp", ListPath, copy);
            Run("/bin/cp", ListPath + "-journal", copy + "-journal");
            tx.Rollback();
        }
        // The copy really is hot: the open write had already changed the database file
        Assert.NotEqual(before, Sha(copy));

        ListRead<ListFacts> facts = new AbuseIpDbList(copy).ReadFacts(LongAgo, TimeSpan.Zero);

        Assert.Equal(AbuseListState.Usable, facts.State);
        Assert.Equal(2, facts.Value!.IPv4Count);
        Assert.Equal(1, facts.Value.IPv6Count);
        Assert.False(File.Exists(copy + "-journal"));
        Assert.Equal(before, Sha(ListPath));
    }

    [Fact]
    public void A_file_that_cant_be_opened_is_a_storage_error()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        string hash = Sha(ListPath);
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.None);
        try
        {
            Assert.Equal(AbuseListState.StorageError, list.ReadFacts(LongAgo, TimeSpan.Zero).State);
            Assert.Equal(AbuseListState.StorageError, list.ReadCurrent(LongAgo, includeIPv6: true).State);
            ListRead<ListFeed> feed = list.ReadFeed(LongAgo, TimeSpan.Zero, includeIPv6: true);
            Assert.Equal(AbuseListState.StorageError, feed.State);
            Assert.Null(feed.Value);
            Assert.Equal(AbuseListState.StorageError, list.RecordAttempt(Gen1.AddDays(1)));
            Assert.Equal(AbuseListState.StorageError,
                         list.Merge(Listing(Gen1.AddDays(1), Entry("9.9.9.9", 50)), Gen1.AddDays(1), LongAgo,
                                    CancellationToken.None).State);
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }

        Assert.Equal(hash, Sha(ListPath));
        Assert.Equal(["abuseipdb.db"], FolderNames());
    }

    [Fact]
    public void A_zero_byte_file_is_an_empty_list_that_a_merge_fills()
    {
        File.WriteAllBytes(ListPath, []);
        AbuseIpDbList list = NewList();

        ListRead<ListFacts> facts = list.ReadFacts(LongAgo, TimeSpan.Zero);
        ListRead<IReadOnlyList<AbuseListEntry>> current = list.ReadCurrent(LongAgo, includeIPv6: true);
        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, facts.State);
        Assert.Equal(ListFacts.Empty, facts.Value);
        Assert.Equal(AbuseListState.Usable, current.State);
        Assert.Empty(current.Value!);
        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        Assert.Equal("1", Metadata()["schema_version"]);
    }

    [Fact]
    public void ReadFacts_counts_each_family_within_the_cutoff_and_gives_the_times()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddDays(2);
        DateTimeOffset downloadedAt = gen2.AddMinutes(7);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("9.9.9.9", 50), Entry("2001:4860:4860::8888", 50)),
                   Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(gen2, Entry("1.1.1.1", 50), Entry("2606:4700:4700::1111", 50), Entry("2001:4860::", 50)),
                   downloadedAt, LongAgo, CancellationToken.None);

        ListRead<ListFacts> all = list.ReadFacts(LongAgo, TimeSpan.Zero);
        ListRead<ListFacts> recent = list.ReadFacts(gen2, TimeSpan.Zero);

        Assert.Equal(AbuseListState.Usable, all.State);
        Assert.Equal(new ListFacts(3, 3, downloadedAt, gen2), all.Value);
        Assert.Equal(new ListFacts(1, 2, downloadedAt, gen2), recent.Value);
    }

    [Fact]
    public void ReadCurrent_filters_by_the_cutoff_and_family_and_orders_by_length_then_bytes()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddDays(1);
        DateTimeOffset reported = Gen1.AddHours(-1);
        list.Merge(Listing(Gen1, Entry("2606:4700:4700::1111", 10), Entry("185.199.108.153", 20), Entry("2001:4860::", 30),
                           Entry("32.1.72.96", 40), Entry("9.9.9.9", 50, "CH", reported), Entry("8.8.8.8", 60)),
                   Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(gen2, Entry("1.1.1.1", 70), Entry("9.9.9.9", 55)), gen2, LongAgo, CancellationToken.None);

        ListRead<IReadOnlyList<AbuseListEntry>> both = list.ReadCurrent(LongAgo, includeIPv6: true);
        ListRead<IReadOnlyList<AbuseListEntry>> ipv4 = list.ReadCurrent(LongAgo, includeIPv6: false);
        ListRead<IReadOnlyList<AbuseListEntry>> recent = list.ReadCurrent(gen2, includeIPv6: true);

        Assert.Equal(AbuseListState.Usable, both.State);
        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "32.1.72.96", "185.199.108.153", "2001:4860::", "2606:4700:4700::1111"],
                     both.Value!.Select(Text));
        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "32.1.72.96", "185.199.108.153"], ipv4.Value!.Select(Text));
        Assert.Equal(["1.1.1.1", "9.9.9.9"], recent.Value!.Select(Text));
        // A row a fraction of a second older than the cutoff is older than it
        Assert.Equal(["1.1.1.1", "9.9.9.9"], list.ReadCurrent(Gen1.AddMilliseconds(500), includeIPv6: true).Value!.Select(Text));
        AbuseListEntry quad9 = recent.Value![1];
        Assert.Equal(Gen1, quad9.FirstSeen);
        Assert.Equal(gen2, quad9.LastSeen);
        Assert.Equal(55, quad9.Score);
        Assert.Equal("CH", quad9.CountryCode);
        Assert.Equal(reported, quad9.LastReportedAt);
        Assert.Null(recent.Value![0].CountryCode);
        Assert.Null(recent.Value![0].LastReportedAt);
    }

    [Fact]
    public void ReadFeed_gives_the_rows_ReadCurrent_gives_and_the_last_download_and_generation_times()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddDays(1);
        DateTimeOffset downloadedAt = gen2.AddMinutes(3);
        list.Merge(Listing(Gen1, Entry("2001:4860::", 30), Entry("9.9.9.9", 50, "CH", Gen1.AddHours(-1)), Entry("8.8.8.8", 60)),
                   Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(gen2, Entry("1.1.1.1", 70), Entry("2606:4700:4700::1111", 10)), downloadedAt, LongAgo,
                   CancellationToken.None);

        foreach (bool includeIPv6 in new[] { true, false })
        {
            foreach (DateTimeOffset cutoff in new[] { LongAgo, gen2 })
            {
                ListRead<ListFeed> feed = list.ReadFeed(cutoff, TimeSpan.Zero, includeIPv6);
                ListRead<IReadOnlyList<AbuseListEntry>> current = list.ReadCurrent(cutoff, includeIPv6);

                Assert.Equal(AbuseListState.Usable, feed.State);
                Assert.Equal(downloadedAt, feed.Value!.LastDownloadAt);
                Assert.Equal(gen2, feed.Value.GeneratedAt);
                Assert.Equal(current.Value!.Select(Describe), feed.Value.Entries.Select(Describe));
            }
        }

        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "2001:4860::", "2606:4700:4700::1111"],
                     list.ReadFeed(LongAgo, TimeSpan.Zero, includeIPv6: true).Value!.Entries.Select(Text));
        Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9"], list.ReadFeed(LongAgo, TimeSpan.Zero, includeIPv6: false).Value!.Entries.Select(Text));
        Assert.Equal(["1.1.1.1", "2606:4700:4700::1111"], list.ReadFeed(gen2, TimeSpan.Zero, includeIPv6: true).Value!.Entries.Select(Text));
    }

    [Fact]
    public void CutoffFor_counts_back_from_the_earlier_of_now_and_the_last_download_or_from_now_without_one()
    {
        TimeSpan keep = TimeSpan.FromDays(30);

        // Now after the last download: from the download
        Assert.Equal(Gen1.AddDays(-30), AbuseIpDbList.CutoffFor(Gen1.AddDays(400), Gen1, keep));
        // Now before it: from now
        Assert.Equal(Gen1.AddHours(-10).AddDays(-30), AbuseIpDbList.CutoffFor(Gen1.AddHours(-10), Gen1, keep));
        Assert.Equal(Gen1.AddDays(-30), AbuseIpDbList.CutoffFor(Gen1, Gen1, keep));
        // No download: from now
        Assert.Equal(Gen1.AddDays(-30), AbuseIpDbList.CutoffFor(Gen1, null, keep));
        // Too early to count back from
        Assert.Equal(DateTimeOffset.MinValue, AbuseIpDbList.CutoffFor(Gen1, DateTimeOffset.MinValue.AddDays(1), keep));
    }

    [Fact]
    public void ReadFeed_and_ReadFacts_long_after_the_last_download_give_the_rows_within_the_keep_days_of_it()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddDays(10);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)), Gen1, LongAgo,
                   CancellationToken.None);
        list.Merge(Listing(gen2, Entry("9.9.9.9", 60)), gen2, gen2.AddDays(-30), CancellationToken.None);
        DateTimeOffset now = gen2.AddDays(400);

        Assert.Equal(["8.8.8.8", "9.9.9.9", "2001:4860:4860::8888"],
                     list.ReadFeed(now, TimeSpan.FromDays(30), includeIPv6: true).Value!.Entries.Select(Text));
        Assert.Equal(["9.9.9.9"], list.ReadFeed(now, TimeSpan.FromDays(5), includeIPv6: true).Value!.Entries.Select(Text));
        Assert.Equal(new ListFacts(2, 1, gen2, gen2), list.ReadFacts(now, TimeSpan.FromDays(30)).Value);
        Assert.Equal(new ListFacts(1, 0, gen2, gen2), list.ReadFacts(now, TimeSpan.FromDays(5)).Value);
    }

    [Fact]
    public void ReadFeed_of_a_missing_file_is_missing_and_empty_and_creates_no_file()
    {
        ListRead<ListFeed> feed = NewList().ReadFeed(LongAgo, TimeSpan.Zero, includeIPv6: true);

        Assert.Equal(AbuseListState.Missing, feed.State);
        Assert.Same(ListFeed.Empty, feed.Value);
        Assert.Empty(Directory.GetFileSystemEntries(_temp.Folder));
    }

    [Fact]
    public void ReadCurrent_leaves_out_stored_rows_that_arent_a_listed_public_address()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US")), Gen1, LongAgo, CancellationToken.None);
        string seen = Unix(Gen1);
        Execute($"""
            INSERT INTO addresses VALUES (X'0A000001', {seen}, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES (X'0909090909', {seen}, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES (X'09090909', {seen}, {seen}, 1000, NULL, NULL);
            INSERT INTO addresses VALUES (X'01010101', {seen}, {seen}, -1, NULL, NULL);
            INSERT INTO addresses VALUES (X'B9C76C99', {seen}, {seen}, 50, 'us', NULL);
            INSERT INTO addresses VALUES (X'20014860000000000000000000008888', {seen}, {seen}, 50, NULL, 999999999999999);
            INSERT INTO addresses VALUES (X'20014860000000000000000000009999', -999999999999999, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES ('8.8.4.4', {seen}, {seen}, 50, NULL, NULL);
            """);

        ListRead<IReadOnlyList<AbuseListEntry>> current = list.ReadCurrent(LongAgo, includeIPv6: true);

        Assert.Equal(AbuseListState.Usable, current.State);
        AbuseListEntry google = Assert.Single(current.Value!);
        Assert.Equal(Bytes("8.8.8.8"), google.Address.Bytes);
        Assert.Equal("8.8.8.8", google.Address.Text);
        Assert.Equal("US", google.CountryCode);
    }

    [Fact]
    public void ReadFacts_counts_only_the_rows_ReadCurrent_returns()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, "US"), Entry("2606:4700:4700::1111", 50)), Gen1, LongAgo,
                   CancellationToken.None);
        string seen = Unix(Gen1);
        Execute($"""
            INSERT INTO addresses VALUES (X'0A000001', {seen}, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES (X'0909090909', {seen}, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES (X'09090909', {seen}, {seen}, 1000, NULL, NULL);
            INSERT INTO addresses VALUES (X'01010101', {seen}, {seen}, -1, NULL, NULL);
            INSERT INTO addresses VALUES (X'B9C76C99', {seen}, {seen}, 50, 'us', NULL);
            INSERT INTO addresses VALUES (X'20014860000000000000000000008888', {seen}, {seen}, 50, NULL, 999999999999999);
            INSERT INTO addresses VALUES (X'20014860000000000000000000009999', -999999999999999, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES (X'FC000000000000000000000000000001', {seen}, {seen}, 50, NULL, NULL);
            INSERT INTO addresses VALUES ('8.8.4.4', {seen}, {seen}, 50, NULL, NULL);
            """);

        ListRead<ListFacts> facts = list.ReadFacts(LongAgo, TimeSpan.Zero);

        Assert.Equal(AbuseListState.Usable, facts.State);
        Assert.Equal(1, facts.Value!.IPv4Count);
        Assert.Equal(1, facts.Value.IPv6Count);
        Assert.Equal(2, list.ReadCurrent(LongAgo, includeIPv6: true).Value!.Count);
    }

    [Fact]
    public void An_address_listed_again_after_the_keep_days_hid_it_is_added_and_keeps_its_first_seen()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddDays(10);
        DateTimeOffset gen3 = Gen1.AddDays(20);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("1.1.1.1", 10)), Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(gen2, Entry("1.1.1.1", 20)), gen2, LongAgo, CancellationToken.None);

        // 8.8.8.8 was last listed at Gen1, before the five keep days of gen2 and of gen3 but within the horizon:
        // hidden, not removed
        ListRead<ListMerge> merge = list.Merge(Listing(gen3, Entry("8.8.8.8", 60), Entry("1.1.1.1", 30)), gen3,
                                               cutoff: gen3.AddDays(-5), CancellationToken.None);

        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        Assert.Equal([Hex("8.8.8.8"), Unix(Gen1), Unix(gen3), "60", "", ""], Row("8.8.8.8"));
        Assert.Equal([Hex("1.1.1.1"), Unix(Gen1), Unix(gen3), "30", "", ""], Row("1.1.1.1"));
        AbuseListEntry google = list.ReadCurrent(gen3.AddDays(-5), includeIPv6: true).Value!.Single(e => Text(e) == "8.8.8.8");
        Assert.Equal(Gen1, google.FirstSeen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_and_Lapsed_count_from_the_keep_days_of_the_previous_download_and_reconcile_with_the_facts(bool listZ)
    {
        AbuseIpDbList list = NewList();
        TimeSpan keep = TimeSpan.FromDays(30);
        DateTimeOffset day20 = Gen1.AddDays(20);
        DateTimeOffset day40 = Gen1.AddDays(40);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, Gen1 - keep, CancellationToken.None);
        list.Merge(Listing(day20, Entry("9.9.9.9", 50)), day20, day20 - keep, CancellationToken.None);
        int before = Shown(list.ReadFacts(day20, keep));

        // At day 40 the keep days of this download start at day 10, past 8.8.8.8's day 0, but the feed and the status
        // held it until now, because they counted from day 20
        ListRead<ListMerge> merge = list.Merge(Listing(day40, Entry(listZ ? "1.1.1.1" : "8.8.8.8", 50)), day40, day40 - keep,
                                               CancellationToken.None);
        int after = Shown(list.ReadFacts(day40, keep));

        Assert.Equal(2, before);
        // Listed again, 8.8.8.8 is neither added nor lapsed; with 1.1.1.1 instead, 1.1.1.1 is added and 8.8.8.8 lapses
        Assert.Equal(listZ ? new ListMerge(true, 1, 1, 0) : new ListMerge(true, 0, 0, 0), merge.Value);
        Assert.Equal(after, before + merge.Value!.Added - merge.Value.Lapsed);
        Assert.Equal(2, after);
    }

    [Fact]
    public void A_fractional_download_time_counts_Added_and_Lapsed_from_the_whole_second_it_stamps()
    {
        AbuseIpDbList list = NewList();
        TimeSpan keep = TimeSpan.FromDays(30);
        DateTimeOffset second = Gen1.AddDays(30).AddMilliseconds(400);
        DateTimeOffset third = second.AddDays(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, Gen1 - keep, CancellationToken.None);
        int before = Shown(list.ReadFacts(second, keep));

        // The stamp is Gen1 + 30 days, so the reads count from Gen1 and still show 8.8.8.8: it hasn't lapsed yet
        ListRead<ListMerge> merge = list.Merge(Listing(Gen1.AddDays(30), Entry("9.9.9.9", 50)), second, second - keep,
                                               CancellationToken.None);
        int after = Shown(list.ReadFacts(second.AddHours(1), keep));

        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        Assert.Equal(1, before);
        Assert.Equal(after, before + merge.Value!.Added - merge.Value.Lapsed);
        // Both addresses, so 8.8.8.8 is among them
        Assert.Equal(2, after);

        // A day later 8.8.8.8 lapses, once over the two merges
        ListRead<ListMerge> next = list.Merge(Listing(Gen1.AddDays(31), Entry("9.9.9.9", 50)), third, third - keep,
                                              CancellationToken.None);
        int last = Shown(list.ReadFacts(third.AddHours(1), keep));

        Assert.Equal(new ListMerge(true, 0, 1, 0), next.Value);
        Assert.Equal(last, after + next.Value!.Added - next.Value.Lapsed);
        Assert.Equal(1, last);
    }

    [Fact]
    public void The_horizon_of_a_fractional_download_time_is_counted_from_the_whole_second_it_stamps()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset fractional = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays).AddMilliseconds(400);
        DateTimeOffset later = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays).AddSeconds(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, Gen1.AddDays(-30), CancellationToken.None);

        // 8.8.8.8 was last seen at Gen1, exactly the stamp (Gen1 + 365 days) less 365 days, so it is kept
        ListRead<ListMerge> kept = list.Merge(Listing(Gen1.AddDays(1), Entry("9.9.9.9", 50)), fractional,
                                              fractional.AddDays(-30), CancellationToken.None);

        Assert.Equal(0, kept.Value!.Expired);
        Assert.Equal(Unix(Gen1), Row("8.8.8.8")[2]);

        // A second later its last sighting is before the horizon
        ListRead<ListMerge> expired = list.Merge(Listing(Gen1.AddDays(2), Entry("9.9.9.9", 50)), later,
                                                 later.AddDays(-30), CancellationToken.None);

        Assert.Equal(1, expired.Value!.Expired);
        Assert.Equal([Hex("9.9.9.9")], Rows().Select(row => row[0]));
    }

    [Fact]
    public void The_first_merge_with_no_earlier_download_lapses_nothing_and_adds_what_it_lists()
    {
        AbuseIpDbList list = NewList();
        TimeSpan keep = TimeSpan.FromDays(30);
        DateTimeOffset downloadedAt = Gen1.AddDays(1);
        int before = Shown(list.ReadFacts(downloadedAt, keep));

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860:4860::8888", 50)),
                                               downloadedAt, downloadedAt - keep, CancellationToken.None);

        Assert.Equal(new ListMerge(true, 2, 0, 0), merge.Value);
        Assert.Equal(Shown(list.ReadFacts(downloadedAt, keep)), before + merge.Value!.Added - merge.Value.Lapsed);
    }

    [Fact]
    public void A_merge_downloaded_before_the_previous_download_counts_both_sides_from_its_own_cutoff()
    {
        AbuseIpDbList list = NewList();
        TimeSpan keep = TimeSpan.FromDays(30);
        DateTimeOffset day10 = Gen1.AddDays(10);
        DateTimeOffset day5 = Gen1.AddDays(5);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), day10, day10 - keep, CancellationToken.None);
        int before = Shown(list.ReadFacts(day5, keep));

        // A clock set back since the last download: the earlier time is this one, so the cutoff is its own
        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("9.9.9.9", 50)), day5, day5 - keep, CancellationToken.None);

        Assert.Equal(new ListMerge(false, 1, 0, 0), merge.Value);
        Assert.Equal(Shown(list.ReadFacts(day5, keep)), before + merge.Value!.Added - merge.Value.Lapsed);
    }

    [Fact]
    public void An_older_generation_after_a_newer_one_keeps_the_newer_score_and_country()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddHours(1);
        DateTimeOffset reported = Gen1.AddHours(-1);
        list.Merge(Listing(gen2, Entry("8.8.8.8", 70, "US", reported), Entry("1.1.1.1", 40)), gen2.AddHours(1), LongAgo,
                   CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 10, "GB", Gen1.AddHours(-5)),
                                                       Entry("1.1.1.1", 20, "AU")),
                                               Gen1.AddDays(1), LongAgo, CancellationToken.None);

        Assert.Equal(new ListMerge(false, 0, 0, 0), merge.Value);
        Assert.Equal([Hex("8.8.8.8"), Unix(gen2.AddHours(1)), Unix(Gen1.AddDays(1)), "70", "US", Unix(reported)], Row("8.8.8.8"));
        Assert.Equal([Hex("1.1.1.1"), Unix(gen2.AddHours(1)), Unix(Gen1.AddDays(1)), "40", "", ""], Row("1.1.1.1"));
        Assert.Equal(Unix(gen2), Metadata()["last_generated_at"]);
    }

    [Fact]
    public void Last_reported_never_moves_back_whatever_the_generation()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddHours(1);
        DateTimeOffset early = Gen1.AddHours(-5);
        DateTimeOffset middle = Gen1.AddHours(-1);
        DateTimeOffset late = Gen1.AddMinutes(-10);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50, reported: middle), Entry("9.9.9.9", 50, reported: middle),
                           Entry("1.1.1.1", 50)),
                   Gen1, LongAgo, CancellationToken.None);

        // A newer generation with an earlier or no report time keeps the later one, and fills one that was missing
        list.Merge(Listing(gen2, Entry("8.8.8.8", 60, reported: early), Entry("9.9.9.9", 60), Entry("1.1.1.1", 60, reported: early)),
                   gen2, LongAgo, CancellationToken.None);
        Assert.Equal(Unix(middle), Row("8.8.8.8")[5]);
        Assert.Equal(Unix(middle), Row("9.9.9.9")[5]);
        Assert.Equal(Unix(early), Row("1.1.1.1")[5]);

        // An older generation with a later report time still moves it forward
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 10, reported: late)), gen2.AddHours(1), LongAgo, CancellationToken.None);
        Assert.Equal(Unix(late), Row("8.8.8.8")[5]);
        Assert.Equal("60", Row("8.8.8.8")[3]);
    }

    [Fact]
    public void A_download_time_earlier_than_a_stored_last_seen_keeps_the_later_last_seen()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(5);
        DateTimeOffset earlier = Gen1.AddDays(3);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), later, LongAgo, CancellationToken.None);

        list.Merge(Listing(Gen1.AddHours(1), Entry("8.8.8.8", 60)), earlier, LongAgo, CancellationToken.None);

        Assert.Equal([Hex("8.8.8.8"), Unix(later), Unix(later), "60", "", ""], Row("8.8.8.8"));
    }

    [Fact]
    public void Merge_refuses_a_cutoff_inside_the_shortest_keep_before_it_opens_the_file()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset downloadedAt = Gen1.AddDays(10);
        DateTimeOffset latest = downloadedAt.AddDays(-AbuseIpDbLimits.MinKeepDays);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), downloadedAt, latest.AddTicks(1), CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), downloadedAt, downloadedAt.AddDays(1), CancellationToken.None));
        Assert.Empty(FolderNames());

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), downloadedAt, latest, CancellationToken.None);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        string hash = Sha(ListPath);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            list.Merge(Listing(Gen1, Entry("9.9.9.9", 50)), downloadedAt, latest.AddSeconds(1), CancellationToken.None));
        Assert.Equal(hash, Sha(ListPath));
        Assert.Equal(["abuseipdb.db"], FolderNames());
    }

    [Fact]
    public void Delete_removes_the_file_and_its_side_files_and_a_missing_file_is_true()
    {
        AbuseIpDbList list = NewList();
        Assert.True(list.Delete());

        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        foreach (string suffix in new[] { "-journal", "-wal", "-shm" })
            File.WriteAllBytes(ListPath + suffix, []);
        File.WriteAllText(_temp.FilePath("other.txt"), "kept");

        Assert.True(list.Delete());

        Assert.Equal(["other.txt"], FolderNames());
    }

    [Fact]
    public void Delete_is_false_when_the_file_cant_be_removed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Folder permissions are a Unix check here.");
            return;
        }

        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        UnixFileMode mode = File.GetUnixFileMode(_temp.Folder);
        File.SetUnixFileMode(_temp.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.False(list.Delete());
        }
        finally
        {
            File.SetUnixFileMode(_temp.Folder, mode);
        }

        Assert.True(File.Exists(ListPath));
    }

    /// <summary>Rebuilds <c>metadata</c> without its NOT NULL and with no column types, so a test can store any value.</summary>
    private const string LooseMetadata = """
        CREATE TABLE metadata2 (key TEXT PRIMARY KEY, value TEXT);
        INSERT INTO metadata2 SELECT key, value FROM metadata;
        DROP TABLE metadata;
        ALTER TABLE metadata2 RENAME TO metadata;
        """;

    [Fact]
    public void Delete_leaves_the_journal_when_the_database_cant_be_deleted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("chflags is a macOS check here.");
            return;
        }

        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        File.WriteAllBytes(ListPath + "-journal", [1, 2, 3]);
        Chflags("uchg", ListPath);
        try
        {
            Assert.False(list.Delete());
            Assert.True(File.Exists(ListPath));
            Assert.True(File.Exists(ListPath + "-journal"));
        }
        finally
        {
            Chflags("nouchg", ListPath);
        }

        Assert.True(list.Delete());
        Assert.Empty(FolderNames());
    }

    [Fact]
    public void The_first_download_records_every_listed_address_as_new()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset downloadedAt = Gen1.AddHours(3);

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1, Entry("9.9.9.9", 40), Entry("8.8.8.8", 90, "US"),
                                                      Entry("2001:4860::8888", 75)),
                                               downloadedAt, LongAgo, CancellationToken.None);

        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(AbuseListState.Usable, set.State);
        Assert.Equal(downloadedAt, set.Value!.LastDownloadAt);
        Assert.Equal(["8.8.8.8", "9.9.9.9", "2001:4860::8888"], set.Value.Entries!.Select(Text));
        Assert.Equal(merge.Value!.Added, set.Value.Entries!.Count);
        Assert.Equal(Unix(downloadedAt), Metadata()["new_addresses_at"]);
        Assert.Equal(3, list.ReadFacts(LongAgo, TimeSpan.Zero, countNew: true).Value!.NewCount);
        // The download's pre-check doesn't ask for the set
        Assert.Equal(0, list.ReadFacts(LongAgo, TimeSpan.Zero).Value!.NewCount);
    }

    [Fact]
    public void The_set_reads_IPv4_first_in_byte_order_then_IPv6()
    {
        AbuseIpDbList list = NewList();

        list.Merge(Listing(Gen1, Entry("2001:4860:4860::8888", 50), Entry("185.199.108.153", 40), Entry("8.8.8.8", 90)),
                   Gen1, LongAgo, CancellationToken.None);

        // 185.199.108.153's first byte is above 0x20, the IPv6 address's first byte, so only the length orders it first
        Assert.Equal(["8.8.8.8", "185.199.108.153", "2001:4860:4860::8888"],
                     list.ReadNew().Value!.Entries!.Select(Text));
    }

    [Fact]
    public void A_second_download_records_only_the_addresses_it_brings_in_both_families()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddHours(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50)), Gen1, LongAgo, CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(gen2, Entry("8.8.8.8", 60), Entry("2001:4860::8844", 30),
                                                      Entry("2001:4860::8888", 60), Entry("8.8.4.4", 20)),
                                               gen2, LongAgo, CancellationToken.None);

        AbuseListEntry[] entries = list.ReadNew().Value!.Entries!.ToArray();
        Assert.Equal(["8.8.4.4", "2001:4860::8844"], entries.Select(Text));
        Assert.Equal([20, 30], entries.Select(e => e.Score));
        Assert.Equal(2, merge.Value!.Added);
    }

    [Fact]
    public void An_address_the_keep_days_only_hid_is_new_again_and_keeps_its_first_seen()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(40);
        DateTimeOffset again = Gen1.AddDays(41);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(later, Entry("9.9.9.9", 60)), later, later.AddDays(-30), CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(again, Entry("8.8.8.8", 70), Entry("9.9.9.9", 60)), again,
                                               again.AddDays(-30), CancellationToken.None);

        AbuseListEntry google = Assert.Single(list.ReadNew().Value!.Entries!);
        Assert.Equal("8.8.8.8", Text(google));
        Assert.Equal(Gen1, google.FirstSeen);
        Assert.Equal(1, merge.Value!.Added);
    }

    [Fact]
    public void An_address_listed_again_past_the_horizon_starts_again_and_is_new()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset middle = Gen1.AddDays(100);
        DateTimeOffset later = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays + 1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(middle, Entry("9.9.9.9", 60)), middle, middle.AddDays(-30), CancellationToken.None);

        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("8.8.8.8", 70)), later, later.AddDays(-30),
                                               CancellationToken.None);

        AbuseListEntry google = Assert.Single(list.ReadNew().Value!.Entries!);
        Assert.Equal("8.8.8.8", Text(google));
        Assert.Equal(later, google.FirstSeen);
        Assert.Equal(1, merge.Value!.Added);
    }

    [Fact]
    public void An_address_last_seen_exactly_at_the_previous_cutoff_is_not_new()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset second = Gen1.AddDays(30);
        DateTimeOffset third = second.AddHours(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        list.Merge(Listing(second, Entry("9.9.9.9", 50)), second, second.AddDays(-30), CancellationToken.None);

        // The status before this download counted back 30 days from the second download, to Gen1 exactly, so 8.8.8.8
        // was still shown and isn't new
        ListRead<ListMerge> merge = list.Merge(Listing(third, Entry("8.8.8.8", 60), Entry("9.9.9.9", 60)), third,
                                               third.AddDays(-30), CancellationToken.None);

        Assert.Equal(0, merge.Value!.Added);
        Assert.Empty(list.ReadNew().Value!.Entries!);
        Assert.Equal(Unix(third), Metadata()["new_addresses_at"]);
    }

    [Fact]
    public void A_cancelled_merge_keeps_the_previous_set_and_its_stamp()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("9.9.9.9", 10)), Gen1, LongAgo, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var entries = new CancellingList([Entry("8.8.8.8", 70), Entry("8.8.4.4", 20), Entry("2001:4860::8888", 50)],
                                         cancelAt: 2, cts);

        Assert.Throws<OperationCanceledException>(() =>
            list.Merge(new Blacklist(Gen1.AddHours(1), entries, 0, 0, 0), Gen1.AddHours(1), LongAgo, cts.Token));

        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(Gen1, set.Value!.LastDownloadAt);
        Assert.Equal(["8.8.8.8", "9.9.9.9"], set.Value.Entries!.Select(Text));
        Assert.Equal(Unix(Gen1), Metadata()["new_addresses_at"]);
    }

    [Fact]
    public void Clearing_the_list_removes_the_set_with_the_file()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        Assert.True(list.Delete());

        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(AbuseListState.Missing, set.State);
        Assert.Same(ListNew.Empty, set.Value);
        Assert.Empty(FolderNames());
    }

    [Theory]
    [InlineData("UPDATE metadata SET value = '1' WHERE key = 'new_addresses_at';")]
    [InlineData("DELETE FROM metadata WHERE key = 'new_addresses_at';")]
    [InlineData("DROP TABLE new_addresses;")]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip BLOB);")]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip TEXT PRIMARY KEY) WITHOUT ROWID;")]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip BLOB PRIMARY KEY, note TEXT);")]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (address BLOB PRIMARY KEY);")]
    public void A_set_that_is_stale_missing_or_of_another_shape_is_not_known_and_the_list_stays_usable(string change)
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("2001:4860::8888", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute(change);

        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(AbuseListState.Usable, set.State);
        Assert.Equal(Gen1, set.Value!.LastDownloadAt);
        Assert.Null(set.Value.Entries);
        // The status reads 0 new and is otherwise as it was
        Assert.Equal(new ListFacts(1, 1, Gen1, Gen1, 0), list.ReadFacts(LongAgo, TimeSpan.Zero, countNew: true).Value);
    }

    [Theory]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip TEXT PRIMARY KEY) WITHOUT ROWID;")]
    [InlineData("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip BLOB PRIMARY KEY, note TEXT);")]
    [InlineData("DROP TABLE new_addresses;")]
    public void The_next_merge_replaces_a_missing_or_wrong_shaped_set(string change)
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddHours(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute(change);

        list.Merge(Listing(gen2, Entry("8.8.8.8", 50), Entry("9.9.9.9", 50)), gen2, LongAgo, CancellationToken.None);

        Assert.Equal(["9.9.9.9"], list.ReadNew().Value!.Entries!.Select(Text));
        Assert.Equal(Unix(gen2), Metadata()["new_addresses_at"]);
    }

    [Fact]
    public void A_row_of_the_set_that_fails_the_read_judgement_is_left_out()
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50), Entry("9.9.9.9", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute($"UPDATE addresses SET score = 500 WHERE ip = x'{Hex("8.8.8.8")}';");

        Assert.Equal(["9.9.9.9"], list.ReadNew().Value!.Entries!.Select(Text));
        Assert.Equal(1, list.ReadFacts(LongAgo, TimeSpan.Zero, countNew: true).Value!.NewCount);
    }

    [Theory]
    // An index of another table holds the name, so the set's CREATE TABLE fails and the merge goes on without it
    [InlineData("DROP TABLE new_addresses; CREATE TABLE other (x); CREATE INDEX new_addresses ON other (x);")]
    public void Nothing_about_the_set_stops_a_merge_and_a_set_that_failed_is_not_known(string change)
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset gen2 = Gen1.AddHours(1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute(change);

        ListRead<ListMerge> merge = list.Merge(Listing(gen2, Entry("8.8.8.8", 60), Entry("9.9.9.9", 60)), gen2, LongAgo,
                                               CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        Assert.Equal(2, Rows().Count);
        Assert.Equal(Unix(gen2), Metadata()["last_download_at"]);
        Assert.DoesNotContain("new_addresses_at", Metadata().Keys);
        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(AbuseListState.Usable, set.State);
        Assert.Null(set.Value!.Entries);
        Assert.Equal(new ListFacts(2, 0, gen2, gen2, 0), list.ReadFacts(LongAgo, TimeSpan.Zero, countNew: true).Value);
        Assert.False(File.Exists(ListPath + "-journal"));
    }

    [Fact]
    public void A_list_made_by_create_and_a_merge_holds_only_its_own_objects()
    {
        NewList().Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        (string Type, string Name, string Table, string? Sql)[] objects = SchemaObjects();

        Assert.Equal([("index", "idx_addresses_last_seen", "addresses"), ("index", "sqlite_autoindex_metadata_1", "metadata"),
                      ("table", "addresses", "addresses"), ("table", "metadata", "metadata"),
                      ("table", "new_addresses", "new_addresses")],
                     objects.Select(o => (o.Type, o.Name, o.Table)));
        string? SqlOf(string name) => objects.Single(o => o.Name == name).Sql;
        Assert.Equal("CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)", Collapse(SqlOf("metadata")));
        Assert.Equal("CREATE TABLE addresses (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, "
                     + "score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL) WITHOUT ROWID",
                     Collapse(SqlOf("addresses")));
        Assert.Equal("CREATE INDEX idx_addresses_last_seen ON addresses (last_seen)", Collapse(SqlOf("idx_addresses_last_seen")));
        Assert.Null(SqlOf("sqlite_autoindex_metadata_1"));
    }

    [Theory]
    [InlineData("ANALYZE;")]
    [InlineData("CREATE TABLE notes (x INTEGER); CREATE INDEX notes_x ON notes (x);")]
    [InlineData("CREATE INDEX new_addresses_ip ON new_addresses (ip);")]
    public void Tables_and_indexes_of_other_tables_leave_the_list_usable(string change)
    {
        AbuseIpDbList list = NewList();
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute(change);

        AssertSecondMergeIsUsable(list);
    }

    [Fact]
    public void A_list_made_with_CRLF_line_ends_is_usable()
    {
        Execute("CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);\r\n"
                + "CREATE TABLE addresses (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,\r\n"
                + "  score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL) WITHOUT ROWID;\r\n"
                + "CREATE INDEX idx_addresses_last_seen ON addresses (last_seen);\r\n"
                + "INSERT INTO metadata (key, value) VALUES ('schema_version', '1');\r\n", create: true);
        // The stored text keeps the line end, as a build from a CRLF checkout stores it
        Assert.Contains("\r\n", SchemaObjects().Single(o => o.Name == "addresses").Sql);
        AbuseIpDbList list = NewList();

        ListRead<ListMerge> first = list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, first.State);
        Assert.Equal(new ListMerge(true, 1, 0, 0), first.Value);
        AssertSecondMergeIsUsable(list);
    }

    [Fact]
    public void A_foreign_key_from_another_table_never_blocks_the_horizon_delete()
    {
        AbuseIpDbList list = NewList();
        DateTimeOffset later = Gen1.AddDays(AbuseIpDbLimits.MaxKeepDays + 1);
        list.Merge(Listing(Gen1, Entry("8.8.8.8", 50)), Gen1, LongAgo, CancellationToken.None);
        Execute("CREATE TABLE other (ip BLOB REFERENCES addresses (ip)); INSERT INTO other SELECT ip FROM addresses;");

        ListRead<ListMerge> merge = list.Merge(Listing(later, Entry("9.9.9.9", 60)), later,
                                               cutoff: later.AddDays(-AbuseIpDbLimits.MaxKeepDays), CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(1, merge.Value!.Expired);
        Assert.Equal([Hex("9.9.9.9")], Rows().Select(row => row[0]));
    }

    /// <summary>A second merge, an hour after Gen1, is usable and records only the address it brings.</summary>
    private void AssertSecondMergeIsUsable(AbuseIpDbList list)
    {
        DateTimeOffset gen2 = Gen1.AddHours(1);

        ListRead<ListMerge> merge = list.Merge(Listing(gen2, Entry("8.8.8.8", 60), Entry("9.9.9.9", 60)), gen2, LongAgo,
                                               CancellationToken.None);

        Assert.Equal(AbuseListState.Usable, merge.State);
        Assert.Equal(new ListMerge(true, 1, 0, 0), merge.Value);
        Assert.Equal(Unix(gen2), Metadata()["new_addresses_at"]);
        ListRead<ListNew> set = list.ReadNew();
        Assert.Equal(AbuseListState.Usable, set.State);
        Assert.Equal(["9.9.9.9"], set.Value!.Entries!.Select(Text));
    }

    /// <summary>Every row of sqlite_master, by type and then name.</summary>
    private (string Type, string Name, string Table, string? Sql)[] SchemaObjects()
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var objects = new List<(string, string, string, string?)>();
        while (reader.Read())
            objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return objects.ToArray();
    }

    private static string? Collapse(string? sql) => sql is null ? null : Regex.Replace(sql, @"\s+", " ").Trim();

    private static void Chflags(string flag, string path) => Run("/usr/bin/chflags", flag, path);

    private static void Run(string program, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Inserts count rows of 8.x.x.x addresses, all last seen at the given Unix seconds.</summary>
    private static string InsertManySql(int count, string seen) => $"""
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count})
        INSERT INTO addresses SELECT unhex(printf('08%06X', i)), {seen}, {seen}, 50, NULL, NULL FROM n WHERE true
        ON CONFLICT(ip) DO NOTHING;
        """;

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void AssertEveryMethodGives(AbuseIpDbList list, AbuseListState state)
    {
        string hash = Sha(ListPath);

        ListRead<ListFacts> facts = list.ReadFacts(LongAgo, TimeSpan.Zero);
        Assert.Equal(state, facts.State);
        Assert.Null(facts.Value);
        Assert.Equal(hash, Sha(ListPath));

        ListRead<IReadOnlyList<AbuseListEntry>> current = list.ReadCurrent(LongAgo, includeIPv6: true);
        Assert.Equal(state, current.State);
        Assert.Null(current.Value);
        Assert.Equal(hash, Sha(ListPath));

        ListRead<ListFeed> feed = list.ReadFeed(LongAgo, TimeSpan.Zero, includeIPv6: true);
        Assert.Equal(state, feed.State);
        Assert.Null(feed.Value);
        Assert.Equal(hash, Sha(ListPath));

        ListRead<ListFacts> factsWithNew = list.ReadFacts(LongAgo, TimeSpan.Zero, countNew: true);
        Assert.Equal(state, factsWithNew.State);
        Assert.Null(factsWithNew.Value);
        Assert.Equal(hash, Sha(ListPath));

        ListRead<ListNew> added = list.ReadNew();
        Assert.Equal(state, added.State);
        Assert.Null(added.Value);
        Assert.Equal(hash, Sha(ListPath));

        Assert.Equal(state, list.RecordAttempt(Gen1.AddDays(1)));
        Assert.Equal(hash, Sha(ListPath));

        ListRead<ListMerge> merge = list.Merge(Listing(Gen1.AddDays(1), Entry("9.9.9.9", 50)), Gen1.AddDays(1),
                                               LongAgo, CancellationToken.None);
        Assert.Equal(state, merge.State);
        Assert.Null(merge.Value);
        Assert.Equal(hash, Sha(ListPath));

        Assert.Equal(["abuseipdb.db"], FolderNames());
    }

    private static byte[] Bytes(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        return address.GetAddressBytes();
    }

    private static BlacklistEntry Entry(string address, int score, string? country = null, DateTimeOffset? reported = null)
    {
        Assert.True(PublicAddress.TryCreate(Bytes(address), out PublicAddress? value));
        return new BlacklistEntry(value, score, reported, country);
    }

    private static Blacklist Listing(DateTimeOffset generatedAt, params BlacklistEntry[] entries) =>
        new(generatedAt, entries, 0, 0, 0);

    /// <summary>Both families' counts of a facts read, which a missing file gives as zero.</summary>
    private static int Shown(ListRead<ListFacts> facts) => facts.Value!.IPv4Count + facts.Value.IPv6Count;

    private static string Text(AbuseListEntry entry) => entry.Address.Text;

    private static string Describe(AbuseListEntry entry) =>
        string.Join('|', entry.Address.Text, Unix(entry.FirstSeen), Unix(entry.LastSeen),
                    entry.Score.ToString(CultureInfo.InvariantCulture), entry.CountryCode ?? "",
                    entry.LastReportedAt is DateTimeOffset at ? Unix(at) : "");

    private static string Unix(DateTimeOffset time) => time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string Hex(string address) => Convert.ToHexString(Bytes(address));

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string[] FolderNames() =>
        Directory.GetFileSystemEntries(_temp.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private SqliteConnection OpenRaw(bool create = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ListPath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private void Execute(string sql, bool create = false)
    {
        using SqliteConnection conn = OpenRaw(create);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private Dictionary<string, string> Metadata()
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    /// <summary>Every row as text: the address bytes in hex, first and last seen, score, country, last reported ("" for null).</summary>
    private List<string[]> Rows()
    {
        using SqliteConnection conn = OpenRaw();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hex(ip), first_seen, last_seen, score, country_code, last_reported FROM addresses ORDER BY ip;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var rows = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[6];
            for (int i = 0; i < 6; i++)
                row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(row);
        }
        return rows;
    }

    private string[] Row(string address) => Rows().Single(row => row[0] == Hex(address));

    private string Dump()
    {
        var text = new StringBuilder();
        foreach (string[] row in Rows())
            text.AppendLine(string.Join('|', row));
        foreach (KeyValuePair<string, string> pair in Metadata().OrderBy(p => p.Key, StringComparer.Ordinal))
            text.AppendLine($"{pair.Key}={pair.Value}");
        return text.ToString();
    }

    /// <summary>
    /// A list that cancels the token when the entry at one index is read for the given time (only the reads of that
    /// index count), however it is read.
    /// </summary>
    private sealed class CancellingList(BlacklistEntry[] entries, int cancelAt, CancellationTokenSource cts,
                                        int cancelOnRead = 1)
        : IReadOnlyList<BlacklistEntry>
    {
        private int _reads;

        public BlacklistEntry this[int index]
        {
            get
            {
                if (index == cancelAt && ++_reads == cancelOnRead)
                    cts.Cancel();
                return entries[index];
            }
        }

        public int Count => entries.Length;

        public IEnumerator<BlacklistEntry> GetEnumerator()
        {
            for (int i = 0; i < entries.Length; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
