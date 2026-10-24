using System.Globalization;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

public static partial class AppText
{
    public const string SettingsCaption = "Export folder, feeds, API keys and the networks no feed blocks";
    public const string OptionsCaption = "Feed types, the export folder, IPv6, large ranges and AbuseIPDB";
    public const string AsnExportHeader = "ASN export";
    public const string AsnExportWritesFeeds = "Write the ticked feed types with each ASN export";
    public const string IncludeIPv4 = "Include IPv4 networks";
    public const string IncludeIPv6 = "Include IPv6 networks";
    public const string SplitByFamily = "Write IPv4 and IPv6 to separate files";
    public const string EnterFirstRuleId = "Enter the first rule id for new lists.";
    public const string FixModSecurityLocation = "Correct the ModSecurity location or clear it.";

    public const string FeedTypesHeader = "Write feed";

    public const string FeedTypesCaption =
        "Write feed and Export write each feed once for every type ticked, and a type you untick keeps its files as "
        + "they were. Empty feed is the exception: it empties the AbuseIPDB files in both folders, whatever is ticked.";

    public const string WriteFortigateFeedsLabel = "Fortigate threat feeds, in the fortigate folder";
    public const string WriteModSecurityListsLabel = "ModSecurity IP lists, in the modsecurity folder";
    public const string ModSecurityLocationName = "ModSecurity list location";
    public const string ModSecurityFirstRuleIdLabel = "First rule id for new lists";
    public const string ModSecurityFirstRuleIdName = "First rule id for new lists";

    // Not a const: the highest rule id is formatted from its constant
    public static readonly string ModSecurityFirstRuleIdCaption =
        "Each list keeps the rule id it was first given. A new list takes this id, or the next one above every id kept, "
        + "up to " + ModSecurityLimits.RuleIdMax.ToString("N0", CultureInfo.InvariantCulture)
        + ". After Start fresh, raise it above every rule id already in your server's configuration.";
}
