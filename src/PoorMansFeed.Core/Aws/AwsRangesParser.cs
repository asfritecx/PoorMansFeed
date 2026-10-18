using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace PoorMansFeed.Core.Aws;

/// <summary>What one ip-ranges.json holds, before it is tied to a download.</summary>
internal sealed record ParsedAwsRanges(string SyncToken, DateTimeOffset CreatedUtc, IReadOnlyList<AwsService> Services,
                                       int SkippedPrefixes);

/// <summary>
/// Reads ip-ranges.json. An entry that cannot be used is skipped and counted, never fatal. Service, region and
/// border group are checked because the first two end up in feed file names and header lines.
/// </summary>
internal static class AwsRangesParser
{
    internal const string GlobalRegion = "GLOBAL";
    private const int MaxSyncTokenLength = 20;
    private const int MaxServiceLength = 64;
    private const int MaxRegionLength = 32;
    private const int MaxBorderGroupLength = 64;
    private const string CreateDateFormat = "yyyy-MM-dd-HH-mm-ss";

    private readonly record struct Entry(string Service, string Region, AwsPrefix Prefix);

    public static ParsedAwsRanges? TryParse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "syncToken", out string? syncToken) || !IsValidSyncToken(syncToken)
                || !TryGetString(root, "createDate", out string? createDate)
                || !DateTimeOffset.TryParseExact(createDate, CreateDateFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset createdUtc))
            {
                return null;
            }

            var entries = new List<Entry>();
            int skipped = 0;
            ReadEntries(root, "prefixes", "ip_prefix", entries, ref skipped);
            ReadEntries(root, "ipv6_prefixes", "ipv6_prefix", entries, ref skipped);
            if (entries.Count == 0)
                return null;

            return new ParsedAwsRanges(syncToken, createdUtc, BuildServices(entries), skipped);
        }
    }

    /// <summary>Digits only. The sync token is written into a feed header, so nothing else may pass.</summary>
    public static bool IsValidSyncToken([NotNullWhen(true)] string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxSyncTokenLength)
            return false;

        foreach (char character in token)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static void ReadEntries(JsonElement root, string arrayName, string cidrName, List<Entry> entries, ref int skipped)
    {
        if (!root.TryGetProperty(arrayName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (TryReadEntry(item, cidrName, out Entry entry))
                entries.Add(entry);
            else
                skipped++;
        }
    }

    private static bool TryReadEntry(JsonElement item, string cidrName, out Entry entry)
    {
        entry = default;
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetString(item, cidrName, out string? cidr)
            || !TryGetString(item, "region", out string? region) || !IsValidRegion(region)
            || !TryGetString(item, "service", out string? service) || !IsValidService(service)
            || !CidrBlock.TryParse(cidr, out CidrBlock? block))
        {
            return false;
        }

        string borderGroup = region;
        if (item.TryGetProperty("network_border_group", out JsonElement group) && group.ValueKind != JsonValueKind.Null)
        {
            if (group.ValueKind != JsonValueKind.String || group.GetString() is not { } text || !IsValidBorderGroup(text))
                return false;
            borderGroup = text;
        }

        entry = new Entry(service, region, new AwsPrefix(block, borderGroup));
        return true;
    }

    private static List<AwsService> BuildServices(List<Entry> entries)
    {
        var services = new List<AwsService>();
        foreach (var serviceGroup in entries
            .GroupBy(entry => entry.Service, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var regions = new List<AwsRegionRanges>();
            var everything = new List<AwsPrefix>();
            var seenEverywhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var regionGroup in serviceGroup
                .GroupBy(entry => entry.Region, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var unique = new List<AwsPrefix>();
                foreach (Entry entry in regionGroup)
                {
                    if (seen.Add(entry.Prefix.Block.Text))
                        unique.Add(entry.Prefix);
                }

                List<AwsPrefix> sorted = Sort(unique);
                regions.Add(new AwsRegionRanges(regionGroup.Key, sorted));
                foreach (AwsPrefix prefix in sorted)
                {
                    if (seenEverywhere.Add(prefix.Block.Text))
                        everything.Add(prefix);
                }
            }

            services.Add(new AwsService(serviceGroup.Key, regions, Sort(everything)));
        }

        return services;
    }

    /// <summary>IPv4 first, then by start address, then by prefix length. A stable sort, so equal keys keep file order.</summary>
    private static List<AwsPrefix> Sort(List<AwsPrefix> prefixes) => prefixes
        .OrderBy(prefix => prefix.Block.IsIpv6)
        .ThenBy(prefix => prefix.Block.Start, ByteOrder.Instance)
        .ThenBy(prefix => prefix.Block.PrefixLength)
        .ToList();

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return value is not null;
    }

    /// <summary>Capital letters, digits and underscores.</summary>
    private static bool IsValidService(string service)
    {
        if (service.Length == 0 || service.Length > MaxServiceLength)
            return false;

        foreach (char character in service)
        {
            if (!(character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }

    /// <summary>"GLOBAL", or lower-case letters, digits and hyphens.</summary>
    private static bool IsValidRegion(string region)
    {
        if (string.Equals(region, GlobalRegion, StringComparison.Ordinal))
            return true;

        if (region.Length == 0 || region.Length > MaxRegionLength)
            return false;

        foreach (char character in region)
        {
            if (!(character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return true;
    }

    private static bool IsValidBorderGroup(string group)
    {
        if (group.Length == 0 || group.Length > MaxBorderGroupLength)
            return false;

        foreach (char character in group)
        {
            if (!(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return true;
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? left, byte[]? right) =>
            ((ReadOnlySpan<byte>)left).SequenceCompareTo(right);
    }
}
