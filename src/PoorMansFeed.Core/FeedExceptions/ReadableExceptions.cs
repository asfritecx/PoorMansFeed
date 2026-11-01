namespace PoorMansFeed.Core.FeedExceptions;

/// <summary>An exception list a feed writer may cut with; only a readable list can be wrapped.</summary>
internal sealed class ReadableExceptions
{
    private readonly ExceptionList _list;

    private ReadableExceptions(ExceptionList list) => _list = list;

    // Called only by IpInfoService.ExceptionsForFeed, the one refusal point; null while the list can't be read
    internal static ReadableExceptions? WrapForFeed(ExceptionList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.IsReadable ? new ReadableExceptions(list) : null;
    }

    public IReadOnlyList<string>? Cut(byte[] start, byte[] end) => _list.Cut(start, end);

    public bool Covers(byte[] address) => _list.Covers(address);

    public ExceptionCover Classify(byte[] start, byte[] end) => _list.Classify(start, end);
}
