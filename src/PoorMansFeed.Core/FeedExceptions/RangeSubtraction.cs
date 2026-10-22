using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace PoorMansFeed.Core.FeedExceptions;

/// <summary>An inclusive range of addresses of one family, as numbers. IPv4 uses the low 32 bits.</summary>
internal readonly record struct AddressInterval(UInt128 Start, UInt128 End);

/// <summary>
/// Range arithmetic for exceptions: merging the exceptions, cutting them out of a range, and writing what is left
/// as the fewest CIDRs that cover exactly the same addresses.
/// </summary>
internal static class RangeSubtraction
{
    public static UInt128 ToNumber(ReadOnlySpan<byte> address) => address.Length switch
    {
        4 => BinaryPrimitives.ReadUInt32BigEndian(address),
        16 => BinaryPrimitives.ReadUInt128BigEndian(address),
        _ => throw new ArgumentException("An address is 4 or 16 bytes.", nameof(address)),
    };

    /// <summary>Sorts the intervals and joins the ones that overlap or touch, so the result is sorted and disjoint.</summary>
    public static List<AddressInterval> Merge(IEnumerable<AddressInterval> intervals)
    {
        var sorted = intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToList();
        var merged = new List<AddressInterval>(sorted.Count);
        foreach (AddressInterval next in sorted)
        {
            if (merged.Count > 0)
            {
                AddressInterval last = merged[^1];
                // The top of the space has no next address, so it absorbs everything after it
                if (last.End == UInt128.MaxValue || next.Start <= last.End + 1)
                {
                    if (next.End > last.End)
                        merged[^1] = last with { End = next.End };
                    continue;
                }
            }

            merged.Add(next);
        }

        return merged;
    }

    /// <summary>
    /// What is left of the range once every exception is cut out. Null when no exception overlaps the range; an
    /// empty list when the exceptions cover it. The exceptions must be sorted and disjoint (Merge).
    /// </summary>
    public static List<AddressInterval>? Subtract(AddressInterval range, IReadOnlyList<AddressInterval> exceptions)
    {
        int index = FirstEndingAtOrAfter(exceptions, range.Start);
        if (index >= exceptions.Count || exceptions[index].Start > range.End)
            return null;

        var left = new List<AddressInterval>();
        UInt128 cursor = range.Start;
        for (int i = index; i < exceptions.Count && exceptions[i].Start <= range.End; i++)
        {
            AddressInterval exception = exceptions[i];
            if (exception.Start > cursor)
                left.Add(new AddressInterval(cursor, exception.Start - 1));

            if (exception.End >= range.End)
                return left;

            // exception.End is below range.End here, so the next address exists
            cursor = exception.End + 1;
        }

        left.Add(new AddressInterval(cursor, range.End));
        return left;
    }

    /// <summary>True when an exception holds the address. The exceptions must be sorted and disjoint (Merge).</summary>
    public static bool Contains(IReadOnlyList<AddressInterval> exceptions, UInt128 address)
    {
        int index = FirstEndingAtOrAfter(exceptions, address);
        return index < exceptions.Count && exceptions[index].Start <= address;
    }

    /// <summary>
    /// Appends the fewest CIDRs that cover exactly the interval, lowest first, in canonical text (as CidrBlock
    /// writes it). Each step takes the largest block that starts at the next address and ends inside the interval.
    /// </summary>
    public static void AppendCidrs(AddressInterval interval, bool ipv6, List<string> cidrs)
    {
        int totalBits = ipv6 ? 128 : 32;
        UInt128 current = interval.Start;
        while (true)
        {
            int hostBits = current == UInt128.Zero ? totalBits : Math.Min(totalBits, (int)UInt128.TrailingZeroCount(current));
            while (hostBits > 0 && current + Span(hostBits) > interval.End)
                hostBits--;

            UInt128 blockEnd = current + Span(hostBits);
            cidrs.Add(Format(current, ipv6) + "/" + (totalBits - hostBits).ToString(CultureInfo.InvariantCulture));
            if (blockEnd >= interval.End)
                return;

            current = blockEnd + 1;
        }
    }

    // The number of addresses in a block with this many host bits, less one. A block is aligned, so adding this
    // to its start never passes the top of the space.
    private static UInt128 Span(int hostBits) => hostBits >= 128 ? UInt128.MaxValue : (UInt128.One << hostBits) - 1;

    private static string Format(UInt128 value, bool ipv6)
    {
        byte[] bytes = new byte[ipv6 ? 16 : 4];
        if (ipv6)
            BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);

        return new IPAddress(bytes).ToString();
    }

    // The index of the first interval whose end is at or after the address, or Count when there is none
    private static int FirstEndingAtOrAfter(IReadOnlyList<AddressInterval> intervals, UInt128 address)
    {
        int low = 0;
        int high = intervals.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (intervals[middle].End < address)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
