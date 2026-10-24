using CommunityToolkit.Mvvm.ComponentModel;
using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed;

/// <summary>Why the token dialog is shown: the first run, or the user asked for a new token.</summary>
public enum TokenPromptReason { FirstRun, Reset }

/// <summary>What the secret dialog shows: the IPinfo token's texts or the AbuseIPDB key's.</summary>
public sealed record SecretPrompt(string Title, string Body, string BoxName, string ShowText, string SaveText)
{
    // A reset has already been explained, so it gets the body without the first sentence
    public static SecretPrompt ForIpInfo(TokenPromptReason reason) => reason == TokenPromptReason.Reset
        ? new(AppText.TokenResetTitle, AppText.TokenResetBody, AppText.TokenBoxName, AppText.ShowToken, AppText.SaveToken)
        : new(AppText.TokenFirstTitle, AppText.TokenBody, AppText.TokenBoxName, AppText.ShowToken, AppText.SaveToken);

    // Rejected: AbuseIPDB refused the saved key, so the body says that instead of what a check sends. Replace: the user
    // chose to set a key, so the body says nothing is sent.
    public static SecretPrompt ForAbuseIpDb(AbuseKeyAsk why) => why switch
    {
        AbuseKeyAsk.Rejected => new(AppText.AbuseKeyReplaceTitle, AppText.AbuseKeyReplaceBody, AppText.AbuseKeyBoxName, AppText.ShowKey, AppText.SaveKey),
        AbuseKeyAsk.Replace => new(AppText.AbuseKeySetTitle, AppText.AbuseKeySetBody, AppText.AbuseKeyBoxName, AppText.ShowKey, AppText.SaveKey),
        _ => new(AppText.AbuseKeyFirstTitle, AppText.AbuseKeyBody, AppText.AbuseKeyBoxName, AppText.ShowKey, AppText.SaveKey),
    };
}

/// <summary>How the configure dialog closed. Esc and the window's own close button are Close.</summary>
public enum ConfigureFeedsChoice
{
    // First, so a dialog closed without a result, which gives the default value, reads as Close
    Close,
    OpenSettings,
}

/// <summary>What the large-range dialog shows. Built by the view model; the dialog returns the networks ticked.</summary>
public sealed record LargeRangePrompt(string Title, string Message, IReadOnlyList<LargeRangeChoice> Choices);

/// <summary>
/// One line of the new addresses dialog, with its texts ready-made, so the dialog only binds, and the two pages its menu
/// opens, built from the address the read judged.
/// </summary>
public sealed record NewAddressRow(string Address, string Score, string LastReported, WebPage IpinfoPage, WebPage AbuseIpDbPage);

/// <summary>
/// What the new addresses dialog shows, built by the view model. The rows are already capped; CopyText holds every
/// address, and CopiedText is the line shown after a copy.
/// </summary>
public sealed record NewAddressesPrompt(string Title, string Message, string CountText, IReadOnlyList<NewAddressRow> Rows,
                                        string CopyText, string CopiedText);

/// <summary>One large range in the dialog. Unticked by default, so a large range is left out unless the user ticks it.</summary>
public sealed class LargeRangeChoice : ObservableObject
{
    private bool _isIncluded;

    public LargeRangeChoice(string network, string sizeText)
    {
        Network = network;
        SizeText = sizeText;
        AccessibleText = $"{network}, {sizeText}";
    }

    public string Network { get; }

    public string SizeText { get; }

    public string AccessibleText { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public static LargeRangeChoice From(LargeRange range)
    {
        string size = range.IsIpv6 && range.PrefixLength <= 64
            ? AppText.Subnets64(System.Numerics.BigInteger.Pow(2, 64 - range.PrefixLength))
            : AppText.Addresses(range.AddressCount);
        return new LargeRangeChoice(range.Network, size);
    }
}

/// <summary>What the rules dialog starts from: a class, so no generated ToString prints the location.</summary>
public sealed class ModSecurityRulesPrompt
{
    public ModSecurityRulesPrompt(IReadOnlyList<string> fileNames, IReadOnlyList<int> ruleIds, string feedLabel,
                                  string location, ModSecurityAction action)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(ruleIds);
        ArgumentNullException.ThrowIfNull(feedLabel);
        ArgumentNullException.ThrowIfNull(location);
        if (fileNames.Count != ruleIds.Count)
        {
            throw new ArgumentException("Each file needs one rule id.", nameof(ruleIds));
        }

        FileNames = [.. fileNames];
        RuleIds = [.. ruleIds];
        FeedLabel = feedLabel;
        Location = location;
        Action = action;
    }

    public IReadOnlyList<string> FileNames { get; }
    public IReadOnlyList<int> RuleIds { get; }
    public string FeedLabel { get; }
    public string Location { get; }
    public ModSecurityAction Action { get; }
}
