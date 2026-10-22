using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.Config;

/// <summary>
/// The owner of config.db, the one store for the settings, the feed exceptions and the AbuseIPDB download accounting.
/// It opens the file under SQLite's exclusive lock and holds it until <see cref="Dispose"/>, so no other program can
/// write it while the app runs. The file is classified in plain text before the key is read; then each of its three
/// sections is opened with <see cref="ConfigSeal"/> and its value checked. The last committed values are kept in
/// memory (<see cref="Snapshot"/>), and each save is one transaction that changes one section's row.
/// <para>
/// Nothing here opens config.db or a side file with System.IO while a connection to it is open: on macOS a failed
/// same-process open of the locked file drops SQLite's locks for the whole process. Start fresh copies and deletes only
/// when no connection is open. Traces carry fixed texts, SQLite error codes and exception types, never a path, a
/// message from SQLite, the key, a blob or a value.
/// </para>
/// </summary>
internal sealed class ConfigStore : IAbuseAccounting, IDisposable
{
    internal const int SchemaVersion = 1;

    /// <summary>The busy timeout while the file is opened: another program's lock gives BUSY after about 2 s, not 30.</summary>
    internal const int OpenBusyTimeoutSeconds = 2;

    private const int CommandTimeoutSeconds = 30;
    private const int FileIdLength = 32;

    private const int SqliteBusy = 5, SqliteCorrupt = 11, SqliteNotADatabase = 26;

    private const string SchemaVersionKey = "schema_version", FileIdKey = "file_id", CreatedKey = "created_utc",
        ImportedFromKey = "imported_from", ImportedKey = "imported_utc", OpenedKey = "opened_utc";

    private static readonly ConfigSection[] Sections = [ConfigSection.Settings, ConfigSection.Exceptions, ConfigSection.Accounting];

    // The statements Create runs, one each. SQLite stores each as its text in sqlite_master, without a leading space or
    // the closing semicolon, so the check compares the stored text with these exactly
    private const string MetadataTableSql = "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
    private const string SectionsTableSql = "CREATE TABLE sections (name TEXT PRIMARY KEY, sealed BLOB NOT NULL)";

    // What the marker beside config.db says. Plain ASCII, so any editor shows it as it is
    private const string MadeMarkerText =
        "This application made config.db in this folder. While this file is here, a missing config.db stops the launch "
        + "instead of starting again with no feed exceptions.\n";

    // Pk is the column's 1-based place in the primary key, 0 when it isn't part of it
    private static readonly (string Name, string Type, long Pk)[] MetadataColumns = [("key", "TEXT", 1), ("value", "TEXT", 0)];
    private static readonly (string Name, string Type, long Pk)[] SectionColumns = [("name", "TEXT", 1), ("sealed", "BLOB", 0)];

    // What sqlite_master holds after Create, as (type, name, tbl_name): the two tables and their primary-key indexes
    private static readonly (string Type, string Name, string Table)[] CreatedObjects =
    [
        ("table", "metadata", "metadata"),
        ("index", "sqlite_autoindex_metadata_1", "metadata"),
        ("table", "sections", "sections"),
        ("index", "sqlite_autoindex_sections_1", "sections"),
    ];

    private readonly Lock _gate = new();
    private readonly SqliteConnection _conn;
    private readonly ConfigSeal _seal;
    private volatile ConfigSnapshot _snapshot;
    private bool _disposed;

    private ConfigStore(SqliteConnection conn, ConfigSeal seal, ConfigSnapshot snapshot)
    {
        _conn = conn;
        _seal = seal;
        _snapshot = snapshot;
    }

    /// <summary>The last committed values. It stays the last value after <see cref="Dispose"/>.</summary>
    internal ConfigSnapshot Snapshot => _snapshot;

    /// <inheritdoc/>
    public AbuseAccounting Current => _snapshot.Accounting;

    /// <summary>True until <see cref="Dispose"/>: only then can the accounting be written.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
                return !_disposed;
        }
    }

    /// <summary>
    /// Runs inside every save's transaction, just before the commit. Only tests set it, to make a write fail where
    /// nothing outside the store can reach the held connection.
    /// </summary>
    internal Action? BeforeCommit { get; set; }

    /// <summary>
    /// Opens config.db, importing settings.json and exceptions.json when it is missing or empty. <paramref name="store"/>
    /// is non-null only for <see cref="ConfigOpenState.Opened"/>. The steps, which the comments below name:
    /// <list type="number">
    /// <item>With <paramref name="withoutOldExceptions"/>, exceptions.json is first moved to exceptions.unreadable.json,
    /// so an empty list is imported.</item>
    /// <item>With no config.db: when an earlier launch made one (the config_made marker or a *.migrated file is there),
    /// it is <see cref="ConfigOpenState.Missing"/> whatever settings.json or exceptions.json say, and nothing is made or
    /// imported; otherwise step 9.</item>
    /// <item>The file is opened and the exclusive lock taken and held.</item>
    /// <item>It is classified in plain text, the schema version first. An empty file follows step 2's rule on this
    /// connection. The journal mode is set only on a file this version can use.</item>
    /// <item>The key is read.</item>
    /// <item>Exactly the three sections are opened with it.</item>
    /// <item>Their values are read.</item>
    /// <item>One real write; then the config_made marker is written when it is absent, and any settings.json or
    /// exceptions.json is renamed to a free *.migrated name.</item>
    /// <item>Create: the old files are read, a new key is saved, and the tables and sections are made in one
    /// transaction; then step 8.</item>
    /// </list>
    /// </summary>
    internal static ConfigOpenState TryOpen(AppPaths paths, ConfigKeyStore keys, TimeProvider time,
                                            bool withoutOldExceptions, out ConfigStore? store)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(time);
        store = null;

        // Step 1: before anything else, so the import below finds no exceptions file
        if (withoutOldExceptions && !SetOldExceptionsAside(paths))
            return ConfigOpenState.ExceptionsImportUnreadable;

        try
        {
            // Step 2: File.Exists doesn't open the file
            if (!File.Exists(paths.ConfigPath))
            {
                if (GoneSinceAnEarlierLaunch(paths))
                    return ConfigOpenState.Missing;
                return ImportAndCreate(paths, keys, time, null, out store);
            }

            // Step 3
            ConfigOpenState opened = OpenLocked(paths, out SqliteConnection? conn);
            if (conn is null)
                return opened;
            return OpenOrImport(conn, paths, keys, time, out store);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return StateOf(ex, "open config.db");
        }
    }

    /// <summary>
    /// Replaces a config.db that is <see cref="ConfigOpenState.Newer"/>, <see cref="ConfigOpenState.Unreadable"/>,
    /// <see cref="ConfigOpenState.KeyMismatch"/> or <see cref="ConfigOpenState.Missing"/> by a fresh one holding the
    /// defaults, keeping a copy of the old file as config.unreadable.db (not for Missing, which has nothing to keep).
    /// Any other <paramref name="failed"/> is returned as it is and nothing changes. First config.db is opened again, as
    /// <see cref="TryOpen"/> opens it, because it may have changed since the launch stopped: when it opens now, that
    /// store is the result and nothing is replaced, and any failure other than <paramref name="failed"/> is returned as a
    /// launch would give it, with nothing replaced (with no evidence of an earlier launch, that open may import the old
    /// files and save a new key, as a first launch does). Then the key is settled before anything is touched: it is
    /// kept whenever it can still be read, a mismatch included, so the copy can still be opened with it, and a new one
    /// is saved only when there is none or it can never be read; a key that can't be read or saved now gives
    /// <see cref="ConfigOpenState.KeyUnavailable"/> or <see cref="ConfigOpenState.NoKeyStore"/> with every file as it
    /// was. A new key, when one is made, is saved before the copy, so a copy or delete that then fails leaves the old
    /// file beside a new key. Returns <paramref name="failed"/> itself when the copy or the delete of config.db failed (the
    /// old file is then as it was). The config_made marker is never deleted.
    /// </summary>
    internal static ConfigOpenState StartFresh(AppPaths paths, ConfigKeyStore keys, TimeProvider time,
                                               ConfigOpenState failed, out ConfigStore? store)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(time);
        store = null;
        if (failed is not (ConfigOpenState.Newer or ConfigOpenState.Unreadable or ConfigOpenState.KeyMismatch
                           or ConfigOpenState.Missing))
            return failed;

        // The file as it is now, not as it was when the launch stopped: one put back since may open, and one deleted
        // since is Missing, with nothing to copy
        ConfigOpenState again = TryOpen(paths, keys, time, withoutOldExceptions: false, out store);
        if (again != failed)
            return again;

        byte[]? key = null;
        try
        {
            // The key first, so a store that can't be read or written now leaves the old file and its copy as they were
            ConfigOpenState settled = SettleFreshKey(keys, out key);
            if (settled != ConfigOpenState.Opened)
                return settled;

            // No connection is open, so System.IO may touch the file
            if (failed != ConfigOpenState.Missing && !KeepCopy(paths))
                return failed;
            if (!TryDeleteFile(paths.ConfigPath))
                return failed;

            string[] sideFiles = [paths.ConfigPath + "-journal", paths.ConfigPath + "-wal", paths.ConfigPath + "-shm"];
            bool cleared = true;
            foreach (string file in sideFiles)
                cleared &= TryDeleteFile(file);
            if (!cleared || File.Exists(paths.ConfigPath) || sideFiles.Any(File.Exists))
            {
                Trace.TraceWarning("config.db: a file of the old config couldn't be removed, so no new one was made.");
                return ConfigOpenState.StorageError;
            }

            ConfigOpenState opened = OpenLocked(paths, out SqliteConnection? conn);
            if (conn is null)
                return opened;
            // Opened came with the key
            return Create(conn, paths, time, key!, new UserSettings(), ExceptionList.Empty, "", out store);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return StateOf(ex, "start config.db afresh");
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Seals and replaces the settings row in one transaction. The settings are normalised first.</summary>
    internal bool SaveSettings(UserSettings normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);
        UserSettings settings = SettingsStore.Normalise(normalised);
        return Save(ConfigSection.Settings, "save the settings",
                    snapshot => (SettingsStore.ToJson(settings), snapshot with { Settings = settings }));
    }

    /// <summary>Seals and replaces the exceptions row in one transaction. Throws ArgumentException on an unreadable list.</summary>
    internal bool SaveExceptions(ExceptionList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!list.IsReadable)
            throw new ArgumentException("An unreadable exceptions list can't be saved.", nameof(list));
        return Save(ConfigSection.Exceptions, "save the exceptions",
                    snapshot => (ExceptionListStore.ToJson(list), snapshot with { Exceptions = list }));
    }

    /// <inheritdoc/>
    public bool TryRecordAnswer(AbuseRateLimit rateLimit, DateTimeOffset answeredAt, bool countDownload)
    {
        ArgumentNullException.ThrowIfNull(rateLimit);
        return Save(ConfigSection.Accounting, "save the download accounting", snapshot =>
        {
            byte[] json = AccountingToJson(snapshot.Accounting.After(rateLimit, answeredAt, countDownload));
            // What was sealed, read back, so Current is what the next launch reads (whole seconds, offset zero)
            if (!TryReadAccounting(json, out AbuseAccounting? readBack))
                throw new InvalidOperationException("The download accounting didn't read back.");
            return (json, snapshot with { Accounting = readBack });
        });
    }

    /// <summary>Disposes the seal, then the connection, which releases the lock. Every later save returns false.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _seal.Dispose();
            _conn.Dispose();
        }
    }

    // ----- Opening -----

    /// <summary>
    /// Steps 4 to 8 on a connection that holds the lock: an empty file is created in place, under the lock already
    /// held, unless step 2's rule makes it Missing; any other is checked. The connection is the caller's no longer: it is
    /// disposed unless a store comes back.
    /// </summary>
    private static ConfigOpenState OpenOrImport(SqliteConnection conn, AppPaths paths, ConfigKeyStore keys, TimeProvider time,
                                                out ConfigStore? store)
    {
        store = null;
        try
        {
            // Step 4
            FileKind kind = Classify(conn, out string? fileId);
            if (kind == FileKind.Empty)
            {
                if (GoneSinceAnEarlierLaunch(paths))
                    return ConfigOpenState.Missing;
                return ImportAndCreate(paths, keys, time, conn, out store);
            }
            return OpenExisting(conn, kind, fileId, paths, keys, time, out store);
        }
        finally
        {
            if (store is null)
                conn.Dispose();
        }
    }

    /// <summary>Steps 5 to 8 for a classified file that has tables. Disposes the connection unless a store comes back.</summary>
    private static ConfigOpenState OpenExisting(SqliteConnection conn, FileKind kind, string? fileId, AppPaths paths,
                                                ConfigKeyStore keys, TimeProvider time, out ConfigStore? store)
    {
        store = null;
        ConfigSeal? seal = null;
        try
        {
            switch (kind)
            {
                case FileKind.Newer:
                    return ConfigOpenState.Newer;
                case FileKind.Unreadable or FileKind.Empty:
                    return ConfigOpenState.Unreadable;
            }

            // The end of step 4: only a file this version can use is ever written
            if (!TrySetJournalMode(conn))
                return ConfigOpenState.StorageError;

            // Step 5
            switch (keys.TryLoad(out byte[]? key))
            {
                case KeyState.Read:
                    seal = new ConfigSeal(key!, fileId!);
                    break;
                case KeyState.Unavailable:
                    return ConfigOpenState.KeyUnavailable;
                case KeyState.NoStore:
                    return ConfigOpenState.NoKeyStore;
                default:
                    Trace.TraceWarning("config.db: there is no usable key for it.");
                    return ConfigOpenState.KeyMismatch;
            }

            // Steps 6 and 7
            ConfigOpenState read = ReadSections(conn, seal, out ConfigSnapshot? snapshot);
            if (read != ConfigOpenState.Opened)
                return read;

            // Step 8
            return Finish(conn, seal, snapshot!, paths, time, out store);
        }
        finally
        {
            if (store is null)
            {
                seal?.Dispose();
                conn.Dispose();
            }
        }
    }

    /// <summary>
    /// Step 9: reads the old settings.json and exceptions.json, saves a new key, then creates the file. With no
    /// connection (no config.db) the file is opened only after the key is saved, and one that has tables by then is
    /// checked from step 4 on. Disposes the connection unless a store comes back.
    /// </summary>
    private static ConfigOpenState ImportAndCreate(AppPaths paths, ConfigKeyStore keys, TimeProvider time,
                                                   SqliteConnection? conn, out ConfigStore? store)
    {
        store = null;
        byte[]? key = null;
        try
        {
            var importedFrom = new List<string>(2);
            if (File.Exists(paths.SettingsPath))
                importedFrom.Add(Path.GetFileName(paths.SettingsPath));
            UserSettings settings = new SettingsStore(paths.SettingsPath).Load();

            bool hadExceptions = File.Exists(paths.ExceptionsPath);
            ExceptionList exceptions = new ExceptionListStore(paths.ExceptionsPath).Load();
            if (!exceptions.IsReadable)
            {
                Trace.TraceWarning("config.db: the exceptions file to import can't be read, so nothing was written.");
                return ConfigOpenState.ExceptionsImportUnreadable;
            }
            if (hadExceptions)
                importedFrom.Add(Path.GetFileName(paths.ExceptionsPath));

            // A new file always gets a new key, saved before its tables are made, so no file exists that its key can't open
            key = ConfigSeal.NewKey();
            ConfigOpenState saved = SaveNewKey(keys, key);
            if (saved != ConfigOpenState.Opened)
                return saved;

            if (conn is null)
            {
                ConfigOpenState opened = OpenLocked(paths, out conn);
                if (conn is null)
                    return opened;
                FileKind kind = Classify(conn, out string? fileId);
                if (kind != FileKind.Empty)
                {
                    SqliteConnection taken = conn;
                    conn = null;
                    return OpenExisting(taken, kind, fileId, paths, keys, time, out store);
                }
            }

            return Create(conn, paths, time, key, settings, exceptions, string.Join(",", importedFrom), out store);
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            if (store is null)
                conn?.Dispose();
        }
    }

    /// <summary>
    /// Makes the tables, the metadata and the three sealed rows in one transaction on an empty file the connection
    /// holds, then finishes the open. The key is zeroed. Disposes the connection unless a store comes back.
    /// </summary>
    private static ConfigOpenState Create(SqliteConnection conn, AppPaths paths, TimeProvider time, byte[] key,
                                          UserSettings settings, ExceptionList exceptions, string importedFrom,
                                          out ConfigStore? store)
    {
        store = null;
        ConfigSeal? seal = null;
        try
        {
            // The end of step 4 for an empty file, and the fresh file of Start fresh
            if (!TrySetJournalMode(conn))
                return ConfigOpenState.StorageError;

            string fileId = ConfigSeal.NewFileId();
            seal = new ConfigSeal(key, fileId);
            var snapshot = new ConfigSnapshot(settings, exceptions, AbuseAccounting.Empty);
            string now = Stamp(time);

            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                Execute(conn, tx, MetadataTableSql);
                Execute(conn, tx, SectionsTableSql);
                SetMetadata(conn, tx, SchemaVersionKey, SchemaVersion.ToString(CultureInfo.InvariantCulture));
                SetMetadata(conn, tx, CreatedKey, now);
                SetMetadata(conn, tx, FileIdKey, fileId);
                SetMetadata(conn, tx, ImportedFromKey, importedFrom);
                SetMetadata(conn, tx, ImportedKey, now);
                foreach (ConfigSection section in Sections)
                {
                    using SqliteCommand cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO sections (name, sealed) VALUES ($name, $sealed);";
                    cmd.Parameters.Add("$name", SqliteType.Text).Value = ConfigSeal.SectionName(section);
                    cmd.Parameters.Add("$sealed", SqliteType.Blob).Value = seal.Seal(section, SchemaVersion, Plaintext(section, snapshot));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }

            return Finish(conn, seal, snapshot, paths, time, out store);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (store is null)
            {
                seal?.Dispose();
                conn.Dispose();
            }
        }
    }

    /// <summary>
    /// Step 8: one real write, because a file in a folder that can't be written passes every read; then the usual busy
    /// timeout, the store, the config_made marker when it is absent, and the old files renamed to a free *.migrated
    /// name. A failed marker write or rename is traced only.
    /// </summary>
    private static ConfigOpenState Finish(SqliteConnection conn, ConfigSeal seal, ConfigSnapshot snapshot, AppPaths paths,
                                          TimeProvider time, out ConfigStore? store)
    {
        using (SqliteTransaction tx = conn.BeginTransaction())
        {
            SetMetadata(conn, tx, OpenedKey, Stamp(time));
            tx.Commit();
        }

        conn.DefaultTimeout = CommandTimeoutSeconds;
        store = new ConfigStore(conn, seal, snapshot);

        // Before the renames, so a launch that dies between the two still leaves evidence that config.db was made
        WriteMadeMarker(paths);

        // Once config.db exists it is the only source: a settings.json beside it is renamed and never imported
        RenameMigrated(paths.SettingsPath);
        RenameMigrated(paths.ExceptionsPath);
        return ConfigOpenState.Opened;
    }

    /// <summary>
    /// Opens the file, takes the exclusive lock before anything reads it, and keeps it until the connection is
    /// disposed. <paramref name="conn"/> is null for any result but <see cref="ConfigOpenState.Opened"/>. A SQLite or
    /// IO failure is thrown, with the connection disposed: a half-opened exclusive-mode connection keeps a SHARED lock,
    /// and every later attempt from this process would get BUSY. The journal mode isn't set here, because the file
    /// isn't classified yet (<see cref="TrySetJournalMode"/>).
    /// </summary>
    private static ConfigOpenState OpenLocked(AppPaths paths, out SqliteConnection? conn)
    {
        conn = null;
        string cs = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ConfigPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = OpenBusyTimeoutSeconds,
        }.ToString();
        var opening = new SqliteConnection(cs);
        try
        {
            opening.Open();
            Execute(opening, null, "PRAGMA locking_mode = EXCLUSIVE;");
            // The first read: BUSY when another program holds the file, NOTADB for a foreign one; it rolls back a hot
            // journal, and the EXCLUSIVE lock is held from here until Dispose
            Execute(opening, null, "BEGIN EXCLUSIVE; COMMIT;");
            // A write-protected file opens read-only without an error
            if (SQLitePCL.raw.sqlite3_db_readonly(opening.Handle, "main") == 1)
            {
                Trace.TraceWarning("config.db is read-only, so it can't be used.");
                opening.Dispose();
                return ConfigOpenState.StorageError;
            }

            Execute(opening, null, "PRAGMA synchronous = FULL;");
            // Has an effect on macOS only
            Execute(opening, null, "PRAGMA fullfsync = ON;");
        }
        catch
        {
            opening.Dispose();
            throw;
        }

        conn = opening;
        return ConfigOpenState.Opened;
    }

    /// <summary>
    /// Sets the rollback journal on a file this version can use (Current or Empty). A file in WAL mode is changed by
    /// this pragma, so a Newer or Unreadable file never gets it. False, traced, when the mode didn't take.
    /// </summary>
    private static bool TrySetJournalMode(SqliteConnection conn)
    {
        if (string.Equals(Scalar(conn, "PRAGMA journal_mode = DELETE;"), "delete", StringComparison.OrdinalIgnoreCase))
            return true;
        Trace.TraceWarning("config.db: its journal mode couldn't be set.");
        return false;
    }

    /// <summary>
    /// Step 2's rule: config.db is gone (or empty) though an earlier launch made one. An earlier launch that opened it
    /// left the config_made marker, or a *.migrated file when it imported. Then a settings.json or exceptions.json
    /// beside it doesn't count: it may be one an older version wrote since, or one a rename missed, and importing it
    /// would start with no feed exceptions. The config key isn't evidence, because it is saved before the file is made.
    /// Only those names are checked, never config.db itself.
    /// </summary>
    private static bool GoneSinceAnEarlierLaunch(AppPaths paths)
    {
        if (!File.Exists(paths.ConfigMadePath) && !MigratedNames(paths.SettingsPath).Any(File.Exists)
            && !MigratedNames(paths.ExceptionsPath).Any(File.Exists))
            return false;

        Trace.TraceWarning("config.db is missing, though an earlier launch made it.");
        return true;
    }

    private enum FileKind { Empty, Current, Newer, Unreadable }

    /// <summary>
    /// Step 4, in plain text. No object at all is empty. Otherwise the version comes first: <c>metadata</c> with exactly
    /// its columns, declared types and primary key, holding text only, and an integer schema version (above
    /// <see cref="SchemaVersion"/> is newer, and nothing more is read, so a later layout is never called damaged). At
    /// this version the file must hold exactly the objects <see cref="Create"/> makes (a trigger or view could change
    /// what a save writes), each table's stored text exactly the statement Create runs (a constraint added outside the
    /// app would make every save fail) and no text for the indexes, <c>sections</c> with its columns, one <c>ok</c>
    /// from <c>quick_check</c> and a file id.
    /// </summary>
    private static FileKind Classify(SqliteConnection conn, out string? fileId)
    {
        fileId = null;
        var objects = new HashSet<(string Type, string Name, string Table)>();
        var texts = new Dictionary<string, string?>(StringComparer.Ordinal);
        int count = 0;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                count++;
                objects.Add((TextOrEmpty(reader, 0), TextOrEmpty(reader, 1), TextOrEmpty(reader, 2)));
                texts[TextOrEmpty(reader, 1)] = reader.IsDBNull(3) ? null : TextOrEmpty(reader, 3);
            }
        }

        if (count == 0)
            return FileKind.Empty;
        if (!objects.Contains(("table", "metadata", "metadata")) || !HasColumns(conn, "metadata", MetadataColumns)
            || HasNonTextMetadata(conn))
            return Unreadable("its metadata isn't this application's");

        Dictionary<string, string> meta = ReadMetadata(conn);
        if (!meta.TryGetValue(SchemaVersionKey, out string? text)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version < 1)
            return Unreadable("its schema version can't be read");
        if (version > SchemaVersion)
        {
            Trace.TraceWarning("config.db was written by a newer version.");
            return FileKind.Newer;
        }

        if (count != CreatedObjects.Length || !objects.SetEquals(CreatedObjects))
            return Unreadable("it holds objects this application doesn't make");
        if (texts.GetValueOrDefault("metadata") != MetadataTableSql || texts.GetValueOrDefault("sections") != SectionsTableSql
            || texts.GetValueOrDefault("sqlite_autoindex_metadata_1") is not null
            || texts.GetValueOrDefault("sqlite_autoindex_sections_1") is not null)
            return Unreadable("a table isn't made as this application makes it");
        if (!HasColumns(conn, "sections", SectionColumns))
            return Unreadable("its sections aren't this application's");

        // A damaged page throws only when a query reaches it, so check every page first
        if (!PassesQuickCheck(conn))
            return Unreadable("it has a damaged page");
        if (!meta.TryGetValue(FileIdKey, out string? id) || !IsFileId(id))
            return Unreadable("its file id can't be read");

        fileId = id;
        return FileKind.Current;
    }

    private static FileKind Unreadable(string why)
    {
        Trace.TraceWarning($"config.db can't be read ({why}).");
        return FileKind.Unreadable;
    }

    /// <summary>
    /// Steps 6 and 7: exactly the three rows, each opened with the key, then each value read. A row missing, extra,
    /// not a blob or not opening is a change made outside the app (<see cref="ConfigOpenState.KeyMismatch"/>); a
    /// value that opens but can't be read is <see cref="ConfigOpenState.Unreadable"/>.
    /// </summary>
    private static ConfigOpenState ReadSections(SqliteConnection conn, ConfigSeal seal, out ConfigSnapshot? snapshot)
    {
        snapshot = null;
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int rows = 0;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, sealed, typeof(name), typeof(sealed) FROM sections;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows++;
                if (reader.GetString(2) == "text" && reader.GetString(3) == "blob")
                    blobs[reader.GetString(0)] = reader.GetFieldValue<byte[]>(1);
            }
        }

        var plaintexts = new Dictionary<ConfigSection, byte[]>();
        if (rows != Sections.Length || blobs.Count != Sections.Length)
        {
            Trace.TraceWarning("config.db: its sections were changed outside the app.");
            return ConfigOpenState.KeyMismatch;
        }
        foreach (ConfigSection section in Sections)
        {
            if (!blobs.TryGetValue(ConfigSeal.SectionName(section), out byte[]? blob)
                || !seal.TryOpen(section, SchemaVersion, blob, out byte[]? plaintext))
            {
                Trace.TraceWarning("config.db: a section doesn't open with the saved key.");
                return ConfigOpenState.KeyMismatch;
            }
            plaintexts[section] = plaintext;
        }

        UserSettings? settings = SettingsStore.TryRead(plaintexts[ConfigSection.Settings]);
        if (settings is null)
        {
            Trace.TraceWarning("config.db: its settings can't be read.");
            return ConfigOpenState.Unreadable;
        }

        ExceptionList exceptions = ExceptionListStore.Read(plaintexts[ConfigSection.Exceptions]);
        if (!exceptions.IsReadable)
        {
            Trace.TraceWarning("config.db: its exceptions can't be read.");
            return ConfigOpenState.Unreadable;
        }

        if (!TryReadAccounting(plaintexts[ConfigSection.Accounting], out AbuseAccounting? accounting))
        {
            Trace.TraceWarning("config.db: its download accounting can't be read.");
            return ConfigOpenState.Unreadable;
        }

        snapshot = new ConfigSnapshot(SettingsStore.Normalise(settings), exceptions, accounting);
        return ConfigOpenState.Opened;
    }

    /// <summary>Saves a new key and reads it back. <see cref="ConfigOpenState.Opened"/> when it was saved.</summary>
    private static ConfigOpenState SaveNewKey(ConfigKeyStore keys, byte[] key) => keys.Replace(key) switch
    {
        KeyState.Read => ConfigOpenState.Opened,
        KeyState.NoStore => ConfigOpenState.NoKeyStore,
        _ => ConfigOpenState.KeyUnavailable,
    };

    /// <summary>
    /// Start fresh's key, settled before any file is touched, whatever stopped the launch. A key that reads is kept, even
    /// after a mismatch: a mismatch is most often a section changed or a row missing, which the key may still open in the
    /// copy kept as config.unreadable.db, and a new key would make that copy and every backup unreadable for good. Keeping
    /// it loses nothing, because the new file has a new random file id and every section is sealed under a fresh nonce
    /// with the file id bound in. A new key is saved only when there is none or it can never be read. A key that can't be
    /// read now, or a store that can't keep one, is the failure. <see cref="ConfigOpenState.Opened"/> with the key, or the
    /// failure with <paramref name="key"/> null.
    /// </summary>
    private static ConfigOpenState SettleFreshKey(ConfigKeyStore keys, out byte[]? key)
    {
        key = null;
        switch (keys.TryLoad(out byte[]? loaded))
        {
            case KeyState.Read:
                key = loaded;
                return ConfigOpenState.Opened;
            case KeyState.Unavailable:
                return ConfigOpenState.KeyUnavailable;
            case KeyState.NoStore:
                return ConfigOpenState.NoKeyStore;
        }

        byte[] fresh = ConfigSeal.NewKey();
        ConfigOpenState saved = SaveNewKey(keys, fresh);
        if (saved != ConfigOpenState.Opened)
        {
            CryptographicOperations.ZeroMemory(fresh);
            return saved;
        }
        key = fresh;
        return ConfigOpenState.Opened;
    }

    // ----- Saving -----

    /// <summary>
    /// Seals the section's new plaintext and replaces its row in one transaction that must change exactly one row,
    /// then swaps the snapshot. The change is worked out under the gate from the snapshot then current. False, with
    /// the snapshot unchanged, when the write fails or the store is disposed.
    /// </summary>
    private bool Save(ConfigSection section, string operation, Func<ConfigSnapshot, (byte[] Plaintext, ConfigSnapshot Next)> change)
    {
        lock (_gate)
        {
            if (_disposed)
                return false;

            (byte[] plaintext, ConfigSnapshot next) = change(_snapshot);
            byte[] blob = _seal.Seal(section, SchemaVersion, plaintext);
            try
            {
                using SqliteTransaction tx = _conn.BeginTransaction();
                using (SqliteCommand cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE sections SET sealed = $sealed WHERE name = $name;";
                    cmd.Parameters.Add("$sealed", SqliteType.Blob).Value = blob;
                    cmd.Parameters.Add("$name", SqliteType.Text).Value = ConfigSeal.SectionName(section);
                    if (cmd.ExecuteNonQuery() != 1)
                    {
                        Trace.TraceWarning($"config.db: couldn't {operation} (its row is missing).");
                        return false;
                    }
                }
                BeforeCommit?.Invoke();
                tx.Commit();
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                StateOf(ex, operation);
                return false;
            }

            _snapshot = next;
            return true;
        }
    }

    private static byte[] Plaintext(ConfigSection section, ConfigSnapshot snapshot) => section switch
    {
        ConfigSection.Settings => SettingsStore.ToJson(snapshot.Settings),
        ConfigSection.Exceptions => ExceptionListStore.ToJson(snapshot.Exceptions),
        ConfigSection.Accounting => AccountingToJson(snapshot.Accounting),
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    // ----- The accounting section -----

    /// <summary>
    /// The accounting in JSON web casing:
    /// <c>{"rateLimit":{"limit":5,"remaining":4,"resetAt":1792195200},"downloadsDay":"2026-10-19","downloadsCount":1}</c>.
    /// <c>rateLimit</c> is null when no limit is stored, and <c>resetAt</c> is in Unix seconds.
    /// </summary>
    private static byte[] AccountingToJson(AbuseAccounting accounting)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (accounting.RateLimit is { Remaining: int remaining } limit)
            {
                writer.WriteStartObject("rateLimit");
                if (limit.Limit is int most)
                    writer.WriteNumber("limit", most);
                else
                    writer.WriteNull("limit");
                writer.WriteNumber("remaining", remaining);
                if (limit.ResetAt is DateTimeOffset reset)
                    writer.WriteNumber("resetAt", reset.ToUnixTimeSeconds());
                else
                    writer.WriteNull("resetAt");
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("rateLimit");
            }

            if (accounting.DownloadsDay is DateOnly day)
                writer.WriteString("downloadsDay", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            else
                writer.WriteNull("downloadsDay");
            writer.WriteNumber("downloadsCount", accounting.DownloadsDay is null ? 0 : accounting.DownloadsOnDay);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads the accounting. A bad field reads as absent: a limit with no <c>remaining</c> is no limit, a
    /// <c>resetAt</c> outside <see cref="DateTimeOffset"/>'s range is null, a day that isn't <c>yyyy-MM-dd</c> is
    /// none, and with no day, or a negative count, the count is 0. False when the JSON doesn't parse or isn't an object.
    /// </summary>
    private static bool TryReadAccounting(ReadOnlySpan<byte> json, [NotNullWhen(true)] out AbuseAccounting? accounting)
    {
        accounting = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            AbuseRateLimit? rateLimit = null;
            if (root.TryGetProperty("rateLimit", out JsonElement limit) && limit.ValueKind == JsonValueKind.Object
                && IntOf(limit, "remaining") is int remaining)
                rateLimit = new AbuseRateLimit(IntOf(limit, "limit"), remaining, TimeOf(limit, "resetAt"));

            DateOnly? day = root.TryGetProperty("downloadsDay", out JsonElement dayText)
                            && dayText.ValueKind == JsonValueKind.String
                            && DateOnly.TryParseExact(dayText.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                                      DateTimeStyles.None, out DateOnly parsed)
                ? parsed
                : null;
            int count = day is null ? 0 : Math.Max(0, IntOf(root, "downloadsCount") ?? 0);
            accounting = new AbuseAccounting(rateLimit, day, count);
            return true;
        }
    }

    private static int? IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number) ? number : null;

    /// <summary>A time in Unix seconds, or null when it is absent or outside <see cref="DateTimeOffset"/>'s range.</summary>
    private static DateTimeOffset? TimeOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long seconds)
        && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    // ----- Files beside config.db (only while no connection to it is open, or not config.db's at all) -----

    /// <summary>
    /// Moves exceptions.json to exceptions.unreadable.json, replacing an older copy, so the operator's file is kept and
    /// an empty list is imported only when asked. True when there is none left to import.
    /// </summary>
    private static bool SetOldExceptionsAside(AppPaths paths)
    {
        try
        {
            if (File.Exists(paths.ExceptionsPath))
                File.Move(paths.ExceptionsPath, paths.UnreadableExceptionsPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"config.db: the old exceptions file couldn't be set aside ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Writes the config_made marker through a .tmp file and a move when it is absent, and never replaces one that is
    /// there. Nothing in the app deletes it. A failure is traced by type and the launch goes on.
    /// </summary>
    private static void WriteMadeMarker(AppPaths paths)
    {
        string marker = paths.ConfigMadePath, tmp = marker + ".tmp";
        try
        {
            if (File.Exists(marker))
                return;
            File.WriteAllText(tmp, MadeMarkerText);
            File.Move(tmp, marker, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"config.db: the marker that it was made couldn't be written ({ex.GetType().Name}).");
            try
            {
                File.Delete(tmp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort: a .tmp left behind is never read
            }
        }
    }

    /// <summary>The most names a file is renamed to: <c>{file}.migrated</c>, then <c>{file}.migrated.2</c> up to <c>.99</c>.</summary>
    internal const int MigratedNameCount = 99;

    /// <summary>
    /// Renames an imported file to the first free *.migrated name. An earlier import's file is never replaced, and none
    /// is deleted. With every name taken the file is left where it is and traced; it is never imported again, because
    /// config.db exists from here on.
    /// </summary>
    private static void RenameMigrated(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            foreach (string name in MigratedNames(path))
            {
                // A folder in the way counts as taken too
                if (Path.Exists(name))
                    continue;
                File.Move(path, name, overwrite: false);
                return;
            }
            Trace.TraceWarning($"config.db: {Path.GetFileName(path)} was left as it is (every name to rename it to is taken).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"config.db: {Path.GetFileName(path)} couldn't be renamed ({ex.GetType().Name}).");
        }
    }

    /// <summary>The names an imported file is renamed to, in the order they are tried.</summary>
    private static IEnumerable<string> MigratedNames(string path)
    {
        yield return path + AppPaths.MigratedSuffix;
        for (int number = 2; number <= MigratedNameCount; number++)
            yield return path + AppPaths.MigratedSuffix + "." + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// With no connection open: copies config.db to config.unreadable.db through a .tmp file and a move,
    /// and a journal beside it as config.unreadable.db-journal the same way, so a hot journal stays with its database.
    /// An older copy's journal is deleted before the new copy is moved into place, whether or not there is a journal
    /// now, so a stop between the two moves can't pair the new copy with it and roll it back into the new copy. False,
    /// with the .tmp files deleted, on failure.
    /// </summary>
    private static bool KeepCopy(AppPaths paths)
    {
        string journal = paths.ConfigPath + "-journal";
        string copy = paths.UnreadableConfigPath, copyJournal = copy + "-journal";
        string tmp = copy + ".tmp", journalTmp = copyJournal + ".tmp";
        try
        {
            File.Copy(paths.ConfigPath, tmp, overwrite: true);
            bool hasJournal = File.Exists(journal);
            if (hasJournal)
                File.Copy(journal, journalTmp, overwrite: true);
            if (File.Exists(copyJournal))
                File.Delete(copyJournal);

            File.Move(tmp, copy, overwrite: true);
            if (hasJournal)
                File.Move(journalTmp, copyJournal, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"config.db: a copy of it couldn't be kept ({ex.GetType().Name}).");
            TryDeleteFile(tmp);
            TryDeleteFile(journalTmp);
            return false;
        }
    }

    /// <summary>True when the file is gone (so a missing file is true).</summary>
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
            Trace.TraceWarning($"config.db: a file of it couldn't be deleted ({ex.GetType().Name}).");
            return false;
        }
    }

    // ----- SQLite helpers -----

    private static bool PassesQuickCheck(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check(1);";
        using SqliteDataReader reader = cmd.ExecuteReader();
        int rows = 0;
        bool ok = false;
        while (reader.Read())
        {
            rows++;
            ok = !reader.IsDBNull(0) && reader.GetString(0) == "ok";
        }
        return rows == 1 && ok;
    }

    /// <summary>True when the table has exactly these columns, in any order, with the same declared types and primary key.</summary>
    private static bool HasColumns(SqliteConnection conn, string table, (string Name, string Type, long Pk)[] expected)
    {
        var found = new Dictionary<string, (string Type, long Pk)>(StringComparer.Ordinal);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, type, pk FROM pragma_table_info($table);";
        cmd.Parameters.Add("$table", SqliteType.Text).Value = table;
        using SqliteDataReader reader = cmd.ExecuteReader();
        int columns = 0;
        while (reader.Read())
        {
            columns++;
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                return false;
            found[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));
        }

        return columns == expected.Length && found.Count == expected.Length
            && expected.All(column => found.TryGetValue(column.Name, out var actual)
                                      && actual.Pk == column.Pk
                                      && string.Equals(actual.Type, column.Type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a <c>metadata</c> key or value is NULL or isn't stored as text, which this store never writes.</summary>
    private static bool HasNonTextMetadata(SqliteConnection conn) =>
        Scalar(conn, "SELECT EXISTS (SELECT 1 FROM metadata WHERE typeof(key) <> 'text' OR typeof(value) <> 'text');") != "0";

    private static Dictionary<string, string> ReadMetadata(SqliteConnection conn)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    private static string TextOrEmpty(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? "" : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "";

    private static void SetMetadata(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO metadata (key, value) VALUES ($key, $value);";
        cmd.Parameters.Add("$key", SqliteType.Text).Value = key;
        cmd.Parameters.Add("$value", SqliteType.Text).Value = value;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Stamp(TimeProvider time) => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static bool IsFileId(string id) =>
        id.Length == FileIdLength && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsStorageFailure(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException;

    /// <summary>
    /// BUSY is <see cref="ConfigOpenState.Locked"/>; NOTADB and CORRUPT are <see cref="ConfigOpenState.Unreadable"/>;
    /// CANTOPEN, READONLY (READONLY_DIRECTORY included), any other SQLite error and an IO failure are a
    /// <see cref="ConfigOpenState.StorageError"/>. The trace names the error code or type, never the path or a message.
    /// </summary>
    private static ConfigOpenState StateOf(Exception ex, string operation)
    {
        if (ex is SqliteException sqlite)
        {
            Trace.TraceWarning($"config.db: couldn't {operation} (SQLite error {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}).");
            return sqlite.SqliteErrorCode switch
            {
                SqliteBusy => ConfigOpenState.Locked,
                SqliteCorrupt or SqliteNotADatabase => ConfigOpenState.Unreadable,
                _ => ConfigOpenState.StorageError,
            };
        }

        Trace.TraceWarning($"config.db: couldn't {operation} ({ex.GetType().Name}).");
        return ConfigOpenState.StorageError;
    }
}
