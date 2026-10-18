using System.Reflection;

namespace GraphiteUi;

/// <summary>
/// Display strings derived from one product name and the build version, so no window repeats a hardcoded value.
/// Edit <see cref="Name"/> and nothing else in this file.
/// </summary>
public static class AppInfo
{
    /// <summary>The product name. Window chrome, dialog titles, the macOS menu and the closing-notice button take it from here.</summary>
    public const string Name = "PoorMansFeed";
    public const string AboutTitle = $"About {Name}";
    public const string HelpTitle = $"How to use {Name}";
    public const string AboutMenuHeader = $"About {Name}...";
    public const string CloseAppText = $"Close {Name}";
    public const string InternalUseNotice = "Internal use only";

    /// <summary>The build version from the one &lt;Version&gt; property, such as "1.2.0". Never type the number anywhere else.</summary>
    public static string Version { get; } = ReadVersion();
    public static string VersionLabel { get; } = $"v{Version}";
    public static string Title { get; } = $"{Name} {VersionLabel}";
    public static string VersionText { get; } = $"Version {Version}";

    private static string ReadVersion()
    {
        Assembly assembly = typeof(AppInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Drop any "+commit" suffix
            int plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
