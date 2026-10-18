using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>
/// Behaviour of the three app dialogs in a headless window: what each control shows, what Save and Escape return, and
/// that Enter alone never saves or exports. Each dialog runs modally under a shown owner, as in production.
/// </summary>
public sealed class DialogTests
{
    private const string DefaultFolder = "/exports/default";

    // ----- helpers -----

    private static Window ShownOwner()
    {
        var owner = new Window();
        owner.Show();
        return owner;
    }

    private static void PressEnter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static void PressEscape(Window window) => window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

    // The buttons close their dialog from a Click handler, which RaiseEvent runs (a bound command would not run)
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static CheckBox CheckBoxWithText(Window window, string text) =>
        window.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Content as string == text);

    private static CheckBox CheckBoxForAccessibleName(Window window, string name) =>
        window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => box.GetValue(AutomationProperties.NameProperty) == name);

    private static NumericUpDown PrefixBox(Window window, string label) =>
        window.GetLogicalDescendants().OfType<NumericUpDown>()
            .Single(box => box.GetValue(AutomationProperties.NameProperty) == label);

    private static UserSettings SavedSettings() => new()
    {
        ExportFolder = "/exports/custom",
        FortigateEnabled = true,
        FortigateIncludeIPv4 = false,
        FortigateIncludeIPv6 = true,
        FortigateSplitByFamily = false,
        LargeRangeCheckEnabled = true,
        IPv4MinPrefix = 12,
        IPv6MinPrefix = 40,
    };

    private static LargeRangePrompt SamplePrompt() => new(
        "Export AS15169?",
        "3 networks are larger than the limits in Settings (IPv4 /16, IPv6 /32).",
        [
            new LargeRangeChoice("10.0.0.0/8", "16,777,216 addresses"),
            new LargeRangeChoice("172.16.0.0/12", "1,048,576 addresses"),
            new LargeRangeChoice("2001:db8::/32", "79,228,162,514,264,337,593,543,950,336 addresses"),
        ]);

    // ----- token dialog -----

    [AvaloniaFact]
    public void TokenSaveIsDisabledWhileTheBoxIsBlank()
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(TokenPromptReason.FirstRun);
        Task<string?> result = dialog.ShowDialog<string?>(owner);
        var save = dialog.FindControl<Button>("SaveButton")!;

        Assert.False(save.IsEnabled);
        dialog.FindControl<TextBox>("TokenBox")!.Focus();
        dialog.KeyTextInput("   ");
        Assert.False(save.IsEnabled);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TypingATokenEnablesSave()
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(TokenPromptReason.FirstRun);
        Task<string?> result = dialog.ShowDialog<string?>(owner);
        var save = dialog.FindControl<Button>("SaveButton")!;

        dialog.FindControl<TextBox>("TokenBox")!.Focus();
        dialog.KeyTextInput("abc");
        Assert.True(save.IsEnabled);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void EnterSavesTheTrimmedToken()
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(TokenPromptReason.FirstRun);
        Task<string?> result = dialog.ShowDialog<string?>(owner);

        dialog.FindControl<TextBox>("TokenBox")!.Focus();
        dialog.KeyTextInput("  abc  ");
        PressEnter(dialog);

        Assert.True(result.IsCompleted);
        Assert.Equal("abc", result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeReturnsNullFromTheTokenDialog()
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(TokenPromptReason.Reset);
        Task<string?> result = dialog.ShowDialog<string?>(owner);

        PressEscape(dialog);

        Assert.True(result.IsCompleted);
        Assert.Null(result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void ShowTokenTogglesTheMask()
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(TokenPromptReason.FirstRun);
        Task<string?> result = dialog.ShowDialog<string?>(owner);
        var box = dialog.FindControl<TextBox>("TokenBox")!;
        var show = dialog.FindControl<CheckBox>("ShowToken")!;

        show.IsChecked = true;
        Assert.True(box.RevealPassword);
        show.IsChecked = false;
        Assert.False(box.RevealPassword);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TokenTitleDiffersBetweenFirstRunAndReset()
    {
        var firstRun = new TokenDialog(TokenPromptReason.FirstRun);
        var reset = new TokenDialog(TokenPromptReason.Reset);

        Assert.Equal(AppText.TokenFirstTitle, firstRun.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(AppText.TokenResetTitle, reset.FindControl<TextBlock>("TitleText")!.Text);
        Assert.NotEqual(firstRun.Title, reset.Title);
    }

    [AvaloniaFact]
    public void TokenBodyIsTheFirstRunTextAndTheResetBodyIsItsSharedSentences()
    {
        var firstRun = new TokenDialog(TokenPromptReason.FirstRun);
        var reset = new TokenDialog(TokenPromptReason.Reset);

        Assert.Equal("PoorMansFeed downloads the IPinfo Lite dataset with your IPinfo API token. "
                     + "The token is in your IPinfo account under API token. It's saved on this computer for your account.",
            firstRun.FindControl<TextBlock>("BodyText")!.Text);
        Assert.Equal("The token is in your IPinfo account under API token. It's saved on this computer for your account.",
            reset.FindControl<TextBlock>("BodyText")!.Text);
        Assert.Equal("API token", firstRun.FindControl<TextBox>("TokenBox")!.PlaceholderText);
        Assert.Equal("API token", AutomationProperties.GetName(firstRun.FindControl<TextBox>("TokenBox")!));
    }

    // ----- settings dialog -----

    [AvaloniaFact]
    public void SettingsControlsReflectTheGivenSettings()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        Assert.Equal("/exports/custom", dialog.FindControl<TextBlock>("ExportFolderText")!.Text);
        Assert.True(CheckBoxWithText(dialog, AppText.FortigateEnabled).IsChecked == true);
        Assert.False(CheckBoxWithText(dialog, AppText.IncludeIPv4).IsChecked == true);
        Assert.True(dialog.FindControl<CheckBox>("FortigateIPv6Box")!.IsChecked == true);
        Assert.False(CheckBoxWithText(dialog, AppText.SplitByFamily).IsChecked == true);
        Assert.True(CheckBoxWithText(dialog, AppText.AskLargeRanges).IsChecked == true);
        Assert.Equal((decimal?)12, PrefixBox(dialog, AppText.IPv4PrefixLabel).Value);
        Assert.Equal((decimal?)40, PrefixBox(dialog, AppText.IPv6PrefixLabel).Value);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void UntickingBothAddressFamiliesDisablesSaveAndShowsTheMessageWithoutResizing()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(new UserSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);
        var save = dialog.FindControl<Button>("SaveButton")!;
        var message = dialog.FindControl<TextBlock>("ValidationTextBlock")!;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        double heightBefore = dialog.ClientSize.Height;

        CheckBoxWithText(dialog, AppText.IncludeIPv4).IsChecked = false;
        dialog.FindControl<CheckBox>("FortigateIPv6Box")!.IsChecked = false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.False(save.IsEnabled);
        Assert.Equal(AppText.ChooseAddressFamily, message.Text);
        Assert.Equal(18d, message.Bounds.Height);
        Assert.Equal(heightBefore, dialog.ClientSize.Height);
        Assert.False(result.IsCompleted);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void SaveReturnsTheEditedRecord()
    {
        Window owner = ShownOwner();
        UserSettings current = SavedSettings();
        var dialog = new SettingsDialog(current, DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        PrefixBox(dialog, AppText.IPv4PrefixLabel).Value = 20;
        CheckBoxWithText(dialog, AppText.SplitByFamily).IsChecked = true;
        Click(dialog.FindControl<Button>("SaveButton")!);

        Assert.True(result.IsCompleted);
        Assert.Equal(current with { IPv4MinPrefix = 20, FortigateSplitByFamily = true }, result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void UseDefaultFolderSavesAnEmptyFolder()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        Click(dialog.FindControl<Button>("UseDefaultButton")!);
        Assert.Equal(DefaultFolder, dialog.FindControl<TextBlock>("ExportFolderText")!.Text);
        Click(dialog.FindControl<Button>("SaveButton")!);

        Assert.Equal("", result.Result!.ExportFolder);
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeReturnsNullFromTheSettingsDialog()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        PressEscape(dialog);

        Assert.True(result.IsCompleted);
        Assert.Null(result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void EnterAloneDoesNotSaveTheSettings()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        CheckBoxWithText(dialog, AppText.FortigateEnabled).Focus();
        PressEnter(dialog);

        Assert.False(result.IsCompleted);
        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void IPv6BoxReflectsTheSettingAndUntickingItDisablesTheIPv6Controls()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings() with { IPv6Enabled = true }, DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);
        CheckBox ipv6 = dialog.FindControl<CheckBox>("IPv6EnabledBox")!;
        CheckBox fortigateIPv6 = dialog.FindControl<CheckBox>("FortigateIPv6Box")!;
        NumericUpDown ipv6Prefix = PrefixBox(dialog, AppText.IPv6PrefixLabel);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        double heightBefore = dialog.ClientSize.Height;
        Assert.True(ipv6.IsChecked == true);
        Assert.True(fortigateIPv6.IsEnabled);
        Assert.True(ipv6Prefix.IsEnabled);

        ipv6.IsChecked = false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.False(fortigateIPv6.IsEnabled);
        Assert.False(ipv6Prefix.IsEnabled);
        Assert.Equal(heightBefore, dialog.ClientSize.Height);
        Assert.False(result.IsCompleted);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void IPv6BoxShowsOffWhenTheSettingIsOff()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings() with { IPv6Enabled = false }, DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        Assert.False(dialog.FindControl<CheckBox>("IPv6EnabledBox")!.IsChecked == true);
        Assert.False(dialog.FindControl<CheckBox>("FortigateIPv6Box")!.IsEnabled);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void SaveReturnsIPv6EnabledOffAndKeepsTheIPv6Values()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings() with { FortigateIncludeIPv4 = true }, DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);

        dialog.FindControl<CheckBox>("IPv6EnabledBox")!.IsChecked = false;
        Click(dialog.FindControl<Button>("SaveButton")!);

        Assert.True(result.IsCompleted);
        UserSettings saved = result.Result!;
        Assert.False(saved.IPv6Enabled);
        Assert.Equal(40, saved.IPv6MinPrefix);
        owner.Close();
    }

    [AvaloniaFact]
    public void IPv6OffWithOnlyTheFortigateIPv6BoxTickedDisablesSave()
    {
        Window owner = ShownOwner();
        var dialog = new SettingsDialog(SavedSettings(), DefaultFolder);
        Task<UserSettings?> result = dialog.ShowDialog<UserSettings?>(owner);
        var save = dialog.FindControl<Button>("SaveButton")!;
        var message = dialog.FindControl<TextBlock>("ValidationTextBlock")!;

        dialog.FindControl<CheckBox>("IPv6EnabledBox")!.IsChecked = false;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.False(save.IsEnabled);
        Assert.Equal(AppText.ChooseAddressFamily, message.Text);
        Assert.False(result.IsCompleted);

        PressEscape(dialog);
        owner.Close();
    }

    // ----- large ranges dialog -----

    [AvaloniaFact]
    public void EveryLargeRangeStartsUnticked()
    {
        Window owner = ShownOwner();
        LargeRangePrompt prompt = SamplePrompt();
        var dialog = new LargeRangesDialog(prompt);
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.All(prompt.Choices, choice => Assert.False(choice.IsIncluded));
        Assert.All(dialog.GetLogicalDescendants().OfType<CheckBox>(), box => Assert.NotEqual(true, box.IsChecked));

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void CountStartsAtZeroOfAll()
    {
        Window owner = ShownOwner();
        var dialog = new LargeRangesDialog(SamplePrompt());
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);

        Assert.Equal("0 of 3 ticked", dialog.FindControl<TextBlock>("CountText")!.Text);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void EnterAloneDoesNotExportLargeRanges()
    {
        Window owner = ShownOwner();
        var dialog = new LargeRangesDialog(SamplePrompt());
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);

        dialog.GetLogicalDescendants().OfType<CheckBox>().First().Focus();
        PressEnter(dialog);

        Assert.False(result.IsCompleted);
        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TickingOneRangeAndExportingReturnsOnlyThatNetwork()
    {
        Window owner = ShownOwner();
        LargeRangePrompt prompt = SamplePrompt();
        var dialog = new LargeRangesDialog(prompt);
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        CheckBoxForAccessibleName(dialog, prompt.Choices[1].AccessibleText).IsChecked = true;
        Assert.True(prompt.Choices[1].IsIncluded);
        Assert.Equal("1 of 3 ticked", dialog.FindControl<TextBlock>("CountText")!.Text);

        Click(dialog.FindControl<Button>("ExportButton")!);

        Assert.True(result.IsCompleted);
        Assert.Equal(new[] { "172.16.0.0/12" }, result.Result!);
        owner.Close();
    }

    [AvaloniaFact]
    public void ExportWithNothingTickedReturnsAnEmptyList()
    {
        Window owner = ShownOwner();
        var dialog = new LargeRangesDialog(SamplePrompt());
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);

        Click(dialog.FindControl<Button>("ExportButton")!);

        Assert.True(result.IsCompleted);
        Assert.NotNull(result.Result);
        Assert.Empty(result.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeReturnsNullFromTheLargeRangesDialog()
    {
        Window owner = ShownOwner();
        var dialog = new LargeRangesDialog(SamplePrompt());
        Task<IReadOnlyList<string>?> result = dialog.ShowDialog<IReadOnlyList<string>?>(owner);

        PressEscape(dialog);

        Assert.True(result.IsCompleted);
        Assert.Null(result.Result);
        owner.Close();
    }
}
