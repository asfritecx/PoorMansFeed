using PoorMansFeed.Core;
using PoorMansFeed.Core.Aws;
using PoorMansFeed.Core.Azure;
using PoorMansFeed.Core.GoogleCloud;

namespace PoorMansFeed.Core.CloudSearch;

internal static class CloudAddressIndex
{
    private const string GoogleService = "Google Cloud";
    private const string AzureUmbrella = "AzureCloud";
    private const string AwsUmbrella = "AMAZON";

    /// <summary>
    /// Every saved range holding the address: the umbrella ranges after the rest, each part narrowest first, then by
    /// provider, with the all-regions entries after the rest, then by service, region and prefix text.
    /// </summary>
    internal static IReadOnlyList<CloudAddressMatch> Find(byte[] address, CloudRangeCatalogue? google,
                                                          AzureTagCatalogue? azure, AwsRangeCatalogue? aws)
    {
        ArgumentNullException.ThrowIfNull(address);

        var matches = new List<CloudAddressMatch>();
        var seen = new HashSet<(CloudProvider, string, string, string)>();

        void Add(CloudAddressMatch match)
        {
            if (seen.Add((match.Provider, match.Service, match.Region, match.Prefix.Text)))
                matches.Add(match);
        }

        if (google is not null)
        {
            foreach (CloudPrefix prefix in google.Prefixes)
            {
                if (prefix.Block.Contains(address))
                    Add(new CloudAddressMatch(CloudProvider.GoogleCloud, GoogleService, prefix.Scope.Id, null, prefix.Block, false));
            }
        }

        if (azure is not null)
        {
            foreach (AzureServiceGroup group in azure.Services)
            {
                foreach (AzureServiceTag tag in group.Tags)
                {
                    bool umbrella = string.Equals(tag.BaseName, AzureUmbrella, StringComparison.Ordinal);
                    // A regional tag with no region in the file is named by its suffix, as the region picker names it
                    string region = tag.IsBase ? "" : tag.Region.Length > 0 ? tag.Region : tag.Suffix;
                    foreach (CidrBlock block in tag.Prefixes)
                    {
                        if (block.Contains(address))
                            Add(new CloudAddressMatch(CloudProvider.Azure, tag.Name, region, null, block, umbrella));
                    }
                }
            }
        }

        if (aws is not null)
        {
            // AllRegionsPrefixes keeps each range once and loses its region, so the regions are walked instead
            foreach (AwsService service in aws.Services)
            {
                bool umbrella = string.Equals(service.Name, AwsUmbrella, StringComparison.Ordinal);
                foreach (AwsRegionRanges region in service.Regions)
                {
                    foreach (AwsPrefix prefix in region.Prefixes)
                    {
                        if (!prefix.Block.Contains(address))
                            continue;

                        string? scope = string.Equals(prefix.BorderGroup, region.Region, StringComparison.Ordinal) ? null : prefix.BorderGroup;
                        Add(new CloudAddressMatch(CloudProvider.Aws, service.Name, region.Region, scope, prefix.Block, umbrella));
                    }
                }
            }
        }

        return matches
            .OrderBy(match => match.IsUmbrella)
            .ThenByDescending(match => match.Prefix.PrefixLength)
            .ThenBy(match => match.Provider)
            .ThenBy(match => match.Region.Length == 0)
            .ThenBy(match => match.Service, StringComparer.Ordinal)
            .ThenBy(match => match.Region, StringComparer.Ordinal)
            .ThenBy(match => match.Prefix.Text, StringComparer.Ordinal)
            .ToList();
    }
}
