using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

/// <summary>
/// Every store is disposed before a test reads, copies or changes the file's bytes, because on macOS a System.IO open
/// of the locked file drops SQLite's locks for the whole process.
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    // Evening UTC, so a day taken from local time is a day later east of UTC
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 22, 30, 15, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly AppPaths _paths;
    private readonly InMemorySecretStore _secrets = new();
    private readonly ConfigKeyStore _keys;
    private readonly FakeTimeProvider _time = new(Now);

    public ConfigStoreTests()
    {
        _paths = new AppPaths(_temp.Path, _temp.File("exports"));
        _keys = new ConfigKeyStore(_secrets);
    }

    public void Dispose() => _temp.Dispose();

    private string ConfigFile => _temp.File("config.db");
    private string MarkerFile => _temp.File("config_made");

    // ----- First launch, reopen and saves -----

    [Fact]
    public void A_first_launch_with_no_files_creates_the_file_with_the_defaults_and_a_key()
    {
        using (ConfigStore store = Open())
        {
            Assert.Equal(new UserSettings(), store.Snapshot.Settings);
            Assert.True(store.Snapshot.Exceptions.IsReadable);
            Assert.Empty(store.Snapshot.Exceptions.Entries);
            Assert.Equal(AbuseAccounting.Empty, store.Snapshot.Accounting);
            Assert.Equal(AbuseAccounting.Empty, store.Current);
            Assert.True(_secrets.Marker);
            Assert.Equal(32, _secrets.Secret!.Length);
            Assert.True(File.Exists(ConfigFile));
        }

        Assert.Equal(["metadata", "sections"], Strings(ConfigFile, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
        Assert.Equal(["accounting", "exceptions", "settings"], Strings(ConfigFile, "SELECT name FROM sections ORDER BY name;"));
        Dictionary<string, string> meta = Metadata(ConfigFile);
        Assert.Equal("1", meta["schema_version"]);
        Assert.Matches("^[0-9a-f]{32}$", meta["file_id"]);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), meta["opened_utc"]);
        Assert.Equal(Now.ToString("O", CultureInfo.InvariantCulture), meta["created_utc"]);
        Assert.Equal("", meta["imported_from"]);
    }

    [Fact]
    public void A_reopen_with_the_same_key_reads_back_every_value()
    {
        var settings = new UserSettings
        {
            SchemaVersion = 2,
            ExportFolder = "/Users/someone/Feeds",
            IPv6Enabled = false,
            FortigateEnabled = false,
            FortigateIncludeIPv4 = false,
            FortigateIncludeIPv6 = false,
            FortigateSplitByFamily = false,
            LargeRangeCheckEnabled = false,
            IPv4MinPrefix = 20,
            IPv6MinPrefix = 48,
            AbuseIpDbKeepDays = 90,
            WriteFortigateFeeds = false,
            WriteModSecurityLists = true,
            ModSecurityAction = ModSecurityAction.Deny,
            ModSecurityFirstRuleId = 99_999,
            ModSecurityLocation = "/blocked list",
        };
        ExceptionList exceptions = ExceptionList.Create(
        [
            ExceptionEntryTests.Entry("2001:db8::/32", "Lab"),
            ExceptionEntryTests.Entry("192.0.2.0/24", "Head office \u00e9"),
            ExceptionEntryTests.Entry("198.51.100.7", null),
        ]);
        var limit = new AbuseRateLimit(5, 4, DateTimeOffset.FromUnixTimeSeconds(1792195200));

        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(settings));
            Assert.True(store.SaveExceptions(exceptions));
            Assert.True(store.TryRecordAnswer(limit, Now, countDownload: true));
        }

        using (ConfigStore store = Open())
        {
            Assert.Equal(settings, store.Snapshot.Settings);
            Assert.Equal(exceptions.Entries, store.Snapshot.Exceptions.Entries);
            Assert.Equal(new AbuseAccounting(limit, new DateOnly(2026, 10, 19), 1), store.Current);
        }
    }

    [Fact]
    public void SaveSettings_normalises_what_it_saves()
    {
        using ConfigStore store = Open();

        Assert.True(store.SaveSettings(new UserSettings
        {
            IPv4MinPrefix = 40,
            IPv6MinPrefix = 0,
            AbuseIpDbKeepDays = 999,
            ModSecurityAction = (ModSecurityAction)7,
            ModSecurityFirstRuleId = 0,
            ModSecurityLocation = new string('x', ModSecurityLimits.MaxLocationLength + 1),
        }));

        Assert.Equal(32, store.Snapshot.Settings.IPv4MinPrefix);
        Assert.Equal(1, store.Snapshot.Settings.IPv6MinPrefix);
        Assert.Equal(AbuseIpDbLimits.MaxKeepDays, store.Snapshot.Settings.AbuseIpDbKeepDays);
        Assert.Equal(ModSecurityAction.LogOnly, store.Snapshot.Settings.ModSecurityAction);
        Assert.Equal(ModSecurityLimits.RuleIdMin, store.Snapshot.Settings.ModSecurityFirstRuleId);
        Assert.Equal("", store.Snapshot.Settings.ModSecurityLocation);
    }

    [Fact]
    public void Settings_sealed_before_the_feed_types_reopen_with_FortiGate_on_and_ModSecurity_off()
    {
        Open().Dispose();
        byte[] plaintext = Encoding.UTF8.GetBytes("""
            { "SchemaVersion": 1, "ExportFolder": "", "IPv6Enabled": true, "FortigateEnabled": true,
              "FortigateIncludeIPv4": true, "FortigateIncludeIPv6": true, "FortigateSplitByFamily": true,
              "LargeRangeCheckEnabled": true, "IPv4MinPrefix": 16, "IPv6MinPrefix": 32, "AbuseIpDbKeepDays": 12 }
            """);
        using var seal = new ConfigSeal([.. _secrets.Secret!], Metadata(ConfigFile)["file_id"]);
        SetBlob(ConfigFile, "settings", seal.Seal(ConfigSection.Settings, 1, plaintext));

        using ConfigStore store = Open();

        Assert.Equal(new UserSettings { WriteFortigateFeeds = true, AbuseIpDbKeepDays = 12 }, store.Snapshot.Settings);
    }

    [Theory]
    [InlineData("Newer")]
    [InlineData("Unreadable")]
    [InlineData("KeyMismatch")]
    [InlineData("Missing")]
    public void Start_fresh_after_an_earlier_launch_keeps_FortiGate_feeds_on(string state)
    {
        ConfigOpenState failed = Enum.Parse<ConfigOpenState>(state);
        MakeFailedConfig(failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        using (store)
        {
            Assert.True(store!.Snapshot.Settings.WriteFortigateFeeds);
            Assert.False(store.Snapshot.Settings.WriteModSecurityLists);
        }
        using (ConfigStore reopened = Open())
            Assert.True(reopened.Snapshot.Settings.WriteFortigateFeeds);
    }

    [Fact]
    public void An_empty_file_imported_in_place_beside_a_dataset_keeps_FortiGate_feeds_on()
    {
        MakeEmptyConfig("sqlite");
        File.WriteAllText(_temp.File("ipinfo.db"), "x");

        using ConfigStore store = Open();

        Assert.True(store.Snapshot.Settings.WriteFortigateFeeds);
        Assert.False(store.Snapshot.Settings.WriteModSecurityLists);
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("exceptions")]
    [InlineData("accounting")]
    public void Each_save_changes_only_its_own_row(string saved)
    {
        Open().Dispose();
        Dictionary<string, byte[]> before = Blobs(ConfigFile);

        using (ConfigStore store = Open())
        {
            bool done = saved switch
            {
                "settings" => store.SaveSettings(new UserSettings { IPv6Enabled = false }),
                "exceptions" => store.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")])),
                _ => store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true),
            };
            Assert.True(done);
        }

        Dictionary<string, byte[]> after = Blobs(ConfigFile);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (string name in before.Keys)
        {
            if (name == saved)
                Assert.NotEqual(before[name], after[name]);
            else
                Assert.Equal(before[name], after[name]);
        }
    }

    [Fact]
    public void A_failed_save_returns_false_and_keeps_the_snapshot_and_the_file()
    {
        Open().Dispose();
        Dictionary<string, byte[]> before = Blobs(ConfigFile);

        using (ConfigStore store = Open())
        {
            ConfigSnapshot snapshot = store.Snapshot;
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            Assert.False(store.SaveSettings(new UserSettings { IPv6Enabled = false }));
            Assert.False(store.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", null)])));
            Assert.False(store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true));

            Assert.Same(snapshot, store.Snapshot);
            Assert.Equal(AbuseAccounting.Empty, store.Current);

            // The store still works once the write succeeds
            store.BeforeCommit = null;
            Assert.True(store.SaveSettings(new UserSettings { IPv6Enabled = false }));
            Assert.False(store.Snapshot.Settings.IPv6Enabled);
        }

        Dictionary<string, byte[]> after = Blobs(ConfigFile);
        Assert.Equal(before["exceptions"], after["exceptions"]);
        Assert.Equal(before["accounting"], after["accounting"]);
    }

    [Fact]
    public void Every_save_after_Dispose_returns_false_and_the_snapshot_stays()
    {
        ConfigStore store = Open();
        Assert.True(store.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 7 }));
        ConfigSnapshot snapshot = store.Snapshot;
        store.Dispose();
        store.Dispose();

        Assert.False(store.SaveSettings(new UserSettings()));
        Assert.False(store.SaveExceptions(ExceptionList.Empty));
        Assert.False(store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true));
        Assert.Same(snapshot, store.Snapshot);
    }

    [Fact]
    public void Saves_refuse_an_unreadable_list_and_a_null_limit()
    {
        using ConfigStore store = Open();

        Assert.Throws<ArgumentException>(() => store.SaveExceptions(ExceptionList.Unreadable));
        Assert.Throws<ArgumentNullException>(() => store.TryRecordAnswer(null!, Now, countDownload: true));
    }

    // ----- The download accounting -----

    [Fact]
    public void Two_answers_on_one_UTC_day_count_two_and_a_new_UTC_day_starts_at_one()
    {
        using ConfigStore store = Open();
        var limit = new AbuseRateLimit(5, 4, null);

        // 07:30 at +08 is 23:30 UTC on the 19th
        Assert.True(store.TryRecordAnswer(limit, new DateTimeOffset(2026, 10, 20, 7, 30, 0, TimeSpan.FromHours(8)), countDownload: true));
        Assert.True(store.TryRecordAnswer(limit, new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero), countDownload: true));
        Assert.Equal(new DateOnly(2026, 10, 19), store.Current.DownloadsDay);
        Assert.Equal(2, store.Current.DownloadsOnDay);

        Assert.True(store.TryRecordAnswer(limit, new DateTimeOffset(2026, 10, 20, 0, 10, 0, TimeSpan.Zero), countDownload: true));
        Assert.Equal(new DateOnly(2026, 10, 20), store.Current.DownloadsDay);
        Assert.Equal(1, store.Current.DownloadsOnDay);
    }

    [Fact]
    public void An_answer_that_isnt_counted_keeps_the_day_and_the_count()
    {
        using ConfigStore store = Open();
        Assert.True(store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true));

        Assert.True(store.TryRecordAnswer(new AbuseRateLimit(5, 0, null), Now.AddDays(3), countDownload: false));

        Assert.Equal(new DateOnly(2026, 10, 19), store.Current.DownloadsDay);
        Assert.Equal(1, store.Current.DownloadsOnDay);
        Assert.Equal(0, store.Current.RateLimit!.Remaining);
    }

    [Fact]
    public void An_answer_with_no_remaining_count_keeps_the_stored_limit()
    {
        using ConfigStore store = Open();
        var stored = new AbuseRateLimit(5, 3, DateTimeOffset.FromUnixTimeSeconds(1792195200));
        Assert.True(store.TryRecordAnswer(stored, Now, countDownload: true));

        Assert.True(store.TryRecordAnswer(new AbuseRateLimit(10, null, Now), Now, countDownload: true));

        Assert.Equal(stored, store.Current.RateLimit);
        Assert.Equal(2, store.Current.DownloadsOnDay);
    }

    [Fact]
    public void A_limit_with_no_limit_or_reset_is_kept_as_it_was_given()
    {
        using (ConfigStore store = Open())
            Assert.True(store.TryRecordAnswer(new AbuseRateLimit(null, 0, null), Now, countDownload: false));

        using (ConfigStore store = Open())
        {
            Assert.Equal(new AbuseRateLimit(null, 0, null), store.Current.RateLimit);
            Assert.Null(store.Current.DownloadsDay);
            Assert.Equal(0, store.Current.DownloadsOnDay);
        }
    }

    [Fact]
    public void Current_is_what_a_reopened_store_reads_in_whole_seconds()
    {
        AbuseAccounting saved;
        var reset = new DateTimeOffset(2026, 10, 20, 8, 0, 0, 750, TimeSpan.FromHours(8));
        using (ConfigStore store = Open())
        {
            Assert.True(store.TryRecordAnswer(new AbuseRateLimit(5, 4, reset), Now, countDownload: true));
            saved = store.Current;
        }

        Assert.Equal(TimeSpan.Zero, saved.RateLimit!.ResetAt!.Value.Offset);
        Assert.Equal(0, saved.RateLimit.ResetAt.Value.Millisecond);
        Assert.Equal(reset.ToUnixTimeSeconds(), saved.RateLimit.ResetAt.Value.ToUnixTimeSeconds());
        using (ConfigStore store = Open())
        {
            Assert.Equal(saved, store.Current);
            Assert.Equal(TimeSpan.Zero, store.Current.RateLimit!.ResetAt!.Value.Offset);
        }
    }

    // ----- The lock -----

    [Fact]
    public void The_lock_is_held_while_the_store_is_open_and_released_by_Dispose()
    {
        ConfigStore store = Open();
        try
        {
            using SqliteConnection other = OpenRaw(ConfigFile, timeoutSeconds: 1);
            var error = Assert.Throws<SqliteException>(() => Scalar(other, "SELECT count(*) FROM sections;"));
            Assert.Equal(5, error.SqliteErrorCode);
        }
        finally
        {
            store.Dispose();
        }

        using SqliteConnection after = OpenRaw(ConfigFile, timeoutSeconds: 1);
        Assert.Equal(3L, Scalar(after, "SELECT count(*) FROM sections;"));
    }

    [Fact]
    public void A_file_another_connection_holds_is_locked_after_about_two_seconds()
    {
        Open().Dispose();
        string hash = Sha(ConfigFile);

        var watch = Stopwatch.StartNew();
        using (SqliteConnection other = OpenRaw(ConfigFile))
        {
            Execute(other, "BEGIN EXCLUSIVE;");
            Assert.Equal(ConfigOpenState.Locked, TryOpenFails());
            watch.Stop();
            Execute(other, "ROLLBACK;");
        }

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        Assert.Equal(hash, Sha(ConfigFile));
        // The failed open let go of everything, so the next one works
        Open().Dispose();
    }

    // ----- Unreadable, Newer and KeyMismatch -----

    [Fact]
    public void A_text_file_is_unreadable_and_left_as_it_was()
    {
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
        Assert.Equal(0, _secrets.Reads);
    }

    [Theory]
    [InlineData("CREATE TABLE extra (x TEXT);")]
    [InlineData("ALTER TABLE sections ADD COLUMN extra TEXT;")]
    [InlineData("INSERT INTO metadata (key, value) VALUES ('note', x'01');")]
    [InlineData("UPDATE metadata SET value = '0' WHERE key = 'schema_version';")]
    [InlineData("UPDATE metadata SET value = 'x' WHERE key = 'schema_version';")]
    [InlineData("DELETE FROM metadata WHERE key = 'schema_version';")]
    [InlineData("UPDATE metadata SET value = upper(value) WHERE key = 'file_id';")]
    [InlineData("UPDATE metadata SET value = 'abc' WHERE key = 'file_id';")]
    [InlineData("DELETE FROM metadata WHERE key = 'file_id';")]
    [InlineData("DROP TABLE sections; CREATE TABLE sections (name TEXT PRIMARY KEY, sealed TEXT NOT NULL);")]
    [InlineData("CREATE TRIGGER undo AFTER UPDATE ON sections BEGIN SELECT 1; END;")]
    [InlineData("CREATE VIEW extra AS SELECT name FROM sections;")]
    [InlineData("CREATE INDEX extra ON sections (sealed);")]
    [InlineData("DROP TABLE metadata; CREATE TABLE other (x TEXT);")]
    [InlineData("ALTER TABLE metadata ADD COLUMN extra TEXT;")]
    // The same columns, types and keys, but a constraint every save would trip over, or a default the app never makes
    [InlineData("ALTER TABLE sections RENAME TO old; CREATE TABLE sections (name TEXT PRIMARY KEY, sealed BLOB NOT NULL CHECK (length(sealed) > 0)); INSERT INTO sections SELECT * FROM old; DROP TABLE old;")]
    [InlineData("ALTER TABLE metadata RENAME TO old; CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL DEFAULT ''); INSERT INTO metadata SELECT * FROM old; DROP TABLE old;")]
    public void A_file_that_isnt_this_applications_config_is_unreadable_and_left_as_it_was(string change)
    {
        Open().Dispose();
        Execute(ConfigFile, change);
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
        Assert.Equal(1, _secrets.Reads);   // the read-back of the first launch's key only
    }

    [Fact]
    public void A_damaged_page_is_unreadable_and_left_as_it_was()
    {
        using (ConfigStore store = Open())
            Assert.True(store.SaveSettings(new UserSettings { ExportFolder = new string('x', 9000) }));
        long rootPage;
        long pageSize;
        using (SqliteConnection conn = OpenRaw(ConfigFile))
        {
            rootPage = Scalar(conn, "SELECT rootpage FROM sqlite_master WHERE name = 'sections';");
            pageSize = Scalar(conn, "PRAGMA page_size;");
        }
        Assert.True(rootPage > 1);
        // Overwrite the sections table's root page, after Dispose, as a disk fault would
        using (var file = new FileStream(ConfigFile, FileMode.Open, FileAccess.Write))
        {
            file.Position = (rootPage - 1) * pageSize;
            file.Write(Enumerable.Repeat((byte)0xFF, 64).ToArray());
        }
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Fact]
    public void A_newer_file_is_newer_and_its_key_is_never_read()
    {
        Open().Dispose();
        Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
        string hash = Sha(ConfigFile);
        var secrets = new InMemorySecretStore { Secret = _secrets.Secret, Marker = true };

        Assert.Equal(ConfigOpenState.Newer, ConfigStore.TryOpen(_paths, new ConfigKeyStore(secrets), _time, false, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong")]
    [InlineData("short")]
    public void A_missing_wrong_or_unusable_key_is_a_mismatch_and_the_file_is_kept(string key)
    {
        Open().Dispose();
        string hash = Sha(ConfigFile);
        var secrets = key switch
        {
            "none" => new InMemorySecretStore(),
            "wrong" => new InMemorySecretStore { Secret = RandomNumberGenerator.GetBytes(32), Marker = true },
            _ => new InMemorySecretStore { Secret = RandomNumberGenerator.GetBytes(31), Marker = true },
        };

        Assert.Equal(ConfigOpenState.KeyMismatch, ConfigStore.TryOpen(_paths, new ConfigKeyStore(secrets), _time, false, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("exceptions")]
    [InlineData("accounting")]
    public void One_byte_changed_in_a_section_is_a_mismatch(string section)
    {
        Open().Dispose();
        byte[] blob = Blobs(ConfigFile)[section];
        blob[^1] ^= 0x01;
        SetBlob(ConfigFile, section, blob);
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.KeyMismatch, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Theory]
    [InlineData("DELETE FROM sections WHERE name = 'accounting';")]
    [InlineData("INSERT INTO sections (name, sealed) VALUES ('extra', x'0102');")]
    [InlineData("UPDATE sections SET name = 'swap' WHERE name = 'settings'; UPDATE sections SET name = 'settings' WHERE name = 'exceptions'; UPDATE sections SET name = 'exceptions' WHERE name = 'swap';")]
    [InlineData("UPDATE sections SET name = 'Settings' WHERE name = 'settings';")]
    [InlineData("UPDATE sections SET sealed = CAST(sealed AS TEXT) WHERE name = 'settings';")]
    public void A_section_removed_added_or_swapped_outside_the_app_is_a_mismatch(string change)
    {
        Open().Dispose();
        Execute(ConfigFile, change);
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.KeyMismatch, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Fact]
    public void A_section_copied_from_another_file_with_the_same_key_is_a_mismatch()
    {
        Open().Dispose();
        // A second file under the same key: Start fresh from Unreadable keeps a key it can read
        using var other = new TempFolder();
        var otherPaths = new AppPaths(other.Path, other.File("exports"));
        var otherSecrets = new InMemorySecretStore { Secret = _secrets.Secret, Marker = true };
        string otherFile = other.File("config.db");
        File.WriteAllText(otherFile, "these are not the bytes of a database, but they are long enough to look like a header");
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(otherPaths, new ConfigKeyStore(otherSecrets), _time,
                                                                     ConfigOpenState.Unreadable, out ConfigStore? second));
        second!.Dispose();
        Assert.Equal(_secrets.Secret, otherSecrets.Secret);
        Assert.NotEqual(Metadata(ConfigFile)["file_id"], Metadata(otherFile)["file_id"]);

        SetBlob(ConfigFile, "settings", Blobs(otherFile)["settings"]);
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.KeyMismatch, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
    }

    // ----- The key's store -----

    public static TheoryData<string> ReadFailures => ["SecretStoreException", "IOException", "UnauthorizedAccessException"];

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void A_key_that_cant_be_read_now_stops_the_open_and_keeps_the_file_and_the_key(string failure)
    {
        Open().Dispose();
        string hash = Sha(ConfigFile);
        byte[] key = [.. _secrets.Secret!];
        _secrets.ReadFailure = Failure(failure);

        Assert.Equal(ConfigOpenState.KeyUnavailable, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
        Assert.Equal(key, _secrets.Secret);
        Assert.True(_secrets.Marker);
    }

    [Fact]
    public void With_no_key_store_a_first_launch_makes_no_file()
    {
        var keys = new ConfigKeyStore(new SecretStores.UnavailableSecretStore());

        Assert.Equal(ConfigOpenState.NoKeyStore, ConfigStore.TryOpen(_paths, keys, _time, false, out ConfigStore? store));

        Assert.Null(store);
        Assert.False(File.Exists(ConfigFile));
    }

    [Fact]
    public void With_no_key_store_an_existing_file_is_kept()
    {
        Open().Dispose();
        string hash = Sha(ConfigFile);
        var keys = new ConfigKeyStore(new SecretStores.UnavailableSecretStore());

        Assert.Equal(ConfigOpenState.NoKeyStore, ConfigStore.TryOpen(_paths, keys, _time, false, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("other bytes")]
    [InlineData("write failure")]
    public void A_key_that_cant_be_saved_on_the_first_launch_makes_no_file(string fault)
    {
        switch (fault)
        {
            case "drop": _secrets.DropWrites = true; break;
            case "other bytes": _secrets.ReadsOtherBytes = true; break;
            default: _secrets.WriteFailure = new SecretStoreException("The Keychain couldn't write the secret."); break;
        }

        Assert.Equal(ConfigOpenState.KeyUnavailable, TryOpenFails());

        Assert.False(File.Exists(ConfigFile));
    }

    // ----- Storage errors -----

    [Fact]
    public void A_write_protected_file_is_a_storage_error_found_before_the_key_is_read()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        Open().Dispose();
        var secrets = new InMemorySecretStore { Secret = _secrets.Secret, Marker = true };
        string hash = Sha(ConfigFile);
        UnixFileMode mode = File.GetUnixFileMode(ConfigFile);
        File.SetUnixFileMode(ConfigFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            Assert.Equal(ConfigOpenState.StorageError,
                         ConfigStore.TryOpen(_paths, new ConfigKeyStore(secrets), _time, false, out ConfigStore? store));
            Assert.Null(store);
            Assert.Equal(0, secrets.Reads);
        }
        finally
        {
            File.SetUnixFileMode(ConfigFile, mode);
        }

        Assert.Equal(hash, Sha(ConfigFile));
    }

    [Fact]
    public void A_file_in_a_folder_that_cant_be_written_fails_at_the_real_write()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        Open().Dispose();
        string hash = Sha(ConfigFile);
        UnixFileMode mode = File.GetUnixFileMode(_temp.Path);
        File.SetUnixFileMode(_temp.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(ConfigOpenState.StorageError, TryOpenFails());
            // The checks before it passed: the key was read
            Assert.Equal(2, _secrets.Reads);
        }
        finally
        {
            File.SetUnixFileMode(_temp.Path, mode);
        }

        Assert.Equal(hash, Sha(ConfigFile));
        Open().Dispose();
    }

    // ----- Empty files and the hot journal -----

    [Theory]
    [InlineData("no file")]
    [InlineData("zero bytes")]
    [InlineData("sqlite")]
    public void A_missing_or_empty_file_with_a_settings_file_to_import_imports_it_with_a_new_key(string file)
    {
        MakeEmptyConfig(file);
        File.WriteAllText(_temp.File("settings.json"), "{ \"AbuseIpDbKeepDays\": 12 }");
        // The key an earlier launch left: a file to import still wins
        byte[] oldKey = RandomNumberGenerator.GetBytes(32);
        _secrets.Secret = [.. oldKey];
        _secrets.Marker = true;

        using (ConfigStore store = Open())
            Assert.Equal(12, store.Snapshot.Settings.AbuseIpDbKeepDays);

        Assert.NotEqual(oldKey, _secrets.Secret);
        Assert.Equal(3, Blobs(ConfigFile).Count);
        Assert.Equal("settings.json", Metadata(ConfigFile)["imported_from"]);
        Open().Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_empty_file_with_no_evidence_of_an_earlier_launch_is_created_in_place(bool sqliteFile)
    {
        MakeEmptyConfig(sqliteFile ? "sqlite" : "zero bytes");

        using (ConfigStore store = Open())
            Assert.Equal(new UserSettings(), store.Snapshot.Settings);

        Assert.Equal(32, _secrets.Secret!.Length);
        Assert.Equal(3, Blobs(ConfigFile).Count);
        Open().Dispose();
    }

    [Fact]
    public void An_empty_file_whose_key_cant_be_saved_gets_no_tables()
    {
        Execute(ConfigFile, "CREATE TABLE gone (x); DROP TABLE gone;", create: true);
        _secrets.WriteFailure = new SecretStoreException("The Keychain couldn't write the secret.");

        Assert.Equal(ConfigOpenState.KeyUnavailable, TryOpenFails());

        Assert.Empty(Strings(ConfigFile, "SELECT name FROM sqlite_master;"));
    }

    [Fact]
    public void A_hot_journal_is_rolled_back_at_the_open()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The copy of a locked file uses cp here.");
            return;
        }

        using (ConfigStore saving = Open())
        {
            Assert.True(saving.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
            Assert.True(saving.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")])));
        }
        string before = Sha(ConfigFile);
        using var other = new TempFolder();
        string copy = other.File("config.db");
        using (SqliteConnection conn = OpenRaw(ConfigFile))
        {
            Execute(conn, "PRAGMA cache_size = 1;");
            using SqliteTransaction tx = conn.BeginTransaction();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE sections SET sealed = randomblob(2000000) WHERE name = 'settings';";
                cmd.ExecuteNonQuery();
            }
            // File.Copy's own lock conflicts with SQLite's on macOS, so copy as a crash would leave the files
            Run("/bin/cp", ConfigFile, copy);
            Run("/bin/cp", ConfigFile + "-journal", copy + "-journal");
            tx.Rollback();
        }
        Assert.NotEqual(before, Sha(copy));

        var otherPaths = new AppPaths(other.Path, other.File("exports"));
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(otherPaths, _keys, _time, false, out ConfigStore? store));
        using (store)
        {
            Assert.Equal(12, store!.Snapshot.Settings.AbuseIpDbKeepDays);
            Assert.Equal("Office", Assert.Single(store.Snapshot.Exceptions.Entries).Note);
        }
        Assert.Equal(before, Sha(ConfigFile));
    }

    // ----- Start fresh -----

    [Fact]
    public void Start_fresh_from_newer_keeps_a_key_it_can_read_and_a_copy_of_the_old_file()
    {
        using (ConfigStore saving = Open())
            Assert.True(saving.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        byte[] key = [.. _secrets.Secret!];
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.Newer, failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        using (store)
        {
            Assert.NotNull(store);
            Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, store.Snapshot.Settings);
            Assert.Empty(store.Snapshot.Exceptions.Entries);
            Assert.Equal(AbuseAccounting.Empty, store.Current);
        }

        Assert.Equal(key, _secrets.Secret);
        Assert.Equal(oldFile, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Assert.Equal("", Metadata(ConfigFile)["imported_from"]);
        Assert.Equal("1", Metadata(ConfigFile)["schema_version"]);
        Open().Dispose();
    }

    [Fact]
    public void Start_fresh_from_a_mismatch_keeps_a_key_that_reads_though_it_opened_nothing()
    {
        Open().Dispose();
        byte[] wrong = RandomNumberGenerator.GetBytes(32);
        _secrets.Secret = [.. wrong];
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.KeyMismatch, failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(wrong, _secrets.Secret);
        Assert.True(File.Exists(_temp.File("config.unreadable.db")));
        // The new file is sealed under the key that was kept
        Open().Dispose();
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("exceptions")]
    [InlineData("accounting")]
    public void Start_fresh_after_one_byte_changed_keeps_the_key_so_the_copy_opens_once_put_back(string section)
    {
        using (ConfigStore saving = Open())
            Assert.True(saving.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        byte[] key = [.. _secrets.Secret!];
        byte[] blob = Blobs(ConfigFile)[section];
        blob[^1] ^= 0x01;
        SetBlob(ConfigFile, section, blob);
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.KeyMismatch, failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        store!.Dispose();
        Assert.Equal(key, _secrets.Secret);

        // The copy, with its byte put back, opens with the key that was kept
        using var other = new TempFolder();
        string copied = other.File("config.db");
        File.Copy(_temp.File("config.unreadable.db"), copied);
        blob[^1] ^= 0x01;
        SetBlob(copied, section, blob);
        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.TryOpen(new AppPaths(other.Path, other.File("exports")), _keys, _time, false, out ConfigStore? recovered));
        using (recovered)
            Assert.Equal(12, recovered!.Snapshot.Settings.AbuseIpDbKeepDays);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("short")]
    public void Start_fresh_from_a_mismatch_with_no_key_or_one_that_can_never_be_read_saves_a_new_one(string key)
    {
        Open().Dispose();
        byte[]? before = key == "none" ? null : RandomNumberGenerator.GetBytes(31);
        _secrets.Secret = before is null ? null : [.. before];
        _secrets.Marker = before is not null;
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.KeyMismatch, failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(32, _secrets.Secret!.Length);
        if (before is not null)
            Assert.NotEqual(before, _secrets.Secret);
        Assert.True(_secrets.Marker);
        Assert.True(File.Exists(_temp.File("config.unreadable.db")));
        Open().Dispose();
    }

    [Fact]
    public void Start_fresh_from_unreadable_with_no_key_saves_a_new_one()
    {
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.Unreadable, failed);

        // Still unreadable when it is opened again, so it is replaced, with its copy kept
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(32, _secrets.Secret!.Length);
        Assert.Equal(oldFile, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Open().Dispose();
    }

    [Fact]
    public void Start_fresh_with_a_key_that_cant_be_read_now_touches_nothing()
    {
        Open().Dispose();
        Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
        string hash = Sha(ConfigFile);
        _secrets.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        Assert.Equal(ConfigOpenState.KeyUnavailable,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Newer, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, Sha(ConfigFile));
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
    }

    [Fact]
    public void Start_fresh_whose_new_key_cant_be_saved_touches_nothing()
    {
        Open().Dispose();
        // A key that can never be read, so it must be replaced
        byte[] unusable = RandomNumberGenerator.GetBytes(31);
        _secrets.Secret = [.. unusable];
        Assert.Equal(ConfigOpenState.KeyMismatch, TryOpenFails());
        string hash = Sha(ConfigFile);
        _secrets.WriteFailure = new SecretStoreException("The Keychain couldn't write the secret.");

        Assert.Equal(ConfigOpenState.KeyUnavailable,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.KeyMismatch, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, Sha(ConfigFile));
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
        Assert.Equal(unusable, _secrets.Secret);

        _secrets.WriteFailure = null;
        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.KeyMismatch, out ConfigStore? fresh));
        fresh!.Dispose();
        Open().Dispose();
    }

    public static TheoryData<string, string> FreshKeyFaults => new()
    {
        { "Newer", "read" }, { "Newer", "none" }, { "Newer", "unusable" },
        { "Unreadable", "read" }, { "Unreadable", "none" }, { "Unreadable", "unusable" },
        { "KeyMismatch", "read" }, { "KeyMismatch", "none" }, { "KeyMismatch", "unusable" },
        { "Missing", "read" }, { "Missing", "none" }, { "Missing", "unusable" },
    };

    [Theory]
    [MemberData(nameof(FreshKeyFaults))]
    public void Start_fresh_settles_the_key_before_it_touches_a_file(string state, string fault)
    {
        var failed = Enum.Parse<ConfigOpenState>(state);
        MakeFailedConfig(failed);
        byte[] olderCopy = Encoding.UTF8.GetBytes("an older copy");
        File.WriteAllBytes(_temp.File("config.unreadable.db"), olderCopy);
        string? hash = File.Exists(ConfigFile) ? Sha(ConfigFile) : null;
        if (fault == "read")
        {
            _secrets.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");
        }
        else
        {
            // No key, or one that can never be read, so a new one must be saved, and it can't be
            _secrets.Secret = fault == "none" ? null : RandomNumberGenerator.GetBytes(31);
            _secrets.Marker = fault != "none";
            _secrets.WriteFailure = new SecretStoreException("The Keychain couldn't write the secret.");
        }

        Assert.Equal(ConfigOpenState.KeyUnavailable, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, File.Exists(ConfigFile) ? Sha(ConfigFile) : null);
        Assert.Equal(olderCopy, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public void Start_fresh_from_missing_keeps_the_key_that_reads_and_makes_no_copy()
    {
        Open().Dispose();
        File.Delete(ConfigFile);
        byte[] key = [.. _secrets.Secret!];
        byte[] marker = File.ReadAllBytes(MarkerFile);
        DateTime markerWritten = File.GetLastWriteTimeUtc(MarkerFile);
        ConfigOpenState failed = TryOpenFails();
        Assert.Equal(ConfigOpenState.Missing, failed);

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));
        using (store)
            Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, store!.Snapshot.Settings);

        Assert.Equal(key, _secrets.Secret);
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
        // Start fresh never deletes the marker, nor writes it again
        Assert.Equal(marker, File.ReadAllBytes(MarkerFile));
        Assert.Equal(markerWritten, File.GetLastWriteTimeUtc(MarkerFile));
        Open().Dispose();
    }

    [Fact]
    public void Start_fresh_from_an_empty_missing_file_replaces_it_and_makes_no_copy()
    {
        Open().Dispose();
        File.Delete(ConfigFile);
        MakeEmptyConfig("sqlite");
        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Missing, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(3, Blobs(ConfigFile).Count);
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
    }

    [Fact]
    public void Start_fresh_keeps_a_journal_with_its_copy_and_replaces_an_older_copy()
    {
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        // A first byte of zero isn't a hot journal, so neither the launch's open nor Start fresh's rolls it back or
        // removes it (a hot one would be rolled back at the launch, before Start fresh is offered)
        byte[] journal = [0, 2, 3, 4];
        File.WriteAllBytes(ConfigFile + "-journal", journal);
        File.WriteAllText(_temp.File("config.unreadable.db"), "an older copy");
        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());
        Assert.Equal(journal, File.ReadAllBytes(ConfigFile + "-journal"));

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(oldFile, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Assert.Equal(journal, File.ReadAllBytes(_temp.File("config.unreadable.db-journal")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
        Assert.False(File.Exists(ConfigFile + "-journal"));
        Open().Dispose();
    }

    [Fact]
    public void Start_fresh_with_no_journal_removes_an_older_copys_journal()
    {
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        File.WriteAllText(_temp.File("config.unreadable.db"), "an older copy");
        File.WriteAllBytes(_temp.File("config.unreadable.db-journal"), [1, 2, 3, 4]);

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));
        store!.Dispose();

        Assert.False(File.Exists(_temp.File("config.unreadable.db-journal")));
    }

    // The older copy's journal goes before the new copy is moved in, also when there is a journal now. The move of
    // the copy is made to fail (a folder is in its way), so the order shows: the older journal is already gone.
    [Fact]
    public void Start_fresh_deletes_an_older_copys_journal_before_it_moves_the_new_copy_in()
    {
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        byte[] journal = [0, 2, 3, 4];
        File.WriteAllBytes(ConfigFile + "-journal", journal);
        Directory.CreateDirectory(_temp.File("config.unreadable.db"));
        File.WriteAllBytes(_temp.File("config.unreadable.db-journal"), [1, 2, 3, 4]);

        Assert.Equal(ConfigOpenState.Unreadable,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));
        Assert.Null(store);

        Assert.False(File.Exists(_temp.File("config.unreadable.db-journal")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
        Assert.Equal(oldFile, File.ReadAllBytes(ConfigFile));
        Assert.Equal(journal, File.ReadAllBytes(ConfigFile + "-journal"));
    }

    [Fact]
    public void Start_fresh_that_cant_delete_the_file_returns_the_failure_and_leaves_it_as_it_was()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The folder's access list is set through chmod +a here.");
            return;
        }

        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        DenyDeletingTheFilesThere();
        try
        {
            Assert.Equal(ConfigOpenState.Unreadable,
                         ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));
            Assert.Null(store);
        }
        finally
        {
            RemoveAccessLists();
        }

        Assert.Equal(oldFile, File.ReadAllBytes(ConfigFile));
        Assert.Equal(oldFile, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));

        // Nothing left behind stops the next press
        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? fresh));
        fresh!.Dispose();
    }

    [Fact]
    public void Start_fresh_that_cant_delete_a_journal_is_a_storage_error_and_makes_no_new_file()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The folder's access list is set through chmod +a here.");
            return;
        }

        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] oldFile = File.ReadAllBytes(ConfigFile);
        // Not hot, so neither open rolls it back or removes it
        byte[] journal = [0, 2, 3, 4];
        string journalFile = ConfigFile + "-journal";
        File.WriteAllBytes(journalFile, journal);
        DenyDeletingTheFilesThere();
        Run("/bin/chmod", "+a", $"user:{Environment.UserName} allow delete", ConfigFile);
        try
        {
            Assert.Equal(ConfigOpenState.StorageError,
                         ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));
            Assert.Null(store);
        }
        finally
        {
            RemoveAccessLists();
        }

        Assert.False(File.Exists(ConfigFile));
        Assert.Equal(journal, File.ReadAllBytes(journalFile));
        Assert.Equal(oldFile, File.ReadAllBytes(_temp.File("config.unreadable.db")));
        Assert.Equal(journal, File.ReadAllBytes(_temp.File("config.unreadable.db-journal")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("Locked")]
    [InlineData("StorageError")]
    [InlineData("KeyUnavailable")]
    [InlineData("NoKeyStore")]
    [InlineData("ExceptionsImportUnreadable")]
    [InlineData("Opened")]
    public void Start_fresh_from_any_other_result_returns_it_and_changes_nothing(string result)
    {
        var failed = Enum.Parse<ConfigOpenState>(result);
        Open().Dispose();
        string hash = Sha(ConfigFile);
        byte[] key = [.. _secrets.Secret!];
        int reads = _secrets.Reads;

        Assert.Equal(failed, ConfigStore.StartFresh(_paths, _keys, _time, failed, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(hash, Sha(ConfigFile));
        Assert.Equal(key, _secrets.Secret);
        Assert.Equal(reads, _secrets.Reads);
        Assert.Equal(["config.db", "config_made"],
                     Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ----- Gone since an earlier launch -----

    [Fact]
    public void A_file_deleted_after_a_launch_is_missing_and_nothing_is_made()
    {
        Open().Dispose();
        File.Delete(ConfigFile);
        byte[] key = [.. _secrets.Secret!];

        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.False(File.Exists(ConfigFile));
        Assert.True(File.Exists(MarkerFile));
        Assert.Equal(key, _secrets.Secret);
        Assert.True(_secrets.Marker);
        Assert.Equal(1, _secrets.Reads);   // the read-back of the first launch's key only
    }

    [Theory]
    [InlineData("settings.json.migrated")]
    [InlineData("exceptions.json.migrated")]
    [InlineData("settings.json.migrated.2")]
    public void A_migrated_file_alone_shows_an_earlier_launch(string evidence)
    {
        File.WriteAllText(_temp.File(evidence), "{}");

        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.False(File.Exists(ConfigFile));
        Assert.False(_secrets.Marker);
        Assert.Null(_secrets.Secret);
    }

    [Theory]
    [InlineData("no file")]
    [InlineData("zero bytes")]
    [InlineData("sqlite")]
    public void An_absent_or_empty_file_with_only_the_key_of_an_earlier_launch_is_created(string file)
    {
        // The key is saved before the file is made, so a first launch that failed after saving it left only the key
        MakeEmptyConfig(file);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        _secrets.Secret = [.. key];
        _secrets.Marker = true;

        using (ConfigStore store = Open())
            Assert.Equal(new UserSettings(), store.Snapshot.Settings);

        Assert.NotEqual(key, _secrets.Secret);
        Assert.Equal(3, Blobs(ConfigFile).Count);
        Assert.True(File.Exists(MarkerFile));
        Open().Dispose();
    }

    [Theory]
    [InlineData("no file")]
    [InlineData("zero bytes")]
    [InlineData("sqlite")]
    public void An_absent_or_empty_file_with_the_marker_is_missing_and_stays_empty(string file)
    {
        MakeEmptyConfig(file);
        string? hash = File.Exists(ConfigFile) ? Sha(ConfigFile) : null;
        File.WriteAllText(MarkerFile, "made");

        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.Equal(file != "no file", File.Exists(ConfigFile));
        // Taking the lock on a zero-byte file writes SQLite's empty first page, so only a SQLite file keeps its bytes
        if (file == "sqlite")
            Assert.Equal(hash, Sha(ConfigFile));
        if (file != "no file")
            Assert.Empty(Strings(ConfigFile, "SELECT name FROM sqlite_master;"));
        Assert.Null(_secrets.Secret);
        Assert.False(_secrets.Marker);
        Assert.Equal(0, _secrets.Reads);
        Assert.Equal("made", File.ReadAllText(MarkerFile));
    }

    [Theory]
    [InlineData("config_made")]
    [InlineData("exceptions.json.migrated")]
    public void A_settings_file_beside_evidence_of_an_earlier_launch_is_missing_and_left_as_it_is(string evidence)
    {
        File.WriteAllText(_temp.File(evidence), "{}");
        byte[] settings = Encoding.UTF8.GetBytes("{ \"AbuseIpDbKeepDays\": 12 }");
        File.WriteAllBytes(_temp.File("settings.json"), settings);

        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.False(File.Exists(ConfigFile));
        Assert.Equal(settings, File.ReadAllBytes(_temp.File("settings.json")));
        Assert.False(File.Exists(_temp.File("settings.json.migrated")));
        Assert.Null(_secrets.Secret);
    }

    [Fact]
    public void An_exceptions_file_beside_the_marker_is_missing_and_nothing_is_imported()
    {
        Open().Dispose();
        File.Delete(ConfigFile);
        byte[] key = [.. _secrets.Secret!];
        byte[] exceptions = Encoding.UTF8.GetBytes("""{ "schemaVersion": 1, "exceptions": [ { "network": "192.0.2.0/24" } ] }""");
        File.WriteAllBytes(_temp.File("exceptions.json"), exceptions);

        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.False(File.Exists(ConfigFile));
        Assert.Equal(exceptions, File.ReadAllBytes(_temp.File("exceptions.json")));
        Assert.False(File.Exists(_temp.File("exceptions.json.migrated")));
        Assert.Equal(key, _secrets.Secret);
    }

    [Fact]
    public void An_upgrade_whose_first_launch_failed_after_saving_the_key_imports_the_settings()
    {
        // As after 1.0.0: only settings.json. The first launch saves the key, then can't open the file (a folder in its
        // place), so the key is all it leaves
        File.WriteAllText(_temp.File("settings.json"), "{ \"AbuseIpDbKeepDays\": 12 }");
        Directory.CreateDirectory(ConfigFile);
        Assert.Equal(ConfigOpenState.StorageError, TryOpenFails());
        Assert.True(_secrets.Marker);
        Assert.False(File.Exists(MarkerFile));
        Directory.Delete(ConfigFile);

        using (ConfigStore store = Open())
            Assert.Equal(12, store.Snapshot.Settings.AbuseIpDbKeepDays);

        Assert.Equal("settings.json", Metadata(ConfigFile)["imported_from"]);
        Assert.True(File.Exists(_temp.File("settings.json.migrated")));
    }

    [Fact]
    public void A_first_launch_with_no_evidence_of_an_earlier_one_opens_with_the_defaults()
    {
        // Neither shows that config.db was made: a copy kept by a Start fresh, or exceptions set aside. Both show an
        // earlier install, so FortiGate feeds stay on.
        File.WriteAllText(_temp.File("config.unreadable.db"), "an older copy");
        File.WriteAllText(_temp.File("exceptions.unreadable.json"), "{ not json");

        using (ConfigStore store = Open())
        {
            Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, store.Snapshot.Settings);
            Assert.Empty(store.Snapshot.Exceptions.Entries);
        }
    }

    // ----- The marker that config.db was made -----

    [Fact]
    public void The_first_open_writes_the_marker_once()
    {
        Open().Dispose();

        string text = File.ReadAllText(MarkerFile);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.All(text, c => Assert.True(c < 128));
        Assert.Contains("config.db", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public void A_marker_that_is_there_is_never_written_again()
    {
        Open().Dispose();
        File.WriteAllText(MarkerFile, "an older marker");
        var written = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(MarkerFile, written);

        Open().Dispose();

        Assert.Equal("an older marker", File.ReadAllText(MarkerFile));
        Assert.Equal(written, File.GetLastWriteTimeUtc(MarkerFile));
    }

    [Fact]
    public void A_marker_that_cant_be_written_doesnt_stop_the_open()
    {
        // A folder where the marker's .tmp file goes: the write fails
        Directory.CreateDirectory(MarkerFile + ".tmp");

        using (ConfigStore store = Open())
            Assert.Equal(new UserSettings(), store.Snapshot.Settings);

        Assert.False(File.Exists(MarkerFile));
        Directory.Delete(MarkerFile + ".tmp");
        Open().Dispose();
        Assert.True(File.Exists(MarkerFile));
    }

    // ----- Start fresh opens the file again first -----

    [Fact]
    public void Start_fresh_from_missing_opens_a_file_put_back_since_and_replaces_nothing()
    {
        using (ConfigStore saving = Open())
            Assert.True(saving.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        string aside = _temp.File("aside.db");
        File.Move(ConfigFile, aside);
        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());
        File.Move(aside, ConfigFile);
        Dictionary<string, byte[]> blobs = Blobs(ConfigFile);
        Dictionary<string, string> meta = Metadata(ConfigFile);
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Missing, out ConfigStore? store));
        using (store)
            Assert.Equal(12, store!.Snapshot.Settings.AbuseIpDbKeepDays);

        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
        Assert.Equal(blobs, Blobs(ConfigFile));
        // Only the open's own stamp changed
        Dictionary<string, string> after = Metadata(ConfigFile);
        Assert.Equal(_time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), after["opened_utc"]);
        meta.Remove("opened_utc");
        after.Remove("opened_utc");
        Assert.Equal(meta, after);
    }

    [Fact]
    public void Start_fresh_from_missing_with_an_unreadable_file_put_back_gives_unreadable_and_touches_nothing()
    {
        Open().Dispose();
        File.Delete(ConfigFile);
        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        byte[] put = File.ReadAllBytes(ConfigFile);

        Assert.Equal(ConfigOpenState.Unreadable,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Missing, out ConfigStore? store));

        Assert.Null(store);
        Assert.Equal(put, File.ReadAllBytes(ConfigFile));
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
    }

    [Fact]
    public void Start_fresh_from_unreadable_after_the_file_was_deleted_gives_missing_and_makes_nothing()
    {
        Open().Dispose();
        File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());
        File.Delete(ConfigFile);
        byte[] key = [.. _secrets.Secret!];

        Assert.Equal(ConfigOpenState.Missing,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Unreadable, out ConfigStore? store));

        Assert.Null(store);
        Assert.False(File.Exists(ConfigFile));
        Assert.False(File.Exists(_temp.File("config.unreadable.db")));
        Assert.Equal(key, _secrets.Secret);
    }

    [Fact]
    public void Start_fresh_from_an_empty_missing_file_keeps_an_earlier_copy_as_it_is()
    {
        // The copy an earlier Start fresh kept, then config.db left empty (as by a Start fresh that died while it made the file)
        Open().Dispose();
        File.Copy(ConfigFile, _temp.File("config.unreadable.db"));
        byte[] copy = File.ReadAllBytes(_temp.File("config.unreadable.db"));
        File.Delete(ConfigFile);
        MakeEmptyConfig("sqlite");
        Assert.Equal(ConfigOpenState.Missing, TryOpenFails());

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Missing, out ConfigStore? store));
        store!.Dispose();

        Assert.Equal(3, Blobs(ConfigFile).Count);
        Assert.Equal(copy, File.ReadAllBytes(_temp.File("config.unreadable.db")));
    }

    // ----- The journal mode -----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_this_build_made_is_left_in_rollback_journal_mode(bool emptyInWal)
    {
        if (emptyInWal)
        {
            // An empty file in WAL mode is made in place, and the open sets the mode first
            using (SqliteConnection conn = OpenRaw(ConfigFile, create: true))
            {
                Execute(conn, "PRAGMA journal_mode = WAL;");
                Execute(conn, "CREATE TABLE gone (x); DROP TABLE gone;");
            }
            Assert.Equal(2, File.ReadAllBytes(ConfigFile)[18]);
        }

        Open().Dispose();

        byte[] header = File.ReadAllBytes(ConfigFile)[..20];
        // The header's read and write versions: 1 is the rollback journal, 2 is WAL
        Assert.Equal(1, header[18]);
        Assert.Equal(1, header[19]);
        Assert.Equal(3, Blobs(ConfigFile).Count);
    }

    [Fact]
    public void A_usable_file_switched_to_WAL_outside_the_app_opens_and_is_switched_back()
    {
        using (ConfigStore saving = Open())
            Assert.True(saving.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        using (SqliteConnection conn = OpenRaw(ConfigFile))
            Execute(conn, "PRAGMA journal_mode = WAL;");
        Assert.False(File.Exists(ConfigFile + "-wal"));
        Assert.Equal(2, File.ReadAllBytes(ConfigFile)[18]);

        using (ConfigStore store = Open())
            Assert.Equal(12, store.Snapshot.Settings.AbuseIpDbKeepDays);

        byte[] header = File.ReadAllBytes(ConfigFile)[..20];
        Assert.Equal(1, header[18]);
        Assert.Equal(1, header[19]);
        Assert.False(File.Exists(ConfigFile + "-wal"));
    }

    // ----- What the classification reads first -----

    [Fact]
    public void A_fresh_file_holds_exactly_the_two_tables_and_their_indexes()
    {
        Open().Dispose();

        Assert.Equal(
            [
                "index|sqlite_autoindex_metadata_1|metadata",
                "index|sqlite_autoindex_sections_1|sections",
                "table|metadata|metadata",
                "table|sections|sections",
            ],
            Strings(ConfigFile, "SELECT type || '|' || name || '|' || tbl_name FROM sqlite_master ORDER BY 1;"));
    }

    [Theory]
    [InlineData("CREATE TABLE extra (x TEXT);")]
    [InlineData("ALTER TABLE sections ADD COLUMN extra TEXT;")]
    [InlineData("CREATE TRIGGER undo AFTER UPDATE ON sections BEGIN SELECT 1; END;")]
    [InlineData("DROP TABLE sections;")]
    public void A_newer_file_is_newer_whatever_else_it_holds(string change)
    {
        Open().Dispose();
        Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version'; " + change);
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.Newer, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
        Assert.Equal(1, _secrets.Reads);   // the read-back of the first launch's key only
    }

    [Fact]
    public void A_newer_file_in_WAL_mode_keeps_its_bytes()
    {
        Open().Dispose();
        using (SqliteConnection conn = OpenRaw(ConfigFile))
        {
            Execute(conn, "PRAGMA journal_mode = WAL;");
            Execute(conn, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
        }
        Assert.False(File.Exists(ConfigFile + "-wal"));
        byte[] before = File.ReadAllBytes(ConfigFile);
        // The header's read and write versions: 2 is WAL
        Assert.Equal(2, before[18]);

        Assert.Equal(ConfigOpenState.Newer, TryOpenFails());

        Assert.Equal(before, File.ReadAllBytes(ConfigFile));
    }

    [Fact]
    public void A_damaged_index_page_no_query_reads_is_found_by_the_page_check()
    {
        Open().Dispose();
        long rootPage;
        long pageSize;
        using (SqliteConnection conn = OpenRaw(ConfigFile))
        {
            rootPage = Scalar(conn, "SELECT rootpage FROM sqlite_master WHERE name = 'sqlite_autoindex_sections_1';");
            pageSize = Scalar(conn, "PRAGMA page_size;");
        }
        Assert.True(rootPage > 1);
        // Overwrite the sections index's page, after Dispose: the open's reads all scan the tables, never this page
        using (var file = new FileStream(ConfigFile, FileMode.Open, FileAccess.Write))
        {
            file.Position = (rootPage - 1) * pageSize;
            file.Write(Enumerable.Repeat((byte)0xFF, 64).ToArray());
        }
        string hash = Sha(ConfigFile);

        Assert.Equal(ConfigOpenState.Unreadable, TryOpenFails());

        Assert.Equal(hash, Sha(ConfigFile));
    }

    // ----- Whether the accounting is known -----

    [Fact]
    public void The_store_is_available_until_it_is_disposed()
    {
        ConfigStore store = Open();
        Assert.True(store.IsAvailable);

        store.Dispose();

        Assert.False(store.IsAvailable);
    }

    // ----- The ModSecurity rule ids -----

    private const string RuleIdsKey = "modsecurity_rule_ids";

    [Fact]
    public void A_new_file_has_no_rule_id_key_and_an_empty_map()
    {
        using (ConfigStore store = Open())
            Assert.Same(ModSecurityRuleIds.Empty, store.Snapshot.RuleIds);

        Assert.False(Metadata(ConfigFile).ContainsKey(RuleIdsKey));
        using (ConfigStore store = Open())
            Assert.Same(ModSecurityRuleIds.Empty, store.Snapshot.RuleIds);
    }

    [Fact]
    public void Assigned_rule_ids_are_saved_and_a_reopen_keeps_them_and_continues_upward()
    {
        using (ConfigStore store = Open())
        {
            ModSecurityRuleIdsResult result = store.AssignRuleIds(["AS1_ipv4.txt", "AS1_ipv6.txt"], 10_000);
            Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
            Assert.Equal([10_000, 10_001], result.RuleIds);
            Assert.Equal(2, store.Snapshot.RuleIds.Count);
        }

        Assert.Equal(["text"], Strings(ConfigFile, $"SELECT typeof(value) FROM metadata WHERE key = '{RuleIdsKey}';"));
        using (ConfigSeal seal = TestSeal())
        {
            Assert.True(seal.TryOpen(ConfigSection.ModSecurityRuleIds, ConfigStore.SchemaVersion,
                                     Convert.FromBase64String(Metadata(ConfigFile)[RuleIdsKey]), out byte[]? json));
            Assert.Equal("""{"version":1,"ruleIds":[{"file":"AS1_ipv4.txt","id":10000},{"file":"AS1_ipv6.txt","id":10001}]}""",
                         Encoding.UTF8.GetString(json));
        }

        using (ConfigStore store = Open())
        {
            Assert.Equal(2, store.Snapshot.RuleIds.Count);
            ModSecurityRuleIdsResult result = store.AssignRuleIds(["AS1_ipv6.txt", "AS2_ipv4.txt", "AS1_ipv4.txt"], 500);
            Assert.Equal([10_001, 10_002, 10_000], result.RuleIds);
        }

        using (ConfigStore store = Open())
            Assert.Equal([10_002], store.AssignRuleIds(["AS2_ipv4.txt"], 1).RuleIds);
    }

    [Fact]
    public void A_call_with_no_new_name_writes_nothing()
    {
        using (ConfigStore store = Open())
            Assert.Equal(ModSecurityRuleIdsFailure.None, store.AssignRuleIds(["a.txt", "b.txt"], 10).Failure);
        string value = Metadata(ConfigFile)[RuleIdsKey];

        using (ConfigStore store = Open())
        {
            ModSecurityRuleIds map = store.Snapshot.RuleIds;
            // Any write would fail here
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            Assert.Equal([11, 10, 11], store.AssignRuleIds(["b.txt", "a.txt", "b.txt"], 500).RuleIds);
            ModSecurityRuleIdsResult empty = store.AssignRuleIds([], 500);
            Assert.Equal(ModSecurityRuleIdsFailure.None, empty.Failure);
            Assert.Empty(empty.RuleIds);
            Assert.Same(map, store.Snapshot.RuleIds);
        }

        Assert.Equal(value, Metadata(ConfigFile)[RuleIdsKey]);
    }

    [Fact]
    public void A_failed_rule_id_write_is_a_storage_error_and_keeps_the_snapshot_and_the_file()
    {
        using (ConfigStore store = Open())
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        string value = Metadata(ConfigFile)[RuleIdsKey];

        using (ConfigStore store = Open())
        {
            ConfigSnapshot snapshot = store.Snapshot;
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            ModSecurityRuleIdsResult result = store.AssignRuleIds(["a.txt", "b.txt"], 10);

            Assert.Equal(ModSecurityRuleIdsFailure.StorageError, result.Failure);
            Assert.Empty(result.RuleIds);
            Assert.Same(snapshot, store.Snapshot);
        }

        Assert.Equal(value, Metadata(ConfigFile)[RuleIdsKey]);
        using (ConfigStore store = Open())
        {
            Assert.Equal(1, store.Snapshot.RuleIds.Count);
            Assert.False(store.Snapshot.RuleIds.TryGet("b.txt", out _));
        }
    }

    [Fact]
    public void A_closed_store_gives_a_storage_error_after_the_argument_checks()
    {
        ConfigStore store = Open();
        ConfigSnapshot snapshot = store.Snapshot;
        store.Dispose();

        ModSecurityRuleIdsResult result = store.AssignRuleIds(["a.txt"], 10);

        Assert.Equal(ModSecurityRuleIdsFailure.StorageError, result.Failure);
        Assert.Empty(result.RuleIds);
        Assert.Same(snapshot, store.Snapshot);
        Assert.Throws<ArgumentNullException>(() => store.AssignRuleIds(null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AssignRuleIds(["a.txt"], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AssignRuleIds(["a.txt"], ModSecurityLimits.RuleIdMax + 1));
    }

    [Fact]
    public void Section_saves_leave_the_rule_ids_and_a_rule_id_save_leaves_the_rows()
    {
        using (ConfigStore store = Open())
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        string value = Metadata(ConfigFile)[RuleIdsKey];

        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(new UserSettings { IPv6Enabled = false }));
            Assert.True(store.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")])));
            Assert.True(store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true));
        }
        Assert.Equal(value, Metadata(ConfigFile)[RuleIdsKey]);
        Dictionary<string, byte[]> rows = Blobs(ConfigFile);

        using (ConfigStore store = Open())
            Assert.Equal([11], store.AssignRuleIds(["b.txt"], 10).RuleIds);

        Assert.NotEqual(value, Metadata(ConfigFile)[RuleIdsKey]);
        Dictionary<string, byte[]> after = Blobs(ConfigFile);
        Assert.Equal(rows.Keys.Order(), after.Keys.Order());
        foreach (string name in rows.Keys)
            Assert.Equal(rows[name], after[name]);
    }

    [Fact]
    public void The_snapshot_carries_the_rule_ids_across_a_section_save()
    {
        using ConfigStore store = Open();
        Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        Assert.True(store.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 7 }));
        store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

        ModSecurityRuleIdsResult result = store.AssignRuleIds(["a.txt"], 500);

        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.Equal([10], result.RuleIds);
        Assert.Equal(7, store.Snapshot.Settings.AbuseIpDbKeepDays);
    }

    [Theory]
    [InlineData("another file")]
    [InlineData("a section's blob")]
    [InlineData("not base64")]
    [InlineData("one byte changed")]
    [InlineData("a higher version")]
    [InlineData("an unknown property")]
    [InlineData("empty")]
    [InlineData("shorter than a seal's header")]
    public void Rule_ids_this_build_cant_use_leave_the_launch_going_and_are_never_rewritten(string change)
    {
        var settings = new UserSettings { AbuseIpDbKeepDays = 45, IPv6Enabled = false };
        ExceptionList exceptions = ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")]);
        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(settings));
            Assert.True(store.SaveExceptions(exceptions));
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        }
        SetMetadataValue(ConfigFile, RuleIdsKey, UnusableRuleIds(change));
        string value = Metadata(ConfigFile)[RuleIdsKey];

        using (ConfigStore store = Open())
        {
            Assert.Equal(settings, store.Snapshot.Settings);
            Assert.Equal(exceptions.Entries, store.Snapshot.Exceptions.Entries);
            Assert.Same(ModSecurityRuleIds.Unavailable, store.Snapshot.RuleIds);

            ModSecurityRuleIdsResult result = store.AssignRuleIds(["a.txt", "b.txt"], 10);
            Assert.Equal(ModSecurityRuleIdsFailure.Unavailable, result.Failure);
            Assert.Empty(result.RuleIds);
            Assert.True(store.SaveSettings(settings with { AbuseIpDbKeepDays = 46 }));
        }
        Assert.Equal(value, Metadata(ConfigFile)[RuleIdsKey]);

        using (ConfigStore store = Open())
        {
            Assert.Equal(46, store.Snapshot.Settings.AbuseIpDbKeepDays);
            Assert.Same(ModSecurityRuleIds.Unavailable, store.Snapshot.RuleIds);
        }
        Assert.Equal(value, Metadata(ConfigFile)[RuleIdsKey]);
    }

    [Theory]
    [InlineData("KeyMismatch")]
    [InlineData("Newer")]
    public void Start_fresh_forgets_the_rule_ids(string failed)
    {
        ConfigOpenState state = Enum.Parse<ConfigOpenState>(failed);
        using (ConfigStore first = Open())
            Assert.Equal([10], first.AssignRuleIds(["a.txt"], 10).RuleIds);
        if (state == ConfigOpenState.Newer)
        {
            Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
        }
        else
        {
            byte[] blob = Blobs(ConfigFile)["settings"];
            blob[^1] ^= 0x01;
            SetBlob(ConfigFile, "settings", blob);
        }
        Assert.Equal(state, TryOpenFails());

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.StartFresh(_paths, _keys, _time, state, out ConfigStore? store));
        using (store)
        {
            Assert.Same(ModSecurityRuleIds.Empty, store!.Snapshot.RuleIds);
            Assert.Equal([10_000], store.AssignRuleIds(["b.txt"], 10_000).RuleIds);
        }
        Assert.True(Metadata(ConfigFile).ContainsKey(RuleIdsKey));
        Assert.True(Metadata(_temp.File("config.unreadable.db")).ContainsKey(RuleIdsKey));
    }

    [Fact]
    public void A_file_with_rule_ids_is_still_current_to_this_builds_checks()
    {
        using (ConfigStore store = Open())
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        Assert.Equal("1", Metadata(ConfigFile)["schema_version"]);
        Assert.Equal(["accounting", "exceptions", "settings"], Strings(ConfigFile, "SELECT name FROM sections ORDER BY name;"));

        using (ConfigStore store = Open())
            Assert.Equal(1, store.Snapshot.RuleIds.Count);
    }

    private ConfigSeal TestSeal() => new([.. _secrets.Secret!], Metadata(ConfigFile)["file_id"]);

    private string UnusableRuleIds(string change)
    {
        string value = Metadata(ConfigFile)[RuleIdsKey];
        switch (change)
        {
            case "another file":
                using (var other = new ConfigSeal([.. _secrets.Secret!], "fedcba9876543210fedcba9876543210"))
                    return Convert.ToBase64String(other.Seal(ConfigSection.ModSecurityRuleIds, ConfigStore.SchemaVersion,
                                                             """{"version":1,"ruleIds":[]}"""u8));
            case "a section's blob":
                return Convert.ToBase64String(Blobs(ConfigFile)["settings"]);
            case "empty":
                return "";
            case "shorter than a seal's header":
                return Convert.ToBase64String(Convert.FromBase64String(value)[..(ConfigSeal.HeaderBytes - 1)]);
            case "not base64":
                return "not base64!";
            case "one byte changed":
                byte[] blob = Convert.FromBase64String(value);
                blob[^1] ^= 0x01;
                return Convert.ToBase64String(blob);
            default:
                string json = change == "a higher version"
                    ? """{"version":2,"ruleIds":[{"file":"a.txt","id":10}]}"""
                    : """{"version":1,"ruleIds":[{"file":"a.txt","id":10}],"note":""}""";
                using (ConfigSeal seal = TestSeal())
                    return Convert.ToBase64String(seal.Seal(ConfigSection.ModSecurityRuleIds, ConfigStore.SchemaVersion,
                                                            Encoding.UTF8.GetBytes(json)));
        }
    }

    private static void SetMetadataValue(string path, string key, string value)
    {
        using SqliteConnection conn = OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE metadata SET value = $value WHERE key = $key;";
        cmd.Parameters.Add("$value", SqliteType.Text).Value = value;
        cmd.Parameters.Add("$key", SqliteType.Text).Value = key;
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ----- Helpers -----

    /// <summary>A config.db that is absent, zero bytes, or a SQLite file with no object in it.</summary>
    private void MakeEmptyConfig(string file)
    {
        switch (file)
        {
            case "no file":
                break;
            case "zero bytes":
                File.WriteAllBytes(ConfigFile, []);
                break;
            default:
                Execute(ConfigFile, "CREATE TABLE gone (x); DROP TABLE gone;", create: true);
                Assert.True(new FileInfo(ConfigFile).Length > 0);
                break;
        }
    }

    /// <summary>A config.db that a launch gives the state for, closed, with the first launch's key saved where it has one.</summary>
    private void MakeFailedConfig(ConfigOpenState state)
    {
        switch (state)
        {
            case ConfigOpenState.Newer:
                Open().Dispose();
                Execute(ConfigFile, "UPDATE metadata SET value = '2' WHERE key = 'schema_version';");
                break;
            case ConfigOpenState.Unreadable:
                File.WriteAllText(ConfigFile, "these are not the bytes of a database, but they are long enough to look like a header");
                break;
            case ConfigOpenState.KeyMismatch:
                // A section changed outside the app, so the key still reads
                Open().Dispose();
                byte[] blob = Blobs(ConfigFile)["settings"];
                blob[^1] ^= 0x01;
                SetBlob(ConfigFile, "settings", blob);
                break;
            default:
                Open().Dispose();
                File.Delete(ConfigFile);
                break;
        }
        Assert.Equal(state, TryOpenFails());
    }

    private ConfigStore Open()
    {
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? store));
        Assert.NotNull(store);
        return store;
    }

    private ConfigOpenState TryOpenFails()
    {
        ConfigOpenState state = ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? store);
        Assert.NotEqual(ConfigOpenState.Opened, state);
        Assert.Null(store);
        return state;
    }

    // macOS: files already in the folder can't be deleted, while a new file inherits leave to be. An access list or
    // flag on the file itself won't do: File.Copy clones it with the file, and the copy's move fails first.
    private void DenyDeletingTheFilesThere()
    {
        string user = Environment.UserName;
        Run("/bin/chmod", "+a#", "0", $"user:{user} deny delete_child", _temp.Path);
        Run("/bin/chmod", "+a", $"user:{user} allow delete,file_inherit", _temp.Path);
    }

    private void RemoveAccessLists() => Run("/bin/chmod", "-RN", _temp.Path);

    private static Exception Failure(string name) => name switch
    {
        "SecretStoreException" => new SecretStoreException("The Keychain couldn't read the secret."),
        "IOException" => new IOException("The file couldn't be read."),
        _ => new UnauthorizedAccessException("Access is denied."),
    };

    internal static SqliteConnection OpenRaw(string path, int timeoutSeconds = 30, bool create = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = timeoutSeconds,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    internal static void Execute(string path, string sql, bool create = false)
    {
        using SqliteConnection conn = OpenRaw(path, create: create);
        Execute(conn, sql);
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    internal static List<string> Strings(string path, string sql)
    {
        using SqliteConnection conn = OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    internal static Dictionary<string, string> Metadata(string path)
    {
        using SqliteConnection conn = OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            meta[reader.GetString(0)] = reader.GetString(1);
        return meta;
    }

    private static Dictionary<string, byte[]> Blobs(string path)
    {
        using SqliteConnection conn = OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, sealed FROM sections;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
            blobs[reader.GetString(0)] = reader.GetFieldValue<byte[]>(1);
        return blobs;
    }

    private static void SetBlob(string path, string section, byte[] blob)
    {
        using SqliteConnection conn = OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sections SET sealed = $sealed WHERE name = $name;";
        cmd.Parameters.Add("$sealed", SqliteType.Blob).Value = blob;
        cmd.Parameters.Add("$name", SqliteType.Text).Value = section;
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    internal static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
