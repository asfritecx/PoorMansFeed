namespace PoorMansFeed.Core.ModSecurity;

public static class ModSecurityLimits
{
    // ModSecurity's reference manual reserves rule ids 1 to 99,999 for local use; the OWASP Core Rule Set uses 900,000
    // to 999,999
    public const int RuleIdMin = 1;
    public const int RuleIdMax = 99_999;
    public const int DefaultFirstRuleId = 10_000;
    public const int MaxLocationLength = 2_048;

    // An expired SAS answers 403, so the server won't start and a reload is refused, keeping the lists it had
    public const int SasExpiryWarningDays = 14;
}
