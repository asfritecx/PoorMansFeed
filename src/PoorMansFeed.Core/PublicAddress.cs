using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace PoorMansFeed.Core;

/// <summary>
/// An address <see cref="AddressScope"/> judged public: the only form the AbuseIPDB check sends (and, later, its feed
/// writes). The bytes are copied in and out, so nothing outside can change them after the check, and the text is
/// formatted once from those checked bytes.
/// </summary>
public sealed class PublicAddress
{
    private readonly byte[] _bytes;

    private PublicAddress(byte[] bytes, string text)
    {
        _bytes = bytes;
        Text = text;
    }

    /// <summary>False, with a null value, unless <see cref="AddressScope.IsPublic"/> passes the address.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> address, [NotNullWhen(true)] out PublicAddress? value)
    {
        value = null;
        // The copy is judged, so a caller's array changed after the check can't pass
        byte[] bytes = address.ToArray();
        if (!AddressScope.IsPublic(bytes))
            return false;

        value = new PublicAddress(bytes, new IPAddress(bytes).ToString());
        return true;
    }

    /// <summary>The 4 or 16 big-endian bytes, as a new copy on each read.</summary>
    public byte[] Bytes => (byte[])_bytes.Clone();

    /// <summary>The canonical text of the checked bytes, such as "8.8.8.8" or "2001:4860::8888".</summary>
    public string Text { get; }
}
