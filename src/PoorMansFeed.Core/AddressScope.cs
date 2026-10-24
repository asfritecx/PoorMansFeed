namespace PoorMansFeed.Core;

/// <summary>
/// Blocks from the IANA special-purpose and address space registries. A false "public" leaks a private address and a
/// false "not public" only skips one, so a block only partly reachable is denied whole.
/// </summary>
public static class AddressScope
{
    private static readonly string[] Ipv4DenyTexts =
    [
        "0.0.0.0/8",        // "this network", RFC 791
        "10.0.0.0/8",       // private use, RFC 1918
        "100.64.0.0/10",    // shared address space, RFC 6598
        "127.0.0.0/8",      // loopback, RFC 1122
        "169.254.0.0/16",   // link local, RFC 3927
        "172.16.0.0/12",    // private use, RFC 1918
        "192.0.0.0/24",     // IETF protocol assignments, RFC 6890 (holds the anycast /32s of RFC 7723 and RFC 8155)
        "192.0.2.0/24",     // documentation (TEST-NET-1), RFC 5737
        "192.88.99.0/24",   // deprecated 6to4 relay anycast, RFC 7526
        "192.168.0.0/16",   // private use, RFC 1918
        "198.18.0.0/15",    // benchmarking, RFC 2544
        "198.51.100.0/24",  // documentation (TEST-NET-2), RFC 5737
        "203.0.113.0/24",   // documentation (TEST-NET-3), RFC 5737
        "224.0.0.0/4",      // multicast, RFC 5771
        "240.0.0.0/4",      // reserved, RFC 1112 (holds the limited broadcast address, RFC 919)
    ];

    // Global unicast, RFC 4291: IANA assigns unicast from here only, so every IPv6 address outside it is not public.
    private const string Ipv6GlobalUnicastText = "2000::/3";

    private static readonly string[] Ipv6DenyTexts =
    [
        "2001::/23",      // IETF protocol assignments, RFC 2928 (holds Teredo, benchmarking and ORCHID)
        "2001:db8::/32",  // documentation, RFC 3849
        "2002::/16",      // 6to4, RFC 3056: it embeds an IPv4 address, which may be private
        "3fff::/20",      // documentation, RFC 9637
    ];

    private static readonly Block[] Ipv4Deny = Parse(Ipv4DenyTexts);

    private static readonly Block Ipv6GlobalUnicast = Parse([Ipv6GlobalUnicastText])[0];

    private static readonly Block[] Ipv6Deny = Parse(Ipv6DenyTexts);

    /// <summary>
    /// True only for a 4-byte address outside every IPv4 deny block, or a 16-byte address inside 2000::/3 and
    /// outside every IPv6 deny block. Any other length is false.
    /// </summary>
    public static bool IsPublic(ReadOnlySpan<byte> address)
    {
        switch (address.Length)
        {
            case 4:
                return !InAny(Ipv4Deny, address);
            case 16:
                return Ipv6GlobalUnicast.Contains(address) && !InAny(Ipv6Deny, address);
            default:
                return false;
        }
    }

    private static bool InAny(Block[] blocks, ReadOnlySpan<byte> address)
    {
        foreach (Block block in blocks)
        {
            if (block.Contains(address))
                return true;
        }

        return false;
    }

    private static Block[] Parse(string[] texts)
    {
        var blocks = new Block[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            if (!CidrBlock.TryParse(texts[i], out CidrBlock? block) || block.Text != texts[i])
                throw new InvalidOperationException("An address scope block is not a canonical CIDR: " + texts[i]);

            // Start and End clone on every read, so take them once
            blocks[i] = new Block(block.Start, block.End);
        }

        return blocks;
    }

    // The database's membership rule: same length, then big-endian bytes compared in order
    private sealed record Block(byte[] Start, byte[] End)
    {
        public bool Contains(ReadOnlySpan<byte> address) =>
            address.Length == Start.Length
            && address.SequenceCompareTo(Start) >= 0
            && address.SequenceCompareTo(End) <= 0;
    }
}
