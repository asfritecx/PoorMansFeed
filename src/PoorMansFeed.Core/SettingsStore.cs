using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Core;

/// <summary>
/// Reads the legacy settings.json once, for the import into config.db, and turns the settings section to and from
/// JSON. Nothing writes the legacy file: the settings are saved only in config.db.
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path = path;

    /// <summary>
    /// Loads settings. A missing, unreadable or corrupt file gives defaults.
    /// Every value is clamped to its valid range through <see cref="Normalise"/>.
    /// </summary>
    public UserSettings Load()
    {
        UserSettings? loaded = null;

        if (File.Exists(_path))
        {
            try
            {
                // As earlier versions read it: ReadAllText takes a UTF-8, UTF-16 or UTF-32 byte order mark off, and
                // the JSON reader refuses one
                string text = File.ReadAllText(_path);
                loaded = TryRead(Encoding.UTF8.GetBytes(text));
                if (loaded is null)
                    Trace.TraceWarning($"Settings file '{_path}' is corrupt, using defaults.");
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"Could not read settings file '{_path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning($"Could not read settings file '{_path}': {ex.Message}");
            }
        }

        return Normalise(loaded ?? new UserSettings());
    }

    /// <summary>
    /// Reads settings from their JSON, names in any case, unclamped; null when it isn't settings. Settings written before
    /// the feed types have no WriteFortigateFeeds and read it as true, so an earlier install keeps its FortiGate feeds.
    /// </summary>
    internal static UserSettings? TryRead(ReadOnlySpan<byte> utf8Json)
    {
        UserSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<UserSettings>(utf8Json, s_options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (settings is null || NamesWriteFortigateFeeds(utf8Json))
            return settings;
        return settings with { WriteFortigateFeeds = true };
    }

    // Only called on JSON the serializer has read, and with its reader options, so the reader can't throw
    private static bool NamesWriteFortigateFeeds(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = s_options.AllowTrailingCommas,
            CommentHandling = s_options.ReadCommentHandling,
            MaxDepth = s_options.MaxDepth,
        });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                && string.Equals(reader.GetString(), nameof(UserSettings.WriteFortigateFeeds), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The settings as settings.json holds them: indented UTF-8 with no byte order mark.</summary>
    internal static byte[] ToJson(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToUtf8Bytes(settings, s_options);
    }

    /// <summary>The settings with every value clamped into its range, applied on import, on load and on save.</summary>
    internal static UserSettings Normalise(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            ExportFolder = settings.ExportFolder ?? "",
            IPv4MinPrefix = Math.Clamp(settings.IPv4MinPrefix, 1, 32),
            IPv6MinPrefix = Math.Clamp(settings.IPv6MinPrefix, 1, 128),
            AbuseIpDbKeepDays = Math.Clamp(settings.AbuseIpDbKeepDays, AbuseIpDbLimits.MinKeepDays, AbuseIpDbLimits.MaxKeepDays),
            ModSecurityAction = Enum.IsDefined(settings.ModSecurityAction) ? settings.ModSecurityAction : ModSecurityAction.LogOnly,
            ModSecurityFirstRuleId = Math.Clamp(settings.ModSecurityFirstRuleId, ModSecurityLimits.RuleIdMin, ModSecurityLimits.RuleIdMax),
            ModSecurityLocation = settings.ModSecurityLocation is null
                                  || settings.ModSecurityLocation.Length > ModSecurityLimits.MaxLocationLength
                ? ""
                : settings.ModSecurityLocation,
        };
    }
}
