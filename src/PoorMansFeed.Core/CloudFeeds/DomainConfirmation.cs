using PoorMansFeed.Core;

namespace PoorMansFeed.Core.CloudFeeds;

public static class DomainConfirmation
{
    /// <summary>
    /// NotInDatabase when there is no group. Confirmed when every group's AsDomain, trimmed, equals the domain
    /// under OrdinalIgnoreCase. Mixed otherwise, including a null or blank domain.
    /// </summary>
    public static RangeConfirmation Classify(IReadOnlyList<OverlapGroup> groups, string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return Classify(groups, [domain]);
    }

    /// <summary>
    /// The same rule for a provider with several domains: Confirmed when every group's AsDomain is one of them.
    /// </summary>
    public static RangeConfirmation Classify(IReadOnlyList<OverlapGroup> groups, IReadOnlyCollection<string> domains)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(domains);
        if (domains.Count == 0)
            throw new ArgumentException("At least one domain is required.", nameof(domains));
        if (groups.Count == 0)
            return RangeConfirmation.NotInDatabase;

        foreach (OverlapGroup group in groups)
        {
            string groupDomain = group.AsDomain?.Trim() ?? "";
            bool known = false;
            foreach (string domain in domains)
            {
                if (string.Equals(groupDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
                return RangeConfirmation.Mixed;
        }

        return RangeConfirmation.Confirmed;
    }
}
