using System.Diagnostics;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>
/// A range a write saw, in source order. LargeAnswer is true when included at this write, false when left out, and
/// null when the write didn't ask.
/// </summary>
internal readonly record struct SeenRange(CidrBlock Block, bool Ticked, bool StartsIncluded, bool Written, bool? LargeAnswer);

internal sealed record PickLists(IReadOnlyList<string> HandTicked, IReadOnlyList<string> HandUnticked,
                                 IReadOnlyList<string> LargeIncluded, IReadOnlyList<string> LargeLeftOut,
                                 IReadOnlyList<string> SeenUnconfirmed);

/// <summary>What a write saves of its saved pick's hand ticks and large-range answers.</summary>
internal static class SavedPickCarry
{
    /// <summary>The saved pick of this key, ignoring case, and kind; null when none or unreadable.</summary>
    internal static SavedFeedPick? Prior(IFeedRecords records, string key, FeedKind kind)
    {
        try
        {
            SavedFeedsRead read = records.ReadSavedFeeds();
            if (!read.Available)
                return null;
            return read.Feeds.FirstOrDefault(feed => feed is not null && feed.Kind == kind
                && string.Equals(feed.Key, key, StringComparison.OrdinalIgnoreCase))?.Pick;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"The saved pick couldn't be read before a save ({ex.GetType().FullName}).");
            return null;
        }
    }

    /// <summary>
    /// A saved entry is kept while the range's state still agrees with it, and for a range not seen only when its
    /// family is switched off; seen ranges come first in source order, then carried entries in saved order.
    /// </summary>
    internal static PickLists Lists(IReadOnlyList<SeenRange> seen, SavedFeedPick? prior, FeedFamilies? familiesOff)
    {
        List<CidrBlock> priorTicked = Parsed(prior?.HandTicked);
        List<CidrBlock> priorUnticked = Parsed(prior?.HandUnticked);
        List<CidrBlock> priorIncluded = Parsed(prior?.LargeIncluded);
        List<CidrBlock> priorLeftOut = Parsed(prior?.LargeLeftOut);
        HashSet<string> ticked = Texts(priorTicked), unticked = Texts(priorUnticked);
        HashSet<string> included = Texts(priorIncluded), leftOut = Texts(priorLeftOut);

        var handTicked = new OrderedTexts();
        var handUnticked = new OrderedTexts();
        var largeIncluded = new OrderedTexts();
        var largeLeftOut = new OrderedTexts();
        var seenUnconfirmed = new OrderedTexts();
        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (SeenRange range in seen)
        {
            if (range.Block is null)
                continue;
            string text = range.Block.Text;
            seenTexts.Add(text);
            // Only a confirmed range starts ticked, so a range that doesn't is mixed or not in the database
            if (!range.StartsIncluded)
                seenUnconfirmed.Add(text);
            if (range.Ticked && (!range.StartsIncluded || ticked.Contains(text)))
                handTicked.Add(text);
            if (!range.Ticked && (range.StartsIncluded || unticked.Contains(text)))
                handUnticked.Add(text);

            if (range.LargeAnswer is bool answer)
                (answer ? largeIncluded : largeLeftOut).Add(text);
            else if (range.Written && included.Contains(text))
                largeIncluded.Add(text);
            else if (!range.Written && leftOut.Contains(text))
                largeLeftOut.Add(text);
        }

        void Carry(OrderedTexts list, List<CidrBlock> priorEntries)
        {
            foreach (CidrBlock block in priorEntries)
            {
                FeedFamilies family = block.IsIpv6 ? FeedFamilies.IPv6 : FeedFamilies.IPv4;
                if (!seenTexts.Contains(block.Text) && familiesOff is { } off && (off & family) != 0)
                    list.Add(block.Text);
            }
        }

        Carry(handTicked, priorTicked);
        Carry(handUnticked, priorUnticked);
        Carry(largeIncluded, priorIncluded);
        Carry(largeLeftOut, priorLeftOut);
        Carry(seenUnconfirmed, Parsed(prior?.SeenUnconfirmed));
        return new PickLists(handTicked.Items, handUnticked.Items, largeIncluded.Items, largeLeftOut.Items,
                             seenUnconfirmed.Items);
    }

    private static List<CidrBlock> Parsed(IReadOnlyList<string>? entries)
    {
        var blocks = new List<CidrBlock>();
        foreach (string? entry in entries ?? [])
        {
            if (CidrBlock.TryParse(entry, out CidrBlock? block))
                blocks.Add(block);
        }

        return blocks;
    }

    private static HashSet<string> Texts(List<CidrBlock> blocks) =>
        blocks.Select(block => block.Text).ToHashSet(StringComparer.Ordinal);

    private sealed class OrderedTexts
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public List<string> Items { get; } = [];

        public void Add(string text)
        {
            if (_seen.Add(text))
                Items.Add(text);
        }
    }
}
