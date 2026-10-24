using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The Settings page in the real main window, driven headlessly through the fakes.</summary>
public sealed class SettingsPageTests
{
    private const string DefaultFolder = "/exports/default";

    private static readonly DatasetInfo SampleDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true);

    // ----- helpers -----

    private sealed class Shown
    {
        public required MainWindow Window { get; init; }
        public required MainViewModel Main { get; init; }
        public required SettingsPageViewModel Page { get; init; }
        public required FakeIpInfoService Service { get; init; }
        public required FakeUi Ui { get; init; }

        public T Find<T>(string name) where T : Control => Window.FindControl<T>(name)!;
    }

    private static FakeIpInfoService WithDataset() => new()
    {
        Dataset = SampleDataset,
        HasToken = true,
        StartupResult = new StartupResult(StartupFailure.None, SampleDataset, true),
    };

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
        AbuseIpDbKeepDays = 90,
    };

    // The window as the app composes it: one gate for Lookup and the page, started, with the Settings page on show
    private static async Task<Shown> ShowAsync(FakeIpInfoService? service = null, FakeAbuseIpDbService? abuse = null,
        double width = 1280, double height = 880, bool navigate = true, FakeUi? ui = null,
        Action<FakeIpInfoService>? beforeStart = null)
    {
        service ??= WithDataset();
        ui ??= new FakeUi();
        var gate = new WorkGate();
        AbuseKeyPrompt? keys = abuse is null ? null : new AbuseKeyPrompt(ui, abuse, gate);
        var main = new MainViewModel(service, ui, DefaultFolder, abuse, gate, keys)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        var page = new SettingsPageViewModel(service, ui, DefaultFolder, gate, main.ResetTokenAsync, abuse, keys);
        var window = new MainWindow();
        window.AttachViewModel(main, settings: page);
        beforeStart?.Invoke(service);
        await main.InitializeAsync();
        if (main.PendingCheck is { } check)
        {
            await check;
        }

        window.Width = width;
        window.Height = height;
        window.Show();
        var shown = new Shown { Window = window, Main = main, Page = page, Service = service, Ui = ui };
        if (navigate)
        {
            Navigate(shown, "NavSettings");
        }

        Settle(window);
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

    private static void PressKey(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        Settle(window);
    }

    private static void PressEnter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    private static Rect BoundsIn(Visual visual, Visual relativeTo) =>
        new(visual.TranslatePoint(new Point(0, 0), relativeTo)!.Value, visual.Bounds.Size);

    private static ExceptionList Exceptions(int count) => ExceptionList.Create(
        Enumerable.Range(0, count).Select(i => ExceptionEntryTests.Entry($"192.0.{i}.0/24", "Row " + i)).ToList());

    // ----- the three checks of Avalonia behaviour the page relies on, settled first -----

    // Settled: the DockPanel reads DockPanel.Dock from an inline ListBoxItem. The arrow keys need a panel implementing
    // INavigableContainer (NavItemsPanel): neither DockPanel nor Grid does, and without it Up and Down did nothing.
    [AvaloniaFact]
    public async Task TheSettingsNavItemSitsAtTheFootOfTheNavPane()
    {
        foreach ((double width, double height) in new[] { (1280d, 880d), (960d, 600d) })
        {
            Shown shown = await ShowAsync(width: width, height: height, navigate: false);
            try
            {
                MainWindow window = shown.Window;
                ListBox nav = shown.Find<ListBox>("NavList");
                Rect settings = BoundsIn(shown.Find<ListBoxItem>("NavSettings"), window);
                Rect abuse = BoundsIn(shown.Find<ListBoxItem>("NavAbuseIpDb"), window);
                Rect aws = BoundsIn(shown.Find<ListBoxItem>("NavAws"), window);
                Rect azure = BoundsIn(shown.Find<ListBoxItem>("NavAzure"), window);
                Rect footer = BoundsIn(shown.Find<TextBlock>("FooterNotice"), window);
                Panel panel = nav.GetVisualDescendants().OfType<ItemsPresenter>().Single().Panel!;
                Rect content = BoundsIn(panel, window);

                Assert.True(Math.Abs(settings.Bottom - content.Bottom) <= 1,
                    $"{width}x{height}: Settings ends at {settings.Bottom}, the list's content at {content.Bottom}.");
                Assert.True(settings.Bottom <= footer.Top, $"{width}x{height}: Settings overlaps the attribution.");
                Assert.True(settings.Top > abuse.Bottom + 40, $"{width}x{height}: Settings is not docked at the foot.");
                Assert.Equal(2, aws.Top - azure.Bottom, 1);
                Assert.Equal(2, abuse.Top - aws.Bottom, 1);
            }
            finally
            {
                shown.Window.Close();
            }
        }

        Shown keyed = await ShowAsync(navigate: false);
        try
        {
            ListBox nav = keyed.Find<ListBox>("NavList");
            keyed.Find<ListBoxItem>("NavLookup").Focus();
            PressKey(keyed.Window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Same(keyed.Find<ListBoxItem>("NavGoogleCloud"), nav.SelectedItem);

            Navigate(keyed, "NavAws");
            keyed.Find<ListBoxItem>("NavAws").Focus();
            PressKey(keyed.Window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Same(keyed.Find<ListBoxItem>("NavAbuseIpDb"), nav.SelectedItem);
            Assert.True(keyed.Find<Control>("AbuseIpDbPage").IsVisible);

            PressKey(keyed.Window, Key.Down, PhysicalKey.ArrowDown);
            Assert.Same(keyed.Find<ListBoxItem>("NavSettings"), nav.SelectedItem);
            Assert.True(keyed.Find<Control>("SettingsPage").IsVisible);
            Assert.True(keyed.Find<Control>("SettingsHeading").IsVisible);

            PressKey(keyed.Window, Key.Up, PhysicalKey.ArrowUp);
            Assert.Same(keyed.Find<ListBoxItem>("NavAbuseIpDb"), nav.SelectedItem);
            Assert.True(keyed.Find<Control>("AbuseIpDbPage").IsVisible);
            Assert.True(keyed.Find<Control>("AbuseIpDbHeading").IsVisible);
        }
        finally
        {
            keyed.Window.Close();
        }
    }

    // Settled: with chaining at its default, a wheel over a list that can't scroll moves the page, and over a list
    // that can scroll moves the list first. No fallback was needed.
    [AvaloniaFact]
    public async Task AWheelOverTheExceptionsListAtItsEndScrollsTheSettingsPage()
    {
        foreach (int rows in new[] { 3, 50 })
        {
            FakeIpInfoService service = WithDataset();
            service.Exceptions = Exceptions(rows);
            Shown shown = await ShowAsync(service);
            try
            {
                ScrollViewer page = shown.Find<ScrollViewer>("SettingsScroll");
                ScrollViewer list = shown.Find<ScrollViewer>("ExceptionsScroll");
                Border card = shown.Find<Border>("ExceptionsListCard");
                Assert.True(page.Extent.Height > page.Viewport.Height, "The Settings page does not scroll at 1280x880.");

                // Bring the list's top edge just into view, so the page still has room to scroll
                Point cardInPage = card.TranslatePoint(new Point(0, 0), (Visual)page.Content!)!.Value;
                page.Offset = new Vector(0, Math.Max(0, cardInPage.Y - page.Viewport.Height + 60));
                Settle(shown.Window);
                double pageBefore = page.Offset.Y;
                double listBefore = list.Offset.Y;
                Point over = card.TranslatePoint(new Point(card.Bounds.Width / 2, 20), shown.Window)!.Value;

                shown.Window.MouseWheel(over, new Vector(0, -1), RawInputModifiers.None);
                Settle(shown.Window);

                if (rows == 3)
                {
                    Assert.True(page.Offset.Y > pageBefore, $"The page stayed at {page.Offset.Y}.");
                }
                else
                {
                    Assert.True(list.Offset.Y > listBefore, $"The list stayed at {list.Offset.Y}.");
                    Assert.Equal(pageBefore, page.Offset.Y);
                }
            }
            finally
            {
                shown.Window.Close();
            }
        }
    }

    // Settled: a fraction reaches the model as typed, and the model refuses it, so it is never saved
    [AvaloniaFact]
    public async Task AFractionTypedIntoANumberBoxIsNeverSaved()
    {
        foreach (string name in new[] { "AbuseIpDbKeepDaysBox", "IPv4PrefixBox", "IPv6PrefixBox" })
        {
            FakeIpInfoService service = WithDataset();
            service.Settings = SavedSettings() with { IPv6Enabled = true };
            Shown shown = await ShowAsync(service);
            try
            {
                NumericUpDown box = shown.Find<NumericUpDown>(name);
                Assert.True(box.IsEffectivelyEnabled, name);
                box.Focus();
                box.Text = "2.7";
                shown.Find<Button>("SettingsRevertButton").Focus();
                Settle(shown.Window);

                // What NumericUpDown 12.1.4 did: Value is 2.7 while the box shows "3" (FormatString only formats),
                // so the form's whole-number rule is what refuses it. No ParsingNumberStyle fallback was needed.
                Button save = shown.Find<Button>("SettingsSaveButton");
                Assert.Equal(2.7m, box.Value);
                Assert.Equal("3", box.Text);
                Assert.Equal(AppText.WholeNumberRequired, shown.Find<TextBlock>("SettingsStatus").Text);
                Assert.False(save.IsEffectivelyEnabled, name);
                Assert.False(shown.Page.SaveSettingsCommand.CanExecute(null), name);
                Assert.Equal(0, shown.Service.SaveSettingsCalls);
            }
            finally
            {
                shown.Window.Close();
            }
        }
    }

    // ----- the options -----

    [AvaloniaFact]
    public async Task TheSettingsPageShowsTheSavedSettings()
    {
        FakeIpInfoService service = WithDataset();
        service.Settings = SavedSettings();
        Shown shown = await ShowAsync(service);
        try
        {
            Assert.Equal("/exports/custom", shown.Find<TextBlock>("ExportFolderText").Text);
            Assert.True(shown.Find<CheckBox>("FortigateEnabledBox").IsChecked == true);
            Assert.False(shown.Find<CheckBox>("FortigateIPv4Box").IsChecked == true);
            Assert.True(shown.Find<CheckBox>("FortigateIPv6Box").IsChecked == true);
            Assert.False(shown.Find<CheckBox>("FortigateSplitBox").IsChecked == true);
            Assert.True(shown.Find<CheckBox>("LargeRangeCheckBox").IsChecked == true);
            Assert.Equal((decimal?)12, shown.Find<NumericUpDown>("IPv4PrefixBox").Value);
            Assert.Equal((decimal?)40, shown.Find<NumericUpDown>("IPv6PrefixBox").Value);
            Assert.Equal((decimal?)90, shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox").Value);
            Assert.Equal(AppText.IPv4PrefixLabel, AutomationProperties.GetName(shown.Find<NumericUpDown>("IPv4PrefixBox")));
            Assert.Equal(AppText.IPv6PrefixLabel, AutomationProperties.GetName(shown.Find<NumericUpDown>("IPv6PrefixBox")));
            Assert.Equal(AppText.AbuseIpDbKeepDaysLabel, AutomationProperties.GetName(shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox")));
            Assert.False(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);
            Assert.False(shown.Find<Button>("SettingsRevertButton").IsEffectivelyEnabled);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // Start-up opens the service after the page was built, so the page takes the settings and exceptions again then
    [AvaloniaFact]
    public async Task ThePageTakesWhatStartUpRead()
    {
        Shown shown = await ShowAsync(beforeStart: service =>
        {
            service.Settings = SavedSettings();
            service.Exceptions = Exceptions(2);
        });
        try
        {
            Assert.Equal("/exports/custom", shown.Find<TextBlock>("ExportFolderText").Text);
            Assert.Equal((decimal?)90, shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox").Value);
            Assert.Equal(2, shown.Find<ItemsControl>("ExceptionItems").ItemCount);
            Assert.False(shown.Page.Settings.HasChanges);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UntickingBothFamiliesDisablesSaveAndShowsTheLineWithoutResizing()
    {
        Shown shown = await ShowAsync();
        try
        {
            ScrollViewer page = shown.Find<ScrollViewer>("SettingsScroll");
            TextBlock status = shown.Find<TextBlock>("SettingsStatus");
            double extentBefore = page.Extent.Height;

            shown.Find<CheckBox>("FortigateIPv4Box").IsChecked = false;
            shown.Find<CheckBox>("FortigateIPv6Box").IsChecked = false;
            Settle(shown.Window);

            Assert.False(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);
            Assert.Equal(AppText.ChooseAddressFamily, status.Text);
            Assert.Contains("critical", status.Classes);
            Assert.Equal(18d, status.Bounds.Height);
            Assert.Equal(extentBefore, page.Extent.Height);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheKeepDaysBoxIsLimitedToTheKeepDaysRange()
    {
        Shown shown = await ShowAsync();
        try
        {
            NumericUpDown box = shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox");
            Assert.Equal((decimal)AbuseIpDbLimits.MinKeepDays, box.Minimum);
            Assert.Equal((decimal)AbuseIpDbLimits.MaxKeepDays, box.Maximum);
            Assert.Equal((decimal?)AbuseIpDbLimits.DefaultKeepDays, box.Value);
            Assert.True(box.IsEffectivelyEnabled);
            Assert.Equal(1m, shown.Find<NumericUpDown>("IPv4PrefixBox").Minimum);
            Assert.Equal(32m, shown.Find<NumericUpDown>("IPv4PrefixBox").Maximum);
            Assert.Equal(1m, shown.Find<NumericUpDown>("IPv6PrefixBox").Minimum);
            Assert.Equal(128m, shown.Find<NumericUpDown>("IPv6PrefixBox").Maximum);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ABlankKeepDaysDisablesSaveAndShowsTheLineWithoutResizing()
    {
        Shown shown = await ShowAsync();
        try
        {
            ScrollViewer page = shown.Find<ScrollViewer>("SettingsScroll");
            TextBlock status = shown.Find<TextBlock>("SettingsStatus");
            double extentBefore = page.Extent.Height;

            shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox").Value = null;
            Settle(shown.Window);

            Assert.False(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);
            Assert.Equal(AppText.EnterKeepDays, status.Text);
            Assert.Equal(18d, status.Bounds.Height);
            Assert.Equal(extentBefore, page.Extent.Height);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task IPv6BoxReflectsTheSettingAndUntickingItDisablesTheIPv6Controls()
    {
        FakeIpInfoService service = WithDataset();
        service.Settings = SavedSettings() with { IPv6Enabled = true };
        Shown shown = await ShowAsync(service);
        try
        {
            CheckBox ipv6 = shown.Find<CheckBox>("IPv6EnabledBox");
            CheckBox fortigateIPv6 = shown.Find<CheckBox>("FortigateIPv6Box");
            NumericUpDown prefix = shown.Find<NumericUpDown>("IPv6PrefixBox");
            double extentBefore = shown.Find<ScrollViewer>("SettingsScroll").Extent.Height;
            Assert.True(ipv6.IsChecked == true);
            Assert.True(fortigateIPv6.IsEffectivelyEnabled);
            Assert.True(prefix.IsEffectivelyEnabled);

            ipv6.IsChecked = false;
            Settle(shown.Window);

            Assert.False(fortigateIPv6.IsEffectivelyEnabled);
            Assert.False(prefix.IsEffectivelyEnabled);
            Assert.Equal(extentBefore, shown.Find<ScrollViewer>("SettingsScroll").Extent.Height);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task IPv6BoxShowsOffWhenTheSettingIsOff()
    {
        FakeIpInfoService service = WithDataset();
        service.Settings = SavedSettings() with { IPv6Enabled = false };
        Shown shown = await ShowAsync(service);
        try
        {
            Assert.False(shown.Find<CheckBox>("IPv6EnabledBox").IsChecked == true);
            Assert.False(shown.Find<CheckBox>("FortigateIPv6Box").IsEffectivelyEnabled);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task IPv6OffWithOnlyTheFortigateIPv6BoxTickedDisablesSave()
    {
        FakeIpInfoService service = WithDataset();
        service.Settings = SavedSettings();
        Shown shown = await ShowAsync(service);
        try
        {
            shown.Find<CheckBox>("IPv6EnabledBox").IsChecked = false;
            Settle(shown.Window);

            Assert.False(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);
            Assert.Equal(AppText.ChooseAddressFamily, shown.Find<TextBlock>("SettingsStatus").Text);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnterOnTheSettingsPageNeverSaves()
    {
        Shown shown = await ShowAsync();
        try
        {
            shown.Find<NumericUpDown>("AbuseIpDbKeepDaysBox").Value = 60;
            Settle(shown.Window);
            Assert.True(shown.Find<Button>("SettingsSaveButton").IsEffectivelyEnabled);
            Assert.False(shown.Find<Button>("SettingsSaveButton").IsDefault);
            Assert.False(shown.Find<Button>("ExceptionsSaveButton").IsDefault);
            Assert.False(shown.Find<Button>("AddExceptionButton").IsDefault);

            shown.Find<CheckBox>("FortigateEnabledBox").Focus();
            PressEnter(shown.Window);
            Settle(shown.Window);

            Assert.Equal(0, shown.Service.SaveSettingsCalls);
            Assert.Equal(0, shown.Service.SaveExceptionsCalls);
            Assert.Equal(AppText.ChangesNotSaved, shown.Page.SettingsStatusText);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // ----- the exceptions -----

    [AvaloniaFact]
    public async Task EnterInTheNetworkBoxAddsTheNetworkAndDoesNotSave()
    {
        Shown shown = await ShowAsync();
        try
        {
            Assert.True(shown.Find<TextBlock>("ExceptionsEmpty").IsVisible);
            double extentBefore = shown.Find<ScrollViewer>("SettingsScroll").Extent.Height;
            TextBox network = shown.Find<TextBox>("NetworkBox");
            network.Focus();
            shown.Window.KeyTextInput("203.0.113.7");
            PressEnter(shown.Window);
            Settle(shown.Window);

            Assert.Equal(0, shown.Service.SaveExceptionsCalls);
            Assert.Equal("", network.Text);
            Assert.Equal("203.0.113.7/32", Assert.Single(shown.Page.Exceptions.Rows).NetworkText);
            Assert.Equal(AppText.ExceptionAdded("203.0.113.7/32"), shown.Find<TextBlock>("FeedbackTextBlock").Text);
            Assert.Equal(AppText.ChangesNotSaved, shown.Find<TextBlock>("ExceptionsStatus").Text);
            Assert.Equal(extentBefore, shown.Find<ScrollViewer>("SettingsScroll").Extent.Height);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EnterWithTextThatIsNotANetworkShowsTheErrorAndKeepsTheText()
    {
        Shown shown = await ShowAsync();
        try
        {
            shown.Find<TextBox>("NetworkBox").Focus();
            shown.Window.KeyTextInput("010.0.0.1");
            PressEnter(shown.Window);
            Settle(shown.Window);

            TextBlock feedback = shown.Find<TextBlock>("FeedbackTextBlock");
            Assert.Equal(AppText.ExceptionInvalid, feedback.Text);
            Assert.Contains("critical", feedback.Classes);
            Assert.DoesNotContain("placeholder", feedback.Classes);
            Assert.Equal("010.0.0.1", shown.Find<TextBox>("NetworkBox").Text);
            Assert.Empty(shown.Page.Exceptions.Rows);
            Assert.Equal(0, shown.Service.SaveExceptionsCalls);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheExceptionsListShowsTheSavedEntries()
    {
        FakeIpInfoService service = WithDataset();
        service.Exceptions = Exceptions(2);
        Shown shown = await ShowAsync(service);
        try
        {
            Assert.Equal(2, shown.Find<ItemsControl>("ExceptionItems").ItemCount);
            Assert.False(shown.Find<TextBlock>("ExceptionsEmpty").IsVisible);
            Assert.Equal("2 exceptions", shown.Find<TextBlock>("ExceptionsCountText").Text);
            Assert.Equal(216d, shown.Find<Border>("ExceptionsListCard").Bounds.Height);
            // Each row's remove button is icon-only, so it has a name and a tooltip
            Button remove = shown.Find<ItemsControl>("ExceptionItems").GetVisualDescendants().OfType<Button>().First();
            Assert.Equal(AppText.ExceptionRemoveName("192.0.0.0/24"), AutomationProperties.GetName(remove));
            Assert.NotNull(ToolTip.GetTip(remove));
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheExceptionsEditorShowsTheWarningWhenTheListIsUnreadable()
    {
        FakeIpInfoService service = WithDataset();
        service.Exceptions = ExceptionList.Unreadable;
        Shown shown = await ShowAsync(service);
        try
        {
            TextBlock feedback = shown.Find<TextBlock>("FeedbackTextBlock");
            Assert.Equal(AppText.ExceptionsUnreadableWarning, feedback.Text);
            Assert.Contains("critical", feedback.Classes);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // Add is no default button on any page; Enter adds only from the network and note boxes, and Search stays
    // Lookup's default
    [AvaloniaFact]
    public async Task EnterAddsOnlyFromTheExceptionBoxes()
    {
        Shown shown = await ShowAsync(navigate: false);
        try
        {
            foreach (string item in new[] { "NavLookup", "NavGoogleCloud", "NavAzure", "NavAws", "NavAbuseIpDb", "NavSettings", "NavLookup" })
            {
                Navigate(shown, item);
                Assert.False(shown.Find<Button>("AddExceptionButton").IsDefault);
                Assert.Equal(item == "NavLookup", shown.Find<Button>("SearchButton").IsDefault);
            }

            Navigate(shown, "NavSettings");
            TextBlock feedback = shown.Find<TextBlock>("FeedbackTextBlock");
            shown.Page.Exceptions.NetworkText = "203.0.113.0/24";
            Settle(shown.Window);

            // Enter in a number box or on a check box neither adds the half-typed network nor touches its line
            foreach (string name in new[] { "AbuseIpDbKeepDaysBox", "IPv6EnabledBox", "FortigateEnabledBox" })
            {
                Control control = shown.Find<Control>(name);
                if (control is NumericUpDown number)
                    number.GetVisualDescendants().OfType<TextBox>().First().Focus();
                else
                    control.Focus();
                PressEnter(shown.Window);
                Settle(shown.Window);

                Assert.Empty(shown.Page.Exceptions.Rows);
                Assert.Equal("", shown.Page.Exceptions.FeedbackText);
                Assert.Contains("placeholder", feedback.Classes);
                Assert.Equal("203.0.113.0/24", shown.Find<TextBox>("NetworkBox").Text);
            }

            // Enter in the note box adds the network in the other box
            shown.Find<TextBox>("NoteBox").Focus();
            shown.Window.KeyTextInput("Head office");
            PressEnter(shown.Window);
            Settle(shown.Window);

            ExceptionRow row = Assert.Single(shown.Page.Exceptions.Rows);
            Assert.Equal("203.0.113.0/24", row.NetworkText);
            Assert.Equal("Head office", row.NoteText);
            Assert.Equal(AppText.ExceptionAdded("203.0.113.0/24"), feedback.Text);
            Assert.Equal(0, shown.Service.SaveExceptionsCalls);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // A save loads the saved values back into the form, so nothing can be typed while one runs
    [AvaloniaFact]
    public async Task TheInputsAreOffWhileASaveRuns()
    {
        FakeIpInfoService service = WithDataset();
        service.SaveExceptionsPending = new TaskCompletionSource();
        Shown shown = await ShowAsync(service);
        try
        {
            string[] inputs = ["IPv6EnabledBox", "AbuseIpDbKeepDaysBox", "UseDefaultButton", "NetworkBox", "NoteBox", "AddExceptionButton"];
            Assert.All(inputs, name => Assert.True(shown.Find<Control>(name).IsEffectivelyEnabled, name));
            shown.Page.Exceptions.NetworkText = "203.0.113.0/24";

            Task save = shown.Page.SaveExceptionsCommand.ExecuteAsync(null);
            Settle(shown.Window);

            Assert.True(shown.Page.IsWorking);
            Assert.All(inputs, name => Assert.False(shown.Find<Control>(name).IsEffectivelyEnabled, name));

            service.SaveExceptionsPending.SetResult();
            await save;
            Settle(shown.Window);

            Assert.All(inputs, name => Assert.True(shown.Find<Control>(name).IsEffectivelyEnabled, name));
            Assert.Equal(AppText.ExceptionsSaved(1), shown.Page.ExceptionsStatusText);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EveryStatusLineKeepsItsHeight()
    {
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        Shown shown = await ShowAsync(ui: ui);
        try
        {
            string[] lines = ["SettingsStatus", "KeysStatus", "FeedbackTextBlock", "ExceptionsStatus"];
            double extentBefore = shown.Find<ScrollViewer>("SettingsScroll").Extent.Height;
            foreach (string name in lines)
            {
                TextBlock line = shown.Find<TextBlock>(name);
                Assert.Equal("", line.Text);
                Assert.Contains("placeholder", line.Classes);
                Assert.Equal(18d, line.Bounds.Height);
            }

            shown.Page.Settings.AbuseIpDbKeepDays = 60;
            shown.Page.Exceptions.NetworkText = "203.0.113.0/24";
            shown.Page.AddExceptionCommand.Execute(null);
            await shown.Page.ResetTokenCommand.ExecuteAsync(null);
            Settle(shown.Window);

            foreach (string name in lines)
            {
                TextBlock line = shown.Find<TextBlock>(name);
                Assert.True(line.Text is { Length: > 0 }, name);
                Assert.DoesNotContain("placeholder", line.Classes);
                Assert.Equal(18d, line.Bounds.Height);
            }

            Assert.Equal(AppText.TokenSaved, shown.Find<TextBlock>("KeysStatus").Text);
            Assert.Equal(extentBefore, shown.Find<ScrollViewer>("SettingsScroll").Extent.Height);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // ----- the keys -----

    [AvaloniaFact]
    public async Task TheKeysCardShowsWhetherEachKeyIsSaved()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        Shown shown = await ShowAsync(abuse: abuse);
        try
        {
            Assert.Equal(AppText.TokenSavedStatus, shown.Find<TextBlock>("TokenStatus").Text);
            Assert.Equal(AppText.AbuseKeySavedStatus, shown.Find<TextBlock>("AbuseKeyStatus").Text);
            Assert.True(shown.Find<Button>("ResetTokenButton").IsEffectivelyEnabled);
            Assert.True(shown.Find<Button>("SetAbuseKeyButton").IsEffectivelyEnabled);
            Assert.True(shown.Find<Button>("RemoveAbuseKeyButton").IsEffectivelyEnabled);

            shown.Service.HasToken = false;
            abuse.HasKey = false;
            shown.Page.Reload();
            Settle(shown.Window);

            Assert.Equal(AppText.NoTokenStatus, shown.Find<TextBlock>("TokenStatus").Text);
            Assert.Equal(AppText.NoAbuseKeyStatus, shown.Find<TextBlock>("AbuseKeyStatus").Text);
            Assert.False(shown.Find<Button>("RemoveAbuseKeyButton").IsEffectivelyEnabled);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WithoutAbuseIpDbTheKeyButtonsAreDisabledAndTheRowSaysWhy()
    {
        Shown shown = await ShowAsync(abuse: null);
        try
        {
            Assert.Equal(AppText.AbuseKeysUnavailable, shown.Find<TextBlock>("AbuseKeyStatus").Text);
            Assert.False(shown.Find<Button>("SetAbuseKeyButton").IsEffectivelyEnabled);
            Assert.False(shown.Find<Button>("RemoveAbuseKeyButton").IsEffectivelyEnabled);
            Assert.True(shown.Find<Button>("ResetTokenButton").IsEffectivelyEnabled);
        }
        finally
        {
            shown.Window.Close();
        }
    }

    // ----- the frame -----

    [AvaloniaFact]
    public async Task ThePageFillsTheFrameAndOnlySettingsScrollScrolls()
    {
        Shown shown = await ShowAsync();
        try
        {
            ScrollViewer frame = shown.Find<ScrollViewer>("PageScroll");
            ScrollViewer page = shown.Find<ScrollViewer>("SettingsScroll");
            Assert.True(Math.Abs(frame.Extent.Height - frame.Viewport.Height) < 0.5,
                $"The frame is {frame.Extent.Height} px high in a {frame.Viewport.Height} px viewport.");
            Assert.True(page.Extent.Height > page.Viewport.Height, "The Settings page does not scroll.");
            Assert.Equal(755d, shown.Find<Border>("PageFrame").MinHeight);
        }
        finally
        {
            shown.Window.Close();
        }
    }
}
