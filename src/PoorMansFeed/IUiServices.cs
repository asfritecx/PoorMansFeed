using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>
/// Everything the view model needs from a window, behind an interface so tests use a fake and never open a dialog,
/// the clipboard or a folder. Every dialog returns null (or false) when it is cancelled.
/// </summary>
public interface IUiServices
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task CopyTextAsync(string text);
    Task OpenFolderAsync(string path);
    Task OpenFileAsync(string path);

    /// <summary>Asks the default browser to show the page; throws when it can't be asked.</summary>
    Task OpenWebPageAsync(WebPage page);

    /// <summary>
    /// Shows an owned notice whose only button is <see cref="GraphiteUi.AppInfo.CloseAppText"/>, before the app closes.
    /// Completes however it is dismissed.
    /// </summary>
    Task ShowClosingNoticeAsync(string title, string message);

    /// <summary>Closes the main window without the busy-close prompt, which ends the app.</summary>
    void CloseApplication();

    /// <summary>Asks for the IPinfo API token. Null = cancelled.</summary>
    Task<string?> PromptForTokenAsync(TokenPromptReason reason);

    /// <summary>Asks for the AbuseIPDB key, with the texts for <paramref name="why"/>. Null = cancelled.</summary>
    Task<string?> PromptForAbuseIpDbKeyAsync(AbuseKeyAsk why);

    /// <summary>Asks for a folder, starting in startFolder when it exists. Null = cancelled or not a local folder.</summary>
    Task<string?> ChooseFolderAsync(string? startFolder);

    /// <summary>Asks which large ranges to export. Null = cancelled; otherwise the networks to include.</summary>
    Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt);

    /// <summary>
    /// Shows the new addresses until the user closes the dialog. Copy, inside the dialog, puts CopyText on the clipboard
    /// </summary>
    Task ShowNewAddressesAsync(NewAddressesPrompt prompt);

    /// <summary>Shows the rules for the prompt's files until the dialog is closed; nothing in it is saved.</summary>
    Task ShowModSecurityRulesAsync(ModSecurityRulesPrompt prompt);

    /// <summary>Says no feed type is ticked and offers the Settings page. Completes however it is dismissed.</summary>
    Task<ConfigureFeedsChoice> AskToConfigureFeedsAsync();

    /// <summary>Shows the Settings page, as its nav item does; while start-up has stopped it does nothing.</summary>
    void ShowSettingsPage();
}
