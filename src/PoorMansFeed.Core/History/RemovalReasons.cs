using System.Diagnostics;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Core.History;

/// <summary>Why an entry left a feed, judged by the write's source, the ranges left out and the exceptions.</summary>
internal sealed class RemovalReasons
{
    private readonly ReadableExceptions _exceptions;
    private readonly IEnumerable<(byte[] Start, byte[] End)> _source;
    private readonly IEnumerable<(byte[] Start, byte[] End)> _leftOut;
    private readonly FeedFamilies _familiesOff;
    private readonly FeedRemovalReason _dropped;
    private Sets? _sets;
    private bool _unbuildable;
    private bool _traced;

    public RemovalReasons(ReadableExceptions exceptions,
                          IEnumerable<(byte[] Start, byte[] End)> source,
                          IEnumerable<(byte[] Start, byte[] End)> leftOut,
                          FeedFamilies? familiesOff,
                          FeedRemovalReason dropped)
    {
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(leftOut);
        if (dropped is not (FeedRemovalReason.SourceDropped or FeedRemovalReason.LeftTheList))
            throw new ArgumentOutOfRangeException(nameof(dropped), dropped,
                                                  "The dropped reason is SourceDropped or LeftTheList.");

        _exceptions = exceptions;
        _source = source;
        _leftOut = leftOut;
        _familiesOff = familiesOff ?? 0;
        _dropped = dropped;
    }

    /// <summary>The reason for one removed entry's text; it never throws.</summary>
    public FeedRemovalReason For(string removedText)
    {
        try
        {
            // Built at the first removal, so a write that removes nothing never reads a source of a million addresses
            Sets? sets = _sets ??= Build();
            if (sets is null)
                return FeedRemovalReason.NotTold;

            if (!ExportWriter.TryFeedEntry(removedText, out bool isIpv6, out byte[] start, out byte[] end))
                return FeedRemovalReason.NotTold;
            if ((_familiesOff & (isIpv6 ? FeedFamilies.IPv6 : FeedFamilies.IPv4)) != 0)
                return FeedRemovalReason.FamilyOff;
            if (_exceptions.Classify(start, end) != ExceptionCover.None)
                return FeedRemovalReason.Exception;

            var range = new AddressInterval(RangeSubtraction.ToNumber(start), RangeSubtraction.ToNumber(end));
            if (RangeSubtraction.Subtract(range, isIpv6 ? sets.LeftOutIpv6 : sets.LeftOutIpv4) is { Count: 0 })
                return FeedRemovalReason.LeftOut;

            // The source still has some of it: an old piece of a cut, or a range that shrank
            if (RangeSubtraction.Subtract(range, isIpv6 ? sets.SourceIpv6 : sets.SourceIpv4) is not null)
                return FeedRemovalReason.NotTold;

            return _dropped;
        }
        catch (Exception ex)
        {
            if (!_traced)
            {
                _traced = true;
                Trace.TraceError($"A removal reason couldn't be told ({ex.GetType().FullName}).");
            }

            return FeedRemovalReason.NotTold;
        }
    }

    private Sets? Build()
    {
        if (_unbuildable)
            return null;
        try
        {
            (List<AddressInterval> sourceIpv4, List<AddressInterval> sourceIpv6) = Merged(_source);
            (List<AddressInterval> leftOutIpv4, List<AddressInterval> leftOutIpv6) = Merged(_leftOut);
            return new Sets(sourceIpv4, sourceIpv6, leftOutIpv4, leftOutIpv6);
        }
        catch (Exception ex)
        {
            // By type alone: a message could quote an address of the set
            _unbuildable = true;
            Trace.TraceError($"A feed write's removal reasons couldn't be built ({ex.GetType().FullName}).");
            return null;
        }
    }

    private static (List<AddressInterval> Ipv4, List<AddressInterval> Ipv6) Merged(IEnumerable<(byte[] Start, byte[] End)> ranges)
    {
        var ipv4 = new List<AddressInterval>();
        var ipv6 = new List<AddressInterval>();
        foreach ((byte[] start, byte[] end) in ranges)
        {
            ArgumentNullException.ThrowIfNull(start);
            ArgumentNullException.ThrowIfNull(end);
            if (start.Length != end.Length)
                throw new ArgumentException("A range's start and end are of one family.", nameof(ranges));

            var interval = new AddressInterval(RangeSubtraction.ToNumber(start), RangeSubtraction.ToNumber(end));
            (start.Length == 16 ? ipv6 : ipv4).Add(interval);
        }

        return (RangeSubtraction.Merge(ipv4), RangeSubtraction.Merge(ipv6));
    }

    private sealed record Sets(List<AddressInterval> SourceIpv4, List<AddressInterval> SourceIpv6,
                               List<AddressInterval> LeftOutIpv4, List<AddressInterval> LeftOutIpv6);
}
