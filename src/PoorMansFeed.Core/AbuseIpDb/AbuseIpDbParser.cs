using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// Reads the answer to a check and the blacklist. A check answer must be about the address that was asked, a blacklist
/// keeps only public addresses, and every kept field is checked for its type and range. No free text (ISP, domain,
/// usage type, host names) is kept, because it would reach the screen and later a feed.
/// </summary>
internal static class AbuseIpDbParser
{
    /// <summary>
    /// Null when the body is not a check answer for this address (the service then gives BadResponse), including a body
    /// that parses but holds a string that isn't valid UTF-8.
    /// </summary>
    public static AbuseCheck? TryParseCheck(byte[] json, PublicAddress asked)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(asked);
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
            try
            {
                return ReadCheck(document.RootElement, asked);
            }
            catch (InvalidOperationException)
            {
                // GetString throws this for a string that isn't valid UTF-8, which JsonDocument.Parse lets through
                return null;
            }
        }
    }

    /// <summary>
    /// How many unreadable entries a blacklist may hold whatever its size. Past it, more than 1% of the entries
    /// unreadable refuses the whole list.
    /// </summary>
    private const int UnreadableAllowance = 10;

    /// <summary>How far past the answer's time a generation time may be before it is taken as a wrong clock.</summary>
    private static readonly TimeSpan GeneratedAtLeeway = TimeSpan.FromHours(1);

    private enum EntryRead { Read, Unreadable, NotPublic }

    /// <summary>
    /// The blacklist, one entry per address, or null when the body is not a blacklist (not JSON, a string that isn't
    /// valid UTF-8, no entries), more than 10 entries and more than 1% of them can't be read, or no entry is kept. An entry is judged in
    /// this order: an object with an <c>ipAddress</c> string that reads as an address, a score from 0 to 100, a
    /// readable <c>lastReportedAt</c> when there is one (all <see cref="Blacklist.Unreadable"/>), then the address,
    /// an IPv4-mapped one as IPv4, must be public (<see cref="Blacklist.NotPublic"/>). Of the entries for one address
    /// the latest report wins, then the higher score, then the first in the file; it keeps the first one's place.
    /// <paramref name="answerTime"/> is the answer's time on AbuseIPDB's clock: its <c>Date</c> when the clock guard
    /// accepted it, else the download time. The generation time is clamped to it and taken from it when missing.
    /// </summary>
    public static Blacklist? TryParseBlacklist(byte[] json, DateTimeOffset answerTime)
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
            try
            {
                return ReadBlacklist(document.RootElement, answerTime.ToUniversalTime());
            }
            catch (InvalidOperationException)
            {
                // GetString throws this for a string that isn't valid UTF-8, which JsonDocument.Parse lets through
                return null;
            }
        }
    }

    private static Blacklist? ReadBlacklist(JsonElement root, DateTimeOffset answerTime)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return null;
        }

        DateTimeOffset generatedAt = GeneratedAt(root, answerTime);
        var entries = new List<BlacklistEntry>();
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0, unreadable = 0, notPublic = 0, duplicates = 0;
        foreach (JsonElement element in data.EnumerateArray())
        {
            total++;
            switch (ReadEntry(element, out BlacklistEntry? entry))
            {
                case EntryRead.Unreadable:
                    unreadable++;
                    continue;
                case EntryRead.NotPublic:
                    notPublic++;
                    continue;
            }

            // Keyed on the bytes, so every spelling of an address, and its IPv4-mapped form, is one address
            string key = Convert.ToHexString(entry!.Address.Bytes);
            if (slots.TryGetValue(key, out int slot))
            {
                duplicates++;
                if (Beats(entry, entries[slot]))
                    entries[slot] = entry;
            }
            else
            {
                slots.Add(key, entries.Count);
                entries.Add(entry);
            }
        }

        if (unreadable > UnreadableAllowance && unreadable * 100L > total)
        {
            Trace.TraceWarning("The AbuseIPDB blacklist has too many entries that can't be read, so it was not used.");
            return null;
        }

        // A list with nothing to keep is not a blacklist AbuseIPDB would send
        if (entries.Count == 0)
        {
            Trace.TraceWarning("The AbuseIPDB blacklist has no entry that could be kept, so it was not used.");
            return null;
        }

        return new Blacklist(generatedAt, entries, unreadable, notPublic, duplicates);
    }

    /// <summary>
    /// <c>meta.generatedAt</c> as UTC, or the answer's time (on AbuseIPDB's clock) when it is missing or can't be read.
    /// A time more than an hour past the answer's time is a wrong clock and is taken as that hour.
    /// </summary>
    private static DateTimeOffset GeneratedAt(JsonElement root, DateTimeOffset answerTime)
    {
        if (!root.TryGetProperty("meta", out JsonElement meta) || meta.ValueKind != JsonValueKind.Object
            || !TryGetOptionalDate(meta, "generatedAt", out DateTimeOffset? read) || read is not DateTimeOffset generatedAt)
        {
            Trace.TraceWarning("The AbuseIPDB blacklist has no generation time that can be read, so the answer's time is used.");
            return answerTime;
        }

        DateTimeOffset latest = answerTime + GeneratedAtLeeway;
        if (generatedAt > latest)
        {
            Trace.TraceWarning("The AbuseIPDB blacklist generation time is more than an hour after the answer's time, so it was moved back.");
            return latest;
        }

        return generatedAt;
    }

    private static EntryRead ReadEntry(JsonElement element, out BlacklistEntry? entry)
    {
        entry = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("ipAddress", out JsonElement text) || text.ValueKind != JsonValueKind.String
            || !IpAddressText.TryParse(text.GetString() ?? "", out IPAddress? address)
            || !element.TryGetProperty("abuseConfidenceScore", out JsonElement scoreElement)
            || !TryGetCount(scoreElement, out int score) || score > AbuseIpDbLimits.MaxScore
            || !TryGetOptionalDate(element, "lastReportedAt", out DateTimeOffset? lastReportedAt))
        {
            return EntryRead.Unreadable;
        }

        // Mapped back before it is judged, as a search does, so ::ffff:8.8.8.8 is kept as 8.8.8.8
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (!PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? judged))
            return EntryRead.NotPublic;

        entry = new BlacklistEntry(judged, score, lastReportedAt, CountryCode(element));
        return EntryRead.Read;
    }

    /// <summary>The later report wins (no date is older than any), then the higher score; a tie keeps the first.</summary>
    private static bool Beats(BlacklistEntry candidate, BlacklistEntry kept)
    {
        int byDate = Nullable.Compare(candidate.LastReportedAt, kept.LastReportedAt);
        return byDate != 0 ? byDate > 0 : candidate.Score > kept.Score;
    }

    private static AbuseCheck? ReadCheck(JsonElement root, PublicAddress asked)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object
            || !IsAskedAddress(data, asked)
            || !data.TryGetProperty("abuseConfidenceScore", out JsonElement scoreElement)
            || !TryGetCount(scoreElement, out int score) || score > AbuseIpDbLimits.MaxScore
            || !TryGetOptionalCount(data, "totalReports", out int totalReports)
            || !TryGetOptionalCount(data, "numDistinctUsers", out int distinctUsers)
            || !TryGetOptionalDate(data, "lastReportedAt", out DateTimeOffset? lastReportedAt)
            || !TryGetOptionalFlag(data, "isWhitelisted", out bool? isWhitelisted)
            || !TryGetOptionalFlag(data, "isTor", out bool? isTor))
        {
            return null;
        }

        return new AbuseCheck(score, totalReports, distinctUsers, lastReportedAt, isWhitelisted, isTor,
                              CountryCode(data));
    }

    /// <summary>The answer's address read as plain text, byte for byte the asked one (no IPv4-mapped conversion).</summary>
    private static bool IsAskedAddress(JsonElement data, PublicAddress asked) =>
        data.TryGetProperty("ipAddress", out JsonElement element)
        && element.ValueKind == JsonValueKind.String
        && IpAddressText.TryParse(element.GetString() ?? "", out IPAddress? address)
        && address.GetAddressBytes().AsSpan().SequenceEqual(asked.Bytes);

    /// <summary>A whole number of zero or more that fits an int. A fraction or another type is false.</summary>
    private static bool TryGetCount(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value) && value >= 0;
    }

    /// <summary>Missing or null gives 0; present must be a count.</summary>
    private static bool TryGetOptionalCount(JsonElement data, string name, out int value)
    {
        value = 0;
        return !data.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null
               || TryGetCount(element, out value);
    }

    /// <summary>
    /// Missing or null gives null; present must be an ISO 8601 date, kept as UTC. A time with no offset is UTC as
    /// written, never the machine's local time.
    /// </summary>
    private static bool TryGetOptionalDate(JsonElement data, string name, out DateTimeOffset? value)
    {
        value = null;
        if (!data.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return true;

        if (element.ValueKind != JsonValueKind.String)
            return false;

        // Decided before reading: TryGetDateTimeOffset applies the machine's offset to a time without one, so on a
        // machine east of UTC it refuses 0001-01-01T00:00:00
        if (HasOffset(element.GetString() ?? ""))
        {
            if (!element.TryGetDateTimeOffset(out DateTimeOffset date))
                return false;

            value = date.ToUniversalTime();
            return true;
        }

        // TryGetDateTime leaves a time with no offset unconverted (Kind Unspecified), so the clock reads as written.
        if (!element.TryGetDateTime(out DateTime written))
            return false;

        value = new DateTimeOffset(DateTime.SpecifyKind(written, DateTimeKind.Unspecified), TimeSpan.Zero);
        return true;
    }

    /// <summary>Whether the time part, after the "T", ends in "Z" or carries a + or - offset.</summary>
    private static bool HasOffset(string text)
    {
        int time = text.IndexOf('T');
        return time >= 0 && text.AsSpan(time + 1).IndexOfAny('Z', '+', '-') >= 0;
    }

    /// <summary>Missing or null gives null; present must be true or false.</summary>
    private static bool TryGetOptionalFlag(JsonElement data, string name, out bool? value)
    {
        value = null;
        if (!data.TryGetProperty(name, out JsonElement element))
            return true;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetBoolean();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Two capital letters A to Z, or null. Anything else is dropped, never a failure.</summary>
    private static string? CountryCode(JsonElement data)
    {
        if (!data.TryGetProperty("countryCode", out JsonElement element) || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: 2 } code)
        {
            return null;
        }

        foreach (char character in code)
        {
            if (character is < 'A' or > 'Z')
                return null;
        }

        return code;
    }
}
