namespace PoorMansFeed.Core;

public static class ExportFolders
{
    /// <summary>The configured export folder, or defaultFolder when the setting is blank.</summary>
    public static string Resolve(UserSettings settings, string defaultFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.IsNullOrWhiteSpace(settings.ExportFolder) ? defaultFolder : settings.ExportFolder;
    }
}
