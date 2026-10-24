namespace PoorMansFeed;

public static partial class AppText
{
    // The configure dialog, shown by a feed press while no feed type is ticked in Settings
    public const string ConfigureFeedsTitle = "No feed type ticked";
    public const string ConfigureFeedsBody = "Please configure your write feed settings under Settings and the feed types available to write feed.";
    public const string ConfigureFeedsOpenSettings = "Open Settings";

    // A Write feed press while another task holds the work gate at the moment the dialog would show
    public const string ConfigureFeedsBusy = "The feed wasn't written" + Dash + "press Write feed again when the current task ends";
}
