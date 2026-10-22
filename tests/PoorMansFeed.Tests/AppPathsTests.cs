using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Derived_file_paths_are_under_DataFolder_with_exact_names()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Path, temp.File("exports"));

        Assert.Equal(Path.Combine(temp.Path, "ipinfo.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(temp.Path, "ipinfo.next.db"), paths.NextDatabasePath);
        Assert.Equal(Path.Combine(temp.Path, "ipinfo_lite.json.gz.tmp"), paths.DownloadPath);
        Assert.Equal(Path.Combine(temp.Path, "ipinfo.lock"), paths.LockPath);
        Assert.Equal(Path.Combine(temp.Path, "ipinfo_token"), paths.TokenPath);
        Assert.Equal(Path.Combine(temp.Path, "abuseipdb_key"), paths.AbuseIpDbKeyPath);
        Assert.NotEqual(paths.TokenPath, paths.AbuseIpDbKeyPath);
        Assert.Equal(Path.Combine(temp.Path, "abuseipdb.db"), paths.AbuseIpDbDatabasePath);
        Assert.Equal(Path.Combine(temp.Path, ".ipinfo_token"), paths.LegacyTokenPath);
        Assert.Equal(Path.Combine(temp.Path, "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine(temp.Path, "exceptions.json"), paths.ExceptionsPath);
        Assert.Equal(Path.Combine(temp.Path, "exceptions.unreadable.json"), paths.UnreadableExceptionsPath);
        Assert.Equal("exceptions.unreadable.json", AppPaths.UnreadableExceptionsFileName);
        Assert.Equal(Path.Combine(temp.Path, "config.db"), paths.ConfigPath);
        Assert.Equal("config.db", AppPaths.ConfigFileName);
        Assert.Equal(Path.Combine(temp.Path, "config.unreadable.db"), paths.UnreadableConfigPath);
        Assert.Equal("config.unreadable.db", AppPaths.UnreadableConfigFileName);
        Assert.Equal(Path.Combine(temp.Path, "config_made"), paths.ConfigMadePath);
        Assert.Equal("config_made", AppPaths.ConfigMadeFileName);
        Assert.Equal(Path.Combine(temp.Path, "config_key"), paths.ConfigKeyPath);
        Assert.NotEqual(paths.TokenPath, paths.ConfigKeyPath);
        Assert.NotEqual(paths.AbuseIpDbKeyPath, paths.ConfigKeyPath);
        Assert.Equal(".migrated", AppPaths.MigratedSuffix);
        Assert.Equal(Path.Combine(temp.Path, "azure_service_tags.json"), paths.AzureServiceTagsPath);
        Assert.Equal(Path.Combine(temp.Path, "azure_service_tags.source.json"), paths.AzureServiceTagsSourcePath);
        Assert.Equal(Path.Combine(temp.Path, "aws_ip_ranges.json"), paths.AwsIpRangesPath);
        Assert.Equal(Path.Combine(temp.Path, "aws_ip_ranges.source.json"), paths.AwsIpRangesSourcePath);
    }

    [Fact]
    public void ForCurrentUser_resolves_folders_under_PoorMansFeed()
    {
        AppPaths? paths = AppPaths.ForCurrentUser();

        Assert.NotNull(paths);
        TestContext.Current.SendDiagnosticMessage($"DataFolder: {paths.DataFolder}");
        TestContext.Current.SendDiagnosticMessage($"DefaultExportFolder: {paths.DefaultExportFolder}");
        Assert.EndsWith("PoorMansFeed", paths.DataFolder, StringComparison.Ordinal);
        Assert.EndsWith("PoorMansFeed", paths.DefaultExportFolder, StringComparison.Ordinal);
    }
}
