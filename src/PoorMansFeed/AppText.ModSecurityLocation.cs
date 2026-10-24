using System.Globalization;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>How near a SAS link's expiry is, against the clock it was judged on.</summary>
public enum SasExpiryNearness
{
    Later,
    Soon,
    Passed,
}

/// <summary>
/// The note under a ModSecurity location: why it is refused, or when its SAS expires. Text is empty when there is
/// none, and Nearness is null when the location is refused or gives no expiry.
/// </summary>
public sealed record ModSecurityLocationNote(string Text, SasExpiryNearness? Nearness, bool IsRefused);

public static partial class AppText
{
    public const string ModSecurityLocationLabel = "Where your server reads the lists";
    public const string ModSecurityLocationPlaceholder = "/etc/modsecurity/lists or https://\u2026";

    public const string ModSecurityLocationWarning =
        "The whole location, query included, is kept and shown as plain text, ends up in your server's configuration "
        + "and can appear in its logs. Use a read-only SAS (sp=r or rl) for one container or directory (sr=c or d), "
        + "with its expiry (se) in the link, never the SAS that publishes the lists. A SAS signed with a user "
        + "delegation key stops working when its key expires (ske), within 7 days, whatever expiry (se) the link "
        + "gives, so the expiry shown is the earlier of the two. A SAS made from a stored access policy is accepted "
        + "only when the link itself carries its rights and expiry. To lower a link's rights, make a new read-only "
        + "SAS: editing sp, sr, ss or srt doesn't lower what its signature allows.";

    // Not a const: the link's length limit is formatted from its constant. The folder's 1,024 is private to the rules,
    // so a test ties it to their behaviour
    public static readonly string ModSecurityLocationInvalid =
        "This location can't be used. A folder starts with /, is at most 1,024 characters and holds only letters a "
        + "to z in either case, digits and . _ - /, with no // or . or .. part. A link starts with https:// and is "
        + "at most " + ModSecurityLimits.MaxLocationLength.ToString("N0", CultureInfo.InvariantCulture)
        + " characters. Besides those letters and digits it holds only - . _ ~ / : ? & = + and commas, and any "
        + "other character is written with % escapes, such as %24web for $web. Before the ? it has no & and no . or .. "
        + "part, and none of %26, %3F, %2F or %5C. A SAS token pasted after the link needs a ? in front of it.";

    public const string ModSecurityActionLabel = "Rule action";
    public const string ModSecurityLogOnly = "Log only";
    public const string ModSecurityDeny = "Deny with status 403";
    public const string ModSecurityClose = "Close with no reply (444)";

    public const string ModSecurityStartFreshRuleIds =
        "After Start fresh, raise the first rule id on the Settings page above every rule id already in your server's "
        + "configuration.";

    /// <summary>The line for a refused location, or null for a failure that isn't the location's.</summary>
    public static string? ModSecurityLocationProblem(ModSecurityRuleFailure failure) => failure switch
    {
        ModSecurityRuleFailure.NotHttps =>
            "A link must start with https://, because ModSecurity v2 refuses http:// and v3 reads it as a file path. "
            + "A folder on the server starts with /.",
        ModSecurityRuleFailure.LocationInvalid => ModSecurityLocationInvalid,
        ModSecurityRuleFailure.SasCanWrite =>
            "This SAS can write. Make a new read-only SAS (sp=r or rl): editing sp doesn't lower what its signature "
            + "allows.",
        ModSecurityRuleFailure.SasRightsUnknown =>
            "This link has a signature (sig) whose rights can't be read from the link. Use a read-only SAS with its "
            + "rights in the link (sp=r or rl).",
        ModSecurityRuleFailure.SasScopeTooWide =>
            "This SAS covers a whole account or a single file. Make a read-only SAS for the container or directory "
            + "that holds the lists (sr=c or d).",
        ModSecurityRuleFailure.SasExpiryUnknown =>
            "The link has no expiry the app can read. Make a read-only SAS with one expiry (se) in the link, and one "
            + "key expiry (ske) too for a user delegation SAS. Write the + of a time offset as %2B.",
        ModSecurityRuleFailure.None or ModSecurityRuleFailure.NoLocation or ModSecurityRuleFailure.NoFiles
            or ModSecurityRuleFailure.FileNameInvalid or ModSecurityRuleFailure.RuleIdOutOfRange
            or ModSecurityRuleFailure.RuleIdRepeated => null,
        // A refusal added later still gets a line rather than none
        _ => ModSecurityLocationInvalid,
    };

    /// <summary>Whether a location as typed is refused: trimmed, and empty is not.</summary>
    public static bool ModSecurityLocationIsRefused(string? location) =>
        IsRefusal(ModSecurityRules.CheckLocation((location ?? "").Trim()));

    /// <summary>The note for a location as typed, judged as it will be saved: trimmed, and empty gives none.</summary>
    public static ModSecurityLocationNote ModSecurityLocationNoteFor(string? location, DateTimeOffset now)
    {
        string trimmed = (location ?? "").Trim();
        ModSecurityRuleFailure failure = ModSecurityRules.CheckLocation(trimmed);
        if (IsRefusal(failure))
        {
            return new ModSecurityLocationNote(ModSecurityLocationProblem(failure) ?? ModSecurityLocationInvalid, null,
                IsRefused: true);
        }

        DateTimeOffset? expiry = ModSecurityRules.SasExpiry(trimmed);
        return new ModSecurityLocationNote(
            ModSecuritySasExpiry(expiry, ModSecurityRules.SasExpiryHasTime(trimmed), now) ?? "",
            ModSecuritySasExpiryNearness(expiry, now), IsRefused: false);
    }

    // An empty location is valid: the rule is then given without one
    private static bool IsRefusal(ModSecurityRuleFailure failure) =>
        failure is not (ModSecurityRuleFailure.None or ModSecurityRuleFailure.NoLocation);

    /// <summary>The expiry line under the location box, or null when the link gives no expiry.</summary>
    public static string? ModSecuritySasExpiry(DateTimeOffset? expiry, bool? hasTime, DateTimeOffset now)
    {
        if (ModSecuritySasExpiryNearness(expiry, now) is not { } nearness)
        {
            return null;
        }

        DateTime utc = expiry!.Value.UtcDateTime;
        string date = utc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        string when = hasTime is false
            ? "at the start of " + date + " (UTC)"
            : "on " + date + " at " + utc.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

        return nearness switch
        {
            SasExpiryNearness.Passed => "The SAS in this link expired " + when + ". Make a new one: the server can't "
                                        + "fetch the lists, so it won't start, and a reload is refused, keeping the "
                                        + "lists it had.",
            SasExpiryNearness.Soon => "The SAS in this link expires " + when + ", in less than "
                                      + ModSecurityLimits.SasExpiryWarningDays.ToString(CultureInfo.InvariantCulture)
                                      + " days. Make a new one before then. Once it expires, the server can't fetch the "
                                      + "lists: it won't start, and a reload is refused, keeping the lists it had.",
            _ => "The SAS in this link expires " + when + ".",
        };
    }

    public static SasExpiryNearness? ModSecuritySasExpiryNearness(DateTimeOffset? expiry, DateTimeOffset now)
    {
        if (expiry is not { } at)
        {
            return null;
        }

        if (at <= now)
        {
            return SasExpiryNearness.Passed;
        }

        return at - now < TimeSpan.FromDays(ModSecurityLimits.SasExpiryWarningDays)
            ? SasExpiryNearness.Soon
            : SasExpiryNearness.Later;
    }
}
