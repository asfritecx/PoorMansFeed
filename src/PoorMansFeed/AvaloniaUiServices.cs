using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using PoorMansFeed.Core;
using GraphiteUi;

namespace PoorMansFeed;

internal sealed class AvaloniaUiServices(Window owner) : IUiServices
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ConfirmationDialog(title, message, confirmText);
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task ShowClosingNoticeAsync(string title, string message)
    {
        var dialog = new ConfirmationDialog(title, message, AppInfo.CloseAppText, showCancel: false);
        await dialog.ShowDialog<bool>(owner);
    }

    public void CloseApplication()
    {
        if (owner is MainWindow main)
        {
            main.CloseApproved();
        }
        else
        {
            owner.Close();
        }
    }

    public async Task CopyTextAsync(string text)
    {
        if (owner.Clipboard is null)
        {
            throw new InvalidOperationException("The clipboard is unavailable.");
        }

        await owner.Clipboard.SetTextAsync(text);
    }

    public async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path) || !await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path)))
        {
            throw new InvalidOperationException("The folder could not be opened.");
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path) || !await owner.Launcher.LaunchFileInfoAsync(new FileInfo(path)))
        {
            throw new InvalidOperationException("The file could not be opened.");
        }
    }

    public async Task<string?> PromptForTokenAsync(TokenPromptReason reason)
    {
        var dialog = new TokenDialog(reason);
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task<UserSettings?> EditSettingsAsync(UserSettings current, string defaultExportFolder)
    {
        var dialog = new SettingsDialog(current, defaultExportFolder);
        return await dialog.ShowDialog<UserSettings?>(owner);
    }

    public async Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt)
    {
        var dialog = new LargeRangesDialog(prompt);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
    }
}
