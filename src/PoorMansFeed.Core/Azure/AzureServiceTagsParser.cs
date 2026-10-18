using System.Text.Json;
using System.Text.Json.Serialization;
using PoorMansFeed.Core;

namespace PoorMansFeed.Core.Azure;

/// <summary>What one service tags file holds, before it is tied to a download.</summary>
internal sealed record ParsedAzureTags(long ChangeNumber, string Cloud, IReadOnlyList<AzureServiceGroup> Services,
                                       int SkippedTags, int SkippedPrefixes);

/// <summary>
/// Reads ServiceTags_Public_*.json. A tag or prefix that cannot be used is skipped and counted, never fatal.
/// Names and regions are checked because they end up in feed file names.
/// </summary>
internal static class AzureServiceTagsParser
{
    internal const string PublicCloud = "Public";
    private const int MaxNameLength = 128;
    private const int MaxRegionLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    public static ParsedAzureTags? TryParse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file is null
            || file.ChangeNumber is null or < 0
            || !string.Equals(file.Cloud, PublicCloud, StringComparison.Ordinal)
            || file.Values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tags = new List<AzureServiceTag>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedTags = 0;
        int skippedPrefixes = 0;
        foreach (JsonElement item in file.Values.EnumerateArray())
        {
            if (!TryReadTag(item, seen, ref skippedPrefixes, out AzureServiceTag? tag))
            {
                skippedTags++;
                continue;
            }

            tags.Add(tag);
        }

        if (tags.Count == 0)
            return null;

        var groups = tags
            .GroupBy(tag => tag.BaseName, StringComparer.Ordinal)
            .Select(group => new AzureServiceGroup(group.Key, SortTags(group)))
            .OrderBy(group => group.BaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.BaseName, StringComparer.Ordinal)
            .ToList();
        return new ParsedAzureTags(file.ChangeNumber.Value, PublicCloud, groups, skippedTags, skippedPrefixes);
    }

    private static bool TryReadTag(JsonElement item, HashSet<string> seen, ref int skippedPrefixes, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AzureServiceTag? tag)
    {
        tag = null;
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        TagDto? dto;
        try
        {
            dto = item.Deserialize<TagDto>(JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        string? region = dto?.Properties?.Region ?? "";
        if (dto?.Name is not { } name || !IsValidName(name) || !IsValidRegion(region) || seen.Contains(name))
            return false;

        var prefixes = new List<CidrBlock>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        int bad = 0;
        foreach (string? text in dto.Properties?.AddressPrefixes ?? [])
        {
            if (!CidrBlock.TryParse(text, out CidrBlock? block))
            {
                bad++;
                continue;
            }

            if (unique.Add(block.Text))
                prefixes.Add(block);
        }

        skippedPrefixes += bad;
        if (prefixes.Count == 0)
            return false;

        prefixes.Sort(ComparePrefixes);
        seen.Add(name);
        tag = new AzureServiceTag(name, region, dto.Properties?.ChangeNumber ?? 0, prefixes);
        return true;
    }

    private static IReadOnlyList<AzureServiceTag> SortTags(IEnumerable<AzureServiceTag> tags) => tags
        .OrderBy(tag => tag.IsBase ? 0 : 1)
        .ThenBy(tag => tag.Region.Length > 0 ? tag.Region : tag.Suffix, StringComparer.OrdinalIgnoreCase)
        .ThenBy(tag => tag.Name, StringComparer.Ordinal)
        .ToList();

    private static int ComparePrefixes(CidrBlock left, CidrBlock right)
    {
        int family = left.IsIpv6.CompareTo(right.IsIpv6);
        if (family != 0)
            return family;

        byte[] leftStart = left.Start;
        byte[] rightStart = right.Start;
        for (int i = 0; i < leftStart.Length; i++)
        {
            int byteOrder = leftStart[i].CompareTo(rightStart[i]);
            if (byteOrder != 0)
                return byteOrder;
        }

        return left.PrefixLength.CompareTo(right.PrefixLength);
    }

    /// <summary>One or two dot-separated parts of letters and digits.</summary>
    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || name.Length > MaxNameLength)
            return false;

        int dots = 0;
        char previous = '.';
        foreach (char character in name)
        {
            if (character == '.')
            {
                if (previous == '.' || ++dots > 1)
                    return false;
            }
            else if (!(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
            {
                return false;
            }

            previous = character;
        }

        return previous != '.';
    }

    private static bool IsValidRegion(string region)
    {
        if (region.Length > MaxRegionLength)
            return false;

        foreach (char character in region)
        {
            if (!(character is (>= 'a' and <= 'z') or (>= '0' and <= '9')))
                return false;
        }

        return true;
    }

    private sealed class FileDto
    {
        [JsonPropertyName("changeNumber")]
        public long? ChangeNumber { get; set; }

        [JsonPropertyName("cloud")]
        public string? Cloud { get; set; }

        [JsonPropertyName("values")]
        public JsonElement Values { get; set; }
    }

    private sealed class TagDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("properties")]
        public PropertiesDto? Properties { get; set; }
    }

    private sealed class PropertiesDto
    {
        [JsonPropertyName("changeNumber")]
        public long? ChangeNumber { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("addressPrefixes")]
        public List<string?>? AddressPrefixes { get; set; }
    }
}
