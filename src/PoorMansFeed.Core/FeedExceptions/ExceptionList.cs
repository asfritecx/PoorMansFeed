using System.Globalization;

namespace PoorMansFeed.Core.FeedExceptions;

/// <summary>
/// A feed written as if there were no exceptions could block the operator's own networks, so writers refuse an
/// unreadable list and branch on IsReadable; Entries, Cut and Covers throw on one, so a forgotten check fails.
/// </summary>
public sealed class ExceptionList
{
    public const int MaxEntries = 5_000;

    private readonly IReadOnlyList<ExceptionEntry> _entries;
    private readonly List<AddressInterval> _ipv4;
    private readonly List<AddressInterval> _ipv6;

    private ExceptionList(IReadOnlyList<ExceptionEntry> entries, bool isReadable)
    {
        _entries = entries;
        IsReadable = isReadable;
        _ipv4 = RangeSubtraction.Merge(Intervals(entries, ipv6: false));
        _ipv6 = RangeSubtraction.Merge(Intervals(entries, ipv6: true));
    }

    public static ExceptionList Empty { get; } = new([], isReadable: true);

    public static ExceptionList Unreadable { get; } = new([], isReadable: false);

    /// <summary>In the operator's order. Throws on an unreadable list.</summary>
    public IReadOnlyList<ExceptionEntry> Entries
    {
        get
        {
            ThrowIfUnreadable();
            return _entries;
        }
    }

    public bool IsReadable { get; }

    /// <summary>A readable list of the entries in their order, without repeats of a network (the first is kept).</summary>
    public static ExceptionList Create(IEnumerable<ExceptionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<CidrBlock>();
        var kept = new List<ExceptionEntry>();
        foreach (ExceptionEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (seen.Add(entry.Network))
                kept.Add(entry);
        }

        if (kept.Count > MaxEntries)
            throw new ArgumentException($"At most {MaxEntries} exceptions can be kept.", nameof(entries));

        return new ExceptionList(kept, isReadable: true);
    }

    /// <summary>
    /// The range with every exception cut out, as canonical CIDRs, lowest first. Null when no exception overlaps it
    /// (write the range as it was); empty when the exceptions cover all of it.
    /// </summary>
    internal IReadOnlyList<string>? Cut(byte[] start, byte[] end)
    {
        ThrowIfUnreadable();
        bool ipv6 = start.Length == 16;
        List<AddressInterval> exceptions = ipv6 ? _ipv6 : _ipv4;
        if (exceptions.Count == 0)
            return null;

        var range = new AddressInterval(RangeSubtraction.ToNumber(start), RangeSubtraction.ToNumber(end));
        List<AddressInterval>? left = RangeSubtraction.Subtract(range, exceptions);
        if (left is null)
            return null;

        var cidrs = new List<string>();
        foreach (AddressInterval piece in left)
            RangeSubtraction.AppendCidrs(piece, ipv6, cidrs);

        return cidrs;
    }

    /// <summary>True when an exception holds the address (4 or 16 bytes).</summary>
    internal bool Covers(byte[] address)
    {
        ThrowIfUnreadable();
        List<AddressInterval> exceptions = address.Length == 16 ? _ipv6 : _ipv4;
        return exceptions.Count > 0 && RangeSubtraction.Contains(exceptions, RangeSubtraction.ToNumber(address));
    }

    private void ThrowIfUnreadable()
    {
        if (!IsReadable)
            throw new InvalidOperationException("The exceptions list couldn't be read. Check IsReadable and refuse the feed write first.");
    }

    private static IEnumerable<AddressInterval> Intervals(IReadOnlyList<ExceptionEntry> entries, bool ipv6)
    {
        foreach (ExceptionEntry entry in entries)
        {
            if (entry.Network.IsIpv6 == ipv6)
                yield return new AddressInterval(RangeSubtraction.ToNumber(entry.Network.Start), RangeSubtraction.ToNumber(entry.Network.End));
        }
    }
}

/// <summary>
/// A family's feed header gets an exceptions line only when its cut and removed counts aren't both zero, so a feed no
/// exception touched is written byte for byte as before.
/// </summary>
internal sealed class ExceptionTally
{
    private int _ipv4Cut;
    private int _ipv4Removed;
    private int _ipv6Cut;
    private int _ipv6Removed;

    public int Cut => _ipv4Cut + _ipv6Cut;

    public int Removed => _ipv4Removed + _ipv6Removed;

    /// <summary>Counts one range by what Cut gave for it.</summary>
    public void Count(bool ipv6, IReadOnlyList<string>? pieces)
    {
        if (pieces is null)
            return;

        if (pieces.Count == 0)
        {
            if (ipv6) _ipv6Removed++; else _ipv4Removed++;
        }
        else
        {
            if (ipv6) _ipv6Cut++; else _ipv4Cut++;
        }
    }

    /// <summary>The header line for a file holding these families, or null when exceptions changed nothing in them.</summary>
    public string? HeaderLine(bool includesIpv4, bool includesIpv6)
    {
        int cut = (includesIpv4 ? _ipv4Cut : 0) + (includesIpv6 ? _ipv6Cut : 0);
        int removed = (includesIpv4 ? _ipv4Removed : 0) + (includesIpv6 ? _ipv6Removed : 0);
        if (cut == 0 && removed == 0)
            return null;

        return string.Create(CultureInfo.InvariantCulture, $"Exceptions: {cut} {(cut == 1 ? "range" : "ranges")} cut, {removed} removed");
    }
}
