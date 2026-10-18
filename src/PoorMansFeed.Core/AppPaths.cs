namespace PoorMansFeed.Core;

public sealed record AppPaths(string DataFolder, string DefaultExportFolder)
{
    public const string AppFolderName = "PoorMansFeed";

    public string DatabasePath     => Path.Combine(DataFolder, "ipinfo.db");
    public string NextDatabasePath => Path.Combine(DataFolder, "ipinfo.next.db");
    public string DownloadPath     => Path.Combine(DataFolder, "ipinfo_lite.json.gz.tmp");
    public string LockPath         => Path.Combine(DataFolder, "ipinfo.lock");
    public string SettingsPath     => Path.Combine(DataFolder, "settings.json");

    /// <summary>On Windows the DPAPI-protected token; on macOS an empty marker beside the token's Keychain item.</summary>
    public string TokenPath => Path.Combine(DataFolder, "ipinfo_token");

    /// <summary>The token file of earlier builds, encrypted under a passphrase in the source. Deleted at start-up.</summary>
    public string LegacyTokenPath => Path.Combine(DataFolder, ".ipinfo_token");

    /// <summary>The cached Azure service tags file, exactly as downloaded, and the record that says where it came from.</summary>
    public string AzureServiceTagsPath       => Path.Combine(DataFolder, "azure_service_tags.json");
    public string AzureServiceTagsSourcePath => Path.Combine(DataFolder, "azure_service_tags.source.json");

    /// <summary>The cached AWS ip-ranges.json, exactly as downloaded, and the record that says where it came from.</summary>
    public string AwsIpRangesPath       => Path.Combine(DataFolder, "aws_ip_ranges.json");
    public string AwsIpRangesSourcePath => Path.Combine(DataFolder, "aws_ip_ranges.source.json");

    /// <summary>
    /// Resolves the per-user data folder (LocalApplicationData) and default export folder (MyDocuments).
    /// Creates the data folder. Returns null when the base folder is unknown or the data folder cannot be created.
    /// </summary>
    public static AppPaths? ForCurrentUser()
    {
        string localBase = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localBase))
            return null;

        string dataFolder = Path.Combine(localBase, AppFolderName);
        try
        {
            Directory.CreateDirectory(dataFolder);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
                return null;
            documents = Path.Combine(profile, "Documents");
        }

        return new AppPaths(dataFolder, Path.Combine(documents, AppFolderName));
    }
}
