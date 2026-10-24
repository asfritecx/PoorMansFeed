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
/// The rolling list now, per family within the keep days (<see cref="AbuseIpDbList.CutoffFor"/>).
/// <see cref="NewCount"/> is 0 when the last download's new-address set isn't known or wasn't asked for.
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
/// <see cref="Added"/> and <see cref="Lapsed"/> count the file's rows of both families before a read judges them,
/// so they can differ from the status and the feed. An address can be both Lapsed and <see cref="Expired"/>.
/// </summary>
internal sealed record ListMerge(bool NewGeneration, int Added, int Lapsed, int Expired);

/// <summary>
/// The user's rolling list in <c>abuseipdb.db</c>: rows go only at the horizon (the keep days only cut reads), the
/// file only by <see cref="Delete"/>. Reads open it read-write: only a connection that can write rolls back a crash.
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
    /// From the earlier of now and the last download, so a clock set ahead hides nothing and a download stamped
    /// ahead can't move the cutoff later. <see cref="DateTimeOffset.MinValue"/> when keep reaches past the calendar.
    /// </summary>
    public static DateTimeOffset CutoffFor(DateTimeOffset now, DateTimeOffset? lastDownload, TimeSpan keep)
    {
        DateTimeOffset from = lastDownload is DateTimeOffset last && last < now ? last : now;
        return from.UtcTicks - DateTimeOffset.MinValue.UtcTicks < keep.Ticks ? DateTimeOffset.MinValue : from - keep;
    }

    /// <summary>
    /// Counts per family the rows <see cref="ReadCurrent"/> returns with IPv6, judged alike. With
    /// <paramref name="countNew"/> it counts the new-address set too: 0 when not known or unreadable, never a failure.
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
    /// The pre-send write: SQLite opens a write-protected file read-only with no error and reads still work, so only
    /// a write shows it. It proves the file and folder can be written, not that the disk can take the larger merge.
    /// </summary>
    public AbuseListState RecordAttempt(DateTimeOffset at) =>
        Write("record a download attempt", (conn, tx, meta) =>
        {
            SetMetadata(conn, tx, LastAttemptAtKey, Text(at.ToUnixTimeSeconds()));
            return true;
        });

    /// <summary>
    /// Only the horizon decides what is written; <paramref name="cutoff"/> only counts. One outside the keep-days
    /// bounds throws before the file is opened: it would remove the merge's own rows or show rows the merge removes.
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
    /// The set is derived and the download already spent, so a statement error here rolls back to the savepoint and
    /// gives false. A whole-transaction rollback or a failed commit still fails the merge; a cancel stops the write.
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
    /// Rows judged as <see cref="ReadCurrent"/> does, with no cutoff; the entries are null when the set isn't known.
    /// A table of another shape never makes the list unreadable, but a SQLite error reading the set fails the read.
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
    /// Adds each judged row to <paramref name="into"/> when given (sorted), else only counts, unsorted. Null when the
    /// table is missing, of another shape, or not stamped by the last download. A SQLite error escapes.
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
    /// Every row is judged again on the way out, because the file can be changed outside the app; a row a download
    /// wouldn't store is left out.
    /// </summary>
    public ListRead<IReadOnlyList<AbuseListEntry>> ReadCurrent(DateTimeOffset cutoff, bool includeIPv6) =>
        Read<IReadOnlyList<AbuseListEntry>>([], "read the list", conn => ReadEntries(conn, cutoff, includeIPv6));

    /// <summary>
    /// The entries and the last download and generation times in one read, so the feed's header describes the rows
    /// it holds. The cutoff is <see cref="CutoffFor"/> of now and the last download this read finds.
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
    /// <see cref="ReadCurrent"/> and <see cref="ReadFacts"/> both read through it, so the feed and the status judge
    /// a row alike. The number left out is traced.
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
    /// True when no file is left. When the database itself can't be deleted its side files stay, because a
    /// <c>-journal</c> beside it is what rolls back its unfinished write.
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
    /// One transaction, begun before the file is classified and committed only when the body returns true. A newer or
    /// unreadable file is left as it was.
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
            // The file can be changed outside the app, so nothing in its schema runs with trust. foreign_keys is a
            // no-op inside a transaction, so it is set here; on, a user's foreign key would block the horizon delete
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
    /// At this version any trigger or view, or any change to the own objects or their CREATE text, is unreadable:
    /// one could run inside a write, or fail every merge after AbuseIPDB has counted the download.
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
