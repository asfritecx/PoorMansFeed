using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PoorMansFeed.Core.AbuseIpDb;

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
    /// Reads settings from their JSON, with property names in any letter case. Null when the JSON doesn't parse or is
    /// <c>null</c>. The values are not clamped; the caller applies <see cref="Normalise"/>.
    /// </summary>
    internal static UserSettings? TryRead(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(utf8Json, s_options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The settings as settings.json holds them: indented UTF-8 with no byte order mark.</summary>
    internal static byte[] ToJson(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToUtf8Bytes(settings, s_options);
    }

    /// <summary>
    /// The settings with every value in its valid range: a missing export folder becomes empty, the IPv4 prefix is
    /// clamped to 1-32, the IPv6 prefix to 1-128, and the AbuseIPDB keep days to
    /// <see cref="AbuseIpDbLimits.MinKeepDays"/>-<see cref="AbuseIpDbLimits.MaxKeepDays"/>.
    /// </summary>
    internal static UserSettings Normalise(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            ExportFolder = settings.ExportFolder ?? "",
            IPv4MinPrefix = Math.Clamp(settings.IPv4MinPrefix, 1, 32),
            IPv6MinPrefix = Math.Clamp(settings.IPv6MinPrefix, 1, 128),
            AbuseIpDbKeepDays = Math.Clamp(settings.AbuseIpDbKeepDays, AbuseIpDbLimits.MinKeepDays, AbuseIpDbLimits.MaxKeepDays),
        };
    }
}
