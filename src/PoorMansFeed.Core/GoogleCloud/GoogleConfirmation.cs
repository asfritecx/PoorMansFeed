using PoorMansFeed.Core;
using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core.GoogleCloud;

public static class GoogleConfirmation
{
    public const string GoogleDomain = "google.com";

    /// <summary>The shared rule with google.com as the domain. See <see cref="DomainConfirmation.Classify"/>.</summary>
    public static RangeConfirmation Classify(IReadOnlyList<OverlapGroup> groups) =>
        DomainConfirmation.Classify(groups, GoogleDomain);
}
