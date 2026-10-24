namespace PoorMansFeed.Core;

/// <summary>The feed types a write can make, in write order. Each type's files go in its own folder.</summary>
internal enum FeedType { Fortigate, ModSecurity }

internal static class FeedTypes
{
    public static IReadOnlyList<FeedType> All { get; } = [FeedType.Fortigate, FeedType.ModSecurity];

    // The one place that maps the settings to types; a yes-or-no question asks UserSettings.TicksAnyFeedType()
    public static IReadOnlyList<FeedType> Ticked(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ticked = new List<FeedType>(All.Count);
        if (settings.WriteFortigateFeeds)
            ticked.Add(FeedType.Fortigate);
        if (settings.WriteModSecurityLists)
            ticked.Add(FeedType.ModSecurity);
        return ticked;
    }

    public static string FolderName(FeedType type) => type switch
    {
        FeedType.Fortigate => ExportWriter.FortigateFolderName,
        FeedType.ModSecurity => ExportWriter.ModSecurityFolderName,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // Only this line differs between the copies of one feed, so each copy says which reader it is for
    public static string FirstLine(FeedType type) => type switch
    {
        FeedType.Fortigate => "# PoorMansFeed - Fortigate Threat Feed",
        FeedType.ModSecurity => "# PoorMansFeed - ModSecurity IP list",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
