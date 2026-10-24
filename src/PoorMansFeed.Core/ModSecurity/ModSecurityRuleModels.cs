namespace PoorMansFeed.Core.ModSecurity;

public enum ModSecurityRuleFailure
{
    None,
    NoFiles,
    NoLocation,
    LocationInvalid,
    NotHttps,
    FileNameInvalid,
    RuleIdOutOfRange,
    SasCanWrite,
    RuleIdRepeated,
    SasRightsUnknown,
    SasScopeTooWide,
    SasExpiryUnknown,
}

/// <summary>
/// <see cref="RuleIds"/> holds one id per file, in <see cref="FileNames"/> order. <see cref="Location"/> is an absolute
/// server folder or an https prefix the list files are served under.
/// </summary>
public sealed record ModSecurityRuleRequest(IReadOnlyList<int> RuleIds, ModSecurityAction Action, string Location,
    IReadOnlyList<string> FileNames, string FeedLabel);

/// <summary><see cref="Text"/> is the rules, each ending in "\n"; a failure has no rules and an empty text.</summary>
public sealed record ModSecurityRuleSet(ModSecurityRuleFailure Failure, IReadOnlyList<string> Rules, string Text);
