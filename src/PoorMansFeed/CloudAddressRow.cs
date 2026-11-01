using PoorMansFeed.Core.CloudSearch;

namespace PoorMansFeed;

/// <summary>One saved cloud range that holds the searched address, already formatted for display.</summary>
public sealed record CloudAddressRow(string NetworkText, string ProviderText, string ServiceText, string RegionText,
                                     string ScopeText, string AccessibleText)
{
    private const string AwsGlobalRegionId = "GLOBAL";

    public static CloudAddressRow From(CloudAddressMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        string provider = AppText.CloudProviderName(match.Provider);
        // Only an Azure base tag has an empty region; a regional tag with no region arrives with its suffix
        string region = match.Provider switch
        {
            CloudProvider.Azure when match.Region.Length == 0 => AppText.AzureAllRegions,
            CloudProvider.Aws when match.Region == AwsGlobalRegionId => AppText.AwsGlobalRegion,
            _ => match.Region,
        };
        string scope = match.Scope ?? "";
        string accessible = string.Join(", ",
            new[] { match.Prefix.Text, provider, match.Service, region, scope }.Where(part => part.Length > 0));

        return new CloudAddressRow(match.Prefix.Text, provider, match.Service, region, scope, accessible);
    }
}
