using System.Buffers;
using System.Text.Json;
using PoorMansFeed.Core.CloudFeeds;
using PoorMansFeed.Core.History;

namespace PoorMansFeed.Core;

/// <summary>The saved feeds, kept by key and compared ignoring case. Immutable.</summary>
internal sealed class SavedFeedList
{
    internal const int FormatVersion = 2;
    internal const int MaxFeeds = 500;
    internal const int MaxRangesPerList = 65_536;
    internal const int MaxKeyLength = 200;
    internal const int MaxJsonBytes = 16 * 1024 * 1024;

    private const int MaxTextLength = 200, MaxAsnDigits = 10;

    private static readonly string[] RootProperties = ["version", "feeds"];

    private static readonly string[] FeedPropertiesV1 = ["key", "kind", "lastWriteUtc", "lastRefreshUtc", "retired", "pick"];

    private static readonly string[] FeedProperties = [.. FeedPropertiesV1, "proof"];

    private static readonly string[] PickPropertiesV1 =
    [
        "asn", "googleScope", "googleAddsGlobal", "azureTag", "awsService", "awsRegion", "handTicked", "handUnticked",
        "largeIncluded", "largeLeftOut", "largeRangeCheckEnabled", "ipv4MinPrefix", "ipv6MinPrefix",
    ];

    private static readonly string[] PickProperties = [.. PickPropertiesV1, "seenUnconfirmed"];

    private static readonly string[] ProofProperties = ["settings", "files"];

    private readonly SavedFeed[] _feeds;

    private SavedFeedList(SavedFeed[] feeds, bool isAvailable)
    {
        _feeds = feeds;
        Feeds = Array.AsReadOnly(feeds);
        IsAvailable = isAvailable;
    }

    internal static SavedFeedList Empty { get; } = new([], true);

    internal static SavedFeedList Unavailable { get; } = new([], false);

    internal bool IsAvailable { get; }

    internal IReadOnlyList<SavedFeed> Feeds { get; }

    /// <summary>All or nothing: on a failure, or when nothing changes, Next is this same list.</summary>
    internal (SavedFeedsFailure Failure, SavedFeedList Next) Apply(string key, FeedKind kind, SavedFeedIntent intent,
                                                                   DateTimeOffset atUtc) =>
        Apply(key, kind, intent, atUtc, MaxJsonBytes);

    internal (SavedFeedsFailure Failure, SavedFeedList Next) Apply(string key, FeedKind kind, SavedFeedIntent intent,
                                                                   DateTimeOffset atUtc, int maxJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ThrowUnlessValidKey(key);
        bool overCap = false;
        if (intent is SavedFeedIntent.Written written && (written.Pick is null || !IsValidPick(kind, written.Pick, out overCap)))
            throw new ArgumentException("The pick isn't valid for its kind.", nameof(intent));
        if (intent is SavedFeedIntent.Written { Proof: { } proof } && !SavedFeedWriteProof.IsValid(proof))
            throw new ArgumentException("The write proof isn't valid.", nameof(intent));

        if (!IsAvailable)
            return (SavedFeedsFailure.Unavailable, this);

        int index = IndexOf(key);
        SavedFeed[] feeds;
        switch (intent)
        {
            case SavedFeedIntent.Retired:
                if (index < 0)
                    return (SavedFeedsFailure.NotFound, this);
                if (_feeds[index].Retired)
                    return (SavedFeedsFailure.None, this);
                feeds = (SavedFeed[])_feeds.Clone();
                feeds[index] = feeds[index] with { Retired = true, Proof = null };
                return (SavedFeedsFailure.None, new SavedFeedList(feeds, true));

            case SavedFeedIntent.Written(SavedFeedPick pick, var writeProof):
                if (overCap)
                    return (SavedFeedsFailure.TooMany, this);
                DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(atUtc.ToUnixTimeSeconds());
                if (index < 0)
                {
                    if (_feeds.Length >= MaxFeeds)
                        return (SavedFeedsFailure.TooMany, this);
                    feeds = [.. _feeds, new SavedFeed(key, kind, Copy(pick), at, null, false, Copy(writeProof))];
                }
                else
                {
                    SavedFeed old = _feeds[index];
                    var replaced = new SavedFeed(key, kind, Copy(pick), at, old.LastRefreshUtc, false, Copy(writeProof));
                    if (SameFeed(old, replaced))
                        return (SavedFeedsFailure.None, this);
                    feeds = (SavedFeed[])_feeds.Clone();
                    feeds[index] = replaced;
                }
                break;

            default:
                throw new ArgumentException("The intent isn't one this build knows.", nameof(intent));
        }

        Array.Sort(feeds, (a, b) => CompareKeys(a.Key, b.Key));
        var next = new SavedFeedList(feeds, true);
        if (next.ToJson().Length > maxJsonBytes)
            return (SavedFeedsFailure.TooMany, this);
        return (SavedFeedsFailure.None, next);
    }

    /// <summary>
    /// Each stamp applies only while its feed is stored exactly as the refresh read it. Past the byte cap the applied
    /// stamp whose feed grew most is dropped first; Next is this same list when none applied.
    /// </summary>
    internal (IReadOnlyList<SavedFeedStampOutcome> Outcomes, SavedFeedList Next) ApplyStamps(
        IReadOnlyList<SavedFeedStamp> stamps, int maxJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overCap = new bool[stamps.Count];
        for (int i = 0; i < stamps.Count; i++)
        {
            SavedFeedStamp stamp = stamps[i] ?? throw new ArgumentException("A stamp is missing.", nameof(stamps));
            if (stamp.Expected is null || !keys.Add(stamp.Expected.Key ?? ""))
                throw new ArgumentException("Two stamps name one feed.", nameof(stamps));
            if (stamp.NewPick is not null && !IsValidPick(stamp.Expected.Kind, stamp.NewPick, out overCap[i]))
                throw new ArgumentException("A stamp's pick isn't valid for its kind.", nameof(stamps));
            if (stamp.Proof is not null && !SavedFeedWriteProof.IsValid(stamp.Proof))
                throw new ArgumentException("A stamp's proof isn't valid.", nameof(stamps));
        }

        var outcomes = new SavedFeedStampOutcome[stamps.Count];
        if (!IsAvailable)
        {
            Array.Fill(outcomes, SavedFeedStampOutcome.Unavailable);
            return (outcomes, this);
        }

        var stamped = (SavedFeed[])_feeds.Clone();
        var applied = new List<(int Stamp, int Index, long Growth, string Key)>();
        for (int i = 0; i < stamps.Count; i++)
        {
            SavedFeedStamp stamp = stamps[i];
            int index = IndexOf(stamp.Expected.Key);
            if (index < 0)
            {
                outcomes[i] = SavedFeedStampOutcome.Removed;
                continue;
            }

            SavedFeed stored = _feeds[index];
            // A retired feed's files are header-only until its page writes it again, so no refresh may save it
            if (stored.Retired || !SameFeed(stored, stamp.Expected))
            {
                outcomes[i] = SavedFeedStampOutcome.Superseded;
                continue;
            }
            if (overCap[i])
            {
                outcomes[i] = SavedFeedStampOutcome.TooMany;
                continue;
            }

            SavedFeed next = stored with
            {
                Pick = stamp.NewPick is null ? stored.Pick : Copy(stamp.NewPick),
                Proof = Copy(stamp.Proof),
                LastRefreshUtc = stamp.RefreshedUtc is DateTimeOffset refreshed
                    ? DateTimeOffset.FromUnixTimeSeconds(refreshed.ToUnixTimeSeconds())
                    : stored.LastRefreshUtc,
            };
            if (SameFeed(stored, next))
            {
                outcomes[i] = SavedFeedStampOutcome.Unchanged;
                continue;
            }

            outcomes[i] = SavedFeedStampOutcome.Saved;
            stamped[index] = next;
            applied.Add((i, index, (long)FeedJson(next).Length - FeedJson(stored).Length, stored.Key));
        }

        if (applied.Count == 0)
            return (outcomes, this);

        // Ordered so the next to drop is last: least growth first, and among equal growth the earlier key first
        applied.Sort((a, b) => a.Growth != b.Growth ? a.Growth.CompareTo(b.Growth) : CompareKeys(a.Key, b.Key));
        var list = new SavedFeedList(stamped, true);
        while (list.ToJson().Length > maxJsonBytes && applied.Count > 0)
        {
            (int stampIndex, int feedIndex, _, _) = applied[^1];
            applied.RemoveAt(applied.Count - 1);
            outcomes[stampIndex] = SavedFeedStampOutcome.TooMany;
            stamped[feedIndex] = _feeds[feedIndex];
            list = new SavedFeedList(stamped, true);
        }

        return applied.Count == 0 ? (outcomes, this) : (outcomes, list);
    }

    internal (SavedFeedsFailure Failure, SavedFeedList Next) Remove(string key)
    {
        ThrowUnlessValidKey(key);
        if (!IsAvailable)
            return (SavedFeedsFailure.Unavailable, this);

        int index = IndexOf(key);
        if (index < 0)
            return (SavedFeedsFailure.NotFound, this);
        SavedFeed[] feeds = [.. _feeds[..index], .. _feeds[(index + 1)..]];
        return (SavedFeedsFailure.None, new SavedFeedList(feeds, true));
    }

    internal byte[] ToJson()
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Saved feeds that weren't read can't be written.");

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber(RootProperties[0], FormatVersion);
            writer.WriteStartArray(RootProperties[1]);
            foreach (SavedFeed feed in _feeds)
                WriteFeed(writer, feed);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] FeedJson(SavedFeed feed)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteFeed(writer, feed);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFeed(Utf8JsonWriter writer, SavedFeed feed)
    {
        writer.WriteStartObject();
        writer.WriteString("key", feed.Key);
        writer.WriteString("kind", KindName(feed.Kind));
        writer.WriteNumber("lastWriteUtc", feed.LastWriteUtc.ToUnixTimeSeconds());
        if (feed.LastRefreshUtc is { } refreshed)
            writer.WriteNumber("lastRefreshUtc", refreshed.ToUnixTimeSeconds());
        else
            writer.WriteNull("lastRefreshUtc");
        writer.WriteBoolean("retired", feed.Retired);
        WritePick(writer, feed.Pick);
        if (feed.Proof is { } proof)
        {
            writer.WriteStartObject("proof");
            writer.WriteString("settings", proof.SettingsFingerprint);
            writer.WriteStartObject("files");
            foreach (KeyValuePair<string, string> file in proof.FileSha256.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(file.Key, file.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("proof");
        }
        writer.WriteEndObject();
    }

    /// <summary>Null unless every part is one this build knows, so a later build's list is never rewritten.</summary>
    internal static SavedFeedList? TryRead(ReadOnlySpan<byte> json) => TryRead(json, MaxJsonBytes);

    internal static SavedFeedList? TryRead(ReadOnlySpan<byte> json, int maxBytes)
    {
        if (json.Length > maxBytes)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;
            if (!TryProperties(root, RootProperties, out JsonElement[] top)
                || top[0].ValueKind != JsonValueKind.Number || !top[0].TryGetInt32(out int format) || format is not (1 or FormatVersion)
                || top[1].ValueKind != JsonValueKind.Array || top[1].GetArrayLength() > MaxFeeds)
                return null;

            var feeds = new List<SavedFeed>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in top[1].EnumerateArray())
            {
                if (TryReadFeed(entry, format) is not { } feed || !keys.Add(feed.Key))
                    return null;
                feeds.Add(feed);
            }

            SavedFeed[] ordered = [.. feeds];
            Array.Sort(ordered, (a, b) => CompareKeys(a.Key, b.Key));
            return new SavedFeedList(ordered, true);
        }
        // A string that is valid JSON but not valid text (a lone surrogate escape, bad UTF-8) throws on reading it
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static bool IsValidKey(string? key) =>
        key is not null && key.Length <= MaxKeyLength && FeedDescription.IsSafeStem(key);

    internal static bool IsValidPick(FeedKind kind, SavedFeedPick pick) =>
        pick is not null && IsValidPick(kind, pick, out bool overCap) && !overCap;

    // A list over the cap is a full store, not a caller's mistake, so it is reported apart from the other rules
    private static bool IsValidPick(FeedKind kind, SavedFeedPick pick, out bool overCap)
    {
        overCap = false;
        if (pick.HandTicked is null || pick.HandUnticked is null || pick.LargeIncluded is null || pick.LargeLeftOut is null)
            return false;
        IReadOnlyList<string> seen = pick.SeenUnconfirmed ?? [];
        overCap = pick.HandTicked.Count > MaxRangesPerList || pick.HandUnticked.Count > MaxRangesPerList
                  || pick.LargeIncluded.Count > MaxRangesPerList || pick.LargeLeftOut.Count > MaxRangesPerList
                  || seen.Count > MaxRangesPerList;

        if (!IsTextOrNull(pick.Asn) || !IsTextOrNull(pick.GoogleScope) || !IsTextOrNull(pick.AzureTag)
            || !IsTextOrNull(pick.AwsService) || !IsTextOrNull(pick.AwsRegion)
            || !AreApart(pick.HandTicked, pick.HandUnticked) || !AreApart(pick.LargeIncluded, pick.LargeLeftOut)
            || !AreApart(seen, []))
            return false;

        bool noThresholds = pick.LargeRangeCheckEnabled is null && pick.IPv4MinPrefix is null && pick.IPv6MinPrefix is null;
        bool noHandTicks = pick.HandTicked.Count == 0 && pick.HandUnticked.Count == 0;
        return kind switch
        {
            FeedKind.Asn => IsAsn(pick.Asn) && pick.LargeRangeCheckEnabled is not null
                            && pick.IPv4MinPrefix is >= 1 and <= 32 && pick.IPv6MinPrefix is >= 1 and <= 128
                            && pick.GoogleScope is null && !pick.GoogleAddsGlobal && pick.AzureTag is null
                            && pick.AwsService is null && pick.AwsRegion is null && noHandTicks && seen.Count == 0,
            FeedKind.GoogleCloud => pick.GoogleScope is not null && pick.Asn is null && pick.AzureTag is null
                                    && pick.AwsService is null && pick.AwsRegion is null && noThresholds,
            FeedKind.Azure => pick.AzureTag is not null && pick.Asn is null && pick.GoogleScope is null
                              && !pick.GoogleAddsGlobal && pick.AwsService is null && pick.AwsRegion is null && noThresholds,
            FeedKind.Aws => pick.AwsService is not null && pick.Asn is null && pick.GoogleScope is null
                            && !pick.GoogleAddsGlobal && pick.AzureTag is null && noThresholds,
            FeedKind.AbuseIpDbRolling => pick.Asn is null && pick.GoogleScope is null && !pick.GoogleAddsGlobal
                                         && pick.AzureTag is null && pick.AwsService is null && pick.AwsRegion is null
                                         && noHandTicks && pick.LargeIncluded.Count == 0 && pick.LargeLeftOut.Count == 0
                                         && noThresholds && seen.Count == 0,
            _ => false,
        };
    }

    private static bool IsAsn(string? asn) =>
        asn is { Length: >= 1 and <= MaxAsnDigits } && asn.All(char.IsAsciiDigit);

    private static bool IsTextOrNull(string? text)
    {
        if (text is null)
            return true;
        if (text.Length is 0 or > MaxTextLength)
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c))
                return false;
            // The JSON writer refuses a lone surrogate
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    /// <summary>Each list canonical and without a duplicate, and no network in both.</summary>
    private static bool AreApart(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in first)
        {
            if (!IsCanonical(text) || !seen.Add(text))
                return false;
        }

        var other = new HashSet<string>(StringComparer.Ordinal);
        foreach (string text in second)
        {
            if (!IsCanonical(text) || !other.Add(text) || seen.Contains(text))
                return false;
        }
        return true;
    }

    private static bool IsCanonical(string? text) =>
        text is not null && CidrBlock.TryParse(text, out CidrBlock? block) && block.Text == text;

    private static void ThrowUnlessValidKey(string key)
    {
        if (!IsValidKey(key))
            throw new ArgumentException("The key isn't a feed file stem.", nameof(key));
    }

    private int IndexOf(string key) =>
        Array.FindIndex(_feeds, feed => string.Equals(feed.Key, key, StringComparison.OrdinalIgnoreCase));

    private static int CompareKeys(string a, string b)
    {
        int byCase = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return byCase != 0 ? byCase : string.CompareOrdinal(a, b);
    }

    private static SavedFeedPick Copy(SavedFeedPick pick) => pick with
    {
        HandTicked = [.. pick.HandTicked],
        HandUnticked = [.. pick.HandUnticked],
        LargeIncluded = [.. pick.LargeIncluded],
        LargeLeftOut = [.. pick.LargeLeftOut],
        SeenUnconfirmed = [.. pick.SeenUnconfirmed ?? []],
    };

    private static SavedFeedWriteProof? Copy(SavedFeedWriteProof? proof) =>
        proof is null ? null : new SavedFeedWriteProof(proof.SettingsFingerprint,
                                                       new Dictionary<string, string>(proof.FileSha256, StringComparer.Ordinal));

    private static bool SameFeed(SavedFeed a, SavedFeed b) =>
        string.Equals(a.Key, b.Key, StringComparison.Ordinal) && a.Kind == b.Kind && a.LastWriteUtc == b.LastWriteUtc
        && a.LastRefreshUtc == b.LastRefreshUtc && a.Retired == b.Retired
        && a.Pick.Asn == b.Pick.Asn && a.Pick.GoogleScope == b.Pick.GoogleScope
        && a.Pick.GoogleAddsGlobal == b.Pick.GoogleAddsGlobal && a.Pick.AzureTag == b.Pick.AzureTag
        && a.Pick.AwsService == b.Pick.AwsService && a.Pick.AwsRegion == b.Pick.AwsRegion
        && a.Pick.HandTicked.SequenceEqual(b.Pick.HandTicked, StringComparer.Ordinal)
        && a.Pick.HandUnticked.SequenceEqual(b.Pick.HandUnticked, StringComparer.Ordinal)
        && a.Pick.LargeIncluded.SequenceEqual(b.Pick.LargeIncluded, StringComparer.Ordinal)
        && a.Pick.LargeLeftOut.SequenceEqual(b.Pick.LargeLeftOut, StringComparer.Ordinal)
        && a.Pick.LargeRangeCheckEnabled == b.Pick.LargeRangeCheckEnabled
        && a.Pick.IPv4MinPrefix == b.Pick.IPv4MinPrefix && a.Pick.IPv6MinPrefix == b.Pick.IPv6MinPrefix
        && (a.Pick.SeenUnconfirmed ?? []).SequenceEqual(b.Pick.SeenUnconfirmed ?? [], StringComparer.Ordinal)
        && SameProof(a.Proof, b.Proof);

    private static bool SameProof(SavedFeedWriteProof? a, SavedFeedWriteProof? b) =>
        a is null || b is null
            ? a is null && b is null
            : string.Equals(a.SettingsFingerprint, b.SettingsFingerprint, StringComparison.Ordinal)
              && a.FileSha256.Count == b.FileSha256.Count
              && a.FileSha256.All(pair => b.FileSha256.TryGetValue(pair.Key, out string? hash)
                                          && string.Equals(pair.Value, hash, StringComparison.Ordinal));

    private static string KindName(FeedKind kind) => kind switch
    {
        FeedKind.Asn => "Asn",
        FeedKind.GoogleCloud => "GoogleCloud",
        FeedKind.Azure => "Azure",
        FeedKind.Aws => "Aws",
        FeedKind.AbuseIpDbRolling => "AbuseIpDbRolling",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static FeedKind? KindOf(string? name) => name switch
    {
        "Asn" => FeedKind.Asn,
        "GoogleCloud" => FeedKind.GoogleCloud,
        "Azure" => FeedKind.Azure,
        "Aws" => FeedKind.Aws,
        "AbuseIpDbRolling" => FeedKind.AbuseIpDbRolling,
        _ => null,
    };

    private static void WritePick(Utf8JsonWriter writer, SavedFeedPick pick)
    {
        writer.WriteStartObject("pick");
        writer.WriteString("asn", pick.Asn);
        writer.WriteString("googleScope", pick.GoogleScope);
        writer.WriteBoolean("googleAddsGlobal", pick.GoogleAddsGlobal);
        writer.WriteString("azureTag", pick.AzureTag);
        writer.WriteString("awsService", pick.AwsService);
        writer.WriteString("awsRegion", pick.AwsRegion);
        WriteList(writer, "handTicked", pick.HandTicked);
        WriteList(writer, "handUnticked", pick.HandUnticked);
        WriteList(writer, "largeIncluded", pick.LargeIncluded);
        WriteList(writer, "largeLeftOut", pick.LargeLeftOut);
        if (pick.LargeRangeCheckEnabled is { } check)
            writer.WriteBoolean("largeRangeCheckEnabled", check);
        else
            writer.WriteNull("largeRangeCheckEnabled");
        WriteNumberOrNull(writer, "ipv4MinPrefix", pick.IPv4MinPrefix);
        WriteNumberOrNull(writer, "ipv6MinPrefix", pick.IPv6MinPrefix);
        WriteList(writer, "seenUnconfirmed", pick.SeenUnconfirmed ?? []);
        writer.WriteEndObject();
    }

    private static void WriteList(Utf8JsonWriter writer, string name, IReadOnlyList<string> list)
    {
        writer.WriteStartArray(name);
        foreach (string text in list)
            writer.WriteStringValue(text);
        writer.WriteEndArray();
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
        else
            writer.WriteNull(name);
    }

    // A format 1 feed has no proof and its pick no seen list; it reads as neither
    private static SavedFeed? TryReadFeed(JsonElement entry, int format)
    {
        if (!TryProperties(entry, format == 1 ? FeedPropertiesV1 : FeedProperties, out JsonElement[] v)
            || v[0].ValueKind != JsonValueKind.String || v[0].GetString() is not { } key || !IsValidKey(key)
            || v[1].ValueKind != JsonValueKind.String || KindOf(v[1].GetString()) is not { } kind
            || !TryTime(v[2], out DateTimeOffset written)
            || v[4].ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || TryReadPick(v[5], format) is not { } pick || !IsValidPick(kind, pick))
            return null;

        SavedFeedWriteProof? proof = null;
        if (format != 1 && v[6].ValueKind != JsonValueKind.Null && (proof = TryReadProof(v[6])) is null)
            return null;

        DateTimeOffset? refreshed = null;
        if (v[3].ValueKind != JsonValueKind.Null)
        {
            if (!TryTime(v[3], out DateTimeOffset time))
                return null;
            refreshed = time;
        }
        return new SavedFeed(key, kind, pick, written, refreshed, v[4].GetBoolean(), proof);
    }

    private static SavedFeedWriteProof? TryReadProof(JsonElement element)
    {
        if (!TryProperties(element, ProofProperties, out JsonElement[] v)
            || v[0].ValueKind != JsonValueKind.String || v[0].GetString() is not { } fingerprint
            || v[1].ValueKind != JsonValueKind.Object)
            return null;

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty file in v[1].EnumerateObject())
        {
            if (file.Value.ValueKind != JsonValueKind.String
                || file.Value.GetString() is not { } hash || !files.TryAdd(file.Name, hash))
                return null;
        }

        var proof = new SavedFeedWriteProof(fingerprint, files);
        return SavedFeedWriteProof.IsValid(proof) ? proof : null;
    }

    private static SavedFeedPick? TryReadPick(JsonElement element, int format)
    {
        if (!TryProperties(element, format == 1 ? PickPropertiesV1 : PickProperties, out JsonElement[] v)
            || !TryText(v[0], out string? asn) || !TryText(v[1], out string? googleScope)
            || v[2].ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryText(v[3], out string? azureTag) || !TryText(v[4], out string? awsService)
            || !TryText(v[5], out string? awsRegion)
            || TryList(v[6]) is not { } handTicked || TryList(v[7]) is not { } handUnticked
            || TryList(v[8]) is not { } largeIncluded || TryList(v[9]) is not { } largeLeftOut
            || !TryFlag(v[10], out bool? check) || !TryNumber(v[11], out int? ipv4) || !TryNumber(v[12], out int? ipv6))
            return null;

        string[]? seen = format == 1 ? [] : TryList(v[13]);
        if (seen is null)
            return null;
        return new SavedFeedPick(asn, googleScope, v[2].GetBoolean(), azureTag, awsService, awsRegion,
                                 handTicked, handUnticked, largeIncluded, largeLeftOut, check, ipv4, ipv6, seen);
    }

    private static bool TryTime(JsonElement element, out DateTimeOffset time)
    {
        time = default;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long seconds)
            || seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds() || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return false;
        time = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static bool TryText(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        text = element.GetString();
        return true;
    }

    private static bool TryFlag(JsonElement element, out bool? flag)
    {
        flag = element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        return flag is not null || element.ValueKind == JsonValueKind.Null;
    }

    private static bool TryNumber(JsonElement element, out int? number)
    {
        number = null;
        if (element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
            return false;
        number = value;
        return true;
    }

    private static string[]? TryList(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxRangesPerList)
            return null;

        var list = new string[element.GetArrayLength()];
        int i = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
                return null;
            list[i++] = text;
        }
        return list;
    }

    // Counted, not looked up, because JsonDocument keeps a repeated property and a lookup would see only one of them
    private static bool TryProperties(JsonElement element, string[] names, out JsonElement[] values)
    {
        values = new JsonElement[names.Length];
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var found = new bool[names.Length];
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            int index = Array.FindIndex(names, property.NameEquals);
            if (index < 0 || found[index])
                return false;
            found[index] = true;
            values[index] = property.Value;
            count++;
        }
        return count == names.Length;
    }
}
