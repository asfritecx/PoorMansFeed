using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PoorMansFeed.Core;

public enum DatabaseStatus { Missing, Usable, Outdated, Newer }

public sealed class IpDatabase(string path)
{
    public const int SchemaVersion = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseStatus GetStatus() => Inspect(out _);

    public DatasetInfo? ReadInfo() => Inspect(out DatasetInfo? info) == DatabaseStatus.Usable ? info : null;

    /// <summary>
    /// The networks of an ASN, IPv4 first. With includeIPv6 false the IPv6 rows are left out, whatever the
    /// database holds, so a database built with IPv6 still answers as IPv4 only while the setting is off.
    /// </summary>
    public List<NetworkRecord> QueryAsn(uint asn, int? limit, bool includeIPv6 = true)
    {
        if (limit is not null)
            ArgumentOutOfRangeException.ThrowIfNegative(limit.Value);

        string sql = "SELECT network, country_code, country, continent, asn, as_name, as_domain FROM ip_data "
                   + "WHERE asn = @asn" + Ipv4Only(includeIPv6) + " ORDER BY length(start_ip), start_ip"
                   + (limit is null ? "" : " LIMIT @limit");

        return Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@asn", AsnText(asn));
            if (limit is not null)
                cmd.Parameters.AddWithValue("@limit", (long)limit.Value);
            return ReadRows(cmd);
        }, []);
    }

    public int CountAsn(uint asn, bool includeIPv6 = true)
    {
        return Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ip_data WHERE asn = @asn" + Ipv4Only(includeIPv6) + ";";
            cmd.Parameters.AddWithValue("@asn", AsnText(asn));
            long count = (long)(cmd.ExecuteScalar() ?? 0L);
            return (int)Math.Min(count, int.MaxValue);
        }, 0);
    }

    /// <summary>
    /// The networks that hold the address, the highest start first and, for one start, the broadest first. Only the
    /// starts a network holding the address can have (Cidr.ContainingStarts) are looked up, as index seeks on
    /// idx_ip_range, so a lookup takes the same time wherever the address is. A range scan on start_ip &lt;= @ip read
    /// every row below the address, which made a 223.x lookup hundreds of times slower than a 1.x one.
    /// </summary>
    public List<NetworkRecord> QueryIp(byte[] ip, int limit)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        List<byte[]> starts = Cidr.ContainingStarts(ip);
        // Only parameter names go into the SQL text. The values are bound below.
        string startList = string.Join(", ", Enumerable.Range(0, starts.Count).Select(StartParameter));

        return Query(conn =>
        {
            using var cmd = conn.CreateCommand();
            // Every row is a CIDR whose start has its host bits cleared, so a network holding the address starts at
            // one of these. The length predicate keeps IPv4 lookups away from IPv6 blobs, which share a leading-byte
            // prefix. Start, end and rowid all descend, as the index is read: of the networks sharing a start the
            // broadest comes first, and the same network twice comes back last added first.
            cmd.CommandText = $"""
                SELECT network, country_code, country, continent, asn, as_name, as_domain
                FROM ip_data
                WHERE start_ip IN ({startList}) AND end_ip >= @ip AND length(start_ip) = length(@ip)
                ORDER BY start_ip DESC, end_ip DESC, rowid DESC
                LIMIT @limit;
                """;
            for (int i = 0; i < starts.Count; i++)
                cmd.Parameters.Add(StartParameter(i), SqliteType.Blob).Value = starts[i];
            cmd.Parameters.AddWithValue("@ip", ip);
            cmd.Parameters.AddWithValue("@limit", (long)limit);
            return ReadRows(cmd);
        }, []);
    }

    /// <summary>
    /// Overlaps for each block, under one gate hold and one read-only connection. IPv6 blocks are left out when
    /// includeIPv6 is false, and the file being missing returns an empty list.
    /// </summary>
    public List<NetworkOverlap> QueryOverlaps(IReadOnlyList<CidrBlock> blocks, bool includeIPv6) =>
        QueryOverlaps(blocks, includeIPv6, out _);

    /// <summary>
    /// The overlaps of <see cref="QueryOverlaps(IReadOnlyList{CidrBlock}, bool)"/>, with
    /// <paramref name="answeredBy"/> read from the metadata on the same connection, so it names the file that answered
    /// even right after a <see cref="Promote"/>. Null when the file is missing or its metadata can't be parsed. A file
    /// with no <c>metadata</c> table, or a NULL value in it, fails the query instead (the exception escapes), because
    /// <c>ReadMetadata</c> throws.
    /// </summary>
    public List<NetworkOverlap> QueryOverlaps(IReadOnlyList<CidrBlock> blocks, bool includeIPv6, out DatasetInfo? answeredBy)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        // A lambda can't assign an out parameter, so the label is caught here and assigned after the query
        DatasetInfo? label = null;
        List<NetworkOverlap> found = Query(conn =>
        {
            using var descendants = conn.CreateCommand();
            // The length predicate keeps an IPv6 row out of an IPv4 block.
            descendants.CommandText = """
                SELECT country_code, country, asn, as_name, as_domain, COUNT(*)
                FROM ip_data
                WHERE start_ip BETWEEN @start AND @end AND length(start_ip) = @len
                GROUP BY country_code, country, asn, as_name, as_domain
                ORDER BY country_code, country, asn, as_name, as_domain;
                """;
            var descendantStart = descendants.Parameters.Add("@start", SqliteType.Blob);
            var descendantEnd = descendants.Parameters.Add("@end", SqliteType.Blob);
            var descendantLength = descendants.Parameters.Add("@len", SqliteType.Integer);
            descendants.Prepare();

            using var ancestors = conn.CreateCommand();
            // A containing network's start is this block's start masked to a shorter prefix, because two CIDRs
            // either nest or are disjoint.
            ancestors.CommandText = """
                SELECT country_code, country, asn, as_name, as_domain, COUNT(*)
                FROM ip_data
                WHERE start_ip = @ancestor AND end_ip >= @start
                GROUP BY country_code, country, asn, as_name, as_domain
                ORDER BY country_code, country, asn, as_name, as_domain;
                """;
            var ancestorStart = ancestors.Parameters.Add("@ancestor", SqliteType.Blob);
            var ancestorBlockStart = ancestors.Parameters.Add("@start", SqliteType.Blob);
            ancestors.Prepare();

            var results = new List<NetworkOverlap>(blocks.Count);
            foreach (CidrBlock block in blocks)
            {
                if (block.IsIpv6 && !includeIPv6)
                    continue;

                byte[] start = block.Start;
                byte[] end = block.End;
                var counts = new Dictionary<OverlapKey, int>();

                descendantStart.Value = start;
                descendantEnd.Value = end;
                descendantLength.Value = (long)start.Length;
                ReadOverlapGroups(descendants, counts);

                ancestorBlockStart.Value = start;
                foreach (byte[] ancestor in block.AncestorStarts())
                {
                    ancestorStart.Value = ancestor;
                    ReadOverlapGroups(ancestors, counts);
                }

                var groups = new List<OverlapGroup>(counts.Count);
                foreach ((OverlapKey key, int count) in counts)
                {
                    groups.Add(new OverlapGroup(key.CountryCode, key.Country, key.Asn, key.AsName, key.AsDomain, count));
                }

                results.Add(new NetworkOverlap(block, groups));
            }

            // The schema version isn't checked: the live file was judged at start-up or built by this version
            label = TryParseInfo(ReadMetadata(conn), out DatasetInfo? info) ? info : null;
            return results;
        }, []);
        answeredBy = label;
        return found;
    }

    /// <summary>
    /// Replaces the live database with a completed build. Every connection this class opens is
    /// unpooled and disposed by its caller, so no query can keep the old file open.
    /// </summary>
    public void Promote(string nextPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPath);
        if (!File.Exists(nextPath))
            throw new FileNotFoundException("No built database to promote.", nextPath);

        _gate.Wait();
        try
        {
            File.Move(nextPath, path, overwrite: true);
            DeleteSideFiles(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Builder CreateBuilder(string nextPath, int batchSize, bool includeIPv6 = true) =>
        new(nextPath, batchSize, includeIPv6);

    private DatabaseStatus Inspect(out DatasetInfo? info)
    {
        info = null;
        if (!File.Exists(path))
            return DatabaseStatus.Missing;

        _gate.Wait();
        try
        {
            using var conn = OpenReadOnly();
            Dictionary<string, string> meta = ReadMetadata(conn);

            if (!meta.TryGetValue("schema_version", out string? versionText)
                || !int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                return DatabaseStatus.Outdated;
            }

            if (version > SchemaVersion)
                return DatabaseStatus.Newer;
            if (version < SchemaVersion)
                return DatabaseStatus.Outdated;

            if (!TryParseInfo(meta, out DatasetInfo? parsed))
                return DatabaseStatus.Outdated;

            info = parsed;
            return DatabaseStatus.Usable;
        }
        catch (SqliteException ex)
        {
            // Not a database, or no metadata table: treat as needing a rebuild.
            Trace.TraceWarning(FormattableString.Invariant($"Database at {path} could not be read: {ex.Message}"));
            return DatabaseStatus.Outdated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Query<T>(Func<SqliteConnection, T> body, T whenMissing)
    {
        _gate.Wait();
        try
        {
            if (!File.Exists(path))
                return whenMissing;

            using var conn = OpenReadOnly();
            return body(conn);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection OpenReadOnly()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static Dictionary<string, string> ReadMetadata(SqliteConnection conn)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    private static bool TryParseInfo(Dictionary<string, string> meta, out DatasetInfo? info)
    {
        info = null;
        if (!meta.TryGetValue("last_updated", out string? lastUpdated)
            || !DateOnly.TryParseExact(lastUpdated, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly downloadedOn))
            return false;

        if (!meta.TryGetValue("dataset_checksum", out string? checksum) || string.IsNullOrWhiteSpace(checksum))
            return false;

        if (!meta.TryGetValue("row_count", out string? rowCountText)
            || !long.TryParse(rowCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long rowCount))
            return false;

        if (!meta.TryGetValue("imported_utc", out string? importedText)
            || !DateTimeOffset.TryParse(importedText, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset importedUtc))
            return false;

        // A database built before the setting existed holds IPv6 networks, so a missing key means IPv6 is included
        bool includesIPv6 = true;
        if (meta.TryGetValue("includes_ipv6", out string? includesText))
        {
            if (includesText == "1")
                includesIPv6 = true;
            else if (includesText == "0")
                includesIPv6 = false;
            else
                return false;
        }

        info = new DatasetInfo(downloadedOn, rowCount, checksum, importedUtc, includesIPv6);
        return true;
    }

    private readonly record struct OverlapKey(string? CountryCode, string? Country, string? Asn, string? AsName, string? AsDomain);

    private static void ReadOverlapGroups(SqliteCommand cmd, Dictionary<OverlapKey, int> counts)
    {
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = new OverlapKey(
                NullableString(reader, 0),
                NullableString(reader, 1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                NullableString(reader, 4));
            int count = (int)Math.Min(reader.GetInt64(5), int.MaxValue);
            counts[key] = counts.GetValueOrDefault(key) + count;
        }
    }

    private static List<NetworkRecord> ReadRows(SqliteCommand cmd)
    {
        var rows = new List<NetworkRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NetworkRecord(
                reader.GetString(0),
                NullableString(reader, 1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                NullableString(reader, 6)));
        }
        return rows;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string AsnText(uint asn) => "AS" + asn.ToString(CultureInfo.InvariantCulture);

    private static string StartParameter(int index) => "@s" + index.ToString(CultureInfo.InvariantCulture);

    // An IPv4 start address is 4 bytes and an IPv6 one 16, so the length is what tells them apart
    private static string Ipv4Only(bool includeIPv6) => includeIPv6 ? "" : " AND length(start_ip) = 4";

    private static void DeleteDatabaseFiles(string file)
    {
        DeleteIfExists(file);
        DeleteSideFiles(file);
    }

    private static void DeleteSideFiles(string file)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
            DeleteIfExists(file + suffix);
    }

    private static void DeleteIfExists(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
    }

    public sealed class Builder : IDisposable
    {
        private const string InsertSql = """
            INSERT INTO ip_data (network, country, country_code, continent, continent_code, asn, as_name, as_domain, start_ip, end_ip)
            VALUES (@network, @country, @country_code, @continent, @continent_code, @asn, @as_name, @as_domain, @start_ip, @end_ip);
            """;

        private readonly int _batchSize;
        private readonly bool _includeIPv6;
        private SqliteConnection? _connection;
        private SqliteCommand? _insert;
        private SqliteTransaction? _transaction;
        private long _count;
        private int _inBatch;
        private bool _completed;

        internal Builder(string nextPath, int batchSize, bool includeIPv6)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(nextPath);
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
            _batchSize = batchSize;
            _includeIPv6 = includeIPv6;

            string? directory = Path.GetDirectoryName(Path.GetFullPath(nextPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // The next file is disposable: any leftover from an earlier run is removed first.
            DeleteDatabaseFiles(nextPath);

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = nextPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ConnectionString);
            _connection.Open();

            try
            {
                Execute("PRAGMA journal_mode=OFF;");
                Execute("PRAGMA synchronous=OFF;");
                Execute("PRAGMA locking_mode=EXCLUSIVE;");

                Execute("""
                    CREATE TABLE metadata (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    """);

                Execute("""
                    CREATE TABLE ip_data (
                        network TEXT NOT NULL,
                        country TEXT,
                        country_code TEXT,
                        continent TEXT,
                        continent_code TEXT,
                        asn TEXT,
                        as_name TEXT,
                        as_domain TEXT,
                        start_ip BLOB NOT NULL,
                        end_ip BLOB NOT NULL
                    );
                    """);

                _insert = _connection.CreateCommand();
                _insert.CommandText = InsertSql;
                _insert.Parameters.Add("@network", SqliteType.Text);
                _insert.Parameters.Add("@country", SqliteType.Text);
                _insert.Parameters.Add("@country_code", SqliteType.Text);
                _insert.Parameters.Add("@continent", SqliteType.Text);
                _insert.Parameters.Add("@continent_code", SqliteType.Text);
                _insert.Parameters.Add("@asn", SqliteType.Text);
                _insert.Parameters.Add("@as_name", SqliteType.Text);
                _insert.Parameters.Add("@as_domain", SqliteType.Text);
                _insert.Parameters.Add("@start_ip", SqliteType.Blob);
                _insert.Parameters.Add("@end_ip", SqliteType.Blob);
                _insert.Prepare();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Rows added so far.</summary>
        public long Count => _count;

        /// <summary>False when the importer must leave IPv6 networks out. Recorded as metadata on Complete.</summary>
        public bool IncludesIPv6 => _includeIPv6;

        /// <summary>Adds one row. StartIp and EndIp must already be set. Commits every batchSize rows.</summary>
        public void Add(IpInfoEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ThrowIfNotOpen();

            if (entry.Network is null || entry.StartIp is null || entry.EndIp is null)
                throw new ArgumentException("Entry needs Network, StartIp and EndIp.", nameof(entry));

            _transaction ??= BeginTransaction();

            var parameters = _insert!.Parameters;
            parameters["@network"].Value = entry.Network;
            parameters["@country"].Value = (object?)entry.Country ?? DBNull.Value;
            parameters["@country_code"].Value = (object?)entry.CountryCode ?? DBNull.Value;
            parameters["@continent"].Value = (object?)entry.Continent ?? DBNull.Value;
            parameters["@continent_code"].Value = (object?)entry.ContinentCode ?? DBNull.Value;
            parameters["@asn"].Value = (object?)entry.Asn ?? DBNull.Value;
            parameters["@as_name"].Value = (object?)entry.AsName ?? DBNull.Value;
            parameters["@as_domain"].Value = (object?)entry.AsDomain ?? DBNull.Value;
            parameters["@start_ip"].Value = entry.StartIp;
            parameters["@end_ip"].Value = entry.EndIp;
            _insert.ExecuteNonQuery();

            _count++;
            if (++_inBatch >= _batchSize)
                CommitBatch();
        }

        /// <summary>Commits, creates the indexes, writes metadata and closes the file. The file is not promoted.</summary>
        public void Complete(string checksum, DateOnly downloadedOn, DateTimeOffset importedUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(checksum);
            ThrowIfNotOpen();

            CommitBatch();
            _insert?.Dispose();
            _insert = null;

            Execute("CREATE INDEX idx_asn ON ip_data (asn);");
            Execute("CREATE INDEX idx_ip_range ON ip_data (start_ip, end_ip);");

            WriteMetadata("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            WriteMetadata("last_updated", downloadedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            WriteMetadata("dataset_checksum", checksum);
            WriteMetadata("row_count", _count.ToString(CultureInfo.InvariantCulture));
            WriteMetadata("imported_utc", importedUtc.ToString("O", CultureInfo.InvariantCulture));
            WriteMetadata("includes_ipv6", _includeIPv6 ? "1" : "0");

            _completed = true;
            _connection?.Dispose();
            _connection = null;
        }

        /// <summary>Closes the connection, rolling back any open batch. Does not delete the file.</summary>
        public void Dispose()
        {
            if (_connection is null)
                return;

            try
            {
                _transaction?.Rollback();
            }
            catch (SqliteException)
            {
                // Nothing to roll back.
            }
            finally
            {
                _transaction?.Dispose();
                _transaction = null;
                _insert?.Dispose();
                _insert = null;
                _connection.Dispose();
                _connection = null;
            }
        }

        private SqliteTransaction BeginTransaction()
        {
            _transaction = _connection!.BeginTransaction();
            _insert!.Transaction = _transaction;
            return _transaction;
        }

        private void CommitBatch()
        {
            if (_transaction is null)
                return;

            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
            if (_insert is not null)
                _insert.Transaction = null;
            _inBatch = 0;
        }

        private void WriteMetadata(string key, string value)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO metadata (key, value) VALUES (@key, @value);";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }

        private void Execute(string sql)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private void ThrowIfNotOpen()
        {
            if (_completed)
                throw new InvalidOperationException("The database build is already complete.");
            ObjectDisposedException.ThrowIf(_connection is null, this);
        }
    }
}
