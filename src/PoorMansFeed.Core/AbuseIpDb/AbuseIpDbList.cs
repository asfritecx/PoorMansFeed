using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// What a read or write of the rolling list found. <see cref="Value"/> is set when the state is
/// <see cref="AbuseListState.Usable"/> or <see cref="AbuseListState.Missing"/>, and null otherwise.
/// </summary>
internal readonly record struct ListRead<T>(AbuseListState State, T? Value) where T : class;

/// <summary>
/// The rolling list now: the addresses per family listed within the keep days (counted as
/// <see cref="AbuseIpDbList.CutoffFor"/> says), and when the list now held was downloaded and generated. The download
/// accounting (the rate limit and the day's count) is kept in config.db, not here. <see cref="NewCount"/> is the
/// judged rows of the last download's new-address set (<see cref="AbuseIpDbList.ReadNew"/>), 0 when the set isn't
/// known or wasn't asked for.
/// </summary>
internal sealed record ListFacts(int IPv4Count, int IPv6Count, DateTimeOffset? LastDownloadAt, DateTimeOffset? GeneratedAt,
                                 int NewCount = 0)
{
    /// <summary>A list with nothing in it, as a missing file reads.</summary>
    public static ListFacts Empty { get; } = new(0, 0, null, null);
}

/// <summary>
/// What the rolling feed is written from: the addresses <see cref="AbuseIpDbList.ReadCurrent"/> returns for the cutoff
/// <see cref="AbuseIpDbList.CutoffFor"/> gives, and when the list now held was downloaded and generated.
/// </summary>
internal sealed record ListFeed(IReadOnlyList<AbuseListEntry> Entries, DateTimeOffset? LastDownloadAt, DateTimeOffset? GeneratedAt)
{
    /// <summary>A list with nothing in it, as a missing file reads.</summary>
    public static ListFeed Empty { get; } = new([], null, null);
}

/// <summary>
/// The last download's new-address set: the judged rows, IPv4 first, then by bytes, or null when the set isn't known
/// (no table, a table of another shape, or a set no longer made by the last download), and the last download.
/// </summary>
internal sealed record ListNew(IReadOnlyList<AbuseListEntry>? Entries, DateTimeOffset? LastDownloadAt)
{
    /// <summary>A list with no download, as a missing file reads.</summary>
    public static ListNew Empty { get; } = new(null, null);
}

/// <summary>
/// One merge. <see cref="NewGeneration"/> is false when the list was generated no later than the newest one already
/// merged; its addresses were still refreshed. <see cref="Added"/> and <see cref="Lapsed"/> compare the addresses within
/// the keep days, with the cutoffs the status and the feed use: before the merge from the previous download
/// (<see cref="AbuseIpDbList.CutoffFor"/>), after it from this one. They are counted over the rows in the file, both
/// families together, before a read judges them, so a row a read leaves out (one changed outside the app) makes them
/// differ from the status by that many, and the feed also follows the IPv6 switch, the exceptions and the cap. Added
/// counts those after the merge that weren't before it: new addresses, and addresses listed again after dropping out of
/// the keep days. Lapsed counts those before it that aren't after it, so over those rows the count before plus Added
/// less Lapsed is the count after. <see cref="Expired"/> counts
/// the addresses removed because no download had listed them for <see cref="AbuseIpDbLimits.MaxKeepDays"/> days,
/// whatever the keep days; an address can be both Lapsed and Expired.
/// </summary>
internal sealed record ListMerge(bool NewGeneration, int Added, int Lapsed, int Expired);

/// <summary>
/// The AbuseIPDB rolling list in <c>abuseipdb.db</c>: every address a blacklist download listed, kept until no
/// download has listed it for <see cref="AbuseIpDbLimits.MaxKeepDays"/> days (the horizon). The user's keep days are
/// only a cutoff on what is read and counted, counted back from the earlier of now and the last download
/// (<see cref="CutoffFor"/>), so a clock set ahead hides nothing. Nothing written depends on them, so lowering them
/// hides rows and raising them again brings back what the file still holds, first sightings included. The file holds
/// the user's data, so it is opened read-write (a crashed write's journal is rolled back at the next read, which needs
/// write access), every write is one transaction, and a file this version can't use (also one with a damaged page,
/// which every read and write checks for first) is reported and never written. Only <see cref="Delete"/> removes it.
/// </summary>
internal sealed class AbuseIpDbList(string path)
{
    /// <summary>The layout this version reads and writes. A file with a higher version is <see cref="AbuseListState.Newer"/>.</summary>
    public const int SchemaVersion = 1;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private const string LastDownloadAtKey = "last_download_at";
    private const string LastGeneratedAtKey = "last_generated_at";
    private const string LastAttemptAtKey = "last_attempt_at";
    // The download whose merge made the new_addresses table; the set is read only while it equals last_download_at
    private const string NewAddressesAtKey = "new_addresses_at";

    // In DO UPDATE SET a bare column name is the stored row's value, so every CASE below sees the stored last_seen.
    // $horizon is the expiry's, so a row the keep days only hid keeps its first_seen
    private const string UpsertSql = """
        INSERT INTO addresses (ip, first_seen, last_seen, score, country_code, last_reported)
        VALUES ($ip, $seen, $seen, $score, $country, $reported)
        ON CONFLICT(ip) DO UPDATE SET
          first_seen = CASE WHEN last_seen < $horizon THEN excluded.first_seen ELSE first_seen END,
          last_seen = max(last_seen, excluded.last_seen),
          score = CASE WHEN $fresh = 1 THEN excluded.score ELSE score END,
          country_code = CASE WHEN $fresh = 1 THEN coalesce(excluded.country_code, country_code) ELSE country_code END,
          last_reported = CASE WHEN excluded.last_reported IS NULL THEN last_reported
                               WHEN last_reported IS NULL THEN excluded.last_reported
                               ELSE max(last_reported, excluded.last_reported) END
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private enum FileKind { Empty, Current, Newer, Unreadable }

    /// <summary>Tests only: runs in <see cref="Merge"/> just before the commit.</summary>
    internal Action? BeforeCommit { get; set; }

    /// <summary>
    /// The start of the keep days: keep counted back from the earlier of now and the last download, or from now when the
    /// list has no download. A clock set ahead of the last download changes nothing, so the list reads as that download
    /// left it; a clock behind it counts from now, so a download stamped by a clock that was ahead can't move the cutoff
    /// later. A start too early to count back from gives <see cref="DateTimeOffset.MinValue"/>.
    /// </summary>
    public static DateTimeOffset CutoffFor(DateTimeOffset now, DateTimeOffset? lastDownload, TimeSpan keep)
    {
        DateTimeOffset from = lastDownload is DateTimeOffset last && last < now ? last : now;
        return from.UtcTicks - DateTimeOffset.MinValue.UtcTicks < keep.Ticks ? DateTimeOffset.MinValue : from - keep;
    }

    /// <summary>
    /// The counts per family of the addresses <see cref="ReadCurrent"/> returns with IPv6 included (the same query and
    /// the same judgement of each row), and the last download and generation times. The cutoff is
    /// <see cref="CutoffFor"/> of now, the last download this read finds and keep. Metadata keys this version doesn't
    /// use, such as the rate-limit and download-count keys earlier builds wrote here, are ignored. With
    /// <paramref name="countNew"/> the judged rows of the new-address set are counted too (one join, with no sort and no
    /// list); a set that isn't known, or can't be read, counts 0 and never fails the read. Without it the set isn't
    /// touched.
    /// </summary>
    public ListRead<ListFacts> ReadFacts(DateTimeOffset now, TimeSpan keep, bool countNew = false)
    {
        return Read(ListFacts.Empty, "read the list's facts", conn =>
        {
            Dictionary<string, string> meta = ReadMetadata(conn, null);
            DateTimeOffset? lastDownload = TimeOf(meta, LastDownloadAtKey);
            int ipv4 = 0;
            int ipv6 = 0;
            foreach (AbuseListEntry entry in ReadEntries(conn, CutoffFor(now, lastDownload, keep), includeIPv6: true))
            {
                if (entry.Address.Bytes.Length == 4)
                    ipv4++;
                else
                    ipv6++;
            }

            int newCount = countNew ? CountNewQuietly(conn, meta) : 0;
            return new ListFacts(ipv4, ipv6, lastDownload, TimeOf(meta, LastGeneratedAtKey), newCount);
        });
    }

    /// <summary>
    /// Records that a download is about to be sent, as a real write (the time in <c>last_attempt_at</c>, which nothing
    /// reads yet), so a list that can be read but not written is found before a download is spent on it. SQLite opens
    /// a write-protected file read-only without an error, and a read or a begun transaction still works there, so only
    /// a write shows it. Like every write it creates the file and schema when they are missing. A list that can't be
    /// written now (write-protected, its folder not writable, a lock held past the busy wait) gives
    /// <see cref="AbuseListState.StorageError"/>. It proves only that the file and its folder can be written: a nearly
    /// full disk can still fail a later, larger write such as the merge.
    /// </summary>
    public AbuseListState RecordAttempt(DateTimeOffset at) =>
        Write("record a download attempt", (conn, tx, meta) =>
        {
            SetMetadata(conn, tx, LastAttemptAtKey, Text(at.ToUnixTimeSeconds()));
            return true;
        });

    /// <summary>
    /// Merges a downloaded blacklist into the list, in one transaction. Every listed address is stamped with the local
    /// download time cut to the whole second (the stamp, which is also written as the last download), so the cutoffs
    /// and the stamps share one clock. The horizon is the stamp minus
    /// <see cref="AbuseIpDbLimits.MaxKeepDays"/> days, and it alone decides what is written: an address listed again
    /// whose stored last sighting is before it starts again with this download as its first sighting (the upsert runs
    /// before the expiry, so the row is kept), and then the addresses no download listed since it are removed. The
    /// cutoff is the feed's (the stamp minus the keep days, the keep days being the download time less
    /// <paramref name="cutoff"/>) and only counts: the addresses listed since it after the merge, against those the
    /// previous download's cutoff (<see cref="CutoffFor"/> of the stamp, the last download and the same keep) listed
    /// before it, give <see cref="ListMerge.Added"/> and <see cref="ListMerge.Lapsed"/>, so one the keep days hid and
    /// that is listed again is added and keeps its first sighting. Both are counted over the rows in the file, both
    /// families together, before a read judges them, and the count before plus Added less Lapsed is the count after
    /// over those rows. Every download is merged, also one of a generation already merged; the
    /// stored generation is only ever moved forward. A generation older than the newest merged refreshes its addresses
    /// but leaves their score and country as the newer one gave them; the last report time only ever moves forward.
    /// A cutoff later than the download time minus <see cref="AbuseIpDbLimits.MinKeepDays"/> days would remove the
    /// merge's own rows, and one earlier than the horizon would show rows the merge removes, so either throws
    /// <see cref="ArgumentOutOfRangeException"/> before the file is opened. A cancel
    /// throws <see cref="OperationCanceledException"/> and changes nothing.
    /// </summary>
    public ListRead<ListMerge> Merge(Blacklist list, DateTimeOffset downloadedAt, DateTimeOffset cutoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (cutoff > downloadedAt.AddDays(-AbuseIpDbLimits.MinKeepDays))
            throw new ArgumentOutOfRangeException(nameof(cutoff), "The cutoff is later than the shortest keep allows.");
        if (cutoff < downloadedAt.AddDays(-AbuseIpDbLimits.MaxKeepDays))
            throw new ArgumentOutOfRangeException(nameof(cutoff), "The cutoff is earlier than the longest keep allows.");

        ListMerge? result = null;
        AbuseListState state = Write("merge a blacklist", (conn, tx, meta) =>
        {
            long seen = downloadedAt.ToUnixTimeSeconds();
            long gen = list.GeneratedAt.ToUnixTimeSeconds();
            long? stored = LongOf(meta, LastGeneratedAtKey);
            bool newGeneration = stored is not long merged || gen > merged;
            // The same generation downloaded again still replaces the score; only an older one doesn't
            bool fresh = stored is not long kept || gen >= kept;
            // Every cutoff and the horizon count from the whole second that is stamped as last_seen and
            // last_download_at, so the count after the merge is what the status and the feed read from that stamp
            DateTimeOffset stamp = DateTimeOffset.FromUnixTimeSeconds(seen);
            TimeSpan keep = downloadedAt - cutoff;
            long cutoffSeconds = CutoffSeconds(stamp - keep);
            long horizonSeconds = CutoffSeconds(stamp.AddDays(-AbuseIpDbLimits.MaxKeepDays));
            // "Before" is the list as the status and the feed showed it before this download: counted back from the
            // previous download, or from this one when there was none or it was later
            long beforeSeconds = CutoffSeconds(CutoffFor(stamp, TimeOf(meta, LastDownloadAtKey), keep));

            long before = CountListed(conn, tx, beforeSeconds);
            bool setKept = TryRecordNew(conn, tx, list.Entries, beforeSeconds, ct);

            using (var upsert = conn.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = UpsertSql;
                SqliteParameter ip = upsert.Parameters.Add("$ip", SqliteType.Blob);
                upsert.Parameters.Add("$seen", SqliteType.Integer).Value = seen;
                upsert.Parameters.Add("$horizon", SqliteType.Integer).Value = horizonSeconds;
                upsert.Parameters.Add("$fresh", SqliteType.Integer).Value = fresh ? 1 : 0;
                SqliteParameter score = upsert.Parameters.Add("$score", SqliteType.Integer);
                SqliteParameter country = upsert.Parameters.Add("$country", SqliteType.Text);
                SqliteParameter reported = upsert.Parameters.Add("$reported", SqliteType.Integer);
                upsert.Prepare();

                IReadOnlyList<BlacklistEntry> entries = list.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    BlacklistEntry entry = entries[i];
                    ip.Value = entry.Address.Bytes;
                    score.Value = entry.Score;
                    country.Value = (object?)entry.CountryCode ?? DBNull.Value;
                    reported.Value = entry.LastReportedAt is DateTimeOffset at ? at.ToUnixTimeSeconds() : (object)DBNull.Value;
                    upsert.ExecuteNonQuery();
                }
            }
            ct.ThrowIfCancellationRequested();

            // A row listed again now has this download's time, so what is left between the two cutoffs dropped out.
            // Counted before the expiry, which may remove some of them too
            long lapsed = CountBetween(conn, tx, beforeSeconds, cutoffSeconds);

            int expired;
            using (var expire = conn.CreateCommand())
            {
                expire.Transaction = tx;
                expire.CommandText = "DELETE FROM addresses WHERE last_seen < $horizon;";
                expire.Parameters.Add("$horizon", SqliteType.Integer).Value = horizonSeconds;
                expired = expire.ExecuteNonQuery();
            }

            long added = CountListed(conn, tx, cutoffSeconds) - before + lapsed;

            // The set is read only beside a stamp equal to last_download_at, so a set that failed, or doesn't hold what
            // Added counted, leaves none
            bool setMatches = false;
            if (setKept)
            {
                long held = CountNew(conn, tx);
                setMatches = held == added;
                if (!setMatches)
                    Trace.TraceWarning($"AbuseIPDB list: the new-address set held {held} address(es), not the {added} added.");
            }
            if (setMatches)
                SetMetadata(conn, tx, NewAddressesAtKey, Text(seen));
            else
                DeleteMetadata(conn, tx, NewAddressesAtKey);

            SetMetadata(conn, tx, LastDownloadAtKey, Text(seen));
            if (newGeneration)
                SetMetadata(conn, tx, LastGeneratedAtKey, Text(gen));
            BeforeCommit?.Invoke();
            result = new ListMerge(newGeneration, (int)Math.Min(added, int.MaxValue), (int)Math.Min(lapsed, int.MaxValue),
                                   expired);
            return true;
        });
        return new ListRead<ListMerge>(state, state == AbuseListState.Usable ? result : null);
    }

    /// <summary>
    /// Records, before the upserts change any <c>last_seen</c>, the listed addresses that had no row last seen at or
    /// after <paramref name="beforeSeconds"/>: exactly those <see cref="ListMerge.Added"/> counts. The table is made
    /// again at every merge. The set is derived data and the download is already spent, so a statement that fails here
    /// must not stop the merge: the statements run under a savepoint, and a SQLite error rolls back to it and gives
    /// false. That covers statement-level errors only. A failure SQLite answers by rolling back the whole transaction
    /// (ROLLBACK TO then throws, and the write is a storage error), or one at the commit, where most pages are written,
    /// still fails the merge. That is why the set holds only the new addresses. A cancellation escapes, and it stops the
    /// whole write.
    /// </summary>
    private static bool TryRecordNew(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<BlacklistEntry> entries,
                                     long beforeSeconds, CancellationToken ct)
    {
        Execute(conn, tx, "SAVEPOINT new_addresses_set;");
        try
        {
            Execute(conn, tx, "DROP TABLE IF EXISTS new_addresses;");
            Execute(conn, tx, "CREATE TABLE new_addresses (ip BLOB PRIMARY KEY) WITHOUT ROWID;");
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO new_addresses (ip) SELECT $ip WHERE NOT EXISTS "
                                   + "(SELECT 1 FROM addresses WHERE ip = $ip AND last_seen >= $before);";
                SqliteParameter ip = insert.Parameters.Add("$ip", SqliteType.Blob);
                insert.Parameters.Add("$before", SqliteType.Integer).Value = beforeSeconds;
                insert.Prepare();
                // Before any upsert, so this sees the stored last_seen
                for (int i = 0; i < entries.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ip.Value = entries[i].Address.Bytes;
                    insert.ExecuteNonQuery();
                }
            }
            Execute(conn, tx, "RELEASE new_addresses_set;");
            return true;
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"AbuseIPDB list: the new-address set couldn't be kept (SQLite error {ex.SqliteErrorCode}).");
            Execute(conn, tx, "ROLLBACK TO new_addresses_set;");
            Execute(conn, tx, "RELEASE new_addresses_set;");
            return false;
        }
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long CountNew(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT count(*) FROM new_addresses;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// The last download's new-address set, each row judged as <see cref="ReadCurrent"/> judges it (a row that fails is
    /// left out and traced), with no keep-days cutoff. The entries are null when the set isn't known; a table of another
    /// shape never makes the list unreadable, and the next merge replaces it. A SQLite error reading the set fails the
    /// read as any other read's error does (<see cref="AbuseListState.StorageError"/>, or unreadable for a damaged file).
    /// </summary>
    public ListRead<ListNew> ReadNew() =>
        Read(ListNew.Empty, "read the list's new addresses", conn =>
        {
            Dictionary<string, string> meta = ReadMetadata(conn, null);
            var entries = new List<AbuseListEntry>();
            bool known = ReadNewEntries(conn, meta, entries) is not null;
            return new ListNew(known ? entries : null, TimeOf(meta, LastDownloadAtKey));
        });

    private static readonly (string Name, string Type, long Pk)[] NewAddressColumns = [("ip", "BLOB", 1)];

    /// <summary>
    /// The number of judged rows of the new-address set, adding each to <paramref name="into"/> when it is given (IPv4
    /// first, then by bytes; without a list the rows are only counted, unsorted). Null when there is no such table of
    /// this shape or when its stamp isn't the last download's. A SQLite error escapes.
    /// </summary>
    private static int? ReadNewEntries(SqliteConnection conn, Dictionary<string, string> meta, List<AbuseListEntry>? into)
    {
        if (LongOf(meta, NewAddressesAtKey) is not long madeAt || LongOf(meta, LastDownloadAtKey) is not long lastDownload
            || madeAt != lastDownload)
            return null;
        using (var exists = conn.CreateCommand())
        {
            exists.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'new_addresses');";
            if ((long)(exists.ExecuteScalar() ?? 0L) == 0)
                return null;
        }
        if (!HasColumns(conn, null, "new_addresses", NewAddressColumns, checkTypes: true))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a.ip, a.first_seen, a.last_seen, a.score, a.country_code, a.last_reported, typeof(a.ip), "
                        + "typeof(a.first_seen), typeof(a.last_seen), typeof(a.score), typeof(a.country_code), "
                        + "typeof(a.last_reported) FROM new_addresses n JOIN addresses a ON a.ip = n.ip"
                        + (into is not null ? " ORDER BY length(a.ip), a.ip;" : ";");
        using var reader = cmd.ExecuteReader();
        int count = 0;
        int leftOut = 0;
        while (reader.Read())
        {
            if (TryReadEntry(reader, out AbuseListEntry? entry))
            {
                count++;
                into?.Add(entry);
            }
            else
                leftOut++;
        }
        if (leftOut > 0)
            Trace.TraceWarning($"AbuseIPDB list: {leftOut} new address row(s) couldn't be used and were left out.");
        return count;
    }

    /// <summary>
    /// The count of <see cref="ReadNewEntries"/> for the facts: a set that isn't known counts 0, and so does one that
    /// can't be read (the SQLite error code is traced), so the status never fails on the set.
    /// </summary>
    private static int CountNewQuietly(SqliteConnection conn, Dictionary<string, string> meta)
    {
        try
        {
            return ReadNewEntries(conn, meta, into: null) ?? 0;
        }
        catch (SqliteException ex)
        {
            Trace.TraceWarning($"AbuseIPDB list: couldn't count the new-address set (SQLite error {ex.SqliteErrorCode}).");
            return 0;
        }
    }

    /// <summary>
    /// The addresses listed at or after the cutoff, IPv4 first and each family in byte order. With includeIPv6 false the
    /// IPv6 addresses are left out. Every row is judged again on the way out, because the file can be changed outside
    /// the app: a row whose address isn't public, or whose score, times or country code aren't ones a download stores,
    /// is left out.
    /// </summary>
    public ListRead<IReadOnlyList<AbuseListEntry>> ReadCurrent(DateTimeOffset cutoff, bool includeIPv6) =>
        Read<IReadOnlyList<AbuseListEntry>>([], "read the list", conn => ReadEntries(conn, cutoff, includeIPv6));

    /// <summary>
    /// The addresses <see cref="ReadCurrent"/> returns and the list's last download and generation times, in one read,
    /// so the feed's header describes the rows it holds. The cutoff is <see cref="CutoffFor"/> of now, the last download
    /// this read finds and keep.
    /// </summary>
    public ListRead<ListFeed> ReadFeed(DateTimeOffset now, TimeSpan keep, bool includeIPv6)
    {
        return Read(ListFeed.Empty, "read the list for the feed", conn =>
        {
            Dictionary<string, string> meta = ReadMetadata(conn, null);
            DateTimeOffset? lastDownload = TimeOf(meta, LastDownloadAtKey);
            return new ListFeed(ReadEntries(conn, CutoffFor(now, lastDownload, keep), includeIPv6), lastDownload,
                                TimeOf(meta, LastGeneratedAtKey));
        });
    }

    /// <summary>
    /// The rows listed at or after the cutoff that <see cref="TryReadEntry"/> accepts, IPv4 first and each family in
    /// byte order; the number left out is traced. <see cref="ReadCurrent"/> and <see cref="ReadFacts"/> both read
    /// through it, so the feed and the status judge a row alike.
    /// </summary>
    private static List<AbuseListEntry> ReadEntries(SqliteConnection conn, DateTimeOffset cutoff, bool includeIPv6)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ip, first_seen, last_seen, score, country_code, last_reported, typeof(ip), typeof(first_seen), "
                        + "typeof(last_seen), typeof(score), typeof(country_code), typeof(last_reported) FROM addresses "
                        + "WHERE last_seen >= $cutoff" + (includeIPv6 ? "" : " AND length(ip) = 4")
                        + " ORDER BY length(ip), ip;";
        cmd.Parameters.Add("$cutoff", SqliteType.Integer).Value = CutoffSeconds(cutoff);
        using var reader = cmd.ExecuteReader();
        var entries = new List<AbuseListEntry>();
        int leftOut = 0;
        while (reader.Read())
        {
            if (TryReadEntry(reader, out AbuseListEntry? entry))
                entries.Add(entry);
            else
                leftOut++;
        }
        if (leftOut > 0)
            Trace.TraceWarning($"AbuseIPDB list: {leftOut} stored row(s) couldn't be used and were left out.");
        return entries;
    }

    /// <summary>
    /// One row of <see cref="ReadCurrent"/>'s query (the six columns, then the storage type of each), or false when it
    /// isn't a row a download stores.
    /// </summary>
    private static bool TryReadEntry(SqliteDataReader reader, [NotNullWhen(true)] out AbuseListEntry? entry)
    {
        entry = null;
        if (reader.GetString(6) != "blob"
            || !PublicAddress.TryCreate(reader.GetFieldValue<byte[]>(0), out PublicAddress? address)
            || !TryTime(reader, 1, out DateTimeOffset? firstSeen) || firstSeen is null
            || !TryTime(reader, 2, out DateTimeOffset? lastSeen) || lastSeen is null
            || reader.GetString(9) != "integer"
            || reader.GetInt64(3) is < 0 or > AbuseIpDbLimits.MaxScore
            || !TryTime(reader, 5, out DateTimeOffset? lastReported))
            return false;

        string? country = null;
        switch (reader.GetString(10))
        {
            case "null":
                break;
            case "text":
                country = reader.GetString(4);
                if (country.Length != 2 || !char.IsAsciiLetterUpper(country[0]) || !char.IsAsciiLetterUpper(country[1]))
                    return false;
                break;
            default:
                return false;
        }

        entry = new AbuseListEntry(address, firstSeen.Value, lastSeen.Value, (int)reader.GetInt64(3), country, lastReported);
        return true;
    }

    /// <summary>
    /// A stored time: true with null for a NULL, true with the time for whole Unix seconds within
    /// <see cref="DateTimeOffset"/>'s range, and false for anything else.
    /// </summary>
    private static bool TryTime(SqliteDataReader reader, int column, out DateTimeOffset? time)
    {
        time = null;
        switch (reader.GetString(column + 6))
        {
            case "null":
                return true;
            case "integer":
                long seconds = reader.GetInt64(column);
                if (seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                    return false;
                time = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Deletes the file, then any journal beside it. True when none is left (so a missing file is true); false when one
    /// couldn't be deleted. When the database itself can't be deleted, the side files are left as they are, because a
    /// <c>-journal</c> beside a database is what rolls back its unfinished write.
    /// </summary>
    public bool Delete()
    {
        _gate.Wait();
        try
        {
            if (!TryDeleteFile(path) || File.Exists(path))
                return false;

            bool deleted = true;
            foreach (string file in new[] { path + "-journal", path + "-wal", path + "-shm" })
                deleted &= TryDeleteFile(file);
            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"AbuseIPDB list: a file of the list couldn't be deleted ({ex.GetType().Name}).");
            return false;
        }
    }

    private ListRead<T> Read<T>(T whenEmpty, string operation, Func<SqliteConnection, T> body) where T : class
    {
        _gate.Wait();
        try
        {
            if (!File.Exists(path))
                return new ListRead<T>(AbuseListState.Missing, whenEmpty);

            using var conn = Open(SqliteOpenMode.ReadWrite);
            return Classify(conn, null) switch
            {
                FileKind.Empty => new ListRead<T>(AbuseListState.Usable, whenEmpty),
                FileKind.Current => new ListRead<T>(AbuseListState.Usable, body(conn)),
                FileKind.Newer => new ListRead<T>(AbuseListState.Newer, null),
                _ => new ListRead<T>(AbuseListState.Unreadable, null),
            };
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return new ListRead<T>(StateOf(ex, operation), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one write in one transaction, begun before the file is classified. The schema is created when the database
    /// has no tables. A file that is newer or unreadable is left as it was. The transaction is committed only when the
    /// body returns true; otherwise, or on any failure, it is rolled back.
    /// </summary>
    private AbuseListState Write(string operation, Func<SqliteConnection, SqliteTransaction, Dictionary<string, string>, bool> body)
    {
        _gate.Wait();
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWriteCreate);
            using SqliteTransaction tx = conn.BeginTransaction();
            switch (Classify(conn, tx))
            {
                case FileKind.Newer:
                    return AbuseListState.Newer;
                case FileKind.Unreadable:
                    return AbuseListState.Unreadable;
                case FileKind.Empty:
                    CreateSchema(conn, tx);
                    break;
            }

            if (body(conn, tx, ReadMetadata(conn, tx)))
                tx.Commit();
            return AbuseListState.Usable;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return StateOf(ex, operation);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal SqliteConnection Open(SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        try
        {
            conn.Open();
            // The file can be changed outside the app, so nothing in its schema runs with trust, and a user table's
            // foreign key into addresses never blocks the horizon's delete. foreign_keys is a no-op inside a
            // transaction, so it is set here, before any write begins one
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA trusted_schema = OFF; PRAGMA foreign_keys = OFF;";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return conn;
    }

    /// <summary>
    /// What the file is. No schema object at all is empty. Otherwise <c>metadata</c> and <c>addresses</c> must exist,
    /// <c>metadata</c> must be exactly its two columns holding text, and <c>schema_version</c> an integer of at least 1;
    /// above <see cref="SchemaVersion"/> the file is newer and nothing more is checked. At this version no trigger or
    /// view may exist anywhere, because one would run inside a write, or make every merge fail after AbuseIPDB has
    /// counted the download; <c>metadata</c> and <c>addresses</c> must have exactly the objects
    /// <see cref="CreateSchema"/> makes, with its CREATE text (compared with SQLite's separators collapsed, so a build's
    /// line ends don't matter); <c>addresses</c> must have its columns; and every page must pass <c>quick_check</c>.
    /// Tables and indexes of other tables (<c>sqlite_stat1</c>, a user's own, <c>new_addresses</c> of any shape) aren't
    /// checked here, though <c>quick_check</c> still reads them. The type and table of each row are SQLite's: it reads a
    /// row whose CREATE text says otherwise as a malformed schema, which is unreadable.
    /// </summary>
    private static FileKind Classify(SqliteConnection conn, SqliteTransaction? tx)
    {
        var objects = new List<(string Type, string Name, string Table, string? Sql)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                objects.Add((TextOrEmpty(reader, 0), TextOrEmpty(reader, 1), TextOrEmpty(reader, 2),
                             reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        if (objects.Count == 0)
            return FileKind.Empty;
        var tables = new HashSet<string>(objects.Where(o => o.Type == "table").Select(o => o.Name), StringComparer.Ordinal);
        if (!tables.Contains("metadata") || !tables.Contains("addresses"))
            return FileKind.Unreadable;
        if (!HasColumns(conn, tx, "metadata", MetadataColumns, checkTypes: false) || HasNonTextMetadata(conn, tx))
            return FileKind.Unreadable;

        if (!ReadMetadata(conn, tx).TryGetValue("schema_version", out string? text)
            || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version)
            || version < 1)
            return FileKind.Unreadable;

        // A newer version may change the tables, so only this version's layout is checked
        if (version > SchemaVersion)
            return FileKind.Newer;
        if (!HasOnlyOwnObjects(objects))
        {
            Trace.TraceWarning("AbuseIPDB list: the file holds a trigger, a view or an object this list doesn't make.");
            return FileKind.Unreadable;
        }
        if (!HasColumns(conn, tx, "addresses", AddressColumns, checkTypes: true))
            return FileKind.Unreadable;
        // A damaged page of a table or an index throws only when a query reaches it, so check every page first
        return PassesQuickCheck(conn, tx) ? FileKind.Current : FileKind.Unreadable;
    }

    private static string TextOrEmpty(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? "" : reader.GetString(column);

    private static readonly (string Type, string Name, string Table)[] OwnObjects =
    [
        ("table", "metadata", "metadata"), ("index", "sqlite_autoindex_metadata_1", "metadata"),
        ("table", "addresses", "addresses"), ("index", "idx_addresses_last_seen", "addresses"),
    ];

    // Each own object's CREATE text as CreateSchema stores it (null for the autoindex)
    private static readonly Dictionary<string, string?> OwnObjectSql = new(StringComparer.Ordinal)
    {
        ["metadata"] = "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)",
        ["sqlite_autoindex_metadata_1"] = null,
        ["addresses"] = "CREATE TABLE addresses (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, "
                        + "score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL) WITHOUT ROWID",
        ["idx_addresses_last_seen"] = "CREATE INDEX idx_addresses_last_seen ON addresses (last_seen)",
    };

    /// <summary>
    /// True when no object is a trigger or a view, the objects of <c>metadata</c> and <c>addresses</c> are exactly
    /// <see cref="OwnObjects"/>, and each has the text <see cref="OwnObjectSql"/> gives, whitespace collapsed.
    /// </summary>
    private static bool HasOnlyOwnObjects(List<(string Type, string Name, string Table, string? Sql)> objects)
    {
        if (objects.Any(o => o.Type is "trigger" or "view"))
            return false;

        var own = objects.Where(o => string.Equals(o.Table, "metadata", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(o.Table, "addresses", StringComparison.OrdinalIgnoreCase))
                         .ToList();
        if (own.Count != OwnObjects.Length
            || !own.Select(o => (o.Type, o.Name, o.Table)).ToHashSet().SetEquals(OwnObjects))
            return false;

        return own.All(o => OwnObjectSql[o.Name] is string expected
            ? o.Sql is not null && CollapseSpace(o.Sql) == CollapseSpace(expected)
            : o.Sql is null);
    }

    /// <summary>The text with every run of SQLite's separators (space, tab, line feed, form feed, carriage return) made
    /// one space, then trimmed.</summary>
    private static string CollapseSpace(string sql)
    {
        var text = new StringBuilder(sql.Length);
        bool inSpace = false;
        foreach (char c in sql)
        {
            // Only what SQLite reads as a separator: to SQLite a no-break space is part of a word, so NOT and NULL
            // joined by one are part of a type name, not a constraint, and must not compare equal
            if (c is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                inSpace = true;
                continue;
            }
            if (inSpace && text.Length > 0)
                text.Append(' ');
            inSpace = false;
            text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>
    /// True when <c>PRAGMA quick_check(1)</c> gives the one row <c>ok</c>. Anything else names a damaged page; a check
    /// that can't run at all throws CORRUPT or NOTADB, which <see cref="StateOf"/> maps to unreadable too.
    /// </summary>
    private static bool PassesQuickCheck(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA quick_check(1);";
        using var reader = cmd.ExecuteReader();
        int rows = 0;
        bool ok = false;
        while (reader.Read())
        {
            rows++;
            ok = !reader.IsDBNull(0) && reader.GetString(0) == "ok";
        }
        if (rows == 1 && ok)
            return true;

        Trace.TraceWarning("AbuseIPDB list: the file has a damaged page.");
        return false;
    }

    // Pk is the column's 1-based place in the primary key, 0 when it isn't part of it
    private static readonly (string Name, string Type, long Pk)[] MetadataColumns = [("key", "TEXT", 1), ("value", "TEXT", 0)];

    private static readonly (string Name, string Type, long Pk)[] AddressColumns =
    [
        ("ip", "BLOB", 1), ("first_seen", "INTEGER", 0), ("last_seen", "INTEGER", 0), ("score", "INTEGER", 0),
        ("country_code", "TEXT", 0), ("last_reported", "INTEGER", 0),
    ];

    /// <summary>
    /// True when the table has exactly these columns, in any order: the same names, the same primary key and, with
    /// checkTypes, the same declared types (in any letter case).
    /// </summary>
    private static bool HasColumns(SqliteConnection conn, SqliteTransaction? tx, string table,
                                   (string Name, string Type, long Pk)[] expected, bool checkTypes)
    {
        var found = new Dictionary<string, (string Type, long Pk)>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name, type, pk FROM pragma_table_info($table);";
        cmd.Parameters.Add("$table", SqliteType.Text).Value = table;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                return false;
            found[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));
        }

        return found.Count == expected.Length
            && expected.All(column => found.TryGetValue(column.Name, out var actual)
                                      && actual.Pk == column.Pk
                                      && (!checkTypes || string.Equals(actual.Type, column.Type, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when a <c>metadata</c> key or value is NULL or isn't stored as text, which this list never writes.</summary>
    private static bool HasNonTextMetadata(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM metadata WHERE typeof(key) <> 'text' OR typeof(value) <> 'text');";
        return (long)(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    private static void CreateSchema(SqliteConnection conn, SqliteTransaction tx)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE addresses (ip BLOB PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                  score INTEGER NOT NULL, country_code TEXT NULL, last_reported INTEGER NULL) WITHOUT ROWID;
                CREATE INDEX idx_addresses_last_seen ON addresses (last_seen);
                """;
            cmd.ExecuteNonQuery();
        }
        SetMetadata(conn, tx, "schema_version", Text(SchemaVersion));
    }

    private static Dictionary<string, string> ReadMetadata(SqliteConnection conn, SqliteTransaction? tx)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    private static void SetMetadata(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO metadata (key, value) VALUES ($key, $value) "
                        + "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.Add("$key", SqliteType.Text).Value = key;
        cmd.Parameters.Add("$value", SqliteType.Text).Value = value;
        cmd.ExecuteNonQuery();
    }

    private static void DeleteMetadata(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM metadata WHERE key = $key;";
        cmd.Parameters.Add("$key", SqliteType.Text).Value = key;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The rows last seen at or after the cutoff (in whole seconds): the list as <see cref="ReadCurrent"/> sees it.</summary>
    private static long CountListed(SqliteConnection conn, SqliteTransaction tx, long cutoffSeconds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT count(*) FROM addresses WHERE last_seen >= $cutoff;";
        cmd.Parameters.Add("$cutoff", SqliteType.Integer).Value = cutoffSeconds;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>The rows last seen at or after <paramref name="fromSeconds"/> and before <paramref name="toSeconds"/>.</summary>
    private static long CountBetween(SqliteConnection conn, SqliteTransaction tx, long fromSeconds, long toSeconds)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT count(*) FROM addresses WHERE last_seen >= $from AND last_seen < $to;";
        cmd.Parameters.Add("$from", SqliteType.Integer).Value = fromSeconds;
        cmd.Parameters.Add("$to", SqliteType.Integer).Value = toSeconds;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>The cutoff in whole Unix seconds, rounded up, so a row is kept only when it is not older than the cutoff.</summary>
    private static long CutoffSeconds(DateTimeOffset cutoff)
    {
        long seconds = cutoff.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(seconds) < cutoff ? seconds + 1 : seconds;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static long? LongOf(Dictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out string? text)
        && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    /// <summary>A stored time, or null when the key is absent or its value isn't a time.</summary>
    private static DateTimeOffset? TimeOf(Dictionary<string, string> meta, string key) =>
        LongOf(meta, key) is long seconds
        && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static bool IsStorageFailure(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException;

    /// <summary>
    /// Not a database, or damaged, is <see cref="AbuseListState.Unreadable"/>; anything else is a
    /// <see cref="AbuseListState.StorageError"/>. The trace names the error code or type, never the path or a message.
    /// </summary>
    private static AbuseListState StateOf(Exception ex, string operation)
    {
        if (ex is SqliteException sqlite)
        {
            Trace.TraceWarning($"AbuseIPDB list: couldn't {operation} (SQLite error {sqlite.SqliteErrorCode}).");
            return sqlite.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase
                ? AbuseListState.Unreadable
                : AbuseListState.StorageError;
        }

        Trace.TraceWarning($"AbuseIPDB list: couldn't {operation} ({ex.GetType().Name}).");
        return AbuseListState.StorageError;
    }
}
