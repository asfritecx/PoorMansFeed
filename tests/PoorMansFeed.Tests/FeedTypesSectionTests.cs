using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The Settings page's Write feed section in the real main window, driven headlessly through the fakes.</summary>
public sealed class FeedTypesSectionTests
{
    private const string DefaultFolder = "/exports/default";

    private static readonly DateTimeOffset Now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private static readonly DatasetInfo SampleDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true);

    private static readonly string[] ModSecurityInputs =
        ["ModSecurityLocationBox", "ModSecurityLogOnlyButton", "ModSecurityDenyButton", "ModSecurityCloseButton",
         "ModSecurityFirstRuleIdBox"];

    // ----- helpers -----

    private sealed class Shown
    {
        public required MainWindow Window { get; init; }
        public required SettingsPageViewModel Page { get; init; }
        public required FakeIpInfoService Service { get; init; }
        public required FakeUi Ui { get; init; }

        public T Find<T>(string name) where T : Control => Window.FindControl<T>(name)!;

        public FeedTypesSection Section => Find<FeedTypesSection>("FeedTypesSection");

        public T InSection<T>(string name) where T : Control => Section.FindControl<T>(name)!;
    }

    private static FakeIpInfoService WithDataset(bool modSecurity = false) => new()
    {
        Dataset = SampleDataset,
        HasToken = true,
        StartupResult = new StartupResult(StartupFailure.None, SampleDataset, true),
        Settings = new UserSettings { WriteFortigateFeeds = true, WriteModSecurityLists = modSecurity },
    };

    // The window as the app composes it, started, with the Settings page on show
    private static async Task<Shown> ShowAsync(FakeIpInfoService? service = null, FakeUi? ui = null, TimeProvider? time = null)
    {
        service ??= WithDataset();
        ui ??= new FakeUi();
        var gate = new WorkGate();
        var main = new MainViewModel(service, ui, DefaultFolder, null, gate, null)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        var page = new SettingsPageViewModel(service, ui, DefaultFolder, gate, main.ResetTokenAsync, null, null, time);
        var window = new MainWindow();
        window.AttachViewModel(main, settings: page);
        await main.InitializeAsync();
        if (main.PendingCheck is { } check)
        {
            await check;
        }

        window.Width = 1280;
        window.Height = 880;
        window.Show();
        var shown = new Shown { Window = window, Page = page, Service = service, Ui = ui };
        Navigate(shown, "NavSettings");
        return shown;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Navigate(Shown shown, string item)
    {
        shown.Find<ListBox>("NavList").SelectedItem = shown.Find<ListBoxItem>(item);
        Settle(shown.Window);
    }

    private static void PressEnter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static string SasLink(DateTimeOffset expiry) =>
        "https://acct.blob.core.windows.net/feeds?sv=2026-01-01&sr=c&sp=rl&se="
        + expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        + "&sig=SAMPLEONLY%2Bnot%3Dreal";

    // ----- the place and the inputs -----

    [AvaloniaFact]
    public async Task TheSectionComesFirstAndTheAsnExportBlockSecond()
    {
        Shown shown = await ShowAsync();
        try
        {
            StackPanel inputs = shown.Find<StackPanel>("SettingsInputs");
            Assert.Same(shown.Section, inputs.Children[0]);

            Control block = shown.Find<CheckBox>("FortigateEnabledBox");
            while (!ReferenceEquals(block.Parent, inputs))
            {
                block = (Control)block.Parent!;
            }

            Assert.Same(inputs.Children[1], block);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheAsnExportBlockKeepsItsControlsUnderItsNewTexts()
    {
        Shown shown = await ShowAsync();
        try
        {
            CheckBox box = shown.Find<CheckBox>("FortigateEnabledBox");
            Assert.Equal(AppText.AsnExportWritesFeeds, box.Content);
            var block = (StackPanel)box.Parent!;
            Assert.Equal(AppText.AsnExportHeader, Assert.IsType<TextBlock>(block.Children[0]).Text);
            foreach (string name in new[] { "FortigateIPv4Box", "FortigateIPv6Box", "FortigateSplitBox" })
            {
                Assert.True(shown.Find<CheckBox>(name).GetVisualAncestors().Contains(block), name);
            }
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheModSecurityInputsAreOffUntilModSecurityIsTicked()
    {
        Shown shown = await ShowAsync();
        try
        {
            Assert.False(shown.Page.Settings.WriteModSecurityLists);
            Assert.All(ModSecurityInputs, name => Assert.False(shown.InSection<Control>(name).IsEffectivelyEnabled, name));
            Assert.True(shown.InSection<TextBlock>("ModSecurityLocationWarning").IsEffectivelyEnabled);

            shown.InSection<CheckBox>("WriteModSecurityListsBox").IsChecked = true;
            Settle(shown.Window);

            Assert.True(shown.Page.Settings.WriteModSecurityLists);
            Assert.All(ModSecurityInputs, name => Assert.True(shown.InSection<Control>(name).IsEffectivelyEnabled, name));

            TextBox location = shown.InSection<TextBox>("ModSecurityLocationBox");
            location.Focus();
            shown.Window.KeyTextInput("/srv/lists");
            NumericUpDown firstId = shown.InSection<NumericUpDown>("ModSecurityFirstRuleIdBox");
            firstId.Value = 20_000;
            RadioButton deny = shown.InSection<RadioButton>("ModSecurityDenyButton");
            deny.IsChecked = true;
            shown.InSection<CheckBox>("WriteModSecurityListsBox").IsChecked = false;
            Settle(shown.Window);

            Assert.All(ModSecurityInputs, name => Assert.False(shown.InSection<Control>(name).IsEffectivelyEnabled, name));
            Assert.Equal("/srv/lists", location.Text);
            Assert.Equal(20_000m, firstId.Value);
            Assert.True(deny.IsChecked);
            Assert.Equal("/srv/lists", shown.Page.Settings.ModSecurityLocation);
            Assert.True(shown.Page.Settings.IsDeny);
        }
        finally
        {
            shown.Page.RevertSettingsCommand.Execute(null);
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheBoxesAreLimitedToTheModSecurityLimits()
    {
        Shown shown = await ShowAsync(WithDataset(modSecurity: true));
        try
        {
            NumericUpDown firstId = shown.InSection<NumericUpDown>("ModSecurityFirstRuleIdBox");
            Assert.Equal(ModSecurityLimits.RuleIdMin, firstId.Minimum);
            Assert.Equal(ModSecurityLimits.RuleIdMax, firstId.Maximum);
            Assert.Equal(2 * ModSecurityLimits.MaxLocationLength, shown.InSection<TextBox>("ModSecurityLocationBox").MaxLength);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // ----- the location note -----

    [AvaloniaFact]
    public async Task TheLocationNoteKeepsItsHeightAndShowsItsTone()
    {
        Shown shown = await ShowAsync(WithDataset(modSecurity: true), time: new FakeTimeProvider(Now));
        try
        {
            TextBlock note = shown.InSection<TextBlock>("ModSecurityLocationNote");
            ScrollViewer scroll = shown.Find<ScrollViewer>("SettingsScroll");
            double height = note.Bounds.Height;
            double extent = scroll.Extent.Height;
            Assert.True(height > 0);

            (string Location, bool Critical, bool Caution, bool Empty)[] states =
            [
                ("", false, false, true),
                ("http://h.example/lists", true, false, false),
                (SasLink(Now.AddDays(30)), false, false, false),
                (SasLink(Now.AddDays(7)), false, true, false),
                (SasLink(Now.AddDays(-1)), true, false, false),
            ];
            foreach ((string location, bool critical, bool caution, bool empty) in states)
            {
                shown.Page.Settings.ModSecurityLocation = location;
                Settle(shown.Window);

                Assert.Equal(height, note.Bounds.Height);
                Assert.Equal(extent, scroll.Extent.Height);
                Assert.Equal(critical, note.Classes.Contains("critical"));
                Assert.Equal(caution, note.Classes.Contains("caution"));
                Assert.Equal(empty, note.Classes.Contains("placeholder"));
                Assert.Equal(empty, string.IsNullOrEmpty(note.Text));
            }

            // Unticked, a passed expiry is still named but shown without a tone
            shown.InSection<CheckBox>("WriteModSecurityListsBox").IsChecked = false;
            Settle(shown.Window);
            Assert.DoesNotContain("critical", note.Classes);
            Assert.DoesNotContain("caution", note.Classes);
            Assert.DoesNotContain("placeholder", note.Classes);
            Assert.Equal(height, note.Bounds.Height);
            Assert.Equal(extent, scroll.Extent.Height);
        }
        finally
        {
            shown.Page.RevertSettingsCommand.Execute(null);
            shown.Window.Close();
        }
    }

    // ----- keys and typing -----

    [AvaloniaFact]
    public async Task EnterInTheLocationOrFirstIdBoxNeverSaves()
    {
        Shown shown = await ShowAsync(WithDataset(modSecurity: true));
        try
        {
            TextBox location = shown.InSection<TextBox>("ModSecurityLocationBox");
            location.Focus();
            shown.Window.KeyTextInput("/etc/modsecurity/lists");
            Settle(shown.Window);
            Assert.Equal("/etc/modsecurity/lists", shown.Page.Settings.ModSecurityLocation);
            Assert.True(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);

            PressEnter(shown.Window);
            Settle(shown.Window);
            Assert.Equal(0, shown.Service.SaveSettingsCalls);

            TextBox firstId = shown.InSection<NumericUpDown>("ModSecurityFirstRuleIdBox")
                .GetVisualDescendants().OfType<TextBox>().First();
            firstId.Focus();
            Settle(shown.Window);
            Assert.True(firstId.IsFocused);
            PressEnter(shown.Window);
            Settle(shown.Window);
            Assert.Equal(0, shown.Service.SaveSettingsCalls);
            Assert.Equal(AppText.ChangesNotSaved, shown.Page.SettingsStatusText);
        }
        finally
        {
            shown.Page.RevertSettingsCommand.Execute(null);
            shown.Window.Close();
        }
    }

    // A single-line box keeps only the text before the first line break of typed or pasted input
    [AvaloniaFact]
    public async Task TypedTextWithALineBreakKeepsOnlyItsFirstLine()
    {
        Shown shown = await ShowAsync(WithDataset(modSecurity: true));
        try
        {
            TextBox location = shown.InSection<TextBox>("ModSecurityLocationBox");
            location.Focus();
            shown.Window.KeyTextInput("https://h.example/lists\r\nmore");
            Settle(shown.Window);

            Assert.Equal("https://h.example/lists", location.Text);
            Assert.Equal("https://h.example/lists", shown.Page.Settings.ModSecurityLocation);
        }
        finally
        {
            shown.Page.RevertSettingsCommand.Execute(null);
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheActionRadiosChooseOneAction()
    {
        Shown shown = await ShowAsync(WithDataset(modSecurity: true));
        try
        {
            RadioButton logOnly = shown.InSection<RadioButton>("ModSecurityLogOnlyButton");
            RadioButton deny = shown.InSection<RadioButton>("ModSecurityDenyButton");
            Assert.True(logOnly.IsChecked);
            Assert.False(deny.IsChecked);

            deny.IsChecked = true;
            Settle(shown.Window);
            Assert.True(shown.Page.Settings.IsDeny);
            Assert.False(shown.Page.Settings.IsLogOnly);
            Assert.False(logOnly.IsChecked);

            logOnly.IsChecked = true;
            Settle(shown.Window);
            Assert.True(shown.Page.Settings.IsLogOnly);
            Assert.False(shown.Page.Settings.IsDeny);
            Assert.False(deny.IsChecked);

            RadioButton close = shown.InSection<RadioButton>("ModSecurityCloseButton");
            Assert.Equal(AppText.ModSecurityClose, close.Content);
            close.IsChecked = true;
            Settle(shown.Window);
            Assert.True(shown.Page.Settings.IsClose);
            Assert.False(shown.Page.Settings.IsLogOnly);
            Assert.False(logOnly.IsChecked);
            Assert.False(deny.IsChecked);
            Assert.True(shown.Page.Settings.HasChanges);

            deny.IsChecked = true;
            Settle(shown.Window);
            Assert.False(shown.Page.Settings.IsClose);
            Assert.False(close.IsChecked);
        }
        finally
        {
            shown.Page.RevertSettingsCommand.Execute(null);
            shown.Window.Close();
        }
    }

    // ----- unsaved edits -----

    [AvaloniaFact]
    public async Task TickingModSecurityIsAnUnsavedEditTheCloseAsksAbout()
    {
        var ui = new FakeUi { ConfirmResult = false };
        Shown shown = await ShowAsync(ui: ui);
        AssertEditIsUnsaved(shown, () =>
        {
            shown.InSection<CheckBox>("WriteModSecurityListsBox").IsChecked = true;
            Settle(shown.Window);
        });
    }

    [AvaloniaFact]
    public async Task TypingALocationIsAnUnsavedEditTheCloseAsksAbout()
    {
        var ui = new FakeUi { ConfirmResult = false };
        Shown shown = await ShowAsync(WithDataset(modSecurity: true), ui);
        AssertEditIsUnsaved(shown, () =>
        {
            shown.InSection<TextBox>("ModSecurityLocationBox").Focus();
            shown.Window.KeyTextInput("/etc/modsecurity/lists");
            Settle(shown.Window);
        });
    }

    private static void AssertEditIsUnsaved(Shown shown, Action edit)
    {
        bool closed = false;
        shown.Window.Closed += (_, _) => closed = true;
        try
        {
            TextBlock mark = shown.Find<TextBlock>("NavSettingsMark");
            Assert.Contains("placeholder", mark.Classes);

            edit();

            Assert.True(shown.Page.HasUnsavedChanges);
            Assert.DoesNotContain("placeholder", mark.Classes);

            shown.Window.Close();
            Settle(shown.Window);
            Assert.False(closed);
            (string title, string body, _) = Assert.Single(shown.Ui.Confirms);
            Assert.Equal(AppText.CloseUnsavedTitle, title);
            Assert.Equal(AppText.CloseUnsavedBody, body);
            Assert.Equal(0, shown.Service.SaveSettingsCalls);
        }
        finally
        {
            if (!closed)
            {
                shown.Page.RevertSettingsCommand.Execute(null);
                shown.Window.Close();
            }
        }
    }
}
