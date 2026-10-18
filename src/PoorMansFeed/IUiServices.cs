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

    /// <summary>
    /// Shows an owned notice whose only button is <see cref="GraphiteUi.AppInfo.CloseAppText"/>, before the app closes.
    /// Completes however it is dismissed.
    /// </summary>
    Task ShowClosingNoticeAsync(string title, string message);

    /// <summary>Closes the main window without the busy-close prompt, which ends the app.</summary>
    void CloseApplication();

    /// <summary>Asks for the IPinfo API token. Null = cancelled.</summary>
    Task<string?> PromptForTokenAsync(TokenPromptReason reason);

    /// <summary>Edits the settings. Null = cancelled.</summary>
    Task<UserSettings?> EditSettingsAsync(UserSettings current, string defaultExportFolder);

    /// <summary>Asks which large ranges to export. Null = cancelled; otherwise the networks to include.</summary>
    Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt);
}
