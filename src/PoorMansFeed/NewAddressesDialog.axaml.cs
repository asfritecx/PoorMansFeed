using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PoorMansFeed.Core;

namespace PoorMansFeed;

/// <summary>
/// The rows come capped and ready-made from the prompt, but Copy puts the prompt's CopyText, every address, on the
/// clipboard and keeps the dialog open.
/// </summary>
public sealed partial class NewAddressesDialog : Window
{
    private readonly NewAddressesPrompt? _prompt;
    private readonly Func<string, Task>? _copy;
    private readonly Func<WebPage, Task>? _openPage;
    private bool _copying;

    // The row the last menu request came from, null when it came from no row
    private NewAddressRow? _menuRow;

    public NewAddressesDialog() => InitializeComponent();

    internal NewAddressesDialog(NewAddressesPrompt prompt, Func<string, Task> copy, Func<WebPage, Task> openPage) : this()
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(openPage);
        _prompt = prompt;
        _copy = copy;
        _openPage = openPage;

        Title = prompt.Title;
        this.FindControl<TextBlock>("TitleText")!.Text = prompt.Title;
        this.FindControl<TextBlock>("MessageText")!.Text = prompt.Message;
        this.FindControl<TextBlock>("CountText")!.Text = prompt.CountText;
        ItemsControl list = this.FindControl<ItemsControl>("AddressList")!;
        list.ItemsSource = prompt.Rows;

        // Tunnel, so the row is recorded before the list's menu, a bubble handler, sees the request
        list.AddHandler(ContextRequestedEvent, RecordMenuRow, RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void CopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_prompt is null || _copy is null || _copying)
        {
            return;
        }

        _copying = true;
        string line;
        try
        {
            await _copy(_prompt.CopyText);
            line = _prompt.CopiedText;
        }
        catch (Exception)
        {
            // Whatever the clipboard threw, the dialog says the copy failed and stays open; nothing escapes the handler
            line = AppText.AbuseNewAddressesCopyFailed;
        }
        finally
        {
            _copying = false;
        }

        ShowCopyStatus(line);
    }

    private void RecordMenuRow(object? sender, ContextRequestedEventArgs e) =>
        _menuRow = (e.Source as Control)?.DataContext as NewAddressRow;

    private void MenuOpening(object? sender, CancelEventArgs e)
    {
        if (_menuRow is null)
        {
            e.Cancel = true;
        }
    }

    private void OpenInIpinfoClicked(object? sender, RoutedEventArgs e) => OpenPage(_menuRow?.IpinfoPage);

    private void OpenInAbuseIpDbClicked(object? sender, RoutedEventArgs e) => OpenPage(_menuRow?.AbuseIpDbPage);

    private async void OpenPage(WebPage? page)
    {
        if (page is null || _openPage is null)
        {
            return;
        }

        try
        {
            await _openPage(page);
        }
        catch (Exception)
        {
            // A launched browser proves nothing, so only a failure gets a line; nothing escapes the handler
            ShowCopyStatus(AppText.WebPageOpenFailed);
        }
    }

    private void ShowCopyStatus(string line)
    {
        TextBlock status = this.FindControl<TextBlock>("CopyStatusText")!;
        status.Text = line;
        status.Classes.Set("placeholder", string.IsNullOrEmpty(line));
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
