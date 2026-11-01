using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PoorMansFeed.Core.History;

/// <summary>
/// The user's change history in <c>history.db</c>: one transaction per record, which trims what is past the keep days.
/// A newer or damaged file is never written; only <see cref="Clear"/> removes it.
/// </summary>
internal sealed class FeedHistory(string path, TimeProvider time, long maxBytes = FeedHistory.MaxBytes)
{
    /// <summary>
    /// The layout this version reads and writes.
    /// A file with a higher version is <see cref="HistoryState.Newer"/>.
    /// </summary>
    public const int SchemaVersion = 1;

    public const int KeepDays = 365;

    internal const int CountCap = 100_000;

    internal const long MaxBytes = HistoryLimits.CapMegabytes * 1024L * 1024;

    // Past this many matches, sorting every match costs more than walking the changes newest first to the limit
    internal const int SortCap = 10_000;

    private const string CapMarkKey = "cap_trimmed_before_utc";
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const long SecondsPerDay = 86_400;
    private const int MaxStepDays = 30;
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private const string MetadataTableSql = "CREATE TABLE metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)";
    private const string ChangesTableSql =
        "CREATE TABLE changes (change_id INTEGER PRIMARY KEY, at_utc INTEGER NOT NULL, feed_key TEXT NOT NULL, "
        + "kind TEXT NOT NULL, cause TEXT NOT NULL, dataset_date TEXT NULL, list_downloaded_utc INTEGER NULL)";
    private const string EntriesTableSql =
        "CREATE TABLE entries (change_id INTEGER NOT NULL, is_ipv6 INTEGER NOT NULL, copy TEXT NULL, old_file TEXT NOT NULL, "
        + "added INTEGER NOT NULL, text TEXT NOT NULL, start_ip BLOB NULL, end_ip BLOB NULL, reason TEXT NOT NULL)";
    private const string ChangesAtIndexSql = "CREATE INDEX idx_changes_at_utc ON changes (at_utc)";
    private const string EntriesChangeIndexSql = "CREATE INDEX idx_entries_change ON entries (change_id)";
    private const string EntriesRangeIndexSql = "CREATE INDEX idx_entries_range ON entries (start_ip, end_ip)";

    private static readonly string[] OwnTables = ["metadata", "changes", "entries"];

    private static readonly (string Type, string Name, string Table)[] OwnObjects =
    [
        ("table", "metadata", "metadata"), ("index", "sqlite_autoindex_metadata_1", "metadata"),
        ("table", "changes", "changes"), ("index", "idx_changes_at_utc", "changes"),
        ("table", "entries", "entries"), ("index", "idx_entries_change", "entries"), ("index", "idx_entries_range", "entries"),
    ];

    private static readonly Dictionary<string, string?> OwnObjectSql = new(StringComparer.Ordinal)
    {
        ["metadata"] = MetadataTableSql,
        ["sqlite_autoindex_metadata_1"] = null,
        ["changes"] = ChangesTableSql,
        ["idx_changes_at_utc"] = ChangesAtIndexSql,
        ["entries"] = EntriesTableSql,
        ["idx_entries_change"] = EntriesChangeIndexSql,
        ["idx_entries_range"] = EntriesRangeIndexSql,
    };

    // Pk is the column's 1-based place in the primary key, 0 when it isn't part of it
    private static readonly (string Name, string Type, long Pk)[] MetadataColumns = [("key", "TEXT", 1), ("value", "TEXT", 0)];

    private static readonly (string Name, string Type, long Pk)[] ChangesColumns =
    [
        ("change_id", "INTEGER", 1), ("at_utc", "INTEGER", 0), ("feed_key", "TEXT", 0), ("kind", "TEXT", 0),
        ("cause", "TEXT", 0), ("dataset_date", "TEXT", 0), ("list_downloaded_utc", "INTEGER", 0),
    ];

    private static readonly (string Name, string Type, long Pk)[] EntriesColumns =
    [
        ("change_id", "INTEGER", 0), ("is_ipv6", "INTEGER", 0), ("copy", "TEXT", 0), ("old_file", "TEXT", 0),
        ("added", "INTEGER", 0), ("text", "TEXT", 0), ("start_ip", "BLOB", 0), ("end_ip", "BLOB", 0), ("reason", "TEXT", 0),
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);

    // A page check reads every page, so it runs once per session on a usable file, and again after anything doubts it
    private bool _quickChecked;

    private enum FileKind { Empty, Current, Newer, Unreadable }

    /// <summary>Tests only: runs in <see cref="Record"/> just before the commit.</summary>
    internal Action? BeforeCommit { get; set; }

    /// <summary>Tests only: how many times this instance has run the page check.</summary>
    internal int QuickChecksRun { get; private set; }

    /// <summary>
    /// Records one change in one transaction, trimming what is past the keep days and then past the size cap. Never
    /// throws for a storage failure; throws <see cref="ArgumentException"/> when no family holds an entry.
    /// </summary>
    public HistoryState Record(FeedChangeRecord change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!change.Families.Any(f => f.Added.Count > 0 || f.Removed.Count > 0))
            throw new ArgumentException("A change must hold at least one entry.", nameof(change));

        _gate.Wait();
        try
        {
            using var conn = Open(SqliteOpenMode.ReadWriteCreate);
            using SqliteTransaction tx = conn.BeginTransaction();
            switch (Classify(conn, tx))
            {
                case FileKind.Newer:
                    return HistoryState.Newer;
                case FileKind.Unreadable:
                    _quickChecked = false;
                    return HistoryState.Unreadable;
                case FileKind.Empty:
                    CreateSchema(conn, tx);
                    break;
            }

            Trim(conn, tx, time.GetUtcNow().ToUnixTimeSeconds());
            long changeId = Insert(conn, tx, change);
            TrimToCap(conn, tx, changeId);
            BeforeCommit?.Invoke();
            tx.Commit();
            return HistoryState.Usable;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return StateOf(ex, "record a change");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="HistoryState.Missing"/> when no database is left. When the database itself can't be deleted its side
    /// files stay, because a <c>-journal</c> beside it is what rolls back its unfinished write.
    /// </summary>
    public HistoryState Clear()
    {
        _gate.Wait();
        try
        {
            _quickChecked = false;
            if (!TryDeleteFile(path) || File.Exists(path))
                return HistoryState.StorageError;

            foreach (string file in new[] { path + "-journal", path + "-wal", path + "-shm" })
                TryDeleteFile(file);
            return HistoryState.Missing;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Every recorded entry the filters match, newest change first, then in entry order. Never writes, never creates
    /// the file, and only the caller's own cancellation escapes.
    /// </summary>
    public HistoryRead Search(HistoryQuery query, int limit, CancellationToken ct) => Search(query, limit, null, ct);

    /// <summary>Tests only: <paramref name="changeOrdered"/> forces a row form; null picks it from the count.</summary>
    internal HistoryRead Search(HistoryQuery query, int limit, bool? changeOrdered, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ct.ThrowIfCancellationRequested();

        _gate.Wait(ct);
        try
        {
            if (!File.Exists(path))
                return HistoryRead.Empty(HistoryState.Missing);

            // Read-write only so SQLite can roll back a hot journal; deferred, so the count and the rows read one
            // snapshot without taking the write lock
            using var conn = Open(SqliteOpenMode.ReadWrite);
            using SqliteTransaction tx = conn.BeginTransaction(deferred: true);
            switch (Classify(conn, tx))
            {
                case FileKind.Empty:
                    return HistoryRead.Empty(HistoryState.Usable);
                case FileKind.Newer:
                    return HistoryRead.Empty(HistoryState.Newer);
                case FileKind.Unreadable:
                    _quickChecked = false;
                    return HistoryRead.Empty(HistoryState.Unreadable);
            }

            long matched = CountMatches(conn, tx, query);
            int total = (int)Math.Min(matched, CountCap);
            string sql = changeOrdered is bool forced ? SearchSql(query, count: false, forced) : RowsSql(query, total);
            List<HistoryRow> rows = ReadMatches(conn, tx, query, sql, limit, ct);
            return new HistoryRead(HistoryState.Usable, rows, total, ReadFeedKeys(conn, tx), matched > CountCap,
                                   ReadCapMark(ReadMetadata(conn, tx)));
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return HistoryRead.Empty(StateOf(ex, "search the history"));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string RowsSql(HistoryQuery query, int total) =>
        SearchSql(query, count: false, changeOrdered: query.Match is not null && total > SortCap);

    /// <summary>
    /// The match is the entries starting inside the block plus those starting at one of its ancestor starts, so both
    /// halves seek <c>idx_entries_range</c>; every row is a CIDR, so nothing else can overlap it.
    /// </summary>
    internal static string SearchSql(HistoryQuery query, bool count, bool changeOrdered = false)
    {
        var filters = new List<string>();
        if (query.FeedKey is not null)
            filters.Add("c.feed_key = $feed");
        if (query.SinceUtc is not null)
            filters.Add("c.at_utc >= $since");

        string from;
        if (query.Match is not null && changeOrdered)
        {
            // Change by change, so the first rows come back without sorting every match
            string inside = "e.start_ip BETWEEN $start AND $end AND length(e.start_ip) = $len";
            int ancestors = query.Match.Block.AncestorStarts().Count;
            filters.Add(ancestors == 0
                ? "(" + inside + ")"
                : "((" + inside + ") OR (e.start_ip IN (" + AncestorParameters(ancestors)
                  + ") AND e.end_ip >= $end AND length(e.start_ip) = $len))");
            from = "FROM changes AS c CROSS JOIN entries AS e ON e.change_id = c.change_id";
        }
        else if (query.Match is not null)
        {
            // The length predicate keeps 4 bytes out of 16-byte bounds that sort around them, and the reverse
            string matched = "SELECT rowid AS rid FROM entries WHERE start_ip BETWEEN $start AND $end AND length(start_ip) = $len";
            int ancestors = query.Match.Block.AncestorStarts().Count;
            if (ancestors > 0)
                matched += " UNION ALL SELECT rowid FROM entries WHERE start_ip IN (" + AncestorParameters(ancestors)
                         + ") AND end_ip >= $end AND length(start_ip) = $len";
            // CROSS JOIN fixes the join order, so the planner never trades the seeks for a walk of every change
            from = "FROM (" + matched + ") AS m CROSS JOIN entries AS e ON e.rowid = m.rid "
                 + "CROSS JOIN changes AS c ON c.change_id = e.change_id";
        }
        else
        {
            from = "FROM changes AS c CROSS JOIN entries AS e ON e.change_id = c.change_id";
        }

        string where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
        return count
            ? "SELECT count(*) FROM (SELECT 1 " + from + where + " LIMIT "
              + (CountCap + 1).ToString(CultureInfo.InvariantCulture) + ");"
            : "SELECT c.at_utc, c.feed_key, c.kind, c.cause, c.dataset_date, c.list_downloaded_utc, e.is_ipv6, e.copy, "
              + "e.old_file, e.added, e.text, e.start_ip, e.end_ip, e.reason " + from + where
              + " ORDER BY c.change_id DESC, e.rowid;";
    }

    private static string AncestorParameters(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => "$a" + i.ToString(CultureInfo.InvariantCulture)));

    internal static void BindSearch(SqliteCommand cmd, HistoryQuery query)
    {
        if (query.FeedKey is not null)
            cmd.Parameters.Add("$feed", SqliteType.Text).Value = query.FeedKey;
        if (query.SinceUtc is DateTimeOffset since)
            cmd.Parameters.Add("$since", SqliteType.Integer).Value = since.ToUnixTimeSeconds();
        if (query.Match is null)
            return;

        CidrBlock block = query.Match.Block;
        cmd.Parameters.Add("$start", SqliteType.Blob).Value = block.Start;
        cmd.Parameters.Add("$end", SqliteType.Blob).Value = block.End;
        cmd.Parameters.Add("$len", SqliteType.Integer).Value = (long)block.Start.Length;
        IReadOnlyList<byte[]> ancestors = block.AncestorStarts();
        for (int i = 0; i < ancestors.Count; i++)
            cmd.Parameters.Add("$a" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Blob).Value = ancestors[i];
    }

    /// <summary>Stops one past <see cref="CountCap"/>, so a very wide match costs no more than that.</summary>
    private static long CountMatches(SqliteConnection conn, SqliteTransaction tx, HistoryQuery query)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SearchSql(query, count: true);
        BindSearch(cmd, query);
        return cmd.ExecuteScalar() is long value ? value : 0;
    }

    private static List<HistoryRow> ReadMatches(SqliteConnection conn, SqliteTransaction tx, HistoryQuery query, string sql,
                                                int limit, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        BindSearch(cmd, query);

        var rows = new List<HistoryRow>();
        int leftOut = 0;
        using (var reader = cmd.ExecuteReader())
        {
            while (rows.Count < limit)
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.Read())
                    break;
                if (TryReadRow(reader, out HistoryRow? row))
                    rows.Add(row);
                else
                    leftOut++;
            }
        }
        if (leftOut > 0)
            Trace.TraceWarning($"Feed history: a search left out {leftOut} stored entries this version can't read.");
        return rows;
    }

    /// <summary>The file can be edited outside the app, so every stored value is checked again as it is read.</summary>
    private static bool TryReadRow(SqliteDataReader reader, [NotNullWhen(true)] out HistoryRow? row)
    {
        row = null;
        if (reader.GetValue(0) is not long at || at < MinUnixSeconds || at > MaxUnixSeconds
            || reader.GetValue(1) is not string feedKey
            || !TryName(reader.GetValue(2), out FeedKind kind)
            || !TryName(reader.GetValue(3), out FeedChangeCause cause)
            || reader.GetValue(6) is not long isIPv6 || isIPv6 is not (0 or 1)
            || !TryReadCopy(reader.GetValue(7), out FeedType? copy)
            || !TryName(reader.GetValue(8), out FeedFileState oldFile)
            || reader.GetValue(9) is not long added || added is not (0 or 1)
            || reader.GetValue(10) is not string entry
            || !IsRangeOrNone(reader.GetValue(11), reader.GetValue(12)))
            return false;

        FeedRemovalReason reason = TryName(reader.GetValue(13), out FeedRemovalReason told) ? told : FeedRemovalReason.NotTold;
        DateOnly? datasetDate = reader.GetValue(4) is string dateText
                                && DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                          DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
        DateTimeOffset? downloaded = reader.GetValue(5) is long seconds && seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        row = new HistoryRow(DateTimeOffset.FromUnixTimeSeconds(at), feedKey, kind, cause, isIPv6 == 1, copy, added == 1,
                             entry, reason, oldFile, datasetDate, downloaded);
        return true;
    }

    /// <summary>Only a name this build writes: <c>Enum.TryParse</c> takes a number or another case too.</summary>
    private static bool TryName<T>(object value, out T result) where T : struct, Enum
    {
        result = default;
        return value is string text && Array.IndexOf(Enum.GetNames<T>(), text) >= 0 && Enum.TryParse(text, out result);
    }

    private static bool TryReadCopy(object value, out FeedType? copy)
    {
        copy = null;
        if (value is DBNull)
            return true;
        if (!TryName(value, out FeedType type))
            return false;
        copy = type;
        return true;
    }

    /// <summary>No bytes at all, or one start and end of the same family in order.</summary>
    private static bool IsRangeOrNone(object start, object end) => (start, end) switch
    {
        (DBNull, DBNull) => true,
        (byte[] first, byte[] last) => first.Length is 4 or 16 && first.Length == last.Length
                                       && first.AsSpan().SequenceCompareTo(last) <= 0,
        _ => false,
    };

    private static List<string> ReadFeedKeys(SqliteConnection conn, SqliteTransaction tx)
    {
        var keys = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT DISTINCT feed_key FROM changes WHERE typeof(feed_key) = 'text' ORDER BY feed_key;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
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
            // no-op inside a transaction, so it is set here; on, a user's foreign key could block the trim
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
            Trace.TraceWarning($"Feed history: a file of the history couldn't be deleted ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>Trims changes over the keep days old, each step between changes counting at most a month.</summary>
    private static void Trim(SqliteConnection conn, SqliteTransaction tx, long nowSeconds)
    {
        // Time already counted isn't counted again, so a clock that keeps jumping to one wrong time costs a month once;
        // a stamp that isn't an integer is neither trimmed nor counted.
        long cap = MaxStepDays * SecondsPerDay;
        var counted = new CountedTime();
        var ids = new List<long>();
        var positions = new List<long>();
        long? previous = null;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT change_id, at_utc FROM changes WHERE typeof(at_utc) = 'integer' ORDER BY change_id;";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                long at = reader.GetInt64(1);
                if (previous is long from)
                    counted.AddStep(from, at, cap);
                ids.Add(reader.GetInt64(0));
                positions.Add(counted.Total);
                previous = at;
            }
        }
        if (previous is not long newest)
            return;
        counted.AddStep(newest, nowSeconds, cap);

        // The counted time never falls, so the changes past the keep days are the first ones by id
        int lastAged = positions.FindLastIndex(position => counted.Total - position > KeepDays * SecondsPerDay);
        if (lastAged < 0)
            return;
        using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = """
            DELETE FROM entries WHERE change_id IN (SELECT change_id FROM changes
                WHERE typeof(at_utc) = 'integer' AND change_id <= $last);
            DELETE FROM changes WHERE typeof(at_utc) = 'integer' AND change_id <= $last;
            """;
        delete.Parameters.Add("$last", SqliteType.Integer).Value = ids[lastAged];
        delete.ExecuteNonQuery();
    }

    /// <summary>
    /// Past the cap on pages in use, trims whole changes oldest first to nine tenths of it, never the new one, in
    /// batches sized from the bytes freed per entry; the latest trimmed change's time is kept as a mark.
    /// </summary>
    private void TrimToCap(SqliteConnection conn, SqliteTransaction tx, long newChangeId)
    {
        long used = BytesInUse(conn, tx);
        if (used <= maxBytes)
            return;

        long target = maxBytes / 10 * 9;
        long entries = Math.Max(1, (long)(Scalar(conn, tx, "SELECT count(*) FROM entries;", newChangeId) ?? 0L));
        double bytesPerEntry = Math.Max(1.0, (double)used / entries);
        long? latestTrimmed = null;
        while (used > target)
        {
            long wanted = Math.Max(1, (long)Math.Ceiling((used - target) / bytesPerEntry));
            // The last older change whose running entry count reaches the wanted number, else the newest older one;
            // only integer ids count, as one edited outside the app to a real number would otherwise end the trim
            if (Scalar(conn, tx, """
                    SELECT coalesce(
                        (SELECT change_id FROM (SELECT change_id, sum(n) OVER (ORDER BY change_id) AS run
                            FROM (SELECT change_id, count(*) AS n FROM entries WHERE change_id < $new AND typeof(change_id) = 'integer' GROUP BY change_id))
                         WHERE run >= $wanted ORDER BY change_id LIMIT 1),
                        (SELECT max(change_id) FROM changes WHERE change_id < $new));
                    """, newChangeId, wanted: wanted) is not long last)
                break;

            long removed = (long)(Scalar(conn, tx, "SELECT count(*) FROM entries WHERE change_id <= $last;", newChangeId, last: last)
                                  ?? 0L);
            if (Scalar(conn, tx, "SELECT max(at_utc) FROM changes WHERE typeof(at_utc) = 'integer' AND change_id <= $last;",
                    newChangeId, last: last) is long at)
                latestTrimmed = Math.Max(latestTrimmed ?? at, at);
            using (SqliteCommand delete = CapCommand(conn, tx, """
                       DELETE FROM entries WHERE change_id <= $last;
                       DELETE FROM changes WHERE change_id <= $last;
                       """, newChangeId, last: last))
                delete.ExecuteNonQuery();

            long now = BytesInUse(conn, tx);
            if (removed > 0 && now < used)
                bytesPerEntry = Math.Max(1.0, (double)(used - now) / removed);
            used = now;
        }

        if (latestTrimmed is long trimmed)
            WriteCapMark(conn, tx, trimmed);
    }

    private static object? Scalar(SqliteConnection conn, SqliteTransaction tx, string sql, long newChangeId,
                                  long wanted = 0, long? last = null)
    {
        using SqliteCommand cmd = CapCommand(conn, tx, sql, newChangeId, wanted, last);
        return cmd.ExecuteScalar();
    }

    private static SqliteCommand CapCommand(SqliteConnection conn, SqliteTransaction tx, string sql, long newChangeId,
                                            long wanted = 0, long? last = null)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.Add("$new", SqliteType.Integer).Value = newChangeId;
        cmd.Parameters.Add("$wanted", SqliteType.Integer).Value = wanted;
        // The bound is below the new change in every statement, so a trim can never reach it
        cmd.Parameters.Add("$last", SqliteType.Integer).Value = Math.Min(last ?? 0, newChangeId - 1);
        return cmd;
    }

    private static void WriteCapMark(SqliteConnection conn, SqliteTransaction tx, long trimmedAt)
    {
        if (ReadCapMark(ReadMetadata(conn, tx)) is DateTimeOffset earlier)
            trimmedAt = Math.Max(trimmedAt, earlier.ToUnixTimeSeconds());
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO metadata (key, value) VALUES ($key, $value);";
        cmd.Parameters.Add("$key", SqliteType.Text).Value = CapMarkKey;
        cmd.Parameters.Add("$value", SqliteType.Text).Value = trimmedAt.ToString(CultureInfo.InvariantCulture);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A mark that isn't Unix seconds in range reads as absent: the file can be edited outside.</summary>
    private static DateTimeOffset? ReadCapMark(Dictionary<string, string> metadata) =>
        metadata.TryGetValue(CapMarkKey, out string? text)
        && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds)
        && seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>Pages in use, never the file length: deleted pages stay as free pages and are reused.</summary>
    internal static long BytesInUse(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT (SELECT page_count FROM pragma_page_count()) - (SELECT freelist_count FROM pragma_freelist_count()), "
                        + "(SELECT page_size FROM pragma_page_size());";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return reader.GetInt64(0) * reader.GetInt64(1);
    }

    private static long Insert(SqliteConnection conn, SqliteTransaction tx, FeedChangeRecord change)
    {
        long changeId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO changes (at_utc, feed_key, kind, cause, dataset_date, list_downloaded_utc) "
                            + "VALUES ($at, $key, $kind, $cause, $dataset, $downloaded) RETURNING change_id;";
            cmd.Parameters.Add("$at", SqliteType.Integer).Value = change.AtUtc.ToUnixTimeSeconds();
            cmd.Parameters.Add("$key", SqliteType.Text).Value = change.FeedKey;
            cmd.Parameters.Add("$kind", SqliteType.Text).Value = change.Kind.ToString();
            cmd.Parameters.Add("$cause", SqliteType.Text).Value = change.Cause.ToString();
            cmd.Parameters.Add("$dataset", SqliteType.Text).Value =
                (object?)change.DatasetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value;
            cmd.Parameters.Add("$downloaded", SqliteType.Integer).Value =
                (object?)change.ListDownloadedUtc?.ToUnixTimeSeconds() ?? DBNull.Value;
            changeId = (long)cmd.ExecuteScalar()!;
        }

        using var entry = conn.CreateCommand();
        entry.Transaction = tx;
        entry.CommandText = "INSERT INTO entries (change_id, is_ipv6, copy, old_file, added, text, start_ip, end_ip, reason) "
                          + "VALUES ($change, $ipv6, $copy, $old, $added, $text, $start, $end, $reason);";
        entry.Parameters.Add("$change", SqliteType.Integer).Value = changeId;
        SqliteParameter ipv6 = entry.Parameters.Add("$ipv6", SqliteType.Integer);
        SqliteParameter copy = entry.Parameters.Add("$copy", SqliteType.Text);
        SqliteParameter old = entry.Parameters.Add("$old", SqliteType.Text);
        SqliteParameter added = entry.Parameters.Add("$added", SqliteType.Integer);
        SqliteParameter text = entry.Parameters.Add("$text", SqliteType.Text);
        SqliteParameter start = entry.Parameters.Add("$start", SqliteType.Blob);
        SqliteParameter end = entry.Parameters.Add("$end", SqliteType.Blob);
        SqliteParameter reason = entry.Parameters.Add("$reason", SqliteType.Text);

        foreach (FeedFamilyChange family in change.Families)
        {
            ipv6.Value = family.IsIPv6 ? 1L : 0L;
            copy.Value = (object?)family.Copy?.ToString() ?? DBNull.Value;
            old.Value = family.OldFile.ToString();
            var rows = family.Added.Select(a => (Text: a, Added: true, Reason: FeedRemovalReason.NotTold))
                             .Concat(family.Removed.Select(r => (r.Text, Added: false, r.Reason)));
            foreach ((string entryText, bool isAdded, FeedRemovalReason why) in rows)
            {
                added.Value = isAdded ? 1L : 0L;
                text.Value = entryText;
                // A line the feed parser refuses keeps its text but no bytes, so no range is invented for it
                if (ExportWriter.TryFeedEntry(entryText, out _, out byte[] startBytes, out byte[] endBytes))
                {
                    start.Value = startBytes;
                    end.Value = endBytes;
                }
                else
                {
                    start.Value = DBNull.Value;
                    end.Value = DBNull.Value;
                }
                reason.Value = why.ToString();
                entry.ExecuteNonQuery();
            }
        }
        return changeId;
    }

    /// <summary>
    /// At this version any trigger or view, or any change to the own objects or their CREATE text, is unreadable:
    /// one could run inside a record or make every record fail.
    /// </summary>
    private FileKind Classify(SqliteConnection conn, SqliteTransaction tx)
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
        if (!OwnTables.All(tables.Contains))
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
            Trace.TraceWarning("Feed history: the file holds a trigger, a view or an object this history doesn't make.");
            return FileKind.Unreadable;
        }
        if (!HasColumns(conn, tx, "changes", ChangesColumns, checkTypes: true)
            || !HasColumns(conn, tx, "entries", EntriesColumns, checkTypes: true))
            return FileKind.Unreadable;
        if (_quickChecked)
            return FileKind.Current;

        // A damaged page throws only when a query reaches it, so every page is checked before the first use
        QuickChecksRun++;
        if (!PassesQuickCheck(conn, tx))
            return FileKind.Unreadable;
        _quickChecked = true;
        return FileKind.Current;
    }

    private static string TextOrEmpty(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? "" : reader.GetString(column);

    /// <summary>
    /// True when no object is a trigger or a view, the objects of the own tables are exactly <see cref="OwnObjects"/>,
    /// and each has the text <see cref="OwnObjectSql"/> gives, separators collapsed.
    /// </summary>
    private static bool HasOnlyOwnObjects(List<(string Type, string Name, string Table, string? Sql)> objects)
    {
        if (objects.Any(o => o.Type is "trigger" or "view"))
            return false;

        var own = objects.Where(o => OwnTables.Any(t => string.Equals(o.Table, t, StringComparison.OrdinalIgnoreCase)))
                         .ToList();
        if (own.Count != OwnObjects.Length
            || !own.Select(o => (o.Type, o.Name, o.Table)).ToHashSet().SetEquals(OwnObjects))
            return false;

        return own.All(o => OwnObjectSql[o.Name] is string expected
            ? o.Sql is not null && CollapseSpace(o.Sql) == CollapseSpace(expected)
            : o.Sql is null);
    }

    /// <summary>The text with every run of SQLite's separators made one space, then trimmed.</summary>
    private static string CollapseSpace(string sql)
    {
        var text = new StringBuilder(sql.Length);
        bool inSpace = false;
        foreach (char c in sql)
        {
            // Only what SQLite reads as a separator: to SQLite a no-break space is part of a word
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
    /// True when <c>PRAGMA quick_check(1)</c> gives the one row <c>ok</c>. A check that can't run at all throws CORRUPT
    /// or NOTADB, which <see cref="StateOf"/> maps to unreadable too.
    /// </summary>
    private static bool PassesQuickCheck(SqliteConnection conn, SqliteTransaction tx)
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

        Trace.TraceWarning("Feed history: the file has a damaged page.");
        return false;
    }

    /// <summary>
    /// True when the table has exactly these columns, in any order: the same names, the same primary key and, with
    /// checkTypes, the same declared types (in any letter case).
    /// </summary>
    private static bool HasColumns(SqliteConnection conn, SqliteTransaction tx, string table,
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

    private static bool HasNonTextMetadata(SqliteConnection conn, SqliteTransaction tx)
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
            cmd.CommandText = string.Join(";\n", MetadataTableSql, ChangesTableSql, EntriesTableSql, ChangesAtIndexSql,
                                          EntriesChangeIndexSql, EntriesRangeIndexSql) + ";";
            cmd.ExecuteNonQuery();
        }
        using var meta = conn.CreateCommand();
        meta.Transaction = tx;
        meta.CommandText = "INSERT INTO metadata (key, value) VALUES ('schema_version', $value);";
        meta.Parameters.Add("$value", SqliteType.Text).Value = SchemaVersion.ToString(CultureInfo.InvariantCulture);
        meta.ExecuteNonQuery();
    }

    private static Dictionary<string, string> ReadMetadata(SqliteConnection conn, SqliteTransaction tx)
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

    private static bool IsStorageFailure(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException;

    /// <summary>
    /// Not a database, or damaged, is <see cref="HistoryState.Unreadable"/>; anything else is a
    /// <see cref="HistoryState.StorageError"/>. The trace names the error code or type, never the path or a message.
    /// </summary>
    private HistoryState StateOf(Exception ex, string operation)
    {
        if (ex is SqliteException sqlite)
        {
            Trace.TraceWarning($"Feed history: couldn't {operation} (SQLite error {sqlite.SqliteErrorCode}).");
            if (sqlite.SqliteErrorCode is not (SqliteCorrupt or SqliteNotADatabase))
                return HistoryState.StorageError;
            _quickChecked = false;
            return HistoryState.Unreadable;
        }

        Trace.TraceWarning($"Feed history: couldn't {operation} ({ex.GetType().Name}).");
        return HistoryState.StorageError;
    }
}
