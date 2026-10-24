using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using PoorMansFeed.Core;
using GraphiteUi;

namespace PoorMansFeed;

internal sealed class AvaloniaUiServices(Window owner, TimeProvider? time = null) : IUiServices
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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

    // True means only that the browser was asked, not that the page showed. The https check repeats WebPage's own
    // rule, because Windows hands the text to ShellExecute.
    public async Task OpenWebPageAsync(WebPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Uri.Scheme != Uri.UriSchemeHttps || !await owner.Launcher.LaunchUriAsync(page.Uri))
        {
            throw new InvalidOperationException("The page could not be opened.");
        }
    }

    public async Task<string?> PromptForTokenAsync(TokenPromptReason reason)
    {
        var dialog = new TokenDialog(reason);
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task<string?> PromptForAbuseIpDbKeyAsync(AbuseKeyAsk why)
    {
        var dialog = new TokenDialog(SecretPrompt.ForAbuseIpDb(why));
        return await dialog.ShowDialog<string?>(owner);
    }

    public async Task<string?> ChooseFolderAsync(string? startFolder)
    {
        // Start the picker in the folder given, when that folder still exists
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder)
            && Uri.TryCreate(Path.GetFullPath(startFolder), UriKind.Absolute, out Uri? folderUri))
        {
            start = await owner.StorageProvider.TryGetFolderFromPathAsync(folderUri);
        }

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppText.ChooseFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        // A cancelled picker gives no folders, and a folder without a local path cannot be used
        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public async Task<IReadOnlyList<string>?> ChooseLargeRangesAsync(LargeRangePrompt prompt)
    {
        var dialog = new LargeRangesDialog(prompt);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
    }

    public async Task ShowNewAddressesAsync(NewAddressesPrompt prompt)
    {
        // Copy goes through the same clipboard path as Lookup's Copy
        var dialog = new NewAddressesDialog(prompt, CopyTextAsync, OpenWebPageAsync);
        await dialog.ShowDialog(owner);
    }

    public async Task ShowModSecurityRulesAsync(ModSecurityRulesPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var dialog = new ModSecurityRulesDialog(new ModSecurityRulesModel(prompt, _time, CopyTextAsync));
        await dialog.ShowDialog(owner);
    }

    public async Task<ConfigureFeedsChoice> AskToConfigureFeedsAsync()
    {
        var dialog = new ConfigureFeedsDialog();
        return await dialog.ShowDialog<ConfigureFeedsChoice>(owner);
    }

    public void ShowSettingsPage()
    {
        if (owner is MainWindow main)
        {
            main.ShowSettingsPage();
        }
    }
}
