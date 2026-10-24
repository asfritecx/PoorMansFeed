using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace PoorMansFeed.Core.FeedExceptions;

/// <summary>A network that no Fortigate feed may block, with the operator's note on why.</summary>
public sealed record ExceptionEntry
{
    public const int MaxNoteLength = 200;

    public ExceptionEntry(CidrBlock network, string? note)
    {
        Network = network ?? throw new ArgumentNullException(nameof(network));
        Note = CleanNote(note);
    }

    public CidrBlock Network { get; }

    public string Note { get; }

    /// <summary>
    /// A bare IPv4-mapped address is read as IPv4, but an IPv4-mapped CIDR is rejected, as CidrBlock rejects it.
    /// hostBitsCleared says whether clearing the host bits changed the address, so the operator can be told.
    /// </summary>
    public static bool TryParseNetwork(string? text, [NotNullWhen(true)] out CidrBlock? network, out bool hostBitsCleared)
    {
        network = null;
        hostBitsCleared = false;
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
            return false;

        int slash = value.IndexOf('/');
        if (slash < 0)
        {
            if (!IpAddressText.TryParse(value, out IPAddress? address))
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            string bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32";
            return CidrBlock.TryParse(address + bits, out network);
        }

        if (!CidrBlock.TryParse(value, out network))
            return false;

        // CidrBlock has read the address part already, so this cannot fail
        if (IpAddressText.TryParse(value.AsSpan(0, slash), out IPAddress? given))
            hostBitsCleared = !given.GetAddressBytes().AsSpan().SequenceEqual(network.Start);

        return true;
    }

    /// <summary>
    /// The note on one line: control characters and line or paragraph separators become spaces, the ends are
    /// trimmed, and it is cut to MaxNoteLength without splitting a surrogate pair.
    /// </summary>
    public static string CleanNote(string? note)
    {
        if (string.IsNullOrEmpty(note))
            return "";

        string single = string.Create(note.Length, note, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = (char.IsControl(c) || c is '\u2028' or '\u2029') ? ' ' : c;
            }
        }).Trim();

        if (single.Length <= MaxNoteLength)
            return single;

        int cut = char.IsHighSurrogate(single[MaxNoteLength - 1]) ? MaxNoteLength - 1 : MaxNoteLength;
        return single[..cut].TrimEnd();
    }
}
