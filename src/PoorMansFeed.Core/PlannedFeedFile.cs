namespace PoorMansFeed.Core;

/// <summary>
/// One file a feed write moves into place, in write order. The file's text is its type's first line, the line end and
/// Body; Lines are Body's entry lines as written, and Emptied marks a family's old file rewritten header-only.
/// </summary>
internal sealed record PlannedFeedFile(string Path, FeedType Type, FeedFamilies Families, string Body,
                                       IReadOnlyList<string> Lines, bool Emptied)
{
    public string Text => FeedTypes.FirstLine(Type) + FeedText.LineEnd + Body;
}

/// <summary>An ASN feed write's files and the counts its result reports.</summary>
internal sealed record AsnFeedPlan(IReadOnlyList<PlannedFeedFile> Files, int ExceptionCuts, int ExceptionRemovals,
                                   int WholeRangesLeftOut)
{
    public static AsnFeedPlan Empty { get; } = new([], 0, 0, 0);
}
