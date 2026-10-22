using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>
/// Behaviour of the token, large-range and new addresses dialogs in a headless window: what each control shows, what Save,
/// Copy and Escape do, and that Enter alone never saves, exports or closes. Each dialog runs modally under a shown owner, as in production.
/// </summary>
public sealed class DialogTests
{
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

    private static CheckBox CheckBoxForAccessibleName(Window window, string name) =>
        window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => box.GetValue(AutomationProperties.NameProperty) == name);

    private static LargeRangePrompt SamplePrompt() => new(
        "Export AS15169?",
        "3 networks are larger than the limits in Settings (IPv4 /16, IPv6 /32).",
        [
            new LargeRangeChoice("10.0.0.0/8", "16,777,216 addresses"),
            new LargeRangeChoice("172.16.0.0/12", "1,048,576 addresses"),
            new LargeRangeChoice("2001:db8::/32", "79,228,162,514,264,337,593,543,950,336 addresses"),
        ]);

    // A row as the view model makes it, its pages built from a judged address. PublicAddress refuses the
    // documentation ranges, so the rows use public addresses
    private static NewAddressRow AddressRow(string text, string score, string reported)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? parsed));
        Assert.True(PublicAddress.TryCreate(parsed.GetAddressBytes(), out PublicAddress? address));
        return new NewAddressRow(address.Text, score, reported, WebPage.IpInfoAddress(address), WebPage.AbuseIpDbCheck(address));
    }

    private static Task NoPage(WebPage page) => Task.CompletedTask;

    private static NewAddressesPrompt SampleNewAddresses() => new(
        "New addresses",
        "These addresses weren't in the list before the last download.",
        "3 new addresses",
        [
            AddressRow("8.8.8.8", "100", "2026-10-19"),
            AddressRow("9.9.9.9", "45", "Unknown"),
            AddressRow("2001:4860::8888", "12", "2026-10-18"),
        ],
        "8.8.8.8\n9.9.9.9\n2001:4860::8888",
        "Copied 3 addresses");

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

    // ----- AbuseIPDB key dialog: the token dialog with the key's texts -----

    // A made-up key: the dialog must return it exactly, and nothing else may hold it
    private const string SentinelKey = "abuseipdb-sentinel-key-0123456789";

    [AvaloniaTheory]
    [InlineData(AbuseKeyAsk.Missing, "Enter AbuseIPDB key",
        "Check AbuseIPDB sends the searched address to AbuseIPDB with your API key. "
        + "The key is in your AbuseIPDB account under API. It's saved on this computer for your account.")]
    [InlineData(AbuseKeyAsk.Rejected, "Enter a new AbuseIPDB key",
        "AbuseIPDB didn't accept the saved key. "
        + "The key is in your AbuseIPDB account under API. It's saved on this computer for your account.")]
    [InlineData(AbuseKeyAsk.Replace, "Enter AbuseIPDB key",
        "The new key replaces any saved one. Nothing is sent to AbuseIPDB when you save it. "
        + "The key is in your AbuseIPDB account under API. It's saved on this computer for your account.")]
    public void AbuseKeyDialogShowsTheKeyTexts(AbuseKeyAsk why, string title, string body)
    {
        var dialog = new TokenDialog(SecretPrompt.ForAbuseIpDb(why));
        var box = dialog.FindControl<TextBox>("TokenBox")!;

        Assert.Equal(title, dialog.Title);
        Assert.Equal(title, dialog.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(body, dialog.FindControl<TextBlock>("BodyText")!.Text);
        Assert.Equal("AbuseIPDB key", box.PlaceholderText);
        Assert.Equal("AbuseIPDB key", AutomationProperties.GetName(box));
        Assert.Equal("Show key", dialog.FindControl<CheckBox>("ShowToken")!.Content);
        Assert.Equal("Save key", dialog.FindControl<Button>("SaveButton")!.Content);
        Assert.Equal(AppText.Cancel, dialog.FindControl<Button>("CancelButton")!.Content);
    }

    [AvaloniaTheory]
    [InlineData(AbuseKeyAsk.Missing)]
    [InlineData(AbuseKeyAsk.Rejected)]
    [InlineData(AbuseKeyAsk.Replace)]
    public void EnterSavesTheTrimmedAbuseKey(AbuseKeyAsk why)
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(SecretPrompt.ForAbuseIpDb(why));
        Task<string?> result = dialog.ShowDialog<string?>(owner);
        var save = dialog.FindControl<Button>("SaveButton")!;

        Assert.False(save.IsEnabled);
        dialog.FindControl<TextBox>("TokenBox")!.Focus();
        dialog.KeyTextInput("   ");
        Assert.False(save.IsEnabled);
        dialog.KeyTextInput(SentinelKey + "  ");
        Assert.True(save.IsEnabled);
        PressEnter(dialog);

        Assert.True(result.IsCompleted);
        Assert.Equal(SentinelKey, result.Result);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(AbuseKeyAsk.Missing)]
    [InlineData(AbuseKeyAsk.Rejected)]
    [InlineData(AbuseKeyAsk.Replace)]
    public void EscapeReturnsNullFromTheAbuseKeyDialog(AbuseKeyAsk why)
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(SecretPrompt.ForAbuseIpDb(why));
        Task<string?> result = dialog.ShowDialog<string?>(owner);

        dialog.FindControl<TextBox>("TokenBox")!.Focus();
        dialog.KeyTextInput(SentinelKey);
        PressEscape(dialog);

        Assert.True(result.IsCompleted);
        Assert.Null(result.Result);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(AbuseKeyAsk.Missing)]
    [InlineData(AbuseKeyAsk.Rejected)]
    [InlineData(AbuseKeyAsk.Replace)]
    public void TheAbuseKeyIsMaskedUntilShowKeyIsTicked(AbuseKeyAsk why)
    {
        Window owner = ShownOwner();
        var dialog = new TokenDialog(SecretPrompt.ForAbuseIpDb(why));
        Task<string?> result = dialog.ShowDialog<string?>(owner);
        var box = dialog.FindControl<TextBox>("TokenBox")!;
        var show = dialog.FindControl<CheckBox>("ShowToken")!;

        Assert.Equal('\u2022', box.PasswordChar);
        Assert.False(box.RevealPassword);
        show.IsChecked = true;
        Assert.True(box.RevealPassword);
        show.IsChecked = false;
        Assert.False(box.RevealPassword);

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

    // ----- new addresses dialog -----

    [AvaloniaFact]
    public void TheNewAddressesDialogShowsThePromptsTexts()
    {
        Window owner = ShownOwner();
        NewAddressesPrompt prompt = SampleNewAddresses();
        var dialog = new NewAddressesDialog(prompt, _ => Task.CompletedTask, NoPage);
        Task result = dialog.ShowDialog(owner);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(prompt.Title, dialog.Title);
        Assert.Equal(prompt.Title, dialog.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(prompt.Message, dialog.FindControl<TextBlock>("MessageText")!.Text);
        TextBlock count = dialog.FindControl<TextBlock>("CountText")!;
        Assert.Equal(prompt.CountText, count.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(count));
        Assert.Same(prompt.Rows, dialog.FindControl<ItemsControl>("AddressList")!.ItemsSource);
        Assert.Equal(AppText.NewAddressesCopy, dialog.FindControl<Button>("CopyButton")!.Content);
        Assert.Equal(AppText.Close, dialog.FindControl<Button>("CloseButton")!.Content);

        // Each row shows its address in mono, its score and its last report
        List<string?> shown = dialog.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.All(prompt.Rows, row =>
        {
            Assert.Contains(row.Address, shown);
            Assert.Contains(row.Score, shown);
            Assert.Contains(row.LastReported, shown);
        });
        TextBlock address = dialog.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "9.9.9.9");
        Assert.Contains("mono", address.Classes);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void ANewAddressRowLeavesTheAddressTheWidthAndRightAlignsTheScore()
    {
        Window owner = ShownOwner();
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ => Task.CompletedTask, NoPage);
        Task result = dialog.ShowDialog(owner);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Stub text metrics can't show trimming, so the structure is pinned and the real widths are measured apart
        TextBlock address = dialog.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "2001:4860::8888");
        Grid row = address.GetVisualAncestors().OfType<Grid>().First();
        Assert.Equal(3, row.ColumnDefinitions.Count);
        Assert.Equal(new GridLength(1, GridUnitType.Star), row.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(32), row.ColumnDefinitions[1].Width);
        Assert.Equal(new GridLength(80), row.ColumnDefinitions[2].Width);
        Assert.Equal(12, row.ColumnSpacing);

        TextBlock score = row.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 1);
        Assert.Equal("12", score.Text);
        Assert.Equal(Avalonia.Media.TextAlignment.Right, score.TextAlignment);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, address.TextTrimming);
        Assert.Equal("2001:4860::8888", ToolTip.GetTip(address));

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TheNewAddressesListIsVirtualisedInACardThatKeeps216()
    {
        Window owner = ShownOwner();
        NewAddressRow[] rows = Enumerable.Range(1, 300)
            .Select(i => AddressRow($"8.8.{i / 256}.{i % 256}", "50", "2026-10-19")).ToArray();
        NewAddressesPrompt prompt = SampleNewAddresses() with { Rows = rows, CountText = "Showing 300 of 1,000 new addresses" };
        var dialog = new NewAddressesDialog(prompt, _ => Task.CompletedTask, NoPage);
        Task result = dialog.ShowDialog(owner);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        ItemsControl list = dialog.FindControl<ItemsControl>("AddressList")!;
        Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
        Assert.InRange(list.ItemsPanelRoot!.Children.Count, 1, 20);
        Assert.Equal(216, dialog.FindControl<Border>("AddressCard")!.Bounds.Height);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeClosesTheNewAddressesDialogAndEnterDoesNothing()
    {
        Window owner = ShownOwner();
        int copies = 0;
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ =>
        {
            copies++;
            return Task.CompletedTask;
        }, NoPage);
        Task result = dialog.ShowDialog(owner);

        // No button is the default, so Enter on the dialog neither copies nor closes
        Assert.False(dialog.FindControl<Button>("CopyButton")!.IsDefault);
        Assert.False(dialog.FindControl<Button>("CloseButton")!.IsDefault);
        Assert.True(dialog.FindControl<Button>("CloseButton")!.IsCancel);
        PressEnter(dialog);
        Dispatcher.UIThread.RunJobs();
        Assert.False(result.IsCompleted);
        Assert.Equal(0, copies);

        PressEscape(dialog);
        Assert.True(result.IsCompleted);
        owner.Close();
    }

    [AvaloniaFact]
    public void CloseClosesTheNewAddressesDialog()
    {
        Window owner = ShownOwner();
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ => Task.CompletedTask, NoPage);
        Task result = dialog.ShowDialog(owner);

        Click(dialog.FindControl<Button>("CloseButton")!);

        Assert.True(result.IsCompleted);
        owner.Close();
    }

    [AvaloniaFact]
    public void CopyCopiesEveryAddressShowsTheCopiedLineAndKeepsTheDialogOpen()
    {
        Window owner = ShownOwner();
        NewAddressesPrompt prompt = SampleNewAddresses();
        var copied = new List<string>();
        var dialog = new NewAddressesDialog(prompt, text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        }, NoPage);
        Task result = dialog.ShowDialog(owner);
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;

        Click(dialog.FindControl<Button>("CopyButton")!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([prompt.CopyText], copied);
        Assert.Equal(prompt.CopiedText, status.Text);
        Assert.DoesNotContain("placeholder", status.Classes);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.False(result.IsCompleted);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void CopyRunsOneCopyAtATime()
    {
        Window owner = ShownOwner();
        int copies = 0;
        var held = new TaskCompletionSource();
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ =>
        {
            copies++;
            return held.Task;
        }, NoPage);
        Task result = dialog.ShowDialog(owner);
        Button copy = dialog.FindControl<Button>("CopyButton")!;

        // A second press while the first copy is out starts nothing
        Click(copy);
        Click(copy);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, copies);

        held.SetResult();
        Dispatcher.UIThread.RunJobs();
        Click(copy);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, copies);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void ACopyThatThrowsShowsTheCopyFailedLine()
    {
        Window owner = ShownOwner();
        var dialog = new NewAddressesDialog(SampleNewAddresses(),
            _ => Task.FromException(new InvalidOperationException("clipboard unavailable")), NoPage);
        Task result = dialog.ShowDialog(owner);
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;

        Click(dialog.FindControl<Button>("CopyButton")!);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppText.AbuseNewAddressesCopyFailed, status.Text);
        Assert.DoesNotContain("placeholder", status.Classes);
        Assert.False(result.IsCompleted);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void TheCopyStatusLineKeepsItsHeightWhileEmpty()
    {
        Window owner = ShownOwner();
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ => Task.CompletedTask, NoPage);
        Task result = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;
        var content = (Control)dialog.Content!;
        double emptyHeight = content.Bounds.Height;

        Assert.True(string.IsNullOrEmpty(status.Text));
        Assert.Contains("placeholder", status.Classes);
        Assert.True(status.IsVisible);
        Assert.Equal(18, status.Bounds.Height);

        Click(dialog.FindControl<Button>("CopyButton")!);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.Equal(18, status.Bounds.Height);
        Assert.Equal(emptyHeight, content.Bounds.Height);

        PressEscape(dialog);
        owner.Close();
    }

    // ----- the new addresses menu -----

    // A right-click in the gap between a row's address and score columns, where no text is: the row's own
    // background is all that can take it there
    private static (ContextMenu Menu, MenuItem Ipinfo, MenuItem AbuseIpDb) RightClickRowGap(NewAddressesDialog dialog, int index)
    {
        ItemsControl list = dialog.FindControl<ItemsControl>("AddressList")!;
        Border row = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("row"))
            .ElementAt(index);
        TextBlock address = row.GetVisualDescendants().OfType<TextBlock>().First();
        Point gap = address.TranslatePoint(new Point(address.Bounds.Width + 6, address.Bounds.Height / 2), dialog)!.Value;
        Assert.Same(row, dialog.InputHitTest(gap));
        // Headless drawing hit-tests a border's whole rectangle, with or without a fill, so the fill that makes the
        // gap hit-testable on a real renderer is pinned here
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);

        dialog.MouseDown(gap, MouseButton.Right);
        dialog.MouseUp(gap, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        ContextMenu menu = list.ContextMenu!;
        Assert.True(menu.IsOpen);
        List<MenuItem> items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal([AppText.OpenInIpinfo, AppText.OpenInAbuseIpDb], items.Select(item => item.Header as string));
        return (menu, items[0], items[1]);
    }

    [AvaloniaFact]
    public void ARightClickOnANewAddressRowOpensItsMenuAndEachItemOpensThatRowsPage()
    {
        Window owner = ShownOwner();
        NewAddressesPrompt prompt = SampleNewAddresses();
        var opened = new List<string>();
        var dialog = new NewAddressesDialog(prompt, _ => Task.CompletedTask, page =>
        {
            opened.Add(page.Uri.AbsoluteUri);
            return Task.CompletedTask;
        });
        Task result = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // The second row, so the first row's pages can't pass by accident
        (ContextMenu menu, MenuItem ipinfo, MenuItem abuse) = RightClickRowGap(dialog, 1);
        Assert.True(ipinfo.IsEffectivelyEnabled);
        Assert.True(abuse.IsEffectivelyEnabled);
        ipinfo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["https://ipinfo.io/9.9.9.9"], opened);

        abuse.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["https://ipinfo.io/9.9.9.9", "https://www.abuseipdb.com/check/9.9.9.9"], opened);

        // A success shows nothing: a launched browser proves nothing
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;
        Assert.True(string.IsNullOrEmpty(status.Text));
        Assert.Contains("placeholder", status.Classes);
        Assert.False(result.IsCompleted);

        menu.Close();
        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void AMenuRequestThatIsntOnARowOpensNothing()
    {
        Window owner = ShownOwner();
        int opens = 0;
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ => Task.CompletedTask, _ =>
        {
            opens++;
            return Task.CompletedTask;
        });
        Task result = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        ItemsControl list = dialog.FindControl<ItemsControl>("AddressList")!;

        // A row's menu first, so a row recorded earlier can't carry over to a request from no row
        (ContextMenu menu, _, _) = RightClickRowGap(dialog, 0);
        menu.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(menu.IsOpen);

        list.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();

        Assert.False(menu.IsOpen);
        Assert.Equal(0, opens);

        PressEscape(dialog);
        owner.Close();
    }

    [AvaloniaFact]
    public void APageThatWontOpenShowsItsLine()
    {
        Window owner = ShownOwner();
        var dialog = new NewAddressesDialog(SampleNewAddresses(), _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("no browser")));
        Task result = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;

        (ContextMenu menu, MenuItem ipinfo, _) = RightClickRowGap(dialog, 0);
        ipinfo.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AppText.WebPageOpenFailed, status.Text);
        Assert.DoesNotContain("placeholder", status.Classes);
        Assert.False(result.IsCompleted);

        menu.Close();
        PressEscape(dialog);
        owner.Close();
    }
}
