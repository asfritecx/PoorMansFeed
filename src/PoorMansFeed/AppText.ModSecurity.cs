using System.Globalization;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

public static partial class AppText
{
    // A rule build whose rule ids couldn't be assigned; None never shows, as a rule was made
    public static string ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure failure) => failure switch
    {
        ModSecurityRuleIdsFailure.StorageError => "The ModSecurity rule ids couldn't be saved, so no rule was made." + TryAgain,
        ModSecurityRuleIdsFailure.Unavailable =>
            "The saved ModSecurity rule ids can't be read by this version, so no rule was made. Nothing in the app clears them yet.",
        ModSecurityRuleIdsFailure.RangeFull => "No ModSecurity rule id is left above the highest one kept ("
            + Number(ModSecurityLimits.RuleIdMax) + " at most), so no rule was made.",
        ModSecurityRuleIdsFailure.TooMany => "Too many ModSecurity lists have rule ids kept, so no rule was made.",
        _ => Generic,
    };

    public const string ModSecurityRulesTitle = "ModSecurity rules";
    public const string ModSecurityRulesButton = "ModSecurity rules" + Ellipsis;
    public const string ModSecurityRulesBusy = "The ModSecurity rules weren't shown, because something else is open or "
        + "running. Press again once it has finished.";

    // The tool that made the rule, in each rule's msg; the file name beside it already says which feed
    public const string ModSecurityRuleLabel = "PoorMansFeed";

    public const string ModSecurityRulesEnterLocation =
        "Enter where your server reads the lists, a folder starting with / or an https:// link.";
    public const string ModSecurityRulesPreviewName = "The rules to copy";
    public const string ModSecurityRulesNotesName = "Notes on the rules";
    public const string ModSecurityRulesCopy = "Copy";
    public const string ModSecurityRulesCopyFailed = "The rules couldn't be copied.";

    public static string ModSecurityRulesCopied(int count) => "Copied " + Count(count, "rule", "rules");

    // Plain digits, as each rule writes its id
    public static string ModSecurityRulesIds(IReadOnlyList<string> fileNames, IReadOnlyList<int> ruleIds)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(ruleIds);
        string pairs = string.Join(", ", fileNames.Select((name, i) =>
            name + " " + ruleIds[i].ToString(CultureInfo.InvariantCulture)));
        return (fileNames.Count == 1 ? "Rule id kept for this list: " : "Rule ids kept for these lists: ") + pairs + ".";
    }

    public const string ModSecurityRulesNoteFile = "Load the rules from a file, with modsecurity_rules_file on nginx or "
        + "Include on Apache, never inline in the server configuration.";
    public const string ModSecurityRulesNoteHttps =
        "An https list ties the server's start to that host and to the SAS expiry.";
    public const string ModSecurityRulesNoteIds = "Each list keeps its rule id: replace a list's earlier rule rather than "
        + "adding a second, as ModSecurity won't load two rules with the same id. Leave the OWASP CRS ranges alone.";
    public const string ModSecurityRulesNoteProxy = "Behind a reverse proxy, load balancer or CDN, ModSecurity sees the "
        + "proxy's address, not the visitor's, so these rules match nothing until the web server takes the visitor's "
        + "address from the proxy's header: mod_remoteip on Apache, or the realip module (set_real_ip_from and "
        + "real_ip_header in the http or server block) on nginx, trusting only your proxy's addresses.";
    public const string ModSecurityRulesNoteClose = ModSecurityClose + " sends nothing back, so the server spends no "
        + "response on a listed address. nginx logs the request as 444; Apache logs it as 403, and its error log says "
        + "the connection was closed. Behind a reverse proxy, load balancer or CDN, it is the proxy's connection that "
        + "closes: the proxy answers the visitor with its own error, and may first try another server.";
    public const string ModSecurityRulesNoteListen = "On nginx, keep the two lines listen 80; and listen [::]:80;. With a "
        + "single listen [::]:80 ipv6only=off; line instead, ModSecurity v3 sees every IPv4 visitor as ::ffff:a.b.c.d, "
        + "and the IPv4 lists match nothing.";
    public const string ModSecurityRulesNoteWindows =
        "A Windows folder can't be used: the location is a folder on the server or a link.";

    /// <summary>The dialog's notes, in order: the location's warning first, so it shows unscrolled.</summary>
    public static IReadOnlyList<string> ModSecurityRulesNotes { get; } =
    [
        ModSecurityLocationWarning,
        ModSecurityRulesNoteFile,
        ModSecurityRulesNoteHttps,
        ModSecurityRulesNoteIds,
        ModSecurityStartFreshRuleIds,
        ModSecurityRulesNoteClose,
        ModSecurityRulesNoteProxy,
        ModSecurityRulesNoteListen,
        ModSecurityRulesNoteWindows,
    ];

    public const string HelpModSecurityHeading = "6. ModSecurity";

    /// <summary>Help's ModSecurity section, one short paragraph each.</summary>
    public static IReadOnlyList<string> HelpModSecurityTexts { get; } =
    [
        "Your web server reads the ModSecurity lists only when it starts and when it reloads, so reload it after each "
            + "write: nginx -s reload, or apachectl graceful on Apache. A reload that a list fails is refused, and the "
            + "old lists stay in use.",
        "Don't edit the lists by hand: one line ModSecurity can't read can fail the whole list, and by default a "
            + "server won't start with a list that fails.",
        "A link must start with https://: ModSecurity v2 refuses http://, and v3 reads it as a file path.",
        ModSecurityRulesNoteIds,
        ModSecurityStartFreshRuleIds,
        "Unticking a feed type in Settings leaves its files as they were, and they stop updating: Write feed and "
            + "Export no longer write or delete them.",
        "Empty feed, on " + AbuseIpDbTitle + ", empties the AbuseIPDB files in both the fortigate and modsecurity "
            + "folders, whatever is ticked. To retire the AbuseIPDB list of a type you unticked, press Empty feed and "
            + "then Write feed, which fills the ticked type's files again.",
        "On nginx, ModSecurity writes a log-only match to the error_log at info, so an error_log at notice or above "
            + "hides it; the audit log has it too while SecAuditEngine is On. On Apache it writes log-only, deny and "
            + "close matches at error. When a list fails to load, ModSecurity v3 names only the rules file and line: the "
            + "rule on that line names the list.",
        ModSecurityRulesNoteClose,
        ModSecurityRulesNoteListen,
        "Known limits: when a Fortigate file can't be written, the ModSecurity copies after it aren't written either. "
            + "The AbuseIPDB ModSecurity list keeps FortiOS's cap of " + Number(AbuseIpDbLimits.MaxFeedEntries)
            + " addresses a family, and its # Left out: line.",
    ];
}
