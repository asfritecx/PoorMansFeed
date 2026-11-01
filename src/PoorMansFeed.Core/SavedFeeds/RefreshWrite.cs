namespace PoorMansFeed.Core;

/// <summary>What a refresh tells its write, and what the write tells it back: a skip or its proof.</summary>
internal sealed class RefreshWrite(SavedFeed of)
{
    public SavedFeed Of { get; } = of;
    public bool Skipped { get; set; }
    public SavedFeedWriteProof? Proof { get; set; }
}
