using PoorMansFeed.Core.History;

namespace PoorMansFeed;

/// <summary>One entry added to or removed from a feed, already formatted.</summary>
public sealed class HistoryChangeRow
{
    private HistoryChangeRow(HistoryRow row, string feedName)
    {
        WhenText = AppText.HistoryWhen(row.AtUtc);
        FeedText = feedName;
        EntryText = row.Entry;
        ChangeText = row.Added ? AppText.HistoryAdded : AppText.HistoryRemoved;
        CauseText = AppText.HistoryCause(row.Cause);

        // An added entry has no removal reason, whatever the row carries
        ReasonText = row.Added ? "" : AppText.HistoryReason(row.Reason);
        FamilyText = AppText.HistoryFamily(row.IsIPv6, row.Copy);
        CheckedAgainstText = AppText.HistoryCheckedAgainst(row.DatasetDate, row.ListDownloadedUtc);
        AccessibleText = string.Join(", ", new[]
            {
                WhenText, FeedText, EntryText, ChangeText, CauseText, ReasonText, FamilyText, CheckedAgainstText,
            }.Where(text => text.Length > 0));
    }

    /// <summary>The feed's saved name when its key is saved, otherwise the key.</summary>
    internal static HistoryChangeRow From(HistoryRow row, string feedName)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(feedName);
        return new HistoryChangeRow(row, feedName);
    }

    public string WhenText { get; }
    public string FeedText { get; }
    public string EntryText { get; }
    public string ChangeText { get; }
    public string CauseText { get; }
    public string ReasonText { get; }
    public string FamilyText { get; }
    public string CheckedAgainstText { get; }
    public string AccessibleText { get; }
}
