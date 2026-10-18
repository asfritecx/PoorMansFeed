using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));

        Assert.Equal(new UserSettings(), store.Load());
    }

    [Fact]
    public void Save_then_Load_round_trips_every_property()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        var original = new UserSettings
        {
            SchemaVersion = 2,
            ExportFolder = "C:\\Exports\\Feeds",
            FortigateEnabled = false,
            FortigateIncludeIPv4 = false,
            FortigateIncludeIPv6 = false,
            FortigateSplitByFamily = false,
            LargeRangeCheckEnabled = false,
            IPv4MinPrefix = 20,
            IPv6MinPrefix = 48,
        };

        Assert.True(store.Save(original));
        Assert.Equal(original, store.Load());
    }

    [Fact]
    public void Save_then_Load_keeps_IPv6Enabled_off()
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));

        Assert.True(store.Save(new UserSettings { IPv6Enabled = false }));

        Assert.False(store.Load().IPv6Enabled);
    }

    [Fact]
    public void Load_settings_file_without_IPv6Enabled_defaults_to_on()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"ExportFolder\": \"C:\\\\Exports\", \"IPv4MinPrefix\": 16 }");
        var store = new SettingsStore(path);

        UserSettings loaded = store.Load();

        Assert.True(loaded.IPv6Enabled);
        Assert.Equal(16, loaded.IPv4MinPrefix);
    }

    [Fact]
    public void Load_corrupt_json_returns_defaults()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new SettingsStore(path);

        Assert.Equal(new UserSettings(), store.Load());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 32)]
    [InlineData(500, 32)]
    public void Load_clamps_IPv4MinPrefix_to_1_to_32(int stored, int expected)
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        Assert.True(store.Save(new UserSettings { IPv4MinPrefix = stored }));

        Assert.Equal(expected, store.Load().IPv4MinPrefix);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 99)]
    [InlineData(500, 128)]
    public void Load_clamps_IPv6MinPrefix_to_1_to_128(int stored, int expected)
    {
        using var temp = new TempFolder();
        var store = new SettingsStore(temp.File("settings.json"));
        Assert.True(store.Save(new UserSettings { IPv6MinPrefix = stored }));

        Assert.Equal(expected, store.Load().IPv6MinPrefix);
    }

    [Fact]
    public void Save_leaves_no_tmp_file_behind()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);

        Assert.True(store.Save(new UserSettings()));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
