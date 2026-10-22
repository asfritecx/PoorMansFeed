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
    public void A_file_of_ToJson_loads_every_property()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);
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
            AbuseIpDbKeepDays = 90,
        };

        File.WriteAllBytes(path, SettingsStore.ToJson(original));
        Assert.Equal(original, store.Load());
    }

    [Fact]
    public void A_file_of_ToJson_keeps_IPv6Enabled_off()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);

        File.WriteAllBytes(path, SettingsStore.ToJson(new UserSettings { IPv6Enabled = false }));

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
    public void Load_settings_file_without_AbuseIpDbKeepDays_defaults_to_30()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"ExportFolder\": \"C:\\\\Exports\", \"IPv4MinPrefix\": 16 }");
        var store = new SettingsStore(path);

        UserSettings loaded = store.Load();

        Assert.Equal(30, loaded.AbuseIpDbKeepDays);
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
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllBytes(path, SettingsStore.ToJson(new UserSettings { IPv4MinPrefix = stored }));

        Assert.Equal(expected, store.Load().IPv4MinPrefix);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 99)]
    [InlineData(500, 128)]
    public void Load_clamps_IPv6MinPrefix_to_1_to_128(int stored, int expected)
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllBytes(path, SettingsStore.ToJson(new UserSettings { IPv6MinPrefix = stored }));

        Assert.Equal(expected, store.Load().IPv6MinPrefix);
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(366, 365)]
    [InlineData(1000, 365)]
    public void Load_clamps_AbuseIpDbKeepDays_to_1_to_365(int stored, int expected)
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllBytes(path, SettingsStore.ToJson(new UserSettings { AbuseIpDbKeepDays = stored }));

        Assert.Equal(expected, store.Load().AbuseIpDbKeepDays);
    }

    [Fact]
    public void ToJson_then_TryRead_round_trips_every_property()
    {
        var original = new UserSettings
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
        };

        Assert.Equal(original, SettingsStore.TryRead(SettingsStore.ToJson(original)));
    }

    [Fact]
    public void TryRead_reads_names_in_any_case_and_doesnt_clamp()
    {
        UserSettings? read = SettingsStore.TryRead("{ \"ipv4minprefix\": 40, \"ABUSEIPDBKEEPDAYS\": 0 }"u8);

        Assert.NotNull(read);
        Assert.Equal(40, read.IPv4MinPrefix);
        Assert.Equal(0, read.AbuseIpDbKeepDays);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"IPv4MinPrefix\": \"x\" }")]
    public void TryRead_gives_null_for_json_that_isnt_settings(string json)
    {
        Assert.Null(SettingsStore.TryRead(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ToJson_gives_indented_UTF8_with_no_byte_order_mark_that_Load_reads()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        var settings = new UserSettings { ExportFolder = "C:\\Exports \u00e9", AbuseIpDbKeepDays = 7 };
        byte[] json = SettingsStore.ToJson(settings);

        Assert.False(json.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.Contains((byte)'\n', json);
        File.WriteAllBytes(path, json);
        Assert.Equal(settings, new SettingsStore(path).Load());
    }

    [Fact]
    public void Load_still_reads_a_file_with_a_byte_order_mark()
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "{ \"IPv4MinPrefix\": 20 }"u8]);

        Assert.Equal(20, new SettingsStore(path).Load().IPv4MinPrefix);
    }

    [Theory]
    [InlineData("UTF-16 LE")]
    [InlineData("UTF-16 BE")]
    [InlineData("UTF-32 LE")]
    public void Load_reads_a_UTF16_or_UTF32_file_by_its_byte_order_mark(string encoding)
    {
        using var temp = new TempFolder();
        string path = temp.File("settings.json");
        System.Text.Encoding chosen = encoding switch
        {
            "UTF-16 LE" => new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "UTF-16 BE" => new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: true),
        };
        File.WriteAllText(path, "{ \"IPv4MinPrefix\": 20, \"ExportFolder\": \"C:\\\\Feeds \u00e9\" }", chosen);

        UserSettings loaded = new SettingsStore(path).Load();

        Assert.Equal(20, loaded.IPv4MinPrefix);
        Assert.Equal("C:\\Feeds \u00e9", loaded.ExportFolder);
    }
}
