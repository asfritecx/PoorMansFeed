namespace PoorMansFeed.Core;

public sealed record UserSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string ExportFolder { get; init; } = "";      // empty = AppPaths.DefaultExportFolder
    public bool IPv6Enabled { get; init; } = true;        // false: IPv6 is left out of the dataset, searches and exports
    public bool FortigateEnabled { get; init; } = true;
    public bool FortigateIncludeIPv4 { get; init; } = true;
    public bool FortigateIncludeIPv6 { get; init; } = true;
    public bool FortigateSplitByFamily { get; init; } = true;
    public bool LargeRangeCheckEnabled { get; init; } = true;
    public int IPv4MinPrefix { get; init; } = LargeRangePolicy.DefaultIPv4MinPrefix;   // 16
    public int IPv6MinPrefix { get; init; } = LargeRangePolicy.DefaultIPv6MinPrefix;   // 32
}
