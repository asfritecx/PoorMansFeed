using PoorMansFeed.Core;
using PoorMansFeed.Core.History;

namespace PoorMansFeed;

/// <summary>One saved feed, already formatted; Retire and Remove send its own key and kind together.</summary>
public sealed class SavedFeedRow
{
    private readonly FeedRefreshResult? _result;

    private SavedFeedRow(SavedFeed feed, FeedRefreshResult? result, string? asName)
    {
        Feed = feed;
        _result = result;
        Key = feed.Key;
        Kind = feed.Kind;
        IsRetired = feed.Retired;
        NameText = AppText.SavedFeedName(feed);
        AsNameText = asName is null ? "" : AppText.SavedFeedAsName(asName);
        DisplayNameText = AsNameText.Length == 0 ? NameText : NameText + AppText.Joiner + AsNameText;
        KindText = AppText.SavedFeedKind(feed.Kind);
        LastWriteText = AppText.SavedFeedLastWrite(feed.LastWriteUtc);
        LastRefreshText = result is null
            ? AppText.SavedFeedLastRefresh(feed.LastRefreshUtc)
            : AppText.SavedFeedRefreshCell(result);
        LastRefreshTip = result is null ? LastRefreshText : AppText.SavedFeedRefreshTip(result, feed.LastRefreshUtc);
        IsRefreshWarning = result is not null && AppText.SavedFeedRefreshIsWarning(result);
        StateText = feed.Retired ? AppText.SavedFeedRetiredState : "";
        RetireName = AppText.RetireSavedFeedName(NameText);
        RemoveName = AppText.RemoveSavedFeedName(NameText);
        AccessibleText = string.Join(", ", new[] { NameText, AsNameText, KindText, LastWriteText, LastRefreshTip, StateText }
            .Where(text => text.Length > 0));
    }

    /// <summary>With no result from the last Refresh all, the cell shows when the feed was last refreshed. The AS name,
    /// from the dataset, shows only beside the name: Retire, Remove and the history name the feed by its key.</summary>
    internal static SavedFeedRow From(SavedFeed feed, FeedRefreshResult? result = null, string? asName = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        return new SavedFeedRow(feed, result, asName);
    }

    /// <summary>The same row with the AS name the dataset gave, or none.</summary>
    internal SavedFeedRow WithAsName(string? asName) => new(Feed, _result, asName);

    internal SavedFeed Feed { get; }

    public string Key { get; }
    public FeedKind Kind { get; }
    public bool IsRetired { get; }
    public string NameText { get; }
    public string AsNameText { get; }
    public string DisplayNameText { get; }
    public string KindText { get; }
    public string LastWriteText { get; }
    public string LastRefreshText { get; }
    public string LastRefreshTip { get; }
    public bool IsRefreshWarning { get; }
    public string StateText { get; }
    public string RetireName { get; }
    public string RemoveName { get; }
    public string AccessibleText { get; }
}
