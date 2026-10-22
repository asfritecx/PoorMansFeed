using System.Reflection;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The Settings page's view model, driven through fakes on a started gate. No window, no Avalonia.</summary>
public sealed class SettingsPageViewModelTests
{
    private const string DefaultFolder = "/exports/default";

    // A made-up key: never a real one, and never in an assertion message
    private const string SentinelKey = "abuseipdb-sentinel-key-0123456789";

    private sealed class Page
    {
        public required FakeIpInfoService Service { get; init; }
        public required FakeUi Ui { get; init; }
        public required FakeAbuseIpDbService Abuse { get; init; }
        public required WorkGate Gate { get; init; }
        public required AbuseKeyPrompt Keys { get; init; }
        public required SettingsPageViewModel Vm { get; init; }
        public List<SettingsSavedEventArgs> Saved { get; } = [];
        public Queue<TokenResetOutcome> Resets { get; } = new();
        public int ResetCalls { get; set; }
    }

    private static Page NewPage(FakeIpInfoService? service = null, bool withAbuse = true, bool started = true)
    {
        service ??= new FakeIpInfoService();
        var ui = new FakeUi();
        var abuse = new FakeAbuseIpDbService();
        var gate = new WorkGate();
        gate.SetStarted(started);
        var keys = new AbuseKeyPrompt(ui, abuse, gate);
        Page? page = null;
        var vm = new SettingsPageViewModel(service, ui, DefaultFolder, gate, () =>
            {
                page!.ResetCalls++;
                return Task.FromResult(page.Resets.TryDequeue(out TokenResetOutcome outcome) ? outcome : TokenResetOutcome.Saved);
            },
            withAbuse ? abuse : null, withAbuse ? keys : null);
        page = new Page { Service = service, Ui = ui, Abuse = abuse, Gate = gate, Keys = keys, Vm = vm };
        vm.SettingsSaved += (_, e) => page.Saved.Add(e);
        return page;
    }

    private static ExceptionList OneException() => ExceptionList.Create([ExceptionEntryTests.Entry("192.0.2.0/24", "Head office")]);

    // ----- settings -----

    [Fact]
    public async Task SavingStoresTheSettingsRaisesSettingsSavedAndShowsTheLine()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.ExportFolder = "/exports/chosen";
        vm.Settings.IPv6Enabled = false;
        Assert.Equal(AppText.ChangesNotSaved, vm.SettingsStatusText);

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("/exports/chosen", page.Service.SavedSettings!.ExportFolder);
        Assert.False(page.Service.SavedSettings.IPv6Enabled);
        Assert.True(Assert.Single(page.Saved).IPv6Changed);
        Assert.Equal(AppText.SettingsSavedFolderChanged, vm.SettingsStatusText);
        Assert.True(vm.HasSettingsStatus);
        Assert.False(vm.IsSettingsStatusError);
        Assert.False(vm.Settings.HasChanges);
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.True(page.Gate.CanStart);

        // A save that leaves IPv6 alone says so
        vm.Settings.AbuseIpDbKeepDays = 60;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.False(page.Saved[1].IPv6Changed);
        Assert.Equal(2, page.Service.SaveSettingsCalls);
        Assert.Equal(AppText.SettingsSaved, vm.SettingsStatusText);
    }

    // Feeds go only to the new folder after a save that moves it, so the line says the earlier copies stay
    [Fact]
    public async Task ASaveThatChangesTheExportFolderSaysTheEarlierFeedsStay()
    {
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { ExportFolder = "/exports/saved" } });
        var vm = page.Vm;
        vm.Settings.ExportFolder = "/exports/moved";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(AppText.SettingsSavedFolderChanged, vm.SettingsStatusText);
        Assert.False(vm.IsSettingsStatusError);
        Assert.Single(page.Saved);
        Assert.False(vm.HasUnsavedChanges);

        // The line clears on the next edit, as the plain line does
        vm.Settings.AbuseIpDbKeepDays = 60;
        Assert.Equal(AppText.ChangesNotSaved, vm.SettingsStatusText);
    }

    // The folders are compared as resolved full paths, not as the saved text: an empty setting is the default folder
    [Fact]
    public async Task ASaveFromTheDefaultToTheDefaultWrittenOutGivesThePlainLine()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.ExportFolder = DefaultFolder + Path.DirectorySeparatorChar;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(DefaultFolder + Path.DirectorySeparatorChar, page.Service.SavedSettings!.ExportFolder);
        Assert.Equal(AppText.SettingsSaved, vm.SettingsStatusText);
        Assert.Single(page.Saved);
    }

    [Fact]
    public async Task ASaveThatLeavesTheFolderAloneGivesThePlainLine()
    {
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { ExportFolder = "/exports/saved" } });
        var vm = page.Vm;
        vm.Settings.IPv6Enabled = false;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("/exports/saved", page.Service.SavedSettings!.ExportFolder);
        Assert.Equal(AppText.SettingsSaved, vm.SettingsStatusText);
    }

    [Fact]
    public async Task AFailedSaveThatChangesTheFolderKeepsTheFailureLine()
    {
        var page = NewPage(new FakeIpInfoService { SaveSettingsResult = false });
        var vm = page.Vm;
        vm.Settings.ExportFolder = "/exports/moved";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(AppText.SettingsSaveFailed, vm.SettingsStatusText);
        Assert.True(vm.IsSettingsStatusError);
        Assert.Empty(page.Saved);
    }

    // The comparison runs after the save has committed, so a folder that can't be made a full path never stops the
    // saved event or the line
    [Fact]
    public async Task AFolderThatCantBeMadeFullStillSavesAndShowsALine()
    {
        const string BadFolder = "/exports/bad\0folder";
        Assert.ThrowsAny<ArgumentException>(() => Path.GetFullPath(BadFolder));
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { ExportFolder = BadFolder } });
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 60;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Service.SaveSettingsCalls);
        Assert.Single(page.Saved);
        Assert.Equal(AppText.SettingsSaved, vm.SettingsStatusText);
        Assert.False(vm.HasUnsavedChanges);

        // Moving away from it still says the folder changed
        vm.Settings.ExportFolder = "/exports/moved";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Saved.Count);
        Assert.Equal(AppText.SettingsSavedFolderChanged, vm.SettingsStatusText);
    }

    [Fact]
    public void RevertGoesBackToTheSavedSettings()
    {
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { AbuseIpDbKeepDays = 45 } });
        var vm = page.Vm;
        Assert.False(vm.RevertSettingsCommand.CanExecute(null));
        vm.Settings.AbuseIpDbKeepDays = null;
        vm.Settings.ExportFolder = "/exports/edited";
        Assert.True(vm.RevertSettingsCommand.CanExecute(null));

        vm.RevertSettingsCommand.Execute(null);

        Assert.Equal(45m, vm.Settings.AbuseIpDbKeepDays);
        Assert.Equal(DefaultFolder, vm.Settings.ExportFolder);
        Assert.False(vm.Settings.HasChanges);
        Assert.Equal("", vm.SettingsStatusText);
        Assert.False(vm.HasSettingsStatus);
        Assert.Equal(0, page.Service.SaveSettingsCalls);
        Assert.Empty(page.Saved);
    }

    [Fact]
    public async Task AFailedSettingsSaveKeepsTheEditsAndShowsTheLine()
    {
        var page = NewPage(new FakeIpInfoService { SaveSettingsResult = false });
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 90;

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(90m, vm.Settings.AbuseIpDbKeepDays);
        Assert.True(vm.Settings.HasChanges);
        Assert.Equal(AppText.SettingsSaveFailed, vm.SettingsStatusText);
        Assert.True(vm.IsSettingsStatusError);
        Assert.Empty(page.Saved);
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));

        // The next edit drops the failure; the line then says the edits are unsaved
        vm.Settings.AbuseIpDbKeepDays = 91;
        Assert.Equal(AppText.ChangesNotSaved, vm.SettingsStatusText);
        Assert.False(vm.IsSettingsStatusError);
    }

    [Fact]
    public void EditsStayWhenThePageIsLeftAndSayChangesNotSaved()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 12;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        Assert.True(vm.Exceptions.TryAdd());

        // Other work comes and goes meanwhile (an update, another page's dialog); nothing is reloaded or saved
        page.Gate.SetBusy(true);
        page.Gate.SetBusy(false);
        page.Gate.Hold().Dispose();

        Assert.Equal(12m, vm.Settings.AbuseIpDbKeepDays);
        Assert.Single(vm.Exceptions.Rows);
        Assert.Equal(AppText.ChangesNotSaved, vm.SettingsStatusText);
        Assert.Equal(AppText.ChangesNotSaved, vm.ExceptionsStatusText);
        Assert.False(vm.IsSettingsStatusError);
        Assert.False(vm.IsExceptionsStatusError);
        Assert.Equal(0, page.Service.SaveSettingsCalls);
        Assert.Equal(0, page.Service.SaveExceptionsCalls);
    }

    [Fact]
    public void SaveIsDisabledWhileInvalidOrUnchanged()
    {
        var vm = NewPage().Vm;
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));

        vm.Settings.AbuseIpDbKeepDays = 2.7m;
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.Equal(AppText.WholeNumberRequired, vm.SettingsStatusText);
        Assert.True(vm.IsSettingsStatusError);

        vm.Settings.AbuseIpDbKeepDays = null;
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.Equal(AppText.EnterKeepDays, vm.SettingsStatusText);

        vm.Settings.AbuseIpDbKeepDays = 3;
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));
        Assert.False(vm.IsSettingsStatusError);
    }

    [Fact]
    public async Task SaveIsDisabledWhileAnUpdateRuns()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 3;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        int changes = 0;
        vm.SaveSettingsCommand.CanExecuteChanged += (_, _) => changes++;

        page.Gate.SetBusy(true);

        Assert.True(changes > 0);
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.False(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.False(vm.ChooseFolderCommand.CanExecute(null));
        Assert.False(vm.ResetTokenCommand.CanExecute(null));
        Assert.False(vm.SetAbuseKeyCommand.CanExecute(null));
        // Editing in place needs no gate
        Assert.True(vm.RevertSettingsCommand.CanExecute(null));
        Assert.True(vm.AddExceptionCommand.CanExecute(null));
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(0, page.Service.SaveSettingsCalls);

        page.Gate.SetBusy(false);
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChooseFolderSetsTheFolderAndACancelChangesNothing()
    {
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { ExportFolder = "/exports/saved" } });
        var vm = page.Vm;
        bool canStartInPicker = true;
        page.Ui.OnDialogOpen = () => canStartInPicker = page.Gate.CanStart;
        page.Ui.FolderToReturn = null;

        await vm.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal("/exports/saved", vm.Settings.ExportFolder);
        Assert.False(vm.Settings.HasChanges);
        Assert.False(canStartInPicker);

        page.Ui.FolderToReturn = "/exports/picked";
        await vm.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(["/exports/saved", "/exports/saved"], page.Ui.FolderPrompts);
        Assert.Equal("/exports/picked", vm.Settings.ExportFolder);
        Assert.Equal(AppText.ChangesNotSaved, vm.SettingsStatusText);
        Assert.Equal(0, page.Service.SaveSettingsCalls);
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public async Task APickerThatFailsSaysSo()
    {
        var page = NewPage();
        page.Ui.FailFolderPicker = true;

        await page.Vm.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(AppText.FolderPickerFailed, page.Vm.SettingsStatusText);
        Assert.True(page.Vm.IsSettingsStatusError);
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public async Task UseDefaultFolderSavesAnEmptyFolder()
    {
        var page = NewPage(new FakeIpInfoService { Settings = new UserSettings { ExportFolder = "/exports/saved" } });
        var vm = page.Vm;

        vm.UseDefaultFolderCommand.Execute(null);
        Assert.Equal(DefaultFolder, vm.Settings.ExportFolder);
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("", page.Service.SavedSettings!.ExportFolder);
    }

    // ----- exceptions -----

    [Fact]
    public async Task SavedExceptionsAreStoredAndCounted()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        vm.AddExceptionCommand.Execute(null);
        vm.Exceptions.NetworkText = "2001:db8::/32";
        vm.AddExceptionCommand.Execute(null);
        Assert.Equal(AppText.ChangesNotSaved, vm.ExceptionsStatusText);

        await vm.SaveExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(["203.0.113.0/24", "2001:db8::/32"], page.Service.SavedExceptions!.Select(entry => entry.Network.Text));
        Assert.Equal(AppText.ExceptionsSaved(2), vm.ExceptionsStatusText);
        Assert.Equal("Saved 2 exceptions", vm.ExceptionsStatusText);
        Assert.False(vm.IsExceptionsStatusError);
        Assert.False(vm.Exceptions.HasChanges);
        Assert.False(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.Empty(page.Saved);
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public void RevertGoesBackToTheSavedExceptions()
    {
        var page = NewPage(new FakeIpInfoService { Exceptions = OneException() });
        var vm = page.Vm;
        vm.Exceptions.Remove(vm.Exceptions.Rows[0]);
        Assert.True(vm.RevertExceptionsCommand.CanExecute(null));

        vm.RevertExceptionsCommand.Execute(null);

        Assert.Equal("192.0.2.0/24", Assert.Single(vm.Exceptions.Rows).NetworkText);
        Assert.False(vm.Exceptions.HasChanges);
        Assert.Equal("", vm.ExceptionsStatusText);
        Assert.Equal(0, page.Service.SaveExceptionsCalls);
    }

    [Fact]
    public async Task AFailedExceptionsSaveShowsItsLine()
    {
        var page = NewPage(new FakeIpInfoService { SaveExceptionsResult = ExceptionsSaveOutcome.Failed });
        var vm = page.Vm;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        vm.AddExceptionCommand.Execute(null);

        await vm.SaveExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(AppText.ExceptionsSaveFailed, vm.ExceptionsStatusText);
        Assert.True(vm.IsExceptionsStatusError);
        Assert.True(vm.HasExceptionsStatus);
        Assert.Single(vm.Exceptions.Rows);
        Assert.True(vm.Exceptions.HasChanges);
    }

    [Fact]
    public async Task SaveAddsANetworkStillInTheBox()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        vm.Exceptions.NoteText = "Typed, not added";
        Assert.True(vm.SaveExceptionsCommand.CanExecute(null));

        await vm.SaveExceptionsCommand.ExecuteAsync(null);

        ExceptionEntry saved = Assert.Single(page.Service.SavedExceptions!);
        Assert.Equal("203.0.113.0/24", saved.Network.Text);
        Assert.Equal("Typed, not added", saved.Note);
    }

    [Fact]
    public async Task SaveWithTextThatIsNotANetworkSavesNothing()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Exceptions.NetworkText = "not a network";

        await vm.SaveExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(0, page.Service.SaveExceptionsCalls);
        Assert.Equal(AppText.ExceptionInvalid, vm.Exceptions.FeedbackText);
        Assert.Equal("not a network", vm.Exceptions.NetworkText);
    }

    [Fact]
    public async Task ASecondSaveWaitsWhileOneRuns()
    {
        var service = new FakeIpInfoService { SaveExceptionsPending = new TaskCompletionSource() };
        var page = NewPage(service);
        var vm = page.Vm;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        vm.AddExceptionCommand.Execute(null);
        vm.Settings.AbuseIpDbKeepDays = 3;

        Task first = vm.SaveExceptionsCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.False(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.False(vm.AddExceptionCommand.CanExecute(null));
        Assert.False(vm.RevertExceptionsCommand.CanExecute(null));
        Assert.False(page.Gate.CanStart);

        service.SaveExceptionsPending!.SetResult();
        await first;

        Assert.Equal(1, service.SaveExceptionsCalls);
        Assert.False(vm.IsWorking);
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public async Task EveryLookupCommandWaitsWhileASaveRuns()
    {
        var service = new FakeIpInfoService
        {
            Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true),
            HasToken = true,
            SaveExceptionsPending = new TaskCompletionSource(),
        };
        service.StartupResult = new StartupResult(StartupFailure.None, service.Dataset, true);
        var ui = new FakeUi();
        var gate = new WorkGate();
        using var main = new MainViewModel(service, ui, DefaultFolder, abuse: null, gate: gate);
        await main.InitializeAsync();
        if (main.PendingCheck is { } check)
        {
            await check;
        }

        main.SearchText = "AS15169";
        using var vm = new SettingsPageViewModel(service, ui, DefaultFolder, gate, main.ResetTokenAsync);
        Assert.True(main.SearchCommand.CanExecute(null));
        vm.Exceptions.NetworkText = "203.0.113.0/24";

        Task save = vm.SaveExceptionsCommand.ExecuteAsync(null);
        Assert.False(main.SearchCommand.CanExecute(null));
        Assert.False(main.UpdateDatasetCommand.CanExecute(null));
        Assert.False(main.ExportCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await main.ResetTokenAsync());

        service.SaveExceptionsPending!.SetResult();
        await save;

        Assert.True(main.SearchCommand.CanExecute(null));
        Assert.True(main.UpdateDatasetCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheTokenTextFollowsATokenSavedOutsideThePage()
    {
        var service = new FakeIpInfoService
        {
            Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true),
            HasToken = false,
        };
        service.StartupResult = new StartupResult(StartupFailure.None, service.Dataset, false);
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var gate = new WorkGate();
        using var main = new MainViewModel(service, ui, DefaultFolder, abuse: null, gate: gate);
        using var vm = new SettingsPageViewModel(service, ui, DefaultFolder, gate, main.ResetTokenAsync);
        await main.InitializeAsync();
        Assert.Equal(AppText.NoTokenStatus, vm.TokenStatusText);

        // Each raise is recorded with the text it would show then; only those after the prompt opened count
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.TokenStatusText))
            {
                raised.Add(vm.TokenStatusText);
            }
        };
        ui.OnDialogOpen = raised.Clear;

        await main.ResetTokenAsync();
        if (main.PendingCheck is { } check)
        {
            await check;
        }

        Assert.Equal(1, service.SaveTokenCalls);
        Assert.Contains(AppText.TokenSavedStatus, raised);
        Assert.Equal(AppText.TokenSavedStatus, vm.TokenStatusText);
    }

    [Fact]
    public async Task TheKeyTextFollowsAKeySavedByCheckAbuseIpDbsPrompt()
    {
        var service = new FakeIpInfoService
        {
            Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true),
            HasToken = true,
        };
        service.StartupResult = new StartupResult(StartupFailure.None, service.Dataset, true);
        var address = new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.NoResults, address, [], 0);
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(new AbuseCheckResult(AbuseIpDbFailure.NoKey, null, null));
        abuse.CheckResults.Enqueue(new AbuseCheckResult(AbuseIpDbFailure.ServiceError, null, null));
        var gate = new WorkGate();
        var keys = new AbuseKeyPrompt(ui, abuse, gate);
        using var main = new MainViewModel(service, ui, DefaultFolder, abuse, gate, keys);
        using var vm = new SettingsPageViewModel(service, ui, DefaultFolder, gate, main.ResetTokenAsync, abuse, keys);
        await main.InitializeAsync();
        if (main.PendingCheck is { } check)
        {
            await check;
        }

        main.SearchText = "8.8.8.8";
        await main.SearchCommand.ExecuteAsync(null);
        Assert.Equal(AppText.NoAbuseKeyStatus, vm.AbuseKeyStatusText);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.AbuseKeyStatusText))
            {
                raised.Add(vm.AbuseKeyStatusText);
            }
        };
        ui.OnDialogOpen = raised.Clear;

        await main.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Contains(AppText.AbuseKeySavedStatus, raised);
        Assert.Equal(AppText.AbuseKeySavedStatus, vm.AbuseKeyStatusText);
    }

    [Fact]
    public void AKeyPromptBuiltOnAnotherGateIsRefused()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi();
        var abuse = new FakeAbuseIpDbService();
        var gate = new WorkGate();
        Func<Task<TokenResetOutcome>> reset = () => Task.FromResult(TokenResetOutcome.Saved);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new SettingsPageViewModel(
            service, ui, DefaultFolder, gate, reset, abuse, new AbuseKeyPrompt(ui, abuse, new WorkGate())));
        Assert.Equal("abuseKeys", refused.ParamName);

        using var vm = new SettingsPageViewModel(service, ui, DefaultFolder, gate, reset, abuse, new AbuseKeyPrompt(ui, abuse, gate));
    }

    // ----- API keys -----

    [Theory]
    [InlineData("Saved", AppText.TokenSaved)]
    [InlineData("Declined", "")]
    [InlineData("Busy", AppText.KeysBusy)]
    [InlineData("ClearFailed", AppText.TokenClearFailed)]
    [InlineData("NoNewToken", AppText.TokenRemovedNoNew)]
    [InlineData("SaveFailed", AppText.TokenSaveFailed)]
    [InlineData("DialogFailed", AppText.Generic)]
    public async Task ResetTokenShowsEachOutcomeInTheKeysLine(string outcome, string expected)
    {
        var page = NewPage(new FakeIpInfoService { HasToken = true });
        page.Resets.Enqueue(Enum.Parse<TokenResetOutcome>(outcome));
        Assert.Equal(AppText.TokenSavedStatus, page.Vm.TokenStatusText);
        page.Service.HasToken = false;
        var raised = new List<string?>();
        page.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await page.Vm.ResetTokenCommand.ExecuteAsync(null);

        Assert.Equal(1, page.ResetCalls);
        Assert.Equal(expected, page.Vm.KeysStatusText);
        Assert.Equal(expected.Length > 0, page.Vm.HasKeysStatus);
        Assert.Equal(AppText.NoTokenStatus, page.Vm.TokenStatusText);
        Assert.Contains(nameof(SettingsPageViewModel.TokenStatusText), raised);
    }

    [Fact]
    public void EveryResetOutcomeHasALine()
    {
        Assert.Equal(7, Enum.GetValues<TokenResetOutcome>().Length);
    }

    [Fact]
    public async Task SetKeyOpensTheReplacePromptAndSendsNothing()
    {
        var page = NewPage();
        page.Ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        Assert.Equal(AppText.NoAbuseKeyStatus, page.Vm.AbuseKeyStatusText);

        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);

        Assert.Equal([AbuseKeyAsk.Replace], page.Ui.AbuseKeyPrompts);
        Assert.Equal([SentinelKey], page.Abuse.SavedKeys);
        Assert.Equal(0, page.Abuse.CheckCalls);
        Assert.Equal(0, page.Abuse.DownloadCalls);
        Assert.Equal(AppText.AbuseKeySaved, page.Vm.KeysStatusText);
        Assert.Equal(AppText.AbuseKeySavedStatus, page.Vm.AbuseKeyStatusText);
        Assert.True(page.Vm.RemoveAbuseKeyCommand.CanExecute(null));
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public async Task SetKeyShowsEachOutcome()
    {
        var page = NewPage();

        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);
        Assert.Equal("", page.Vm.KeysStatusText);

        page.Ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        page.Abuse.SaveResults.Enqueue(AbuseKeySave.NotAKey);
        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);
        Assert.Equal(AppText.AbuseKeyNotAKey, page.Vm.KeysStatusText);

        page.Ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        page.Abuse.SaveResults.Enqueue(AbuseKeySave.Failed);
        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);
        Assert.Equal(AppText.AbuseKeySaveFailed, page.Vm.KeysStatusText);

        page.Ui.FailAbuseKeyPrompt = true;
        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);
        Assert.Equal(AppText.Generic, page.Vm.KeysStatusText);
        Assert.False(page.Abuse.HasKey);
    }

    [Fact]
    public async Task WhileThePromptIsOpenTheKeyButtonsWait()
    {
        var page = NewPage();
        page.Abuse.HasKey = true;
        bool setWhileOpen = true;
        bool removeWhileOpen = true;
        page.Ui.OnDialogOpen = () =>
        {
            setWhileOpen = page.Vm.SetAbuseKeyCommand.CanExecute(null);
            removeWhileOpen = page.Vm.RemoveAbuseKeyCommand.CanExecute(null);
        };

        await page.Keys.AskAndSaveAsync(AbuseKeyAsk.Missing);

        Assert.False(setWhileOpen);
        Assert.False(removeWhileOpen);
        Assert.True(page.Vm.SetAbuseKeyCommand.CanExecute(null));
        Assert.True(page.Vm.RemoveAbuseKeyCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemoveKeyAsksFirstAndADeclineRemovesNothing()
    {
        var page = NewPage();
        page.Abuse.HasKey = true;
        page.Ui.ConfirmResult = false;

        await page.Vm.RemoveAbuseKeyCommand.ExecuteAsync(null);

        (string title, string message, string confirm) = Assert.Single(page.Ui.Confirms);
        Assert.Equal(AppText.AbuseKeyRemoveTitle, title);
        Assert.Equal(AppText.AbuseKeyRemoveBody, message);
        Assert.Equal(AppText.AbuseKeyRemoveButton, confirm);
        Assert.Equal(0, page.Abuse.ClearKeyCalls);
        Assert.True(page.Abuse.HasKey);
        Assert.Equal("", page.Vm.KeysStatusText);

        page.Ui.ConfirmResult = true;
        await page.Vm.RemoveAbuseKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Abuse.ClearKeyCalls);
        Assert.Equal(AppText.AbuseKeyRemoved, page.Vm.KeysStatusText);
        Assert.Equal(AppText.NoAbuseKeyStatus, page.Vm.AbuseKeyStatusText);
        Assert.False(page.Vm.RemoveAbuseKeyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARemoveConfirmationThatFailsToOpenShowsTheGenericLine()
    {
        var page = NewPage();
        page.Abuse.HasKey = true;
        page.Ui.OnDialogOpen = () => throw new InvalidOperationException("no dialog");

        await page.Vm.RemoveAbuseKeyCommand.ExecuteAsync(null);

        Assert.Equal(AppText.Generic, page.Vm.KeysStatusText);
        Assert.Equal(0, page.Abuse.ClearKeyCalls);
        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public async Task RemoveKeyThatFailsSaysSoAndKeepsTheButton()
    {
        var page = NewPage();
        page.Abuse.HasKey = true;
        page.Abuse.ClearKeyResults.Enqueue(false);

        await page.Vm.RemoveAbuseKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Abuse.ClearKeyCalls);
        Assert.Equal(AppText.AbuseKeyRemoveFailed, page.Vm.KeysStatusText);
        Assert.Equal(AppText.AbuseKeySavedStatus, page.Vm.AbuseKeyStatusText);
        Assert.True(page.Vm.RemoveAbuseKeyCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemoveKeyNeverTouchesTheListOrTheAccounting()
    {
        var page = NewPage();
        page.Abuse.HasKey = true;

        await page.Vm.RemoveAbuseKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, page.Abuse.ClearKeyCalls);
        Assert.Equal(0, page.Abuse.ClearCalls);
        Assert.Equal(0, page.Abuse.StatusCalls);
        Assert.Equal(0, page.Abuse.DownloadCalls);
        Assert.Equal(0, page.Abuse.FeedCalls);
        Assert.Equal(0, page.Abuse.EmptyCalls);
        Assert.Equal(0, page.Abuse.NetworksCalls);
        Assert.Equal(0, page.Abuse.CheckCalls);
        Assert.Empty(page.Abuse.SavedKeys);
    }

    [Fact]
    public void WithoutAbuseIpDbTheKeyButtonsAreDisabledAndTheRowSaysWhy()
    {
        var vm = NewPage(withAbuse: false).Vm;

        Assert.False(vm.CanUseAbuseKeys);
        Assert.Equal(AppText.AbuseKeysUnavailable, vm.AbuseKeyStatusText);
        Assert.False(vm.SetAbuseKeyCommand.CanExecute(null));
        Assert.False(vm.RemoveAbuseKeyCommand.CanExecute(null));
        Assert.True(vm.ResetTokenCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoPropertyHoldsATypedKey()
    {
        var page = NewPage();
        page.Ui.AbuseKeyAnswers.Enqueue(SentinelKey);

        await page.Vm.SetAbuseKeyCommand.ExecuteAsync(null);
        Assert.Equal([SentinelKey], page.Abuse.SavedKeys);

        foreach (object instance in new object[] { page.Vm, page.Keys })
        {
            foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
                {
                    string? text = (string?)property.GetValue(instance);
                    Assert.True(text is null || !text.Contains(SentinelKey, StringComparison.Ordinal),
                        $"{instance.GetType().Name}.{property.Name} holds the typed key.");
                }
            }

            foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.FieldType == typeof(string))
                {
                    string? text = (string?)field.GetValue(instance);
                    Assert.True(text is null || !text.Contains(SentinelKey, StringComparison.Ordinal),
                        $"{instance.GetType().Name}.{field.Name} holds the typed key.");
                }
            }
        }
    }

    // ----- life cycle -----

    [Fact]
    public async Task ReloadTakesTheServicesValuesAndClearsTheLines()
    {
        var page = NewPage(new FakeIpInfoService { SaveSettingsResult = false, SaveExceptionsResult = ExceptionsSaveOutcome.Failed });
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 9;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        await vm.SaveExceptionsCommand.ExecuteAsync(null);
        page.Resets.Enqueue(TokenResetOutcome.ClearFailed);
        await vm.ResetTokenCommand.ExecuteAsync(null);
        Assert.True(vm.HasSettingsStatus && vm.HasExceptionsStatus && vm.HasKeysStatus);

        page.Service.Settings = new UserSettings { AbuseIpDbKeepDays = 120, IPv6Enabled = false };
        page.Service.Exceptions = OneException();
        page.Service.HasToken = true;
        page.Abuse.HasKey = true;
        vm.Reload();

        Assert.Equal(120m, vm.Settings.AbuseIpDbKeepDays);
        Assert.False(vm.Settings.IPv6Enabled);
        Assert.Equal("192.0.2.0/24", Assert.Single(vm.Exceptions.Rows).NetworkText);
        Assert.Equal("", vm.SettingsStatusText);
        Assert.Equal("", vm.ExceptionsStatusText);
        Assert.Equal("", vm.KeysStatusText);
        Assert.False(vm.HasSettingsStatus || vm.HasExceptionsStatus || vm.HasKeysStatus);
        Assert.Equal(AppText.TokenSavedStatus, vm.TokenStatusText);
        Assert.Equal(AppText.AbuseKeySavedStatus, vm.AbuseKeyStatusText);
        Assert.True(vm.RemoveAbuseKeyCommand.CanExecute(null));
    }

    [Fact]
    public void NothingRunsBeforeTheStart()
    {
        var vm = NewPage(started: false).Vm;
        vm.Settings.AbuseIpDbKeepDays = 3;

        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.False(vm.ChooseFolderCommand.CanExecute(null));
        Assert.False(vm.ResetTokenCommand.CanExecute(null));
        Assert.False(vm.SetAbuseKeyCommand.CanExecute(null));
    }

    // Before start-up the page shows the defaults and start-up replaces them, so nothing can be edited until it starts
    [Fact]
    public void NothingCanBeEditedBeforeTheStart()
    {
        var page = NewPage(started: false);
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 3;
        vm.Exceptions.NetworkText = "192.0.2.0/24";

        Assert.False(vm.CanEditInputs);
        Assert.False(vm.AddExceptionCommand.CanExecute(null));
        Assert.False(vm.UseDefaultFolderCommand.CanExecute(null));
        Assert.False(vm.RevertSettingsCommand.CanExecute(null));
        Assert.False(vm.RevertExceptionsCommand.CanExecute(null));

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        page.Gate.SetStarted(true);

        Assert.Contains(nameof(SettingsPageViewModel.CanEditInputs), raised);
        Assert.True(vm.CanEditInputs);
        Assert.True(vm.AddExceptionCommand.CanExecute(null));
        Assert.True(vm.RevertSettingsCommand.CanExecute(null));
    }

    // Started while a hold is out: CanStart doesn't flip, but the inputs still turn on
    [Fact]
    public void StartingWhileHeldStillTurnsTheInputsOn()
    {
        var page = NewPage(started: false);
        IDisposable hold = page.Gate.Hold();
        page.Gate.SetStarted(true);

        Assert.True(page.Vm.CanEditInputs);
        Assert.True(page.Vm.AddExceptionCommand.CanExecute(null));
        hold.Dispose();
    }

    [Fact]
    public void CanEditInputsFollowsIsWorking()
    {
        var service = new FakeIpInfoService { SaveExceptionsPending = new TaskCompletionSource() };
        var vm = NewPage(service).Vm;
        vm.Exceptions.NetworkText = "192.0.2.0/24";
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Task save = vm.SaveExceptionsCommand.ExecuteAsync(null);
        Assert.False(vm.CanEditInputs);
        Assert.Contains(nameof(SettingsPageViewModel.CanEditInputs), raised);

        service.SaveExceptionsPending.SetResult();
        Assert.True(save.IsCompleted);
        Assert.True(vm.CanEditInputs);
    }

    // Either section's unsaved edits count, and the property is raised as they come and go
    [Fact]
    public async Task HasUnsavedChangesCoversBothSectionsAndIsRaised()
    {
        var page = NewPage();
        var vm = page.Vm;
        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(SettingsPageViewModel.HasUnsavedChanges) ? 1 : 0;
        Assert.False(vm.HasUnsavedChanges);

        vm.Settings.AbuseIpDbKeepDays = 3;
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(raised > 0);
        vm.RevertSettingsCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);

        // An exception added but not saved counts on its own
        vm.Exceptions.NetworkText = "192.0.2.0/24";
        vm.AddExceptionCommand.Execute(null);
        Assert.False(vm.Settings.HasChanges);
        Assert.True(vm.HasUnsavedChanges);
        raised = 0;
        await vm.SaveExceptionsCommand.ExecuteAsync(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(raised > 0);

        vm.Settings.AbuseIpDbKeepDays = 4;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ConfirmDiscardAsksOnlyWhenSomethingIsUnsaved()
    {
        var page = NewPage();
        var vm = page.Vm;
        Assert.True(await vm.ConfirmDiscardAsync());
        Assert.Empty(page.Ui.Confirms);

        vm.Exceptions.NetworkText = "192.0.2.0/24";
        vm.AddExceptionCommand.Execute(null);
        page.Ui.ConfirmResult = false;
        Assert.False(await vm.ConfirmDiscardAsync());
        Assert.Equal((AppText.CloseUnsavedTitle, AppText.CloseUnsavedBody, AppText.CloseUnsavedButton), Assert.Single(page.Ui.Confirms));
        Assert.True(vm.HasUnsavedChanges);

        page.Ui.ConfirmResult = true;
        Assert.True(await vm.ConfirmDiscardAsync());
    }

    [Fact]
    public async Task AConfirmationThatFailsKeepsTheEdits()
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 3;
        page.Ui.ConfirmException = new InvalidOperationException("dialog failed");

        Assert.False(await vm.ConfirmDiscardAsync());
        Assert.True(vm.HasUnsavedChanges);
    }

    // A network typed but not added is unsaved work: Save would add it, so leaving or closing must not drop it quietly
    [Fact]
    public async Task ANetworkTypedButNotAddedIsUnsavedAndRevertClearsIt()
    {
        var page = NewPage();
        var vm = page.Vm;
        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(SettingsPageViewModel.HasUnsavedChanges) ? 1 : 0;

        vm.Exceptions.NetworkText = "203.0.113.0/24";

        Assert.Empty(vm.Exceptions.Rows);
        Assert.False(vm.Exceptions.HasChanges);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(raised > 0);
        Assert.Equal(AppText.ChangesNotSaved, vm.ExceptionsStatusText);
        Assert.True(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.True(vm.RevertExceptionsCommand.CanExecute(null));
        page.Ui.ConfirmResult = false;
        Assert.False(await vm.ConfirmDiscardAsync());
        Assert.Equal(AppText.CloseUnsavedTitle, Assert.Single(page.Ui.Confirms).Title);

        vm.RevertExceptionsCommand.Execute(null);

        Assert.Equal("", vm.Exceptions.NetworkText);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("", vm.ExceptionsStatusText);
        Assert.False(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.False(vm.RevertExceptionsCommand.CanExecute(null));
        Assert.Equal(0, page.Service.SaveExceptionsCalls);
    }

    // Save adds nothing for a note typed with no network, so the note alone isn't unsaved work
    [Fact]
    public async Task ANoteTypedAloneIsNotUnsaved()
    {
        var page = NewPage();
        var vm = page.Vm;

        vm.Exceptions.NoteText = "Head office";
        vm.Exceptions.NetworkText = "   ";

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("", vm.ExceptionsStatusText);
        Assert.False(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.False(vm.RevertExceptionsCommand.CanExecute(null));
        Assert.True(await vm.ConfirmDiscardAsync());
        Assert.Empty(page.Ui.Confirms);
    }

    // Text that isn't a network is still typed work: it counts as unsaved, and Save refuses it with the reason
    [Fact]
    public async Task TextThatIsNotANetworkIsUnsavedAndSaveRefusesIt()
    {
        var page = NewPage();
        var vm = page.Vm;

        vm.Exceptions.NetworkText = "not a network";

        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(AppText.ChangesNotSaved, vm.ExceptionsStatusText);
        Assert.True(vm.SaveExceptionsCommand.CanExecute(null));
        Assert.True(vm.RevertExceptionsCommand.CanExecute(null));
        page.Ui.ConfirmResult = false;
        Assert.False(await vm.ConfirmDiscardAsync());
        Assert.Single(page.Ui.Confirms);

        await vm.SaveExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(0, page.Service.SaveExceptionsCalls);
        Assert.Equal(AppText.ExceptionInvalid, vm.Exceptions.FeedbackText);
        Assert.True(vm.Exceptions.IsFeedbackError);
        Assert.Equal("not a network", vm.Exceptions.NetworkText);
        Assert.True(vm.HasUnsavedChanges);
    }

    // The discard question holds the gate like every other confirmation, so the menu's Reset API token can't start
    // behind it; the hold is gone once it is answered, either way
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheDiscardQuestionHoldsTheGateWhileItIsOpen(bool answer)
    {
        var page = NewPage();
        var vm = page.Vm;
        vm.Settings.AbuseIpDbKeepDays = 3;
        bool? canResetWhileOpen = null;
        bool? canStartWhileOpen = null;
        page.Ui.ConfirmResult = answer;
        page.Ui.OnDialogOpen = () =>
        {
            canResetWhileOpen = vm.ResetTokenCommand.CanExecute(null);
            canStartWhileOpen = page.Gate.CanStart;
        };
        Assert.True(vm.ResetTokenCommand.CanExecute(null));

        Assert.Equal(answer, await vm.ConfirmDiscardAsync());

        Assert.False(canResetWhileOpen);
        Assert.False(canStartWhileOpen);
        Assert.True(page.Gate.CanStart);
        Assert.True(vm.ResetTokenCommand.CanExecute(null));
    }

    // A confirmation that fails to open releases the hold too
    [Fact]
    public async Task ADiscardQuestionThatFailsReleasesTheGate()
    {
        var page = NewPage();
        page.Vm.Settings.AbuseIpDbKeepDays = 3;
        page.Ui.ConfirmException = new InvalidOperationException("dialog failed");

        Assert.False(await page.Vm.ConfirmDiscardAsync());

        Assert.True(page.Gate.CanStart);
    }

    [Fact]
    public void IsStartedFollowsTheGateAndIsRaised()
    {
        var page = NewPage(started: false);
        var vm = page.Vm;
        Assert.False(vm.IsStarted);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        page.Gate.SetStarted(true);

        Assert.True(vm.IsStarted);
        Assert.Contains(nameof(SettingsPageViewModel.IsStarted), raised);
    }

    [Fact]
    public void DisposeDisablesEveryCommand()
    {
        var page = NewPage();
        var vm = page.Vm;
        page.Abuse.HasKey = true;
        vm.Settings.AbuseIpDbKeepDays = 3;
        vm.Exceptions.NetworkText = "203.0.113.0/24";
        Assert.True(vm.SaveSettingsCommand.CanExecute(null));

        vm.Dispose();

        foreach (System.Windows.Input.ICommand command in new System.Windows.Input.ICommand[]
                 {
                     vm.SaveSettingsCommand, vm.RevertSettingsCommand, vm.ChooseFolderCommand, vm.UseDefaultFolderCommand,
                     vm.AddExceptionCommand, vm.SaveExceptionsCommand, vm.RevertExceptionsCommand, vm.ResetTokenCommand,
                     vm.SetAbuseKeyCommand, vm.RemoveAbuseKeyCommand,
                 })
        {
            Assert.False(command.CanExecute(null));
        }

        // Unsubscribed: a later edit or gate change raises nothing on the page
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Settings.AbuseIpDbKeepDays = 4;
        page.Gate.SetBusy(true);
        Assert.Empty(raised);
        vm.Dispose();
    }
}
