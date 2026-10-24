using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>
/// The first launch with config.db: settings.json and exceptions.json are imported once, then renamed *.migrated and
/// never read again. An exceptions file that can't be read stops the import until it is set aside.
/// </summary>
public sealed class ConfigImportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 22, 30, 15, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly AppPaths _paths;
    private readonly InMemorySecretStore _secrets = new();
    private readonly ConfigKeyStore _keys;
    private readonly FakeTimeProvider _time = new(Now);

    public ConfigImportTests()
    {
        _paths = new AppPaths(_temp.Path, _temp.File("exports"));
        _keys = new ConfigKeyStore(_secrets);
    }

    public void Dispose() => _temp.Dispose();

    private string ConfigFile => _temp.File("config.db");
    private string SettingsFile => _temp.File("settings.json");
    private string ExceptionsFile => _temp.File("exceptions.json");

    private static readonly string EverySetting = """
        { "SchemaVersion": 3, "ExportFolder": "/Users/someone/Feeds", "IPv6Enabled": false, "FortigateEnabled": false,
          "FortigateIncludeIPv4": false, "FortigateIncludeIPv6": false, "FortigateSplitByFamily": false,
          "LargeRangeCheckEnabled": false, "IPv4MinPrefix": 40, "IPv6MinPrefix": 0, "AbuseIpDbKeepDays": 999,
          "WriteModSecurityLists": true, "ModSecurityAction": 9, "ModSecurityFirstRuleId": 100000,
          "ModSecurityLocation": "LONG_LOCATION" }
        """.Replace("LONG_LOCATION", new string('x', ModSecurityLimits.MaxLocationLength + 1));

    private const string ThreeExceptions = """
        { "schemaVersion": 1, "exceptions": [
          { "network": "2001:db8::/32", "note": "Lab" },
          { "network": "192.0.2.0/24", "note": "Head office" },
          { "network": "192.0.2.9/24", "note": "Repeated" },
          { "network": "198.51.100.7" } ] }
        """;

    [Fact]
    public void Both_files_are_imported_normalised_in_order_and_renamed()
    {
        File.WriteAllText(SettingsFile, EverySetting);
        File.WriteAllText(ExceptionsFile, ThreeExceptions);
        byte[] settingsBytes = File.ReadAllBytes(SettingsFile);
        byte[] exceptionsBytes = File.ReadAllBytes(ExceptionsFile);

        using (ConfigStore store = Open())
        {
            Assert.Equal(new UserSettings
            {
                SchemaVersion = 3,
                ExportFolder = "/Users/someone/Feeds",
                IPv6Enabled = false,
                FortigateEnabled = false,
                FortigateIncludeIPv4 = false,
                FortigateIncludeIPv6 = false,
                FortigateSplitByFamily = false,
                LargeRangeCheckEnabled = false,
                IPv4MinPrefix = 32,
                IPv6MinPrefix = 1,
                AbuseIpDbKeepDays = AbuseIpDbLimits.MaxKeepDays,
                WriteFortigateFeeds = true,
                WriteModSecurityLists = true,
                ModSecurityAction = ModSecurityAction.LogOnly,
                ModSecurityFirstRuleId = ModSecurityLimits.RuleIdMax,
                ModSecurityLocation = "",
            }, store.Snapshot.Settings);
            Assert.Equal(["2001:db8::/32", "192.0.2.0/24", "198.51.100.7/32"],
                         store.Snapshot.Exceptions.Entries.Select(entry => entry.Network.Text));
            Assert.Equal(["Lab", "Head office", ""], store.Snapshot.Exceptions.Entries.Select(entry => entry.Note));
        }

        Assert.False(File.Exists(SettingsFile));
        Assert.False(File.Exists(ExceptionsFile));
        Assert.Equal(settingsBytes, File.ReadAllBytes(SettingsFile + ".migrated"));
        Assert.Equal(exceptionsBytes, File.ReadAllBytes(ExceptionsFile + ".migrated"));
        Dictionary<string, string> meta = ConfigStoreTests.Metadata(ConfigFile);
        Assert.Equal("settings.json,exceptions.json", meta["imported_from"]);
        Assert.Equal(meta["created_utc"], meta["imported_utc"]);

        // The values are config.db's now
        using (ConfigStore store = Open())
        {
            Assert.Equal(32, store.Snapshot.Settings.IPv4MinPrefix);
            Assert.Equal(3, store.Snapshot.Exceptions.Entries.Count);
        }
    }

    [Fact]
    public void Only_the_files_that_existed_are_named()
    {
        File.WriteAllText(ExceptionsFile, ThreeExceptions);

        Open().Dispose();

        Assert.Equal("exceptions.json", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);
    }

    [Fact]
    public void A_corrupt_settings_file_gives_the_defaults_and_is_still_renamed_and_named()
    {
        File.WriteAllText(SettingsFile, "{ not json");

        using (ConfigStore store = Open())
            Assert.Equal(new UserSettings { WriteFortigateFeeds = true }, store.Snapshot.Settings);

        Assert.False(File.Exists(SettingsFile));
        Assert.Equal("{ not json", File.ReadAllText(SettingsFile + ".migrated"));
        Assert.Equal("settings.json", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);
    }

    public static TheoryData<string> EarlierInstallFiles =>
    [
        "ipinfo.db", "ipinfo.next.db", "ipinfo_lite.json.gz.tmp", "ipinfo_token", ".ipinfo_token", "settings.json",
        "exceptions.json", "exceptions.unreadable.json", "config.unreadable.db", "abuseipdb_key",
        "azure_service_tags.json", "azure_service_tags.source.json", "aws_ip_ranges.json", "aws_ip_ranges.source.json",
    ];

    [Theory]
    [MemberData(nameof(EarlierInstallFiles))]
    public void A_file_of_an_earlier_install_keeps_FortiGate_feeds_on_at_the_import(string file)
    {
        File.WriteAllText(_temp.File("ipinfo.lock"), "1");
        File.WriteAllText(_temp.File(file), file == "exceptions.json" ? "{ \"schemaVersion\": 1, \"exceptions\": [] }" : "{ not json");

        using ConfigStore store = Open();

        Assert.True(store.Snapshot.Settings.WriteFortigateFeeds);
        Assert.False(store.Snapshot.Settings.WriteModSecurityLists);
    }

    [Fact]
    public void A_new_data_folder_with_only_the_lock_and_the_key_file_starts_with_no_feed_type()
    {
        File.WriteAllText(_temp.File("ipinfo.lock"), "1");
        File.WriteAllText(_temp.File("config_key"), "key");

        using ConfigStore store = Open();

        Assert.Equal(new UserSettings(), store.Snapshot.Settings);
        Assert.False(store.Snapshot.Settings.WriteFortigateFeeds);
    }

    [Fact]
    public void A_settings_file_turning_FortiGate_off_still_imports_it_on_and_keeps_its_ModSecurity()
    {
        File.WriteAllText(SettingsFile, "{ \"WriteFortigateFeeds\": false, \"WriteModSecurityLists\": true }");

        using ConfigStore store = Open();

        Assert.True(store.Snapshot.Settings.WriteFortigateFeeds);
        Assert.True(store.Snapshot.Settings.WriteModSecurityLists);
    }

    public static TheoryData<string> MissingEvidence => ["config_made", "settings.json.migrated.2", "exceptions.json.migrated"];

    [Theory]
    [MemberData(nameof(MissingEvidence))]
    public void Start_fresh_beside_a_file_of_an_earlier_launch_keeps_FortiGate_feeds_on(string file)
    {
        File.WriteAllText(_temp.File(file), "x");
        Assert.Equal(ConfigOpenState.Missing, ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? none));
        Assert.Null(none);

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.Missing, out ConfigStore? store));
        using (store)
        {
            Assert.True(store!.Snapshot.Settings.WriteFortigateFeeds);
            Assert.False(store.Snapshot.Settings.WriteModSecurityLists);
        }
    }

    public static TheoryData<string> UnreadableExceptions => ["bad json", "schema 2", "not a network", "over the cap", "over the size"];

    [Theory]
    [MemberData(nameof(UnreadableExceptions))]
    public void An_unreadable_exceptions_file_stops_the_import_until_it_is_set_aside(string fault)
    {
        File.WriteAllText(SettingsFile, EverySetting);
        File.WriteAllBytes(ExceptionsFile, UnreadableExceptionsFile(fault));
        byte[] exceptionsBytes = File.ReadAllBytes(ExceptionsFile);

        Assert.Equal(ConfigOpenState.ExceptionsImportUnreadable,
                     ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? refused));

        Assert.Null(refused);
        Assert.False(File.Exists(ConfigFile));
        Assert.False(_secrets.Marker);
        Assert.Null(_secrets.Secret);
        Assert.True(File.Exists(SettingsFile));
        Assert.True(File.Exists(ExceptionsFile));

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(_paths, _keys, _time, true, out ConfigStore? store));
        using (store)
        {
            Assert.True(store!.Snapshot.Exceptions.IsReadable);
            Assert.Empty(store.Snapshot.Exceptions.Entries);
            Assert.Equal(32, store.Snapshot.Settings.IPv4MinPrefix);
        }

        Assert.Equal(exceptionsBytes, File.ReadAllBytes(_temp.File("exceptions.unreadable.json")));
        Assert.False(File.Exists(ExceptionsFile));
        Assert.False(File.Exists(ExceptionsFile + ".migrated"));
        Assert.True(File.Exists(SettingsFile + ".migrated"));
        Assert.Equal("settings.json", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);
    }

    [Fact]
    public void Start_without_exceptions_beside_only_an_unreadable_exceptions_file_keeps_FortiGate_feeds_on()
    {
        File.WriteAllBytes(ExceptionsFile, UnreadableExceptionsFile("bad json"));

        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(_paths, _keys, _time, true, out ConfigStore? store));
        using (store)
        {
            Assert.True(store!.Snapshot.Settings.WriteFortigateFeeds);
            Assert.False(store.Snapshot.Settings.WriteModSecurityLists);
            Assert.Equal(0, store.Snapshot.RuleIds.Count);
        }
        Assert.False(File.Exists(ExceptionsFile));
        Assert.True(File.Exists(_temp.File("exceptions.unreadable.json")));
    }

    [Fact]
    public void An_older_migrated_file_is_kept_and_the_rename_takes_the_next_free_name()
    {
        // A *.migrated file shows that config.db was made, so this is a later open of it, not an import
        Open().Dispose();
        byte[] older = Encoding.UTF8.GetBytes("an older import");
        File.WriteAllBytes(SettingsFile + ".migrated", older);
        File.WriteAllText(SettingsFile, EverySetting);

        Open().Dispose();

        Assert.Equal(older, File.ReadAllBytes(SettingsFile + ".migrated"));
        Assert.Equal(EverySetting, File.ReadAllText(SettingsFile + ".migrated.2"));
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void With_every_migrated_name_taken_the_file_is_left_where_it_is_and_the_open_goes_on()
    {
        // Every name taken means earlier opens, so config.db is there and the file beside it is never imported
        using (ConfigStore store = Open())
            Assert.True(store.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        var taken = Enumerable.Range(1, ConfigStore.MigratedNameCount)
            .Select(number => SettingsFile + ".migrated" + (number == 1 ? "" : "." + number))
            .ToList();
        foreach (string name in taken)
            File.WriteAllText(name, Path.GetFileName(name));
        File.WriteAllText(SettingsFile, EverySetting);

        using (ConfigStore store = Open())
            Assert.Equal(12, store.Snapshot.Settings.AbuseIpDbKeepDays);

        Assert.Equal(EverySetting, File.ReadAllText(SettingsFile));
        Assert.All(taken, name => Assert.Equal(Path.GetFileName(name), File.ReadAllText(name)));
        Assert.False(File.Exists(SettingsFile + ".migrated.100"));
        Assert.Equal("", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);

        using (ConfigStore store = Open())
            Assert.Equal(12, store.Snapshot.Settings.AbuseIpDbKeepDays);
        Assert.Equal(EverySetting, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void An_import_writes_the_marker_and_renames_the_old_files()
    {
        File.WriteAllText(SettingsFile, EverySetting);
        File.WriteAllText(ExceptionsFile, ThreeExceptions);

        Open().Dispose();

        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(_temp.File("config_made"))));
        Assert.True(File.Exists(SettingsFile + ".migrated"));
        Assert.True(File.Exists(ExceptionsFile + ".migrated"));
    }

    [Fact]
    public void A_settings_file_in_UTF16_with_its_byte_order_mark_is_imported()
    {
        File.WriteAllText(SettingsFile, EverySetting, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(SettingsFile)[..2]);

        using (ConfigStore store = Open())
        {
            Assert.Equal(32, store.Snapshot.Settings.IPv4MinPrefix);
            Assert.Equal("/Users/someone/Feeds", store.Snapshot.Settings.ExportFolder);
        }
    }

    [Fact]
    public void Once_config_exists_a_new_settings_file_is_renamed_and_never_imported()
    {
        using (ConfigStore store = Open())
            Assert.True(store.SaveSettings(new UserSettings { AbuseIpDbKeepDays = 12 }));
        File.WriteAllText(SettingsFile, EverySetting);
        File.WriteAllText(ExceptionsFile, ThreeExceptions);

        using (ConfigStore store = Open())
        {
            Assert.Equal(new UserSettings { AbuseIpDbKeepDays = 12 }, store.Snapshot.Settings);
            Assert.Empty(store.Snapshot.Exceptions.Entries);
        }

        Assert.False(File.Exists(SettingsFile));
        Assert.Equal(EverySetting, File.ReadAllText(SettingsFile + ".migrated"));
        Assert.Equal(ThreeExceptions, File.ReadAllText(ExceptionsFile + ".migrated"));
        Assert.Equal("", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);
    }

    [Fact]
    public void A_folder_in_the_way_of_the_rename_is_skipped()
    {
        Directory.CreateDirectory(SettingsFile + ".migrated");
        File.WriteAllText(Path.Combine(SettingsFile + ".migrated", "inside"), "x");
        File.WriteAllText(SettingsFile, EverySetting);

        using (ConfigStore store = Open())
            Assert.Equal(32, store.Snapshot.Settings.IPv4MinPrefix);

        Assert.False(File.Exists(SettingsFile));
        Assert.Equal("x", File.ReadAllText(Path.Combine(SettingsFile + ".migrated", "inside")));
        Assert.Equal(EverySetting, File.ReadAllText(SettingsFile + ".migrated.2"));
        Assert.Equal("settings.json", ConfigStoreTests.Metadata(ConfigFile)["imported_from"]);
    }

    [Fact]
    public void Exceptions_that_cant_be_set_aside_stop_the_open_before_anything_else()
    {
        File.WriteAllText(ExceptionsFile, "not json");
        Directory.CreateDirectory(_temp.File("exceptions.unreadable.json"));
        File.WriteAllText(Path.Combine(_temp.File("exceptions.unreadable.json"), "inside"), "x");

        Assert.Equal(ConfigOpenState.ExceptionsImportUnreadable,
                     ConfigStore.TryOpen(_paths, _keys, _time, true, out ConfigStore? store));

        Assert.Null(store);
        Assert.False(File.Exists(ConfigFile));
        Assert.Null(_secrets.Secret);
        Assert.Equal("not json", File.ReadAllText(ExceptionsFile));
    }

    private ConfigStore Open()
    {
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? store));
        Assert.NotNull(store);
        return store;
    }

    private static byte[] UnreadableExceptionsFile(string fault)
    {
        switch (fault)
        {
            case "bad json":
                return Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [");
            case "schema 2":
                return Encoding.UTF8.GetBytes("{ \"schemaVersion\": 2, \"exceptions\": [] }");
            case "not a network":
                return Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [ { \"network\": \"010.0.0.1\" } ] }");
            case "over the cap":
                var entries = Enumerable.Range(0, ExceptionList.MaxEntries + 1)
                    .Select(i => new { network = $"10.{i / 256}.{i % 256}.0/24", note = "" });
                return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, exceptions = entries });
            default:
                byte[] json = Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"exceptions\": [] }");
                byte[] file = new byte[ExceptionListStore.MaxFileBytes + 1];
                Array.Fill(file, (byte)' ');
                json.CopyTo(file, 0);
                return file;
        }
    }
}
