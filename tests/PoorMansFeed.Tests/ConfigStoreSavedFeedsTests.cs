using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Config;
using PoorMansFeed.Core.FeedExceptions;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Tests;

/// <summary>Every store is disposed before a test reads or changes the file, as in ConfigStoreTests.</summary>
public sealed class ConfigStoreSavedFeedsTests : IDisposable
{
    private const string SavedFeedsKey = "saved_feeds";
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 15, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly AppPaths _paths;
    private readonly InMemorySecretStore _secrets = new();
    private readonly ConfigKeyStore _keys;
    private readonly FakeTimeProvider _time = new(Now);

    public ConfigStoreSavedFeedsTests()
    {
        _paths = new AppPaths(_temp.Path, _temp.File("exports"));
        _keys = new ConfigKeyStore(_secrets);
    }

    public void Dispose() => _temp.Dispose();

    private string ConfigFile => _temp.File("config.db");

    private static SavedFeedIntent WrittenAsn() => new SavedFeedIntent.Written(SavedFeedListTests.AsnPick());

    [Fact]
    public void A_new_file_has_no_value_and_no_saved_feeds()
    {
        using (ConfigStore store = Open())
        {
            Assert.Same(SavedFeedList.Empty, store.Snapshot.SavedFeeds);
            Assert.True(store.SavedFeeds.Available);
            Assert.Empty(store.SavedFeeds.Feeds);
        }

        Assert.False(ConfigStoreTests.Metadata(ConfigFile).ContainsKey(SavedFeedsKey));
        using (ConfigStore store = Open())
            Assert.Same(SavedFeedList.Empty, store.Snapshot.SavedFeeds);
    }

    [Fact]
    public void A_saved_feed_is_sealed_as_text_and_survives_a_reopen()
    {
        using (ConfigStore store = Open())
        {
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
            Assert.Equal(["AS15169"], store.SavedFeeds.Feeds.Select(feed => feed.Key));
        }

        Assert.Equal(["text"], ConfigStoreTests.Strings(ConfigFile, $"SELECT typeof(value) FROM metadata WHERE key = '{SavedFeedsKey}';"));
        using (ConfigSeal seal = TestSeal())
        {
            Assert.True(seal.TryOpen(ConfigSection.SavedFeeds, ConfigStore.SchemaVersion,
                                     Convert.FromBase64String(ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]), out byte[]? json));
            Assert.StartsWith("""{"version":2,"feeds":[{"key":"AS15169","kind":"Asn",""", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        }

        using (ConfigStore store = Open())
        {
            SavedFeed feed = Assert.Single(store.SavedFeeds.Feeds);
            Assert.Equal(Now, feed.LastWriteUtc);
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, new SavedFeedIntent.Retired(), Now));
        }

        using (ConfigStore store = Open())
        {
            Assert.True(Assert.Single(store.SavedFeeds.Feeds).Retired);
            Assert.Equal(SavedFeedsFailure.None, store.RemoveSavedFeed("as15169"));
            Assert.Empty(store.SavedFeeds.Feeds);
        }

        using (ConfigStore store = Open())
        {
            Assert.True(store.SavedFeeds.Available);
            Assert.Empty(store.SavedFeeds.Feeds);
        }
    }

    [Fact]
    public void A_failed_commit_keeps_the_snapshot_and_the_file()
    {
        using (ConfigStore store = Open())
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        string value = ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey];

        using (ConfigStore store = Open())
        {
            ConfigSnapshot snapshot = store.Snapshot;
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            Assert.Equal(SavedFeedsFailure.StorageError,
                         store.ApplySavedFeed("AWS_AMAZON_all", FeedKind.Aws, new SavedFeedIntent.Written(SavedFeedListTests.AwsPick()), Now));
            Assert.Equal(SavedFeedsFailure.StorageError, store.RemoveSavedFeed("AS15169"));
            Assert.Same(snapshot, store.Snapshot);
        }

        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);
        using (ConfigStore store = Open())
            Assert.Equal(["AS15169"], store.SavedFeeds.Feeds.Select(feed => feed.Key));
    }

    [Fact]
    public void A_change_that_changes_nothing_or_fails_writes_nothing()
    {
        using (ConfigStore store = Open())
        {
            ConfigSnapshot snapshot = store.Snapshot;
            // Any write would fail here
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            Assert.Equal(SavedFeedsFailure.NotFound, store.ApplySavedFeed("AS1", FeedKind.Asn, new SavedFeedIntent.Retired(), Now));
            Assert.Equal(SavedFeedsFailure.NotFound, store.RemoveSavedFeed("AS1"));
            Assert.Throws<ArgumentException>(() => store.ApplySavedFeed("a b", FeedKind.Asn, WrittenAsn(), Now));
            Assert.Same(snapshot, store.Snapshot);

            store.BeforeCommit = null;
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        }
    }

    [Fact]
    public void A_closed_store_gives_a_storage_error()
    {
        ConfigStore store = Open();
        ConfigSnapshot snapshot = store.Snapshot;
        store.Dispose();

        Assert.Equal(SavedFeedsFailure.StorageError, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        Assert.Equal(SavedFeedsFailure.StorageError, store.RemoveSavedFeed("AS15169"));
        Assert.Same(snapshot, store.Snapshot);
    }

    [Fact]
    public void Section_and_rule_id_saves_leave_the_saved_feeds_and_a_saved_feed_save_leaves_the_rest()
    {
        using (ConfigStore store = Open())
        {
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        }
        string value = ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey];

        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(new UserSettings { IPv6Enabled = false }));
            Assert.True(store.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")])));
            Assert.True(store.TryRecordAnswer(new AbuseRateLimit(5, 4, null), Now, countDownload: true));
            Assert.Equal([11], store.AssignRuleIds(["b.txt"], 10).RuleIds);
            Assert.Single(store.SavedFeeds.Feeds);
        }
        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);
        Dictionary<string, byte[]> rows = Blobs(ConfigFile);
        string ruleIds = ConfigStoreTests.Metadata(ConfigFile)["modsecurity_rule_ids"];

        using (ConfigStore store = Open())
        {
            Assert.Equal(SavedFeedsFailure.None, store.RemoveSavedFeed("AS15169"));
            Assert.False(store.Snapshot.Settings.IPv6Enabled);
            Assert.Equal(2, store.Snapshot.RuleIds.Count);
        }

        Assert.NotEqual(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);
        Assert.Equal(ruleIds, ConfigStoreTests.Metadata(ConfigFile)["modsecurity_rule_ids"]);
        Dictionary<string, byte[]> after = Blobs(ConfigFile);
        Assert.Equal(rows.Keys.Order(), after.Keys.Order());
        foreach (string name in rows.Keys)
            Assert.Equal(rows[name], after[name]);
    }

    [Theory]
    [InlineData("another file")]
    [InlineData("the rule ids' seal")]
    [InlineData("not base64")]
    [InlineData("one byte changed")]
    [InlineData("unreadable json")]
    [InlineData("a higher version")]
    [InlineData("empty")]
    public void A_value_this_build_cant_read_is_unavailable_refuses_saves_and_is_never_rewritten(string change)
    {
        var settings = new UserSettings { AbuseIpDbKeepDays = 45 };
        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(settings));
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        }
        SetValue(Unusable(change));
        string value = ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey];

        using (ConfigStore store = Open())
        {
            Assert.Equal(settings, store.Snapshot.Settings);
            Assert.Same(SavedFeedList.Unavailable, store.Snapshot.SavedFeeds);
            Assert.False(store.SavedFeeds.Available);
            Assert.Empty(store.SavedFeeds.Feeds);

            Assert.Equal(SavedFeedsFailure.Unavailable, store.ApplySavedFeed("AS1", FeedKind.Asn, new SavedFeedIntent.Written(SavedFeedListTests.AsnPick("1")), Now));
            Assert.Equal(SavedFeedsFailure.Unavailable, store.ApplySavedFeed("AS15169", FeedKind.Asn, new SavedFeedIntent.Retired(), Now));
            Assert.Equal(SavedFeedsFailure.Unavailable, store.RemoveSavedFeed("AS15169"));
        }
        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);

        using (ConfigStore store = Open())
        {
            Assert.True(store.SaveSettings(settings with { AbuseIpDbKeepDays = 46 }));
            Assert.True(store.SaveExceptions(ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Office")])));
            Assert.Equal([10], store.AssignRuleIds(["a.txt"], 10).RuleIds);
        }
        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);

        using (ConfigStore store = Open())
        {
            Assert.Equal(46, store.Snapshot.Settings.AbuseIpDbKeepDays);
            Assert.Same(SavedFeedList.Unavailable, store.Snapshot.SavedFeeds);
        }
        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);
    }

    [Fact]
    public void Start_fresh_forgets_the_saved_feeds()
    {
        using (ConfigStore first = Open())
            Assert.Equal(SavedFeedsFailure.None, first.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        byte[] blob = Blobs(ConfigFile)["settings"];
        blob[^1] ^= 0x01;
        SetBlob("settings", blob);
        Assert.Equal(ConfigOpenState.KeyMismatch, ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? failed));
        Assert.Null(failed);

        Assert.Equal(ConfigOpenState.Opened,
                     ConfigStore.StartFresh(_paths, _keys, _time, ConfigOpenState.KeyMismatch, out ConfigStore? store));
        using (store)
            Assert.Same(SavedFeedList.Empty, store!.Snapshot.SavedFeeds);

        Assert.False(ConfigStoreTests.Metadata(ConfigFile).ContainsKey(SavedFeedsKey));
        Assert.True(ConfigStoreTests.Metadata(_temp.File("config.unreadable.db")).ContainsKey(SavedFeedsKey));
    }

    [Fact]
    public void A_file_with_saved_feeds_is_still_current_to_this_builds_checks()
    {
        using (ConfigStore store = Open())
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));

        Assert.Equal("1", ConfigStoreTests.Metadata(ConfigFile)["schema_version"]);
        Assert.Equal(["accounting", "exceptions", "settings"],
                     ConfigStoreTests.Strings(ConfigFile, "SELECT name FROM sections ORDER BY name;"));
        using (ConfigStore store = Open())
            Assert.Single(store.SavedFeeds.Feeds);
    }

    [Fact]
    public void The_section_name_is_fixed() =>
        Assert.Equal("saved_feeds", ConfigSeal.SectionName(ConfigSection.SavedFeeds));

    // ----- Helpers -----

    // ----- Refresh stamps -----

    private static SavedFeedStamp Stamp(SavedFeed expected) =>
        new(expected, null, SavedFeedListTests.SampleProof(), Now.AddMinutes(5));

    [Fact]
    public void A_batch_of_stamps_is_one_commit_and_survives_a_reopen()
    {
        using (ConfigStore store = Open())
        {
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS1", FeedKind.Asn,
                                                                      new SavedFeedIntent.Written(SavedFeedListTests.AsnPick("1")), Now));
            int commits = 0;
            store.BeforeCommit = () => commits++;
            IReadOnlyList<SavedFeed> feeds = store.SavedFeeds.Feeds;

            IReadOnlyList<SavedFeedStampOutcome> outcomes = store.ApplySavedFeedStamps([Stamp(feeds[0]), Stamp(feeds[1])]);

            Assert.Equal([SavedFeedStampOutcome.Saved, SavedFeedStampOutcome.Saved], outcomes);
            Assert.Equal(1, commits);
            Assert.All(store.SavedFeeds.Feeds, feed => Assert.Equal(Now.AddMinutes(5), feed.LastRefreshUtc));

            Assert.Equal([SavedFeedStampOutcome.Superseded], store.ApplySavedFeedStamps([Stamp(feeds[0])]));
            Assert.Equal(1, commits);
        }

        using (ConfigStore store = Open())
        {
            Assert.All(store.SavedFeeds.Feeds, feed =>
            {
                Assert.Equal(Now.AddMinutes(5), feed.LastRefreshUtc);
                SavedFeedListTests.AssertSameProof(SavedFeedListTests.SampleProof(), feed.Proof);
            });
        }
    }

    [Fact]
    public void A_failed_stamp_commit_is_a_storage_error_for_every_stamp_that_applied_and_changes_nothing()
    {
        using (ConfigStore store = Open())
            Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        string value = ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey];

        using (ConfigStore store = Open())
        {
            SavedFeed feed = Assert.Single(store.SavedFeeds.Feeds);
            SavedFeed gone = feed with { Key = "AS2" };
            ConfigSnapshot snapshot = store.Snapshot;
            store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

            Assert.Equal([SavedFeedStampOutcome.StorageError, SavedFeedStampOutcome.Removed],
                         store.ApplySavedFeedStamps([Stamp(feed), Stamp(gone)]));
            Assert.Same(snapshot, store.Snapshot);
        }

        Assert.Equal(value, ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey]);
    }

    [Fact]
    public void Stamps_that_change_nothing_write_nothing()
    {
        using ConfigStore store = Open();
        Assert.Equal(SavedFeedsFailure.None, store.ApplySavedFeed("AS15169", FeedKind.Asn, WrittenAsn(), Now));
        Assert.Equal([SavedFeedStampOutcome.Saved], store.ApplySavedFeedStamps([Stamp(store.SavedFeeds.Feeds[0])]));
        store.BeforeCommit = () => throw new SqliteException("disk I/O error", 10);

        Assert.Equal([SavedFeedStampOutcome.Unchanged], store.ApplySavedFeedStamps([Stamp(store.SavedFeeds.Feeds[0])]));
    }

    private ConfigStore Open()
    {
        Assert.Equal(ConfigOpenState.Opened, ConfigStore.TryOpen(_paths, _keys, _time, false, out ConfigStore? store));
        Assert.NotNull(store);
        return store;
    }

    private ConfigSeal TestSeal() => new([.. _secrets.Secret!], ConfigStoreTests.Metadata(ConfigFile)["file_id"]);

    private string Unusable(string change)
    {
        string value = ConfigStoreTests.Metadata(ConfigFile)[SavedFeedsKey];
        switch (change)
        {
            case "another file":
                using (var other = new ConfigSeal([.. _secrets.Secret!], "fedcba9876543210fedcba9876543210"))
                    return Convert.ToBase64String(other.Seal(ConfigSection.SavedFeeds, ConfigStore.SchemaVersion,
                                                             """{"version":1,"feeds":[]}"""u8));
            case "the rule ids' seal":
                using (ConfigSeal seal = TestSeal())
                    return Convert.ToBase64String(seal.Seal(ConfigSection.ModSecurityRuleIds, ConfigStore.SchemaVersion,
                                                            """{"version":1,"feeds":[]}"""u8));
            case "not base64":
                return "not base64!";
            case "one byte changed":
                byte[] blob = Convert.FromBase64String(value);
                blob[^1] ^= 0x01;
                return Convert.ToBase64String(blob);
            case "unreadable json":
                using (ConfigSeal seal = TestSeal())
                    return Convert.ToBase64String(seal.Seal(ConfigSection.SavedFeeds, ConfigStore.SchemaVersion, "{\"version\":1,"u8));
            case "a higher version":
                using (ConfigSeal seal = TestSeal())
                    return Convert.ToBase64String(seal.Seal(ConfigSection.SavedFeeds, ConfigStore.SchemaVersion,
                                                            """{"version":3,"feeds":[]}"""u8));
            default:
                return "";
        }
    }

    private void SetValue(string value)
    {
        using SqliteConnection conn = ConfigStoreTests.OpenRaw(ConfigFile);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE metadata SET value = $value WHERE key = $key;";
        cmd.Parameters.Add("$value", SqliteType.Text).Value = value;
        cmd.Parameters.Add("$key", SqliteType.Text).Value = SavedFeedsKey;
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private static Dictionary<string, byte[]> Blobs(string path)
    {
        using SqliteConnection conn = ConfigStoreTests.OpenRaw(path);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, sealed FROM sections;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        while (reader.Read())
            blobs[reader.GetString(0)] = reader.GetFieldValue<byte[]>(1);
        return blobs;
    }

    private void SetBlob(string section, byte[] blob)
    {
        using SqliteConnection conn = ConfigStoreTests.OpenRaw(ConfigFile);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sections SET sealed = $sealed WHERE name = $name;";
        cmd.Parameters.Add("$sealed", SqliteType.Blob).Value = blob;
        cmd.Parameters.Add("$name", SqliteType.Text).Value = section;
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }
}
