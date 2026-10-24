using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The ModSecurity rules dialog in a headless window, modal under a shown owner, bound to its model.</summary>
public sealed class ModSecurityRulesDialogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
    private const string Folder = "/etc/modsecurity/lists";

    private static Window ShownOwner()
    {
        var owner = new Window();
        owner.Show();
        return owner;
    }

    private static ModSecurityRulesPrompt Prompt(string location = Folder) =>
        new(["AS15169_ipv4.txt", "AS15169_ipv6.txt"], [10000, 10001], "AS15169", location, ModSecurityAction.LogOnly);

    private static ModSecurityRulesModel Model(ModSecurityRulesPrompt? prompt = null, Func<string, Task>? copy = null) =>
        new(prompt ?? Prompt(), new FakeTimeProvider(Now), copy ?? (_ => Task.CompletedTask));

    private static string Sas(string expiry) =>
        "https://example.blob.core.windows.net/lists?sp=r&sr=c&se=" + expiry + "&sig=x";

    private static void PressKey(Window window, Key key, PhysicalKey physical) =>
        window.KeyPress(key, RawInputModifiers.None, physical, null);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void EscapeClosesAndEnterDoesNothing()
    {
        Window owner = ShownOwner();
        int copies = 0;
        var dialog = new ModSecurityRulesDialog(Model(copy: _ =>
        {
            copies++;
            return Task.CompletedTask;
        }));
        Task result = dialog.ShowDialog(owner);

        Assert.False(dialog.FindControl<Button>("CopyButton")!.IsDefault);
        Assert.False(dialog.FindControl<Button>("CloseButton")!.IsDefault);
        Assert.True(dialog.FindControl<Button>("CloseButton")!.IsCancel);
        PressKey(dialog, Key.Enter, PhysicalKey.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.False(result.IsCompleted);
        Assert.Equal(0, copies);

        PressKey(dialog, Key.Escape, PhysicalKey.Escape);
        Assert.True(result.IsCompleted);
        owner.Close();
    }

    [AvaloniaFact]
    public void TheLocationBoxHasFocusWhenTheDialogOpens()
    {
        Window owner = ShownOwner();
        var dialog = new ModSecurityRulesDialog(Model());
        dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialog.FindControl<TextBox>("LocationBox")!.IsFocused);
        Assert.Equal(AppText.ModSecurityRulesTitle, dialog.Title);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void ThePreviewIsReadOnlyAndBoundOneWay()
    {
        Window owner = ShownOwner();
        ModSecurityRulesModel model = Model();
        var dialog = new ModSecurityRulesDialog(model);
        dialog.ShowDialog(owner);
        TextBox preview = dialog.FindControl<TextBox>("PreviewBox")!;

        Assert.True(preview.IsReadOnly);
        Assert.True(preview.AcceptsReturn);
        Assert.Equal(Avalonia.Media.TextWrapping.NoWrap, preview.TextWrapping);
        Assert.Contains("mono", preview.Classes);
        Assert.Equal(112, preview.Height);
        Assert.Equal(AppText.ModSecurityRulesPreviewName, AutomationProperties.GetName(preview));
        Assert.Equal(model.PreviewText, preview.Text);

        string rules = model.PreviewText;
        preview.Text = "changed";
        Assert.Equal(rules, model.PreviewText);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void AProblemLineWrapsInThePreviewsPlaceAtTheSameHeight()
    {
        Window owner = ShownOwner();
        ModSecurityRulesModel model = Model();
        var dialog = new ModSecurityRulesDialog(model);
        dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        Panel area = dialog.FindControl<Panel>("PreviewArea")!;
        Border problem = dialog.FindControl<Border>("ProblemBox")!;
        TextBlock problemText = dialog.FindControl<TextBlock>("ProblemText")!;
        Assert.True(dialog.FindControl<TextBox>("PreviewBox")!.IsVisible);
        Assert.False(problem.IsVisible);
        Assert.Equal(112, area.Bounds.Height);

        model.Location = Sas("2026-12-31").Replace("sp=r", "sp=rw", StringComparison.Ordinal);
        Dispatcher.UIThread.RunJobs();

        Assert.False(dialog.FindControl<TextBox>("PreviewBox")!.IsVisible);
        Assert.True(problem.IsVisible);
        Assert.Contains("inset", problem.Classes);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, problemText.TextWrapping);
        Assert.Equal(model.PreviewText, problemText.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(problemText));
        Assert.Equal(112, area.Bounds.Height);

        // The longest problem line, a location that isn't one, scrolls to its end with the keys
        model.Location = "etc/lists";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AppText.ModSecurityLocationInvalid, problemText.Text);
        ScrollViewer scroll = dialog.FindControl<ScrollViewer>("ProblemScroll")!;
        Assert.True(scroll.Focusable);
        Assert.Equal(model.PreviewText, AutomationProperties.GetName(scroll));
        scroll.Focus();
        PressKey(dialog, Key.End, PhysicalKey.End);
        Assert.True(scroll.Offset.Y > 0);
        PressKey(dialog, Key.Home, PhysicalKey.Home);
        Assert.Equal(0, scroll.Offset.Y);
        Assert.Equal(112, area.Bounds.Height);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void EscapeClosesWhileABoxHoldsASelection()
    {
        foreach (string box in new[] { "LocationBox", "PreviewBox" })
        {
            Window owner = ShownOwner();
            var dialog = new ModSecurityRulesDialog(Model());
            Task result = dialog.ShowDialog(owner);
            Dispatcher.UIThread.RunJobs();
            TextBox text = dialog.FindControl<TextBox>(box)!;
            text.Focus();
            text.SelectAll();
            Assert.True(text.SelectionEnd > text.SelectionStart);

            PressKey(dialog, Key.Escape, PhysicalKey.Escape);
            Assert.True(result.IsCompleted, box);
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void TypingAndTheRadiosChangeThePreview()
    {
        Window owner = ShownOwner();
        ModSecurityRulesModel model = Model();
        var dialog = new ModSecurityRulesDialog(model);
        dialog.ShowDialog(owner);
        TextBox preview = dialog.FindControl<TextBox>("PreviewBox")!;

        dialog.FindControl<TextBox>("LocationBox")!.Text = "";
        Assert.Equal(AppText.ModSecurityRulesEnterLocation, dialog.FindControl<TextBlock>("ProblemText")!.Text);
        Assert.False(preview.IsVisible);
        Assert.False(dialog.FindControl<Button>("CopyButton")!.IsEffectivelyEnabled);

        dialog.FindControl<TextBox>("LocationBox")!.Text = "/srv/lists";
        Assert.True(preview.IsVisible);
        Assert.Contains("@ipMatchFromFile /srv/lists/AS15169_ipv4.txt", preview.Text);
        Assert.True(dialog.FindControl<RadioButton>("LogOnlyRadio")!.IsChecked);

        dialog.FindControl<RadioButton>("DenyRadio")!.IsChecked = true;
        Assert.True(model.IsDeny);
        Assert.False(dialog.FindControl<RadioButton>("LogOnlyRadio")!.IsChecked);
        Assert.Contains("deny,status:403", preview.Text);

        RadioButton close = dialog.FindControl<RadioButton>("CloseRadio")!;
        Assert.Equal(AppText.ModSecurityClose, close.Content);
        Assert.Equal("ModSecurityRuleAction", close.GroupName);
        close.IsChecked = true;
        Assert.True(model.IsClose);
        Assert.False(dialog.FindControl<RadioButton>("DenyRadio")!.IsChecked);
        Assert.Contains("drop,status:444", preview.Text);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void CopyCopiesTheRulesAndShowsItsLine()
    {
        Window owner = ShownOwner();
        var copied = new List<string>();
        ModSecurityRulesModel model = Model(copy: text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        });
        var dialog = new ModSecurityRulesDialog(model);
        Task result = dialog.ShowDialog(owner);
        Button copy = dialog.FindControl<Button>("CopyButton")!;
        TextBlock status = dialog.FindControl<TextBlock>("CopyStatusText")!;
        Assert.Contains("placeholder", status.Classes);

        // A raised Click doesn't run a bound command, so the button's own command is run
        Assert.Same(model.CopyCommand, copy.Command);
        copy.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([model.PreviewText], copied);
        Assert.Equal(AppText.ModSecurityRulesCopied(2), status.Text);
        Assert.DoesNotContain("placeholder", status.Classes);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.False(result.IsCompleted);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void TheExpirySlotTakesItsTone()
    {
        Window owner = ShownOwner();
        ModSecurityRulesModel model = Model();
        var dialog = new ModSecurityRulesDialog(model);
        dialog.ShowDialog(owner);
        TextBlock expiry = dialog.FindControl<TextBlock>("ExpiryText")!;

        Assert.Equal(54, expiry.Height);
        Assert.Contains("placeholder", expiry.Classes);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(expiry));

        model.Location = Sas("2026-10-01");
        Assert.Contains("critical", expiry.Classes);
        Assert.DoesNotContain("caution", expiry.Classes);
        Assert.DoesNotContain("placeholder", expiry.Classes);
        Assert.Equal(model.ExpiryText, expiry.Text);

        model.Location = Sas("2026-10-30");
        Assert.Contains("caution", expiry.Classes);
        Assert.DoesNotContain("critical", expiry.Classes);

        model.Location = Sas("2026-12-31");
        Assert.DoesNotContain("caution", expiry.Classes);
        Assert.DoesNotContain("critical", expiry.Classes);
        Assert.DoesNotContain("placeholder", expiry.Classes);

        model.Location = Folder;
        Assert.Contains("placeholder", expiry.Classes);
        Assert.Equal(54, expiry.Bounds.Height);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void TheNotesScrollWithTheirOwnKeys()
    {
        Window owner = ShownOwner();
        var dialog = new ModSecurityRulesDialog(Model());
        dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        ScrollViewer notes = dialog.FindControl<ScrollViewer>("NotesScroll")!;

        Assert.True(notes.Focusable);
        Assert.Equal(150, notes.MaxHeight);
        Assert.Equal(AppText.ModSecurityRulesNotesName, AutomationProperties.GetName(notes));
        List<string> texts = notes.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();
        Assert.Equal(AppText.ModSecurityRulesNotes, texts);
        Assert.Equal(AppText.ModSecurityLocationWarning, texts[0]);

        notes.Focus();
        PressKey(dialog, Key.Down, PhysicalKey.ArrowDown);
        Assert.True(notes.Offset.Y > 0);
        PressKey(dialog, Key.End, PhysicalKey.End);
        double end = notes.Offset.Y;
        Assert.True(end > 0);
        PressKey(dialog, Key.Home, PhysicalKey.Home);
        Assert.Equal(0, notes.Offset.Y);

        // The location box keeps its own keys: End there moves the caret, not the notes
        TextBox location = dialog.FindControl<TextBox>("LocationBox")!;
        location.Focus();
        location.CaretIndex = 0;
        PressKey(dialog, Key.End, PhysicalKey.End);
        Assert.Equal(0, notes.Offset.Y);
        Assert.Equal(Folder.Length, location.CaretIndex);
        dialog.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task TheWindowServicesShowTheDialogOnTheirClock()
    {
        Window owner = ShownOwner();
        var time = new FakeTimeProvider(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ui = new AvaloniaUiServices(owner, time);

        Task result = ui.ShowModSecurityRulesAsync(Prompt(Sas("2099-01-01")));
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        ModSecurityRulesDialog dialog = owner.OwnedWindows.OfType<ModSecurityRulesDialog>().Single();

        Assert.Contains("critical", dialog.FindControl<TextBlock>("ExpiryText")!.Classes);
        Assert.False(result.IsCompleted);
        Click(dialog.FindControl<Button>("CloseButton")!);
        await result;
        owner.Close();
    }
}
