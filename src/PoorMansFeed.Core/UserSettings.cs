using System.Text;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.ModSecurity;

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
    public int AbuseIpDbKeepDays { get; init; } = AbuseIpDbLimits.DefaultKeepDays;     // a cutoff on what is read and written: the addresses listed within this many days of the last download (counted back from the earlier of now and the last download); nothing stored depends on it
    public bool WriteFortigateFeeds { get; init; } = false;      // a section or file without it reads true, so an earlier install keeps its FortiGate feeds
    public bool WriteModSecurityLists { get; init; } = false;
    public ModSecurityAction ModSecurityAction { get; init; } = ModSecurityAction.LogOnly;
    public int ModSecurityFirstRuleId { get; init; } = ModSecurityLimits.DefaultFirstRuleId;
    public string ModSecurityLocation { get; init; } = "";       // its content is checked where a rule is built

    // A method, so the JSON doesn't carry it; every write asks this rather than the fields, so a new type is one change
    public bool TicksAnyFeedType() => WriteFortigateFeeds || WriteModSecurityLists;

    private const string HiddenLocationText = "(hidden)";

    // The location can carry a SAS, so the text form prints a mark in its place; every other member prints as usual
    public override string ToString()
    {
        string shown = string.IsNullOrEmpty(ModSecurityLocation) ? "" : HiddenLocationText;
        var copy = this with { ModSecurityLocation = shown };
        var text = new StringBuilder("UserSettings { ");
        if (copy.PrintMembers(text))
            text.Append(' ');
        return text.Append('}').ToString();
    }
}
