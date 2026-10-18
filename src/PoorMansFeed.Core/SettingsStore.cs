using System.Diagnostics;
using System.Text.Json;

namespace PoorMansFeed.Core;

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
    /// Prefix lengths are clamped to their valid ranges.
    /// </summary>
    public UserSettings Load()
    {
        UserSettings? loaded = null;

        if (File.Exists(_path))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), s_options);
            }
            catch (IOException ex)
            {
                Trace.TraceWarning($"Could not read settings file '{_path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceWarning($"Could not read settings file '{_path}': {ex.Message}");
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning($"Settings file '{_path}' is corrupt, using defaults: {ex.Message}");
            }
        }

        UserSettings settings = loaded ?? new UserSettings();

        return settings with
        {
            ExportFolder = settings.ExportFolder ?? "",
            IPv4MinPrefix = Math.Clamp(settings.IPv4MinPrefix, 1, 32),
            IPv6MinPrefix = Math.Clamp(settings.IPv6MinPrefix, 1, 128),
        };
    }

    /// <summary>
    /// Saves settings atomically (write to a .tmp file, then move over the target).
    /// Returns false on IO failure; never throws.
    /// </summary>
    public bool Save(UserSettings settings)
    {
        string tmpPath = _path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(settings, s_options);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _path, overwrite: true);
            return true;
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"Could not save settings file '{_path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"Could not save settings file '{_path}': {ex.Message}");
        }

        try
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
        catch (IOException)
        {
            // Best effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup
        }

        return false;
    }
}
