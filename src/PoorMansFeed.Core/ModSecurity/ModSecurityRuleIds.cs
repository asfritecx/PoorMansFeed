using System.Buffers;
using System.Text.Json;

namespace PoorMansFeed.Core.ModSecurity;

/// <summary>The rule id kept for each ModSecurity list file name. Immutable; names are compared ordinally.</summary>
internal sealed class ModSecurityRuleIds
{
    internal const int MaxEntries = 10_000;
    internal const int FormatVersion = 1;

    private const string VersionProperty = "version", RuleIdsProperty = "ruleIds", FileProperty = "file", IdProperty = "id";

    private readonly Dictionary<string, int> _ids;
    private readonly int _highest;

    private ModSecurityRuleIds(Dictionary<string, int> ids, bool isAvailable)
    {
        _ids = ids;
        _highest = ids.Count == 0 ? 0 : ids.Values.Max();
        IsAvailable = isAvailable;
    }

    internal static ModSecurityRuleIds Empty { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal), true);

    internal static ModSecurityRuleIds Unavailable { get; } = new(new Dictionary<string, int>(StringComparer.Ordinal), false);

    internal bool IsAvailable { get; }

    internal int Count => _ids.Count;

    internal bool TryGet(string fileName, out int id) => _ids.TryGetValue(fileName, out id);

    /// <summary>All or nothing: on a failure, or when no name is new, Next is this same map.</summary>
    internal (ModSecurityRuleIdsResult Result, ModSecurityRuleIds Next) Assign(IReadOnlyList<string> fileNames, int firstId)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstId, ModSecurityLimits.RuleIdMin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstId, ModSecurityLimits.RuleIdMax);

        if (!IsAvailable)
            return (Failed(ModSecurityRuleIdsFailure.Unavailable), this);

        foreach (string name in fileNames)
        {
            // The pattern throws on null
            if (name is null || !ModSecurityRules.IsFileName(name))
                return (Failed(ModSecurityRuleIdsFailure.FileNameInvalid), this);
        }

        var ids = new int[fileNames.Count];
        Dictionary<string, int>? added = null;
        int highest = _highest;
        for (int i = 0; i < fileNames.Count; i++)
        {
            string name = fileNames[i];
            if (_ids.TryGetValue(name, out int kept) || (added is not null && added.TryGetValue(name, out kept)))
            {
                ids[i] = kept;
                continue;
            }

            if (_ids.Count + (added?.Count ?? 0) >= MaxEntries)
                return (Failed(ModSecurityRuleIdsFailure.TooMany), this);
            int id = Math.Max(firstId, highest + 1);
            if (id > ModSecurityLimits.RuleIdMax)
                return (Failed(ModSecurityRuleIdsFailure.RangeFull), this);

            added ??= new Dictionary<string, int>(StringComparer.Ordinal);
            added[name] = id;
            highest = id;
            ids[i] = id;
        }

        var result = new ModSecurityRuleIdsResult(ModSecurityRuleIdsFailure.None, ids);
        if (added is null)
            return (result, this);

        var next = new Dictionary<string, int>(_ids, StringComparer.Ordinal);
        foreach ((string name, int id) in added)
            next[name] = id;
        return (result, new ModSecurityRuleIds(next, true));
    }

    internal byte[] ToJson()
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Rule ids that weren't read can't be written.");

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, FormatVersion);
            writer.WriteStartArray(RuleIdsProperty);
            foreach ((string file, int id) in _ids.OrderBy(pair => pair.Value))
            {
                writer.WriteStartObject();
                writer.WriteString(FileProperty, file);
                writer.WriteNumber(IdProperty, id);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Null unless every part is one this build knows, so a later build's map is never rewritten.</summary>
    internal static ModSecurityRuleIds? TryRead(ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryTwoProperties(root, VersionProperty, RuleIdsProperty, out JsonElement version, out JsonElement list)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int format) || format != FormatVersion
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaxEntries)
                return null;

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<int>();
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !TryTwoProperties(entry, FileProperty, IdProperty, out JsonElement file, out JsonElement idValue)
                    || file.ValueKind != JsonValueKind.String || file.GetString() is not { } name
                    || !ModSecurityRules.IsFileName(name)
                    || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out int id)
                    || id < ModSecurityLimits.RuleIdMin || id > ModSecurityLimits.RuleIdMax
                    || !seen.Add(id) || !ids.TryAdd(name, id))
                    return null;
            }
            return new ModSecurityRuleIds(ids, true);
        }
        // A string that is valid JSON but not valid text (a lone surrogate escape, bad UTF-8) throws on reading it
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // Counted, not looked up, because JsonDocument keeps a repeated property and a lookup would see only one of them
    private static bool TryTwoProperties(JsonElement element, string firstName, string secondName,
                                         out JsonElement first, out JsonElement second)
    {
        first = second = default;
        int found = 0, count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            count++;
            if (property.NameEquals(firstName))
            {
                first = property.Value;
                found |= 1;
            }
            else if (property.NameEquals(secondName))
            {
                second = property.Value;
                found |= 2;
            }
        }
        return count == 2 && found == 3;
    }

    private static ModSecurityRuleIdsResult Failed(ModSecurityRuleIdsFailure failure) => new(failure, []);
}
