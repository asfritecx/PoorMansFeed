namespace PoorMansFeed.Core.ModSecurity;

public enum ModSecurityRuleIdsFailure
{
    None,

    /// <summary>A name is null or isn't a list file name.</summary>
    FileNameInvalid,

    /// <summary>A new name would need an id above <see cref="ModSecurityLimits.RuleIdMax"/>.</summary>
    RangeFull,

    /// <summary>The kept ids would name more files than the store keeps.</summary>
    TooMany,

    /// <summary>The kept ids are in a value this build can't open or fully read; it is left as it is.</summary>
    Unavailable,

    /// <summary>The store is closed or the write failed.</summary>
    StorageError,
}

/// <summary>One id per name, in the order given; empty on any failure, and then nothing was saved.</summary>
public sealed record ModSecurityRuleIdsResult(ModSecurityRuleIdsFailure Failure, IReadOnlyList<int> RuleIds);
