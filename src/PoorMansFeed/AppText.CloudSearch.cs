using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.CloudSearch;

namespace PoorMansFeed;

public static partial class AppText
{
    public const string CloudSearchInitial = "Type one address to find it in the saved Google Cloud, Azure and AWS ranges.";
    public const string CloudSearchInvalidInput = "Type one IP address. A network, a range or other text can't be searched.";
    public const string CloudSearchNotAnAddress = "This page searches an IP address, not an AS number. Search AS numbers on IPInfo Lookup.";

    public const string CloudSearchNoSavedRanges =
        "No ranges are saved yet. Download them on Google Cloud Lookup, Azure Lookup or AWS Lookup.";

    public const string CloudSearchNoReadableRanges =
        "The saved ranges couldn't be read. The lines under the search box say what to do.";

    public const string CloudSearchFailed = "The saved ranges couldn't be searched. Please try again.";
    public const string CloudSearchEmptyInitial = "Saved ranges that hold the address will appear here.";
    public const string CloudSearchEmptyNone = "No saved range to show.";

    public const string CloudSearchTitle = "Cloud Address Lookup";
    public const string CloudSearchCaption = "Find an IP address in the saved Google Cloud, Azure and AWS ranges";
    public const string CloudSearchBoxName = "IP address";
    public const string CloudSearchPlaceholder = "IPv4 or IPv6 address";
    public const string CloudSearchResultsHeader = "Saved ranges";
    public const string ColumnProvider = "Provider";
    public const string ColumnScope = "Scope";

    public const string CloudProviderGoogle = "Google Cloud";
    public const string CloudProviderAzure = "Azure";
    public const string CloudProviderAws = "AWS";

    /// <summary>"N saved ranges hold {address}", or "Showing 300 of N ..." when the list is capped.</summary>
    public static string CloudSearchFound(int shown, int total, string address) =>
        shown < total
            ? $"Showing {Number(shown)} of {Number(total)} saved ranges that hold {address}"
            : Count(total, "saved range", "saved ranges") + (total == 1 ? " holds " : " hold ") + address;

    // Names only the providers whose saved ranges were read, and never says a provider doesn't own the address
    public static string CloudSearchNoMatches(string address, IReadOnlyList<CloudProvider> searched)
    {
        string[] names = (searched.Count > 0 ? searched : [CloudProvider.GoogleCloud, CloudProvider.Azure, CloudProvider.Aws])
            .Select(CloudProviderName).ToArray();
        string list = names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " or " + names[^1];
        return "No saved " + list + " range holds " + address + ".";
    }

    public static string CloudProviderName(CloudProvider provider) => provider switch
    {
        CloudProvider.GoogleCloud => CloudProviderGoogle,
        CloudProvider.Azure => CloudProviderAzure,
        _ => CloudProviderAws,
    };

    /// <summary>One provider's source line; SavedUtc is when its saved bytes were first downloaded.</summary>
    public static string CloudSearchSource(SavedRangesStatus status)
    {
        string name = CloudProviderName(status.Provider);
        string page = status.Provider switch
        {
            CloudProvider.GoogleCloud => GoogleCloudTitle,
            CloudProvider.Azure => AzureTitle,
            _ => AwsTitle,
        };

        return status.State switch
        {
            SavedRangesState.Read when status.SavedUtc is { } saved =>
                name + ": saved ranges downloaded " + UtcMinuteDown(saved),
            SavedRangesState.NotSaved => name + ": no ranges saved yet. Download them on " + page + ".",
            _ => name + ": the saved ranges couldn't be read. Search again, or restart and open " + page + ".",
        };
    }

    public const string HelpCloudSearchHeading = "7. " + CloudSearchTitle;

    /// <summary>Help's Cloud Address Lookup section, one short paragraph each.</summary>
    public static IReadOnlyList<string> HelpCloudSearchTexts { get; } =
    [
        CloudSearchTitle + " searches one IPv4 or IPv6 address in the ranges that " + GoogleCloudTitle + ", "
            + AzureTitle + " and " + AwsTitle + " saved. It downloads nothing itself: each of those pages downloads "
            + "its ranges the first time it is opened after " + GraphiteUi.AppInfo.Name + " starts, and "
            + CheckForUpdates + " on " + AzureTitle + " and " + AwsTitle + " looks for newer ones. A page saves ranges "
            + "only when they are new, so the lines under the search box say when the saved ranges were downloaded, "
            + "not when a page last checked them. When the last search looked in the saved ranges, coming back to this "
            + "page searches its address again.",
        "Each row is a saved range that holds the address, with its provider, service, region and scope, the narrowest "
            + "first. AzureCloud and AMAZON are umbrella ranges that span many services, so they come after the rest.",
        "The answer covers only the saved files. Google's lists the ranges Google Cloud customers use, not Google's own "
            + "services such as its public DNS, and Azure's covers Azure, not every Microsoft network. So an address "
            + "that no saved range holds may still belong to one of these companies; " + Title
            + " shows who holds its network.",
        "Right-click a row to copy its network.",
    ];
}
