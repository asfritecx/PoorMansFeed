using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace PoorMansFeed.Tests;

/// <summary>
/// The configure dialog in a headless window, run modally under a shown owner as in production: its texts, what each
/// button and key returns, and the window services that show it and the Settings page.
/// </summary>
public sealed class ConfigureFeedsDialogTests
{
    private static Window ShownOwner()
    {
        var owner = new Window();
        owner.Show();
        return owner;
    }

    private static void PressEnter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static void PressEscape(Window window) => window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

    // The buttons close their dialog from a Click handler, which RaiseEvent runs
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void TheDialogShowsTheConfigureTexts()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        _ = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        Assert.Equal("No feed type ticked", dialog.Title);
        Assert.Equal(AppText.ConfigureFeedsTitle, dialog.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal("Please configure your write feed settings under Settings and the feed types available to write feed.",
            dialog.FindControl<TextBlock>("MessageText")!.Text);
        Assert.Equal("Open Settings", dialog.FindControl<Button>("OpenSettingsButton")!.Content);
        Assert.Equal("Close", dialog.FindControl<Button>("CloseButton")!.Content);
        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TheDialogIsAFixedNoticeWithCloseAsItsCancelAndNoDefault()
    {
        var dialog = new ConfigureFeedsDialog();

        Assert.Equal(440, dialog.Width);
        Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
        Assert.False(dialog.CanResize);
        Assert.False(dialog.ShowInTaskbar);
        Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        Assert.Equal(TextWrapping.Wrap, dialog.FindControl<TextBlock>("MessageText")!.TextWrapping);
        Assert.True(dialog.FindControl<Button>("CloseButton")!.IsCancel);
        Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<Button>(), button => button.IsDefault);
    }

    [AvaloniaFact]
    public void OpenSettingsReturnsOpenSettings()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        Task<ConfigureFeedsChoice> result = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        Click(dialog.FindControl<Button>("OpenSettingsButton")!);

        Assert.True(result.IsCompleted);
        Assert.Equal(ConfigureFeedsChoice.OpenSettings, result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void CloseReturnsClose()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        Task<ConfigureFeedsChoice> result = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        Click(dialog.FindControl<Button>("CloseButton")!);

        Assert.True(result.IsCompleted);
        Assert.Equal(ConfigureFeedsChoice.Close, result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeReturnsClose()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        Task<ConfigureFeedsChoice> result = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        PressEscape(dialog);

        Assert.True(result.IsCompleted);
        Assert.Equal(ConfigureFeedsChoice.Close, result.Result);
        owner.Close();
    }

    // The window's own close button gives no result, so the dialog gives the enum's default
    [AvaloniaFact]
    public void ClosingTheWindowReturnsClose()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        Task<ConfigureFeedsChoice> result = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        dialog.Close();

        Assert.True(result.IsCompleted);
        Assert.Equal(ConfigureFeedsChoice.Close, result.Result);
        owner.Close();
    }

    // Enter presses a focused button even with no default, so nothing is focused here
    [AvaloniaFact]
    public void EnterAloneOpensNothing()
    {
        Window owner = ShownOwner();
        var dialog = new ConfigureFeedsDialog();
        Task<ConfigureFeedsChoice> result = dialog.ShowDialog<ConfigureFeedsChoice>(owner);

        PressEnter(dialog);

        Assert.False(result.IsCompleted);
        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheWindowServicesShowTheDialogAndReturnItsChoice()
    {
        Window owner = ShownOwner();
        var ui = new AvaloniaUiServices(owner);

        Task<ConfigureFeedsChoice> result = ui.AskToConfigureFeedsAsync();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        ConfigureFeedsDialog dialog = owner.OwnedWindows.OfType<ConfigureFeedsDialog>().Single();
        Click(dialog.FindControl<Button>("OpenSettingsButton")!);

        Assert.Equal(ConfigureFeedsChoice.OpenSettings, await result);
        owner.Close();
    }

    [AvaloniaFact]
    public void TheWindowServicesShowTheSettingsPage()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            new AvaloniaUiServices(window).ShowSettingsPage();

            Assert.Same(window.FindControl<ListBoxItem>("NavSettings"), window.FindControl<ListBox>("NavList")!.SelectedItem);
            Assert.True(window.FindControl<Control>("SettingsPage")!.IsVisible);
            Assert.False(window.FindControl<Control>("LookupPage")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
