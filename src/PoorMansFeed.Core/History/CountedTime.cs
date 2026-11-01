namespace PoorMansFeed.Core.History;

/// <summary>The length of the union of the spans counted so far, kept as disjoint spans in order.</summary>
internal sealed class CountedTime
{
    private readonly List<(long Start, long End)> _spans = [];

    internal long Total { get; private set; }

    /// <summary>Counts the time just before <paramref name="to"/>, up to the cap, not counted already.</summary>
    internal void AddStep(long from, long to, long cap)
    {
        if (to <= from)
            return;
        // Saturates instead of wrapping, so a stamp edited to the end of the range still gives at most the cap
        long start = to < long.MinValue + cap ? long.MinValue : to - cap;
        Add(Math.Max(start, from), to);
    }

    private void Add(long start, long end)
    {
        int first = FirstEndingAtOrAfter(start);
        int last = first;
        while (last < _spans.Count && _spans[last].Start <= end)
        {
            (long spanStart, long spanEnd) = _spans[last];
            start = Math.Min(start, spanStart);
            end = Math.Max(end, spanEnd);
            last++;
        }
        long added = end - start;
        for (int i = first; i < last; i++)
            added -= _spans[i].End - _spans[i].Start;
        _spans.RemoveRange(first, last - first);
        _spans.Insert(first, (start, end));
        Total += added;
    }

    private int FirstEndingAtOrAfter(long start)
    {
        int low = 0, high = _spans.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (_spans[mid].End < start)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }
}
