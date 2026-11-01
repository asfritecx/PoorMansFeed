using PoorMansFeed.Core.CloudFeeds;

namespace PoorMansFeed.Core;

/// <summary>
/// What a saved cloud pick chooses from a check, each list in check order: the ranges ticked, the large ranges it
/// includes, and the unconfirmed and large ranges it has never answered.
/// </summary>
public sealed record RefreshPicked(IReadOnlyList<CidrBlock> Ticked, IReadOnlyList<CidrBlock> LargeIncluded,
                                   IReadOnlyList<string> NewUnconfirmed, IReadOnlyList<string> NewLarge);

/// <summary>The one rule for which ranges of a cloud check a saved pick writes, from a page or a refresh.</summary>
public static class RefreshPick
{
    public static RefreshPicked Apply(CloudFeedCheck check, SavedFeedPick pick)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(pick);
        var handTicked = new HashSet<string>(pick.HandTicked, StringComparer.Ordinal);
        var handUnticked = new HashSet<string>(pick.HandUnticked, StringComparer.Ordinal);
        var largeIncluded = new HashSet<string>(pick.LargeIncluded, StringComparer.Ordinal);
        var largeLeftOut = new HashSet<string>(pick.LargeLeftOut, StringComparer.Ordinal);
        var seen = new HashSet<string>(pick.SeenUnconfirmed ?? [], StringComparer.Ordinal);

        var ticked = new List<CidrBlock>();
        var includedLarge = new List<CidrBlock>();
        var newUnconfirmed = new List<string>();
        var newLarge = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (CheckedRange range in check.Ranges)
        {
            string text = range.Prefix.Block.Text;
            if (!done.Add(text))
                continue;

            // A hand tick wins over a hand untick of the same range, as on the page
            bool isTicked = handTicked.Contains(text) || (range.StartsIncluded && !handUnticked.Contains(text));
            if (!range.StartsIncluded && !handTicked.Contains(text) && !handUnticked.Contains(text) && !seen.Contains(text))
                newUnconfirmed.Add(text);
            if (!isTicked)
                continue;

            ticked.Add(range.Prefix.Block);
            if (!range.IsLarge)
                continue;
            if (largeIncluded.Contains(text))
                includedLarge.Add(range.Prefix.Block);
            else if (!largeLeftOut.Contains(text))
                newLarge.Add(text);
        }

        return new RefreshPicked(ticked, includedLarge, newUnconfirmed, newLarge);
    }
}
