namespace PoorMansFeed.Core;

public enum FeedFileState { Missing, Unreadable, Read }

/// <summary>The families a feed file's slot holds, set by its writer: the ASN feed's unsplit file holds both.</summary>
[Flags]
public enum FeedFamilies { IPv4 = 1, IPv6 = 2, Both = IPv4 | IPv6 }

public sealed record FeedEntryChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public static FeedEntryChanges None { get; } = new([], []);

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// One written feed file's changes per family against the file it replaced, which OldFile says was missing, unreadable
/// or read: Added as written, in write order; Removed as the old file had them, in its order.
/// </summary>
public sealed record FeedFileChange(string Path, FeedType Type, FeedFamilies Families,
                                    FeedEntryChanges IPv4, FeedEntryChanges IPv6, FeedFileState OldFile,
                                    string? WrittenSha256 = null)
{
    public bool IsEmpty => IPv4.IsEmpty && IPv6.IsEmpty;
}
