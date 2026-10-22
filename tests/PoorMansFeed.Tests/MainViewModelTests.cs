using System.Numerics;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.FeedExceptions;

namespace PoorMansFeed.Tests;

/// <summary>The view model's behaviour, driven through fakes. No window, no Avalonia.</summary>
public sealed class MainViewModelTests
{
    // Literal copies of the escapes the view model writes, so a wording slip in AppText is caught here
    private const string Dot = "\u00A0\u00B7\u00A0";
    private const string Dash = " \u2014 ";
    private const string Ellipsis = "\u2026";
    private const long Megabyte = 1024 * 1024;

    private static readonly DatasetInfo SampleDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true);

    // ----- helpers -----

    private static FakeIpInfoService WithDataset(bool hasToken = true) => new()
    {
        Dataset = SampleDataset,
        HasToken = hasToken,
        StartupResult = new StartupResult(StartupFailure.None, SampleDataset, hasToken),
    };

    private static FakeIpInfoService WithoutDataset(bool hasToken = false) => new()
    {
        HasToken = hasToken,
        StartupResult = new StartupResult(StartupFailure.None, null, hasToken),
    };

    // Progress is delivered inline, so a report has taken effect by the time the test asserts on it
    private static MainViewModel NewViewModel(
        FakeIpInfoService service, FakeUi ui, string? defaultFolder = null, FakeAbuseIpDbService? abuse = null,
        IUnsavedSettings? unsaved = null) =>
        new(service, ui, defaultFolder, abuse, gate: null, abuseKeys: null, unsaved)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };

    // Starts the view model and waits for its background check, so the state is settled before the test looks
    private static async Task<MainViewModel> StartAsync(
        FakeIpInfoService service, FakeUi ui, string? defaultFolder = null, FakeAbuseIpDbService? abuse = null,
        IUnsavedSettings? unsaved = null)
    {
        var vm = NewViewModel(service, ui, defaultFolder, abuse: abuse, unsaved: unsaved);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return vm;
    }

    private static SearchQuery AsnQuery(uint asn = 15169) => new(SearchKind.Asn, "AS" + asn, asn, null);

    private static NetworkRecord Row(int index, string asName = "Google LLC") =>
        new($"10.{index}.0.0/16", "US", "United States", "NA", "AS15169", asName, "google.com");

    private static SearchResult FoundResult(SearchQuery query, int rows, int total) =>
        new(SearchOutcome.Found, query, Enumerable.Range(0, rows).Select(i => Row(i)).ToList(), total);

    // ----- start-up failures -----

    [Fact]
    public async Task APrivilegedStartShowsTheClosingNoticeAndClosesTheApplication()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.Privileged, null, false) };
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);

        await vm.InitializeAsync();

        (string title, string message) = Assert.Single(ui.ClosingNotices);
        Assert.Equal("Administrator privileges", title);
        Assert.Equal("PoorMansFeed can't be run as an administrator. Please relaunch the application from a standard account.", message);
        Assert.Equal(1, ui.CloseCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnAlreadyRunningStartShowsTheClosingNoticeAndClosesTheApplication()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.AlreadyRunning, null, false) };
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);

        await vm.InitializeAsync();

        (string title, string message) = Assert.Single(ui.ClosingNotices);
        Assert.Equal("Already open", title);
        Assert.Equal("PoorMansFeed is already open on this computer. Please use the window that is already open.", message);
        Assert.Equal(1, ui.CloseCalls);
        Assert.False(vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task StorageUnavailableShowsTheStartupErrorAndNoCommandRuns()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.StorageUnavailable, null, false) };
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);

        await vm.InitializeAsync();
        vm.SearchText = "AS15169";

        Assert.True(vm.IsStopped);
        Assert.Equal("Data folder unavailable", vm.StopTitle);
        Assert.Equal("PoorMansFeed couldn't start. Its data folder couldn't be created. Please contact the Infra Team for assistance.", vm.StopText);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CopyResultsCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await vm.ResetTokenAsync());
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Empty(ui.Confirms);
    }

    // Every failure that stops the app, from the enum, so a new value can't go without its own stop
    public static TheoryData<StartupFailure> EveryStoppingFailure =>
        new(Enum.GetValues<StartupFailure>().Where(failure =>
            failure is not (StartupFailure.None or StartupFailure.Privileged or StartupFailure.AlreadyRunning)));

    [Fact]
    public void ElevenFailuresStopTheApp() => Assert.Equal(11, EveryStoppingFailure.Count);

    [Theory]
    [MemberData(nameof(EveryStoppingFailure))]
    public async Task EveryStoppingFailureShowsItsOwnTitleAndText(StartupFailure failure)
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(failure, null, false) };
        var ui = new FakeUi();
        var vm = NewViewModel(service, ui);
        int started = 0;
        vm.Started += (_, _) => started++;

        await vm.InitializeAsync();
        vm.SearchText = "AS15169";

        Assert.True(vm.IsStopped);
        Assert.False(string.IsNullOrWhiteSpace(vm.StopTitle));
        Assert.False(vm.StopTitle.EndsWith('.'));
        Assert.StartsWith("PoorMansFeed couldn't start.", vm.StopText);
        Assert.Contains(GraphiteUi.AppInfo.Name, vm.StopText);
        Assert.False(vm.HasStopAction);
        foreach (StartupFailure other in EveryStoppingFailure.Select(row => row.Data).Where(other => other != failure))
        {
            Assert.NotEqual(AppText.StopTitle(other), vm.StopTitle);
            Assert.NotEqual(AppText.StopText(other), vm.StopText);
        }

        // It never starts
        Assert.Equal(0, started);
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await vm.ResetTokenAsync());
        Assert.Equal(0, service.CheckCalls);
        Assert.Empty(ui.TokenPrompts);
    }

    [Fact]
    public async Task ADatabaseNewerThanTheAppShowsItsOwnStartupError()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.DatabaseNewer, null, false) };
        var vm = NewViewModel(service, new FakeUi());

        await vm.InitializeAsync();

        Assert.True(vm.IsStopped);
        Assert.Equal("Dataset from a newer version", vm.StopTitle);
        Assert.Equal("PoorMansFeed couldn't start. The dataset on this computer was created by a newer version. Please install the latest version.", vm.StopText);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
    }

    [Theory]
    [MemberData(nameof(EveryStoppingFailure))]
    public async Task StartFreshIsOfferedOnlyForNewerUnreadableKeyMismatchAndMissing(StartupFailure failure)
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(failure, null, false) };
        var vm = NewViewModel(service, new FakeUi());

        await vm.InitializeAsync();

        bool offered = failure is StartupFailure.ConfigNewer or StartupFailure.ConfigUnreadable
            or StartupFailure.ConfigKeyMismatch or StartupFailure.ConfigMissing;
        Assert.Equal(offered, vm.CanStartFresh);
        Assert.Equal(offered, vm.StartFreshCommand.CanExecute(null));
    }

    [Theory]
    [MemberData(nameof(EveryStoppingFailure))]
    public async Task StartWithoutOldExceptionsIsOfferedOnlyForTheImportFailure(StartupFailure failure)
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(failure, null, false) };
        var vm = NewViewModel(service, new FakeUi());

        await vm.InitializeAsync();

        bool offered = failure == StartupFailure.ExceptionsImportUnreadable;
        Assert.Equal(offered, vm.CanStartWithoutOldExceptions);
        Assert.Equal(offered, vm.StartWithoutOldExceptionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStartedAppOffersNeitherWayOn()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());

        Assert.False(vm.IsStopped);
        Assert.False(vm.StartFreshCommand.CanExecute(null));
        Assert.False(vm.StartWithoutOldExceptionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStoppedAppRunsNoCommand()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.ConfigUnreadable, SampleDataset, true) };
        service.Dataset = SampleDataset;
        service.HasToken = true;
        var ui = new FakeUi();
        var gate = new WorkGate();
        using var vm = await StartOnGateAsync(service, ui, gate);
        using var page = new SettingsPageViewModel(service, ui, "/exports/default", gate, vm.ResetTokenAsync);
        vm.SearchText = "AS15169";
        page.Exceptions.NetworkText = "203.0.113.0/24";

        Assert.True(vm.IsStopped);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CopyResultsCommand.CanExecute(null));
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await vm.ResetTokenAsync());
        Assert.False(page.SaveSettingsCommand.CanExecute(null));
        Assert.False(page.RevertSettingsCommand.CanExecute(null));
        Assert.False(page.ChooseFolderCommand.CanExecute(null));
        Assert.False(page.UseDefaultFolderCommand.CanExecute(null));
        Assert.False(page.AddExceptionCommand.CanExecute(null));
        Assert.False(page.SaveExceptionsCommand.CanExecute(null));
        Assert.False(page.RevertExceptionsCommand.CanExecute(null));
        Assert.False(page.ResetTokenCommand.CanExecute(null));
        Assert.False(page.SetAbuseKeyCommand.CanExecute(null));
        Assert.False(page.RemoveAbuseKeyCommand.CanExecute(null));
        Assert.False(page.CanEditInputs);
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.SearchCalls);
        Assert.Empty(ui.Confirms);
    }

    private static async Task<MainViewModel> StoppedAsync(FakeIpInfoService service, FakeUi ui, StartupFailure failure)
    {
        service.StartupResult = new StartupResult(failure, null, false);
        var vm = NewViewModel(service, ui);
        await vm.InitializeAsync();
        Assert.True(vm.IsStopped);
        return vm;
    }

    // The stop's texts say what the buttons and the store really do
    [Fact]
    public void TheStopTextsNameTheButtonAndWhatHappensToTheFiles()
    {
        Assert.Contains("Choose " + AppText.StartWithoutOldExceptionsButton + " to go on with an empty list; the file is then kept as exceptions.unreadable.json",
                        AppText.ExceptionsImportUnreadable);
        Assert.Contains("Or fix exceptions.json", AppText.ExceptionsImportUnreadable);
        Assert.StartsWith("If config.db is back in the app data folder, it is opened as it is. Otherwise a new settings file is made: ",
                          AppText.StartFreshMissingBody);
        Assert.StartsWith("exceptions.json is kept as exceptions.unreadable.json in the app data folder, in place of any older copy. ",
                          AppText.StartWithoutOldExceptionsBody);
        // The stop's action line is 506 px wide and Generic fits it; fewer characters is only a stand-in for the width
        Assert.True(AppText.StartWithoutOldExceptionsFailed.Length < AppText.Generic.Length);
    }

    // A close while the launch's service call runs: the step is left to return, and nothing it returns is applied
    [Theory]
    [InlineData(StartupFailure.ConfigUnreadable, false)]
    [InlineData(StartupFailure.Privileged, false)]
    [InlineData(StartupFailure.None, false)]
    [InlineData(StartupFailure.None, true)]
    public async Task AHeldLaunchWithACloseAppliesNothingWhenItReturns(StartupFailure failure, bool hasDataset)
    {
        FakeIpInfoService service = hasDataset ? WithDataset() : WithoutDataset();
        service.StartupResult = new StartupResult(failure, service.Dataset, service.HasToken);
        var gate = new TaskCompletionSource();
        service.InitializeGate = gate;
        var ui = new FakeUi { TokenToReturn = "a-token" };
        var vm = NewViewModel(service, ui);
        int started = 0;
        vm.Started += (_, _) => started++;

        Task launch = vm.InitializeAsync();
        Assert.True(vm.IsStartStepRunning);
        Task closing = vm.CloseAfterStartStepAsync();
        Assert.False(closing.IsCompleted);
        // The pill says the close is waiting, in the plain style, while the step is still out
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        Assert.False(vm.IsPillWarning);
        gate.SetResult();
        await closing;
        await launch;

        Assert.False(vm.IsStartStepRunning);
        Assert.Equal([true], service.StartStepTokensCancelled);
        Assert.False(vm.IsStopped);
        Assert.Equal(0, started);
        Assert.Empty(ui.TokenPrompts);
        Assert.Empty(ui.ClosingNotices);
        Assert.Equal(0, ui.CloseCalls);
        Assert.Null(vm.PendingCheck);
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        Assert.False(vm.IsPillWarning);
        Assert.False(vm.HasStopAction);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
    }

    // A held launch whose step throws OperationCanceledException after the close, as the real one does for a cancel
    // that lands before its one look at the token, applies nothing either
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldLaunchThatIsCancelledByACloseAppliesNothing(bool hasDataset)
    {
        FakeIpInfoService service = hasDataset ? WithDataset() : WithoutDataset();
        service.StartupResult = new StartupResult(StartupFailure.None, service.Dataset, service.HasToken);
        service.HeldStepThrowsWhenCancelled = true;
        var gate = new TaskCompletionSource();
        service.InitializeGate = gate;
        var ui = new FakeUi { TokenToReturn = "a-token" };
        var vm = NewViewModel(service, ui);
        int started = 0;
        vm.Started += (_, _) => started++;

        Task launch = vm.InitializeAsync();
        Task closing = vm.CloseAfterStartStepAsync();
        gate.SetResult();
        await closing;
        await launch;

        Assert.Equal(1, service.HeldStepsCancelled);
        Assert.False(vm.IsStartStepRunning);
        Assert.False(vm.IsStopped);
        Assert.False(vm.HasStopAction);
        Assert.Equal(0, started);
        Assert.Empty(ui.TokenPrompts);
        Assert.Empty(ui.ClosingNotices);
        Assert.Null(vm.PendingCheck);
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
    }

    // The same for a held Start fresh: no start in place, no new stop and no stop line
    [Theory]
    [InlineData(StartupFailure.None)]
    [InlineData(StartupFailure.ConfigUnreadable)]
    [InlineData(StartupFailure.ConfigLocked)]
    public async Task AHeldStartFreshWithACloseAppliesNothingWhenItReturns(StartupFailure failure)
    {
        var service = WithoutDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "a-token" };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        int started = 0;
        vm.Started += (_, _) => started++;
        var gate = new TaskCompletionSource();
        service.StartFreshGate = gate;
        service.StartFreshResult = new StartupResult(failure, null, false);

        Task leaving = vm.StartFreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsStartStepRunning);
        Task closing = vm.CloseAfterStartStepAsync();
        Assert.False(closing.IsCompleted);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        Assert.False(vm.IsPillWarning);
        gate.SetResult();
        await closing;
        await leaving;

        Assert.False(vm.IsStartStepRunning);
        Assert.Equal(1, service.StartFreshCalls);
        Assert.Equal([true], service.StartStepTokensCancelled);
        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.ConfigUnreadable, vm.StopText);
        Assert.NotEqual(AppText.StartFreshFailed, vm.StopActionText);
        Assert.Equal(0, started);
        Assert.Empty(ui.TokenPrompts);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        Assert.False(vm.IsPillWarning);
    }

    // Once a close is held, nothing can run again: the step has returned, but the stop buttons stay off and a direct
    // run of one asks nothing
    [Fact]
    public async Task AfterAHeldCloseNoCommandCanRunAgain()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "a-token" };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        var gate = new TaskCompletionSource();
        service.StartFreshGate = gate;
        service.StartFreshResult = new StartupResult(StartupFailure.ConfigUnreadable, null, false);
        int confirms = ui.Confirms.Count;
        Assert.True(vm.OpenExportFolderCommand.CanExecute(null));

        Task leaving = vm.StartFreshCommand.ExecuteAsync(null);
        int openFolderNotified = 0;
        vm.OpenExportFolderCommand.CanExecuteChanged += (_, _) => openFolderNotified++;
        Task closing = vm.CloseAfterStartStepAsync();
        gate.SetResult();
        await closing;
        await leaving;
        Assert.Equal(confirms + 1, ui.Confirms.Count);

        Assert.False(vm.StartFreshCommand.CanExecute(null));
        Assert.False(vm.StartWithoutOldExceptionsCommand.CanExecute(null));
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));
        // Open folder is outside CanStart, so it has its own check, and the hold's refresh reaches it
        Assert.False(vm.OpenExportFolderCommand.CanExecute(null));
        Assert.True(openFolderNotified > 0);
        await vm.StartFreshCommand.ExecuteAsync(null);
        await vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);
        Assert.Equal(confirms + 1, ui.Confirms.Count);
        Assert.Equal(1, service.StartFreshCalls);
        Assert.Equal(0, service.StartWithoutOldExceptionsCalls);
    }

    // A held Start fresh that throws OperationCanceledException after the close applies nothing
    [Fact]
    public async Task AHeldStartFreshThatIsCancelledByACloseAppliesNothing()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "a-token" };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        int started = 0;
        vm.Started += (_, _) => started++;
        var gate = new TaskCompletionSource();
        service.StartFreshGate = gate;
        service.StartFreshResult = new StartupResult(StartupFailure.None, null, false);
        service.HeldStepThrowsWhenCancelled = true;

        Task leaving = vm.StartFreshCommand.ExecuteAsync(null);
        Task closing = vm.CloseAfterStartStepAsync();
        gate.SetResult();
        await closing;
        await leaving;

        Assert.Equal(1, service.HeldStepsCancelled);
        Assert.False(vm.IsStartStepRunning);
        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.ConfigUnreadable, vm.StopText);
        Assert.NotEqual(AppText.StartFreshFailed, vm.StopActionText);
        Assert.NotEqual(AppText.Generic, vm.StopActionText);
        Assert.Equal(0, started);
        Assert.Empty(ui.TokenPrompts);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
    }

    // A held Start without exceptions with a close: the same, whether its step returns or throws
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldStartWithoutOldExceptionsWithACloseAppliesNothing(bool throws)
    {
        var service = WithoutDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "a-token" };
        var vm = await StoppedAsync(service, ui, StartupFailure.ExceptionsImportUnreadable);
        int started = 0;
        vm.Started += (_, _) => started++;
        var gate = new TaskCompletionSource();
        service.StartWithoutOldExceptionsGate = gate;
        service.StartWithoutOldExceptionsResult = new StartupResult(StartupFailure.None, null, false);
        service.HeldStepThrowsWhenCancelled = throws;

        Task leaving = vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);
        Assert.True(vm.IsStartStepRunning);
        Task closing = vm.CloseAfterStartStepAsync();
        Assert.False(closing.IsCompleted);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        gate.SetResult();
        await closing;
        await leaving;

        Assert.Equal(1, service.StartWithoutOldExceptionsCalls);
        Assert.Equal([true], service.StartStepTokensCancelled);
        Assert.Equal(throws ? 1 : 0, service.HeldStepsCancelled);
        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.ExceptionsImportUnreadable, vm.StopText);
        Assert.False(vm.HasStopAction && vm.IsStopActionError);
        Assert.Equal(0, started);
        Assert.Empty(ui.TokenPrompts);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.ClosingAfterStartUp, vm.PillText);
        Assert.False(vm.StartWithoutOldExceptionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStopSaysNotStartedInThePillAndAStartInPlaceSaysStarting()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        Assert.Equal(AppText.NotStarted, vm.PillText);
        Assert.Equal("Not started", vm.PillText);
        Assert.True(vm.IsPillWarning);

        (string Pill, bool Warning)? atStart = null;
        vm.Started += (_, _) => atStart = (vm.PillText, vm.IsPillWarning);
        service.StartFreshResult = new StartupResult(StartupFailure.None, null, false);
        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.False(vm.IsStopped);
        Assert.Equal((AppText.Starting, false), atStart);
    }

    // While the question is open, neither button asks again, whatever runs the command
    [Theory]
    [InlineData(StartupFailure.ConfigUnreadable)]
    [InlineData(StartupFailure.ExceptionsImportUnreadable)]
    public async Task AStopButtonAsksOnceWhileItsQuestionIsOpen(StartupFailure failure)
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StoppedAsync(service, ui, failure);
        var gate = new TaskCompletionSource();
        ui.ConfirmGate = gate;
        IAsyncRelayCommand command = failure == StartupFailure.ConfigUnreadable
            ? vm.StartFreshCommand
            : vm.StartWithoutOldExceptionsCommand;

        Task first = command.ExecuteAsync(null);
        await vm.StartFreshCommand.ExecuteAsync(null);
        await vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);
        // A run that returned at once replaces the command's own task, so its CanExecute no longer says it is running
        await command.ExecuteAsync(null);

        Assert.Single(ui.Confirms);
        Assert.False(vm.StartFreshCommand.CanExecute(null));
        Assert.False(vm.StartWithoutOldExceptionsCommand.CanExecute(null));
        gate.SetResult();
        await first;

        Assert.Single(ui.Confirms);
        Assert.Equal(0, service.StartFreshCalls);
        Assert.Equal(0, service.StartWithoutOldExceptionsCalls);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ADeclinedStartFreshCallsNothing()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);

        await vm.StartFreshCommand.ExecuteAsync(null);

        (string title, _, string button) = Assert.Single(ui.Confirms);
        Assert.Equal("Start fresh?", title);
        Assert.Equal("Start fresh", button);
        Assert.Equal(0, service.StartFreshCalls);
        Assert.True(vm.IsStopped);
        Assert.False(vm.HasStopAction);
        Assert.True(vm.StartFreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task AConfirmedStartFreshThatSucceedsStartsInPlaceAndAsksForTheFirstToken()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigNewer);
        int started = 0;
        vm.Started += (_, _) => started++;
        service.StartFreshResult = new StartupResult(StartupFailure.None, null, false);

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.Equal(1, service.StartFreshCalls);
        Assert.False(vm.IsStopped);
        Assert.False(vm.CanStartFresh);
        Assert.False(vm.HasStopAction);
        Assert.False(vm.StartFreshCommand.CanExecute(null));
        Assert.Equal(1, started);
        Assert.Equal(new[] { TokenPromptReason.FirstRun }, ui.TokenPrompts);
    }

    [Fact]
    public async Task AConfirmedStartFreshThatSucceedsStartsInPlaceAndChecksTheDataset()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigKeyMismatch);
        int started = 0;
        vm.Started += (_, _) => started++;
        service.Dataset = SampleDataset;
        service.HasToken = true;
        service.StartFreshResult = new StartupResult(StartupFailure.None, SampleDataset, true);

        await vm.StartFreshCommand.ExecuteAsync(null);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.False(vm.IsStopped);
        Assert.Equal(1, started);
        Assert.Equal(1, service.CheckCalls);
        Assert.Empty(ui.TokenPrompts);
        vm.SearchText = "AS15169";
        Assert.True(vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStartFreshThatGivesTheSameFailureSaysItCouldntBeDoneAndKeepsTheButton()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        service.StartFreshResult = new StartupResult(StartupFailure.ConfigUnreadable, null, false);

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsStopped);
        Assert.Equal("The settings file couldn't be replaced. Please try again or contact the Infra Team.", vm.StopActionText);
        Assert.True(vm.HasStopAction);
        Assert.True(vm.IsStopActionError);
        Assert.True(vm.CanStartFresh);
        Assert.True(vm.StartFreshCommand.CanExecute(null));
        Assert.Equal(AppText.ConfigUnreadable, vm.StopText);
    }

    [Fact]
    public async Task AStartFreshThatGivesAnotherFailureShowsThatFailure()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        service.StartFreshResult = new StartupResult(StartupFailure.ConfigKeyUnavailable, null, false);

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.ConfigKeyUnavailableTitle, vm.StopTitle);
        Assert.Equal(AppText.ConfigKeyUnavailable, vm.StopText);
        Assert.False(vm.CanStartFresh);
        Assert.False(vm.CanStartWithoutOldExceptions);
        Assert.False(vm.StartFreshCommand.CanExecute(null));
        Assert.False(vm.HasStopAction);
    }

    [Fact]
    public async Task AStartFreshThatThrowsIsReportedAsCouldntBeDone()
    {
        var service = new FakeIpInfoService { StartFreshException = new InvalidOperationException("broken") };
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigNewer);

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.StartFreshFailed, vm.StopActionText);
        Assert.True(vm.IsStopActionError);
        Assert.Equal(AppText.ConfigNewer, vm.StopText);
        Assert.True(vm.StartFreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStartFreshConfirmationThatFailsToOpenShowsTheGenericLine()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        ui.ConfirmException = new InvalidOperationException("no dialog");

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.Equal(0, service.StartFreshCalls);
        Assert.True(vm.IsStopped);
        Assert.Equal(AppText.Generic, vm.StopActionText);
        Assert.True(vm.IsStopActionError);
        Assert.True(vm.StartFreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheStopLineSaysStartingFreshWhileTheServiceWorks()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        string? line = null;
        bool? error = null;
        bool? canRun = null;
        service.OnStartFresh = () =>
        {
            line = vm.StopActionText;
            error = vm.IsStopActionError;
            canRun = vm.StartFreshCommand.CanExecute(null);
        };
        service.StartFreshResult = new StartupResult(StartupFailure.ConfigUnreadable, null, false);

        await vm.StartFreshCommand.ExecuteAsync(null);

        Assert.Equal("Starting fresh" + Ellipsis, line);
        Assert.False(error);
        Assert.False(canRun);
    }

    [Fact]
    public async Task ADeclinedStartWithoutOldExceptionsCallsNothing()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StoppedAsync(service, ui, StartupFailure.ExceptionsImportUnreadable);

        await vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);

        (string title, _, string button) = Assert.Single(ui.Confirms);
        Assert.Equal("Start without the old exceptions?", title);
        Assert.Equal("Start without exceptions", button);
        Assert.Equal(0, service.StartWithoutOldExceptionsCalls);
        Assert.Equal(0, service.StartFreshCalls);
        Assert.True(vm.IsStopped);
    }

    [Fact]
    public async Task AConfirmedStartWithoutOldExceptionsThatSucceedsStartsInPlace()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ExceptionsImportUnreadable);
        int started = 0;
        vm.Started += (_, _) => started++;
        service.StartWithoutOldExceptionsResult = new StartupResult(StartupFailure.None, null, false);

        await vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(1, service.StartWithoutOldExceptionsCalls);
        Assert.Equal(0, service.StartFreshCalls);
        Assert.False(vm.IsStopped);
        Assert.False(vm.CanStartWithoutOldExceptions);
        Assert.Equal(1, started);
        Assert.Equal(new[] { TokenPromptReason.FirstRun }, ui.TokenPrompts);
    }

    [Fact]
    public async Task AStartWithoutOldExceptionsThatGivesTheSameFailureSaysItCouldntBeDone()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ExceptionsImportUnreadable);
        service.StartWithoutOldExceptionsResult = new StartupResult(StartupFailure.ExceptionsImportUnreadable, null, false);

        await vm.StartWithoutOldExceptionsCommand.ExecuteAsync(null);

        Assert.True(vm.IsStopped);
        Assert.Equal("exceptions.json couldn't be moved. Please try again or contact the Infra Team.", vm.StopActionText);
        Assert.True(vm.IsStopActionError);
        Assert.True(vm.StartWithoutOldExceptionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheConfirmationsNameTheKeptFiles()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = false };
        var fresh = await StoppedAsync(service, ui, StartupFailure.ConfigUnreadable);
        await fresh.StartFreshCommand.ExecuteAsync(null);
        var without = await StoppedAsync(service, ui, StartupFailure.ExceptionsImportUnreadable);
        await without.StartWithoutOldExceptionsCommand.ExecuteAsync(null);

        Assert.Equal(2, ui.Confirms.Count);
        string freshBody = ui.Confirms[0].Message;
        string withoutBody = ui.Confirms[1].Message;
        Assert.Equal(AppText.StartFreshBody, freshBody);
        Assert.Contains(AppPaths.UnreadableConfigFileName, freshBody);
        Assert.EndsWith("This cannot be undone.", freshBody);
        Assert.Contains(AppPaths.UnreadableExceptionsFileName, withoutBody);
        Assert.DoesNotContain("cannot be undone", withoutBody);
    }

    [Fact]
    public async Task AStartFreshAfterAMissingFileNamesNoKeptCopy()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigMissing);

        await vm.StartFreshCommand.ExecuteAsync(null);

        string body = Assert.Single(ui.Confirms).Message;
        Assert.Equal(AppText.StartFreshMissingBody, body);
        Assert.DoesNotContain(AppPaths.UnreadableConfigFileName, body);
        Assert.DoesNotContain("cannot be undone", body);
    }

    [Fact]
    public void TheStopTextsNameTheFilesFromTheirConstants()
    {
        Assert.Contains(AppPaths.ConfigFileName, AppText.ConfigUnreadable);
        Assert.Contains("settings.json" + AppPaths.MigratedSuffix, AppText.ConfigMissing);
        Assert.Contains("exceptions.json" + AppPaths.MigratedSuffix, AppText.ConfigMissing);
        Assert.Contains(AppPaths.UnreadableExceptionsFileName, AppText.ExceptionsImportUnreadable);
        Assert.Equal("Starting without the old exceptions" + Ellipsis, AppText.StartingWithoutOldExceptions);
    }

    // ----- first run -----

    [Fact]
    public async Task FirstRunWithNoTokenPromptsSavesTheTokenAndStartsAnUpdateWithoutAClick()
    {
        var service = WithoutDataset(hasToken: false);
        service.UpdateResult = new UpdateResult(UpdateFailure.None, false, SampleDataset);
        var ui = new FakeUi { TokenToReturn = "tok-123" };
        var vm = NewViewModel(service, ui);

        await vm.InitializeAsync();

        Assert.Equal(new[] { TokenPromptReason.FirstRun }, ui.TokenPrompts);
        Assert.Equal("tok-123", service.SavedToken);
        Assert.Equal(1, service.UpdateCalls);
        Assert.Equal(DatasetState.Current, vm.State);
        Assert.Equal("Dataset updated", vm.DatasetStatusText);
        Assert.Equal("Dataset up to date", vm.PillText);
        Assert.False(vm.IsPillWarning);
        Assert.Equal(100, vm.ProgressValue);
        Assert.Equal("Downloaded 8 October 2026" + Dot + "1,234,567 ranges", vm.DatasetFactsText);
    }

    [Fact]
    public async Task FirstRunWithTheTokenPromptCancelledLandsInNoDatasetAndSearchStaysOff()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { TokenToReturn = null };
        var vm = await StartAsync(service, ui);

        Assert.Equal(DatasetState.NoDataset, vm.State);
        Assert.Equal("No dataset", vm.PillText);
        Assert.True(vm.IsPillWarning);
        Assert.Equal("An API token is needed to download the dataset. Choose Update dataset to enter one.", vm.DatasetDetailText);
        Assert.Equal("No dataset has been downloaded yet", vm.DatasetFactsText);

        vm.SearchText = "AS15169";
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.True(vm.UpdateDatasetCommand.CanExecute(null));

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal(2, ui.TokenPrompts.Count);
        Assert.Equal(0, service.UpdateCalls);
    }

    // ----- start-up check -----

    [Fact]
    public async Task StartUpWithADatabaseOnlyChecksAndSearchWorksWhileTheCheckIsPending()
    {
        var gate = new TaskCompletionSource();
        var service = WithDataset();
        service.CheckGate = gate;
        var vm = NewViewModel(service, new FakeUi());

        await vm.InitializeAsync();

        Task check = vm.PendingCheck!;
        Assert.False(check.IsCompleted);
        Assert.Equal("Checking for updates" + Ellipsis, vm.PillText);
        Assert.Equal("Checking for updates" + Ellipsis, vm.DatasetStatusText);
        Assert.Equal("You can search while this runs.", vm.DatasetDetailText);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsProgressIndeterminate);
        Assert.Equal(0, service.UpdateCalls);

        vm.SearchText = "AS15169";
        Assert.True(vm.SearchCommand.CanExecute(null));

        gate.SetResult();
        await check;
        Assert.Equal(DatasetState.Current, vm.State);
    }

    [Fact]
    public async Task AnAvailableUpdateShowsTheWarningPillAndAnUpToDateDatabaseDoesNot()
    {
        var available = WithDataset();
        available.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable);
        var availableVm = await StartAsync(available, new FakeUi());

        Assert.Equal("Update available", availableVm.PillText);
        Assert.True(availableVm.IsPillWarning);
        Assert.Equal("A newer dataset is available. Choose Update dataset to download it.", availableVm.DatasetDetailText);
        Assert.Equal(DatasetState.UpdateAvailable, availableVm.State);

        var current = WithDataset();
        current.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        var currentVm = await StartAsync(current, new FakeUi());

        Assert.Equal("Dataset up to date", currentVm.PillText);
        Assert.False(currentVm.IsPillWarning);
    }

    [Fact]
    public async Task EachCheckFailureShowsItsExactDetailAndTheRateLimitUsesTheLimitAndTheRetryTime()
    {
        var cases = new (UpdateCheckResult Result, string Expected)[]
        {
            (new UpdateCheckResult(UpdateCheckOutcome.NoToken),
                "No API token could be read. For updates, choose Reset token in Settings."),
            (new UpdateCheckResult(UpdateCheckOutcome.TokenRejected),
                "The API token wasn't accepted. Choose Reset token in Settings."),
            (new UpdateCheckResult(UpdateCheckOutcome.RateLimited),
                "IPinfo allows 10 downloads a day and the limit has been reached. Please try again later."),
            (new UpdateCheckResult(UpdateCheckOutcome.RateLimited, TimeSpan.FromSeconds(60)),
                "IPinfo allows 10 downloads a day and the limit has been reached. Please try again in about 1 minute."),
            (new UpdateCheckResult(UpdateCheckOutcome.RateLimited, TimeSpan.FromSeconds(90)),
                "IPinfo allows 10 downloads a day and the limit has been reached. Please try again in about 2 minutes."),
            (new UpdateCheckResult(UpdateCheckOutcome.Offline),
                "The update check couldn't be completed. Check the network connection, then choose Update dataset."),
            (new UpdateCheckResult(UpdateCheckOutcome.ServiceError),
                "The update couldn't be completed. Please try again or contact the Infra Team."),
        };

        foreach ((UpdateCheckResult result, string expected) in cases)
        {
            var service = WithDataset();
            service.CheckResult = result;
            var vm = await StartAsync(service, new FakeUi());

            Assert.Equal("Update check failed", vm.PillText);
            Assert.True(vm.IsPillWarning);
            Assert.Equal(DatasetState.CheckFailed, vm.State);
            Assert.Equal(expected, vm.DatasetDetailText);
        }
    }

    [Fact]
    public async Task ADatabaseWithNoTokenIsNotCheckedAndSaysSo()
    {
        var service = WithDataset(hasToken: false);
        var vm = await StartAsync(service, new FakeUi());

        Assert.Equal(0, service.CheckCalls);
        Assert.Equal("No API token could be read. For updates, choose Reset token in Settings.", vm.DatasetDetailText);
        Assert.Equal("Update check failed", vm.PillText);
    }

    [Fact]
    public async Task AThrowingCheckShowsTheGenericServiceErrorAndNoExceptionText()
    {
        var service = WithDataset();
        service.CheckException = new InvalidOperationException("secret detail");
        var vm = await StartAsync(service, new FakeUi());

        Assert.Equal("The update couldn't be completed. Please try again or contact the Infra Team.", vm.DatasetDetailText);
        Assert.DoesNotContain("secret", vm.DatasetDetailText, StringComparison.Ordinal);
    }

    // ----- updates -----

    [Fact]
    public async Task AnUpdateSetsBusyGatesTheCommandsAndReportsEachPhase()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        vm.SearchText = "AS15169";

        Task run = vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await vm.ResetTokenAsync());
        Assert.True(vm.CancelUpdateCommand.CanExecute(null));
        Assert.Equal("Updating" + Ellipsis, vm.PillText);

        IProgress<UpdateProgress> progress = service.LastProgress!;

        progress.Report(new UpdateProgress(UpdatePhase.Downloading, 5 * Megabyte, 10 * Megabyte));
        Assert.Equal("Downloading the dataset" + Ellipsis, vm.DatasetStatusText);
        Assert.Equal("5.0 MB of 10.0 MB", vm.DatasetDetailText);
        Assert.Equal(50, vm.ProgressValue);
        Assert.False(vm.IsProgressIndeterminate);

        progress.Report(new UpdateProgress(UpdatePhase.Downloading, 3 * Megabyte, 0));
        Assert.Equal("3.0 MB downloaded", vm.DatasetDetailText);
        Assert.True(vm.IsProgressIndeterminate);

        progress.Report(new UpdateProgress(UpdatePhase.Importing, 25, 100));
        Assert.Equal("Importing the dataset" + Ellipsis, vm.DatasetStatusText);
        Assert.Equal("25% read", vm.DatasetDetailText);
        Assert.Equal(25, vm.ProgressValue);
        Assert.False(vm.IsProgressIndeterminate);

        progress.Report(new UpdateProgress(UpdatePhase.Indexing, 0, 0));
        Assert.Equal("Building the search index" + Ellipsis, vm.DatasetStatusText);
        Assert.Equal("This can take a minute.", vm.DatasetDetailText);
        Assert.True(vm.IsProgressIndeterminate);

        progress.Report(new UpdateProgress(UpdatePhase.Finishing, 0, 0));
        Assert.Equal("Finishing" + Ellipsis, vm.DatasetStatusText);
        Assert.True(vm.IsProgressIndeterminate);

        gate.SetResult();
        await run;

        Assert.False(vm.IsBusy);
        Assert.False(vm.CancelUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingAnUpdateShowsTheCancelledLineWithThePreviousPillAndStopsBusy()
    {
        var service = WithDataset();
        service.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable);
        var vm = await StartAsync(service, new FakeUi());
        service.UpdateGate = new TaskCompletionSource();

        Task run = vm.UpdateDatasetCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);

        vm.CancelUpdateCommand.Execute(null);
        await run;

        Assert.Equal("Update cancelled", vm.DatasetStatusText);
        Assert.Equal("The existing dataset is still in use.", vm.DatasetDetailText);
        Assert.Equal("Update available", vm.PillText);
        Assert.True(vm.IsPillWarning);
        Assert.False(vm.IsBusy);
        Assert.Equal(DatasetState.UpdateFailed, vm.State);
    }

    [Fact]
    public async Task EachUpdateFailureMapsToItsStringAndOnlyAnExistingDatasetAddsTheStillInUseLine()
    {
        var cases = new (UpdateFailure Failure, string Expected)[]
        {
            (UpdateFailure.TokenRejected, "The API token wasn't accepted. Choose Reset token in Settings."),
            (UpdateFailure.RateLimited, "IPinfo allows 10 downloads a day and the limit has been reached. Please try again later."),
            (UpdateFailure.Offline, "The dataset couldn't be downloaded. Check the network connection, then try again."),
            (UpdateFailure.ChecksumMismatch, "The download couldn't be verified, so it was discarded. Please try again."),
            (UpdateFailure.NotEnoughDiskSpace, "The dataset couldn't be downloaded because the disk is nearly full. Free some space, then try again."),
            (UpdateFailure.CorruptData, "The downloaded dataset couldn't be read, so it was discarded. Please try again."),
            (UpdateFailure.StorageError, "The dataset couldn't be saved. Please try again or contact the Infra Team."),
            (UpdateFailure.ServiceError, "The update couldn't be completed. Please try again or contact the Infra Team."),
        };

        foreach ((UpdateFailure failure, string expected) in cases)
        {
            // A database exists: the failure keeps it and says so
            var withDataset = WithDataset();
            withDataset.UpdateResult = new UpdateResult(failure, false, null);
            var vm = await StartAsync(withDataset, new FakeUi());
            await vm.UpdateDatasetCommand.ExecuteAsync(null);

            Assert.Equal("Update failed", vm.DatasetStatusText);
            Assert.Equal(expected + " The existing dataset is still in use.", vm.DatasetDetailText);
            Assert.Equal("Dataset up to date", vm.PillText);
            Assert.Equal(DatasetState.UpdateFailed, vm.State);

            // No database yet: the same failure, without the suffix
            var withoutDataset = WithoutDataset(hasToken: true);
            withoutDataset.UpdateResult = new UpdateResult(failure, false, null);
            var firstRun = await StartAsync(withoutDataset, new FakeUi());

            Assert.Equal(expected, firstRun.DatasetDetailText);
            Assert.Equal("No dataset", firstRun.PillText);
            Assert.Equal(DatasetState.NoDataset, firstRun.State);
        }
    }

    [Fact]
    public async Task ASuccessfulUpdateRefreshesTheFactsFromTheNewDatasetInfo()
    {
        var newer = new DatasetInfo(new DateOnly(2026, 9, 1), 42, "new", DateTimeOffset.UtcNow, true);
        var service = WithDataset();
        service.UpdateResult = new UpdateResult(UpdateFailure.None, false, newer);
        var vm = await StartAsync(service, new FakeUi());

        Assert.Equal("Downloaded 8 October 2026" + Dot + "1,234,567 ranges", vm.DatasetFactsText);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal("Downloaded 1 September 2026" + Dot + "42 ranges", vm.DatasetFactsText);
        Assert.Equal("Dataset updated", vm.DatasetStatusText);
    }

    [Fact]
    public async Task AnUpdateThatIsAlreadyCurrentSaysSoAndKeepsTheDatabase()
    {
        var service = WithDataset();
        service.UpdateResult = new UpdateResult(UpdateFailure.None, true, null);
        var vm = await StartAsync(service, new FakeUi());

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal("Dataset up to date", vm.PillText);
        Assert.Equal("No newer dataset is available.", vm.DatasetDetailText);
        Assert.Equal(DatasetState.Current, vm.State);
    }

    // ----- closing while busy -----

    [Fact]
    public async Task ConfirmCloseIsTrueWhenIdleAndCancelsAndWaitsWhenBusyAndConfirmed()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StartAsync(service, ui);

        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Empty(ui.Confirms);

        service.UpdateGate = new TaskCompletionSource();
        Task run = vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.True(await vm.ConfirmCloseAsync());
        await run;

        Assert.False(vm.IsBusy);
        (string title, _, string confirmText) = Assert.Single(ui.Confirms);
        Assert.Equal("Cancel the update?", title);
        Assert.Equal("Cancel and close", confirmText);
    }

    [Fact]
    public async Task ConfirmCloseIsFalseWhenDeclinedAndTheUpdateCarriesOn()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StartAsync(service, ui);
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        Task run = vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.False(await vm.ConfirmCloseAsync());
        Assert.True(vm.IsBusy);

        gate.SetResult();
        await run;
        Assert.False(vm.IsBusy);
    }

    // ----- search -----

    [Fact]
    public async Task ACappedAsnSearchShowsTheLimitAndTheTotalAndAnUncappedOneShowsTheCount()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        SearchQuery query = AsnQuery();
        service.SearchFunc = (_, _) => FoundResult(query, 300, 450);

        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(300, vm.Results.Count);
        Assert.True(vm.HasResults);
        Assert.Equal(300, service.LastSearchLimit);
        Assert.Equal("Showing 300 of 450 networks for AS15169" + Dash + "Google LLC", vm.ResultsSummaryText);

        service.SearchFunc = (_, _) => FoundResult(query, 1, 1);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("1 network for AS15169" + Dash + "Google LLC", vm.ResultsSummaryText);

        service.SearchFunc = (_, _) => FoundResult(query, 2, 2);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("2 networks for AS15169" + Dash + "Google LLC", vm.ResultsSummaryText);
    }

    [Fact]
    public async Task AnIpLookupSaysHowManyNetworksContainItAndDisablesExport()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        var query = new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, query, [Row(0)], 1);

        vm.SearchText = "8.8.8.8";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("1 network contains 8.8.8.8", vm.ResultsSummaryText);
        Assert.Equal("Export is available for ASN searches.", vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.CopyResultsCommand.CanExecute(null));
    }

    [Fact]
    public async Task InvalidInputNoResultsAndAFailedSearchSetTheEmptyTextAndClearTheRows()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Results.Count);

        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.InvalidInput, null, [], 0);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Empty(vm.Results);
        Assert.False(vm.HasResults);
        Assert.Equal("Enter an ASN such as AS15169, or an IPv4 or IPv6 address.", vm.ResultsEmptyText);

        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.NoResults, AsnQuery(1), [], 0);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("No networks found for AS1.", vm.ResultsEmptyText);

        var ip = new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.NoResults, ip, [], 0);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("No network found for 8.8.8.8.", vm.ResultsEmptyText);

        service.SearchException = new InvalidOperationException("boom detail");
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Empty(vm.Results);
        Assert.Equal("The search couldn't be completed. Please try again or contact the Infra Team.", vm.ResultsEmptyText);
        Assert.DoesNotContain("boom", vm.ResultsEmptyText, StringComparison.Ordinal);
    }

    // The search still out was asked for with the old settings, so a save runs it again, not the older one shown;
    // the held answer then comes back after the re-run's and is dropped
    [Fact]
    public async Task ASettingsSaveDuringASearchRerunsTheNewestSearchAndDropsTheHeldAnswer()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (input, _) => input == "AS1"
            ? new SearchResult(SearchOutcome.Found, AsnQuery(1),
                [new NetworkRecord("198.51.100.0/24", "US", "United States", "NA", "AS1", "First ISP", "first.example")], 1)
            : new SearchResult(SearchOutcome.Found, AsnQuery(2),
                [new NetworkRecord("192.0.2.0/24", "US", "United States", "NA", "AS2", "Second ISP", "second.example")], 1);

        // AS1 completes, so it is the search shown
        vm.SearchText = "AS1";
        await vm.SearchCommand.ExecuteAsync(null);

        // AS2 is held open, then saving settings re-runs AS2 and that newer search replaces the held one
        var gate = new TaskCompletionSource();
        service.SearchGate = gate;
        vm.SearchText = "AS2";
        Task held = vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, service.SearchCalls);
        Assert.Equal("Searching" + Ellipsis, vm.ResultsSummaryText);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal(3, service.SearchCalls);
        Assert.Equal("AS2", service.LastSearchInput);
        Assert.Equal("192.0.2.0/24", Assert.Single(vm.Results).NetworkText);
        Assert.Equal("1 network for AS2" + Dash + "Second ISP", vm.ResultsSummaryText);

        // With no search out, a save runs the one shown again
        gate.SetResult();
        await held;
        Assert.Equal("192.0.2.0/24", Assert.Single(vm.Results).NetworkText);
        vm.SearchText = "AS1";
        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal(4, service.SearchCalls);
        Assert.Equal("AS2", service.LastSearchInput);
        Assert.Equal("1 network for AS2" + Dash + "Second ISP", vm.ResultsSummaryText);
    }

    // ----- a search from another page -----

    [Fact]
    public async Task SearchForSetsTheBoxAndRunsTheSearch()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);

        Assert.True(await vm.SearchForAsync("AS15169"));

        Assert.Equal("AS15169", vm.SearchText);
        Assert.Equal(1, service.SearchCalls);
        Assert.Equal("AS15169", service.LastSearchInput);
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("2 networks for AS15169" + Dash + "Google LLC", vm.ResultsSummaryText);
    }

    [Fact]
    public async Task SearchForSupersedesASearchStillRunningAndDropsItsAnswer()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        service.SearchFunc = (input, _) => input == "AS1"
            ? new SearchResult(SearchOutcome.Found, AsnQuery(1),
                [new NetworkRecord("198.51.100.0/24", "US", "United States", "NA", "AS1", "First ISP", "first.example")], 1)
            : new SearchResult(SearchOutcome.Found, AsnQuery(2),
                [new NetworkRecord("192.0.2.0/24", "US", "United States", "NA", "AS2", "Second ISP", "second.example")], 1);
        var gate = new TaskCompletionSource();
        service.SearchGate = gate;
        vm.SearchText = "AS1";
        Task held = vm.SearchCommand.ExecuteAsync(null);

        Assert.True(await vm.SearchForAsync("AS2"));
        Assert.Equal("1 network for AS2" + Dash + "Second ISP", vm.ResultsSummaryText);

        gate.SetResult();
        await held;
        Assert.Equal(2, service.SearchCalls);
        Assert.Equal("AS2", vm.SearchText);
        Assert.Equal("192.0.2.0/24", Assert.Single(vm.Results).NetworkText);
        Assert.Equal("1 network for AS2" + Dash + "Second ISP", vm.ResultsSummaryText);
    }

    // Refused: false, the fixed line, the box as it was and nothing searched
    private static void AssertSearchForRefused(bool searched, MainViewModel vm, FakeIpInfoService service, string line)
    {
        Assert.False(searched);
        Assert.Equal(0, service.SearchCalls);
        Assert.Equal("", vm.SearchText);
        Assert.Equal(line, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
    }

    [Fact]
    public async Task SearchForRunsNothingBeforeTheStart()
    {
        var service = WithDataset();
        var vm = NewViewModel(service, new FakeUi());

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);
    }

    [Fact]
    public async Task SearchForRunsNothingWhileTheStartStepRuns()
    {
        var service = WithDataset();
        var gate = new TaskCompletionSource();
        service.InitializeGate = gate;
        var vm = NewViewModel(service, new FakeUi());
        Task launch = vm.InitializeAsync();
        Assert.True(vm.IsStartStepRunning);

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);

        gate.SetResult();
        await launch;
        Assert.Equal(0, service.SearchCalls);
    }

    [Fact]
    public async Task SearchForRunsNothingWhileSomethingHoldsTheGate()
    {
        var service = WithDataset();
        var gate = new WorkGate();
        using var vm = await StartOnGateAsync(service, new FakeUi(), gate);
        using (gate.Hold())
        {
            AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);
        }

        Assert.True(await vm.SearchForAsync("AS15169"));
        Assert.Equal(1, service.SearchCalls);
    }

    [Fact]
    public async Task SearchForRunsNothingWhileAnUpdateRuns()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        Task update = vm.UpdateDatasetCommand.ExecuteAsync(null);

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);

        gate.SetResult();
        await update;
    }

    [Fact]
    public async Task SearchForRunsNothingWhenClosingAfterAStartStep()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        await vm.CloseAfterStartStepAsync();

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);
    }

    [Fact]
    public async Task SearchForRunsNothingAfterDispose()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        vm.Dispose();

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.SearchCantStart);
    }

    [Fact]
    public async Task SearchForRunsNothingWithNoDataset()
    {
        var service = WithoutDataset(hasToken: true);
        var vm = await StartAsync(service, new FakeUi());

        AssertSearchForRefused(await vm.SearchForAsync("AS15169"), vm, service, AppText.ResultsNoDataset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchForRunsNothingForABlankText(string text)
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        vm.SearchText = "AS15169";

        Assert.False(await vm.SearchForAsync(text));

        Assert.Equal(0, service.SearchCalls);
        Assert.Equal("AS15169", vm.SearchText);
        Assert.Equal(AppText.SearchCantStart, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
    }

    // ----- the dataset changing -----

    private static async Task<(MainViewModel Vm, List<string> Changes)> StartCountingChangesAsync(FakeIpInfoService service)
    {
        var vm = NewViewModel(service, new FakeUi());
        var changes = new List<string>();
        // The facts are already shown when the event is raised
        vm.DatasetChanged += (_, _) => changes.Add(vm.DatasetFactsText);
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return (vm, changes);
    }

    [Fact]
    public async Task DatasetChangedIsRaisedOnceWhenTheStartOpensADataset()
    {
        (_, List<string> changes) = await StartCountingChangesAsync(WithDataset());

        Assert.Equal(["Downloaded 8 October 2026" + Dot + "1,234,567 ranges"], changes);
    }

    [Fact]
    public async Task DatasetChangedIsNotRaisedWhenTheStartFindsNoDataset()
    {
        (_, List<string> changes) = await StartCountingChangesAsync(WithoutDataset(hasToken: true));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task DatasetChangedIsRaisedAfterAnUpdateInstallsADifferentDataset()
    {
        var newer = new DatasetInfo(new DateOnly(2026, 9, 1), 42, "new", DateTimeOffset.UtcNow, true);
        var service = WithDataset();
        service.UpdateResult = new UpdateResult(UpdateFailure.None, false, newer);
        (MainViewModel vm, List<string> changes) = await StartCountingChangesAsync(service);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal(2, changes.Count);
        Assert.Equal("Downloaded 1 September 2026" + Dot + "42 ranges", changes[1]);
    }

    [Fact]
    public async Task DatasetChangedIsNotRaisedAfterAnUpdateThatLeavesAnEqualDataset()
    {
        var service = WithDataset();
        // A new instance equal by value, as a second read of the same metadata gives
        service.UpdateResult = new UpdateResult(UpdateFailure.None, false, SampleDataset with { });
        (MainViewModel vm, List<string> changes) = await StartCountingChangesAsync(service);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.NotSame(SampleDataset, service.Dataset);
        Assert.Single(changes);
    }

    [Fact]
    public async Task DatasetChangedIsNotRaisedAfterAFailedOrCancelledUpdate()
    {
        var service = WithDataset();
        service.UpdateResult = new UpdateResult(UpdateFailure.Offline, false, null);
        (MainViewModel vm, List<string> changes) = await StartCountingChangesAsync(service);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);
        Assert.Single(changes);

        service.UpdateGate = new TaskCompletionSource();
        Task update = vm.UpdateDatasetCommand.ExecuteAsync(null);
        vm.CancelUpdateCommand.Execute(null);
        await update;

        Assert.Equal(AppText.UpdateCancelledTitle, vm.DatasetStatusText);
        Assert.Single(changes);
    }

    [Fact]
    public async Task DatasetChangedIsRaisedOnceWhenAFirstRunUpdateInstallsADataset()
    {
        var service = WithoutDataset(hasToken: false);
        service.UpdateResult = new UpdateResult(UpdateFailure.None, false, SampleDataset);
        var vm = NewViewModel(service, new FakeUi { TokenToReturn = "tok-123" });
        var changes = new List<string>();
        vm.DatasetChanged += (_, _) => changes.Add(vm.DatasetFactsText);

        await vm.InitializeAsync();

        Assert.Equal(1, service.UpdateCalls);
        Assert.Equal(["Downloaded 8 October 2026" + Dot + "1,234,567 ranges"], changes);
    }

    [Fact]
    public async Task DatasetChangedIsRaisedOnceWhenStartFreshOpensADataset()
    {
        var service = new FakeIpInfoService();
        var ui = new FakeUi { ConfirmResult = true };
        var vm = await StoppedAsync(service, ui, StartupFailure.ConfigKeyMismatch);
        var changes = new List<string>();
        vm.DatasetChanged += (_, _) => changes.Add(vm.DatasetFactsText);
        service.Dataset = SampleDataset;
        service.HasToken = true;
        service.StartFreshResult = new StartupResult(StartupFailure.None, SampleDataset, true);

        await vm.StartFreshCommand.ExecuteAsync(null);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.False(vm.IsStopped);
        Assert.Equal(["Downloaded 8 October 2026" + Dot + "1,234,567 ranges"], changes);
    }

    // ----- export -----

    [Fact]
    public async Task ExportWithNoLargeRangesShowsNoDialogAndExportsEverything()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportPlan = new ExportPlan(2, "Google LLC", []);
        service.ExportResult = new ExportResult(ExportFailure.None, ["a.txt", "b.txt", "c.txt"], 2, 0);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Empty(ui.LargeRangePrompts);
        Assert.Empty(service.LastIncluded!);
        Assert.Equal(1, service.ExportCalls);
        Assert.Equal("AS15169", service.LastExportQuery!.Text);
        Assert.Equal("Exported 2 networks to 3 files", vm.ResultsActionText);
    }

    [Fact]
    public async Task ExportWithLargeRangesPromptsWithEveryRangeUnticked()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportPlan = new ExportPlan(2, "Google LLC", [new LargeRange("10.0.0.0/8", false, 8, new BigInteger(16777216))]);
        service.ExportResult = new ExportResult(ExportFailure.None, ["a.txt"], 1, 1);
        ui.LargeRangeAnswer = prompt => new List<string> { prompt.Choices[0].Network };

        await vm.ExportCommand.ExecuteAsync(null);

        LargeRangePrompt prompt = Assert.Single(ui.LargeRangePrompts);
        Assert.Equal("Export AS15169?", prompt.Title);
        Assert.Equal("1 network is larger than the limits in Settings (IPv4 /16, IPv6 /32). It's left out of the export unless you tick it.", prompt.Message);
        Assert.Single(prompt.Choices);
        Assert.All(prompt.Choices, choice => Assert.False(choice.IsIncluded));
        Assert.Equal(new[] { "10.0.0.0/8" }, service.LastIncluded!.ToArray());
        Assert.Equal("Exported 1 network to 1 file" + Dash + "1 large range left out", vm.ResultsActionText);
    }

    [Fact]
    public async Task ACancelledLargeRangeDialogExportsNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi { LargeRangeAnswer = _ => null };
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportPlan = new ExportPlan(2, "Google LLC", [new LargeRange("10.0.0.0/8", false, 8, new BigInteger(16777216))]);

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(0, service.ExportCalls);
    }

    [Fact]
    public async Task ExportDoesNothingIfTheViewModelIsDisposedWhileTheLargeRangeDialogIsOpen()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportPlan = new ExportPlan(2, "Google LLC", [new LargeRange("10.0.0.0/8", false, 8, new BigInteger(16777216))]);
        ui.LargeRangeAnswer = prompt => new List<string> { prompt.Choices[0].Network };
        ui.OnDialogOpen = () => vm.Dispose();

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(0, service.ExportCalls);
    }

    [Fact]
    public async Task EachExportOutcomeShowsItsExactLine()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        var cases = new (ExportResult Result, string Expected)[]
        {
            (new ExportResult(ExportFailure.None, ["a"], 1, 0), "Exported 1 network to 1 file"),
            (new ExportResult(ExportFailure.None, ["a", "b", "c"], 5, 2), "Exported 5 networks to 3 files" + Dash + "2 large ranges left out"),
            (new ExportResult(ExportFailure.None, ["a"], 4, 1), "Exported 4 networks to 1 file" + Dash + "1 large range left out"),
            (new ExportResult(ExportFailure.None, [], 0, 3), "Nothing was exported" + Dash + "every network is a large range that was left out"),
            (new ExportResult(ExportFailure.NothingToExport, [], 0, 0), "Nothing was exported" + Dash + "every network is a large range that was left out"),
            (new ExportResult(ExportFailure.FolderUnavailable, [], 0, 0), "The export folder couldn't be used. Choose another folder in Settings."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0), "The export couldn't be written. Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.ExceptionsUnreadable, [], 0, 0), "The exceptions list couldn't be read, so no feed was written. Open Settings to fix it."),
            (new ExportResult(ExportFailure.None, ["a", "b"], 4, 0, 2, 0), "Exported 4 networks to 2 files" + Dash + "2 networks cut by exceptions"),
            (new ExportResult(ExportFailure.None, ["a", "b"], 4, 0, 0, 1), "Exported 4 networks to 2 files" + Dash + "1 network removed by exceptions"),
            (new ExportResult(ExportFailure.None, ["a", "b"], 4, 1, 1, 3),
             "Exported 4 networks to 2 files" + Dash + "1 large range left out" + Dash + "1 network cut and 3 removed by exceptions"),
            (new ExportResult(ExportFailure.None, ["/x/AS15169.txt"], 4, 0, 0, 2, ["/x/fortigate/AS15169_ipv6.txt"]),
             "Exported 4 networks to 1 file" + Dash + "2 networks removed by exceptions" + Dash + "the old AS15169_ipv6.txt was left as it was"),
            (new ExportResult(ExportFailure.None, ["/x/AS15169.txt"], 4, 0, 0, 2, ["/x/fortigate/AS15169_ipv4.txt", "/x/fortigate/AS15169_ipv6.txt"]),
             "Exported 4 networks to 1 file" + Dash + "2 networks removed by exceptions" + Dash
             + "the old AS15169_ipv4.txt and AS15169_ipv6.txt were left as they were"),
        };

        foreach ((ExportResult result, string expected) in cases)
        {
            service.ExportResult = result;
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Equal(expected, vm.ResultsActionText);
        }
    }

    // A written export says the Settings page's unsaved changes weren't used, the same with Fortigate on or off in the
    // saved settings
    [Fact]
    public async Task AnExportWhileTheSettingsPageHasUnsavedChangesSaysTheSavedSettingsWereUsed()
    {
        var service = WithDataset();
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: true);
        var vm = await StartAsync(service, new FakeUi(), unsaved: unsaved);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportResult = new ExportResult(ExportFailure.None, ["a", "b"], 4, 1, 1, 0);

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(
            "Exported 4 networks to 2 files" + Dash + "1 large range left out" + Dash + "1 network cut by exceptions"
            + AppText.UnsavedSettingsNote,
            vm.ResultsActionText);

        service.Settings = service.Settings with { FortigateEnabled = false };
        service.ExportResult = new ExportResult(ExportFailure.None, ["a"], 4, 0);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Exported 4 networks to 1 file" + AppText.UnsavedSettingsNote, vm.ResultsActionText);
        Assert.Equal(Dash + "unsaved changes on the Settings page weren't used", AppText.UnsavedSettingsNote);

        unsaved.HasUnsavedChanges = false;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Exported 4 networks to 1 file", vm.ResultsActionText);
    }

    // The page is read once, just before the export is written, so a save or an edit while it runs doesn't change the note
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheUnsavedNoteIsDecidedWhenTheExportStarts(bool unsavedAtStart)
    {
        var service = WithDataset();
        var unsaved = new FakeUnsavedSettings(hasUnsavedChanges: unsavedAtStart);
        var vm = await StartAsync(service, new FakeUi(), unsaved: unsaved);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ExportResult = new ExportResult(ExportFailure.None, ["a"], 4, 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ExportGate = release;

        Task export = vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(1, service.ExportCalls);
        unsaved.HasUnsavedChanges = !unsavedAtStart;
        release.SetResult();
        await export;

        Assert.Equal("Exported 4 networks to 1 file" + (unsavedAtStart ? AppText.UnsavedSettingsNote : ""),
            vm.ResultsActionText);
        Assert.Equal(1, unsaved.Reads);
    }

    [Fact]
    public async Task NoFailedOrEmptyExportGetsTheUnsavedNote()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi(), unsaved: new FakeUnsavedSettings(hasUnsavedChanges: true));
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        var cases = new (ExportResult Result, string Expected)[]
        {
            (new ExportResult(ExportFailure.None, [], 0, 3), AppText.NothingExported),
            (new ExportResult(ExportFailure.NothingToExport, [], 0, 0), AppText.NothingExported),
            (new ExportResult(ExportFailure.FolderUnavailable, [], 0, 0), AppText.ExportFolderUnavailable),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0), AppText.ExportWriteFailed),
            (new ExportResult(ExportFailure.ExceptionsUnreadable, [], 0, 0), AppText.ExceptionsUnreadable),
        };

        foreach ((ExportResult result, string expected) in cases)
        {
            service.ExportResult = result;
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Equal(expected, vm.ResultsActionText);
        }
    }

    // A failed write names the failed file first, a feed file with its folder (the report and the combined feed share
    // a name), then the files written before it and the old files left; the unsaved note only follows files that were
    // written
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedFileWriteNamesTheFileFirstAndTheFilesWrittenBeforeIt(bool unsavedEdits)
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi(), unsaved: new FakeUnsavedSettings(hasUnsavedChanges: unsavedEdits));
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        string folder = Path.Combine(Path.GetTempPath(), "exports");
        string report = Path.Combine(folder, "AS15169.txt");
        string feeds = Path.Combine(folder, "fortigate");
        string ipv4 = Path.Combine(feeds, "AS15169_ipv4.txt");
        string ipv6 = Path.Combine(feeds, "AS15169_ipv6.txt");
        string combined = Path.Combine(feeds, "AS15169.txt");
        string unsavedNote = unsavedEdits ? " Unsaved changes on the Settings page weren't used." : "";
        // An export folder named like the feed folder: the order, not the folder's name, tells the report from a feed
        string named = Path.Combine(Path.GetTempPath(), "fortigate");
        string namedReport = Path.Combine(named, "AS15169.txt");
        string namedCombined = Path.Combine(named, "fortigate", "AS15169.txt");

        var cases = new (ExportResult Result, string Expected)[]
        {
            // The combined feed first: only its folder tells it apart from the report
            (new ExportResult(ExportFailure.WriteFailed, [report], 0, 0, FailedFile: combined),
             Path.Combine("fortigate", "AS15169.txt") + " couldn't be written. AS15169.txt was written "
             + "before the failure." + unsavedNote + " Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [report, ipv4], 0, 0, FailedFile: ipv6),
             Path.Combine("fortigate", "AS15169_ipv6.txt") + " couldn't be written. AS15169.txt and "
             + Path.Combine("fortigate", "AS15169_ipv4.txt") + " were written before the failure." + unsavedNote
             + " Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0, FailedFile: report),
             "AS15169.txt couldn't be written. Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0, FailedFile: namedReport),
             "AS15169.txt couldn't be written. Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [namedReport], 0, 0, FailedFile: namedCombined),
             Path.Combine("fortigate", "AS15169.txt") + " couldn't be written. AS15169.txt was written "
             + "before the failure." + unsavedNote + " Please try again or contact the Infra Team."),
            // The old files left follow the files written before, a feed again with its folder, then the unsaved note
            (new ExportResult(ExportFailure.WriteFailed, [report, ipv4], 0, 0, OldFilesLeft: [ipv6], FailedFile: ipv6),
             Path.Combine("fortigate", "AS15169_ipv6.txt") + " couldn't be written. AS15169.txt and "
             + Path.Combine("fortigate", "AS15169_ipv4.txt") + " were written before the failure. The old "
             + Path.Combine("fortigate", "AS15169_ipv6.txt") + " was left as it was." + unsavedNote
             + " Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [report], 0, 0, OldFilesLeft: [ipv4, ipv6], FailedFile: ipv6),
             Path.Combine("fortigate", "AS15169_ipv6.txt") + " couldn't be written. AS15169.txt was written before the "
             + "failure. The old " + Path.Combine("fortigate", "AS15169_ipv4.txt") + " and "
             + Path.Combine("fortigate", "AS15169_ipv6.txt") + " were left as they were." + unsavedNote
             + " Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0, OldFilesLeft: [report], FailedFile: report),
             "AS15169.txt couldn't be written. The old AS15169.txt was left as it was. Please try again or contact the "
             + "Infra Team."),
            // A failed report names the old report and every old feed, a feed with its folder; three or more names
            // take commas and one "and"
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0, OldFilesLeft: [report, combined], FailedFile: report),
             "AS15169.txt couldn't be written. The old AS15169.txt and " + Path.Combine("fortigate", "AS15169.txt")
             + " were left as they were. Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0, OldFilesLeft: [report, ipv4, ipv6], FailedFile: report),
             "AS15169.txt couldn't be written. The old AS15169.txt, " + Path.Combine("fortigate", "AS15169_ipv4.txt")
             + " and " + Path.Combine("fortigate", "AS15169_ipv6.txt")
             + " were left as they were. Please try again or contact the Infra Team."),
            (new ExportResult(ExportFailure.WriteFailed, [], 0, 0), AppText.ExportWriteFailed),
        };

        foreach ((ExportResult result, string expected) in cases)
        {
            service.ExportResult = result;
            await vm.ExportCommand.ExecuteAsync(null);
            Assert.Equal(expected, vm.ResultsActionText);
        }
    }

    // ----- copy -----

    [Fact]
    public async Task CopySendsEveryResultToTheClipboardAndReportsTheCount()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        service.ResultsText = "Network\tCountry code\r\n10.0.0.0/16\tUS\r\n";

        await vm.CopyResultsCommand.ExecuteAsync(null);

        Assert.Equal(service.ResultsText, ui.ClipboardText);
        Assert.Equal("Copied 2 results", vm.ResultsActionText);
        Assert.Equal("AS15169", service.LastResultsQuery!.Text);
    }

    [Fact]
    public async Task AClipboardFailureBecomesTheCopyLine()
    {
        var service = WithDataset();
        var ui = new FakeUi { FailClipboard = true };
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        await vm.CopyResultsCommand.ExecuteAsync(null);

        Assert.Equal("The results couldn't be copied.", vm.ResultsActionText);
    }

    // ----- web pages -----

    private static SearchQuery Parsed(string text)
    {
        Assert.True(SearchQuery.TryParse(text, out SearchQuery? query));
        return query;
    }

    private static async Task<(MainViewModel Vm, FakeUi Ui)> SearchedAsync(string text, SearchOutcome outcome = SearchOutcome.Found)
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        SearchQuery query = Parsed(text);
        service.SearchFunc = (_, _) => outcome == SearchOutcome.Found
            ? new SearchResult(SearchOutcome.Found, query, [Row(0)], 1)
            : new SearchResult(outcome, outcome == SearchOutcome.NoResults ? query : null, [], 0);
        vm.SearchText = text;
        await vm.SearchCommand.ExecuteAsync(null);
        return (vm, ui);
    }

    [Fact]
    public async Task AnAsnSearchOpensItsIpinfoPageAndNotAbuseIpDb()
    {
        var (vm, ui) = await SearchedAsync("AS15169");

        Assert.True(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);

        Assert.Equal(["https://ipinfo.io/AS15169"], ui.OpenedPages.Select(page => page.Uri.AbsoluteUri));
        Assert.False(vm.HasResultsAction);
    }

    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData("2001:4860:0:0::8888", "2001:4860::8888")]
    [InlineData("::ffff:8.8.8.8", "8.8.8.8")]
    public async Task APublicAddressSearchOpensTheAddressOnBothSites(string searched, string text)
    {
        var (vm, ui) = await SearchedAsync(searched);

        Assert.True(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.True(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);

        Assert.Equal(["https://ipinfo.io/" + text, "https://www.abuseipdb.com/check/" + text],
            ui.OpenedPages.Select(page => page.Uri.AbsoluteUri));
        // A yes from the launcher means only that the browser was asked, so no line says opened
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    [InlineData("127.0.0.1")]
    public async Task AnAddressThatIsNotPublicOpensNothing(string searched)
    {
        var (vm, ui) = await SearchedAsync(searched);
        Assert.True(vm.HasResults);

        Assert.False(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);
        Assert.Empty(ui.OpenedPages);
    }

    [Theory]
    [InlineData("8.8.8.8", SearchOutcome.NoResults)]
    [InlineData("AS15169", SearchOutcome.NoResults)]
    [InlineData("8.8.8.8", SearchOutcome.InvalidInput)]
    [InlineData("2001:4860::8888", SearchOutcome.IPv6Disabled)]
    public async Task ASearchWithNoRowsOpensNothing(string searched, SearchOutcome outcome)
    {
        var (vm, ui) = await SearchedAsync(searched, outcome);
        Assert.False(vm.HasResults);

        Assert.False(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        Assert.Empty(ui.OpenedPages);
    }

    [Fact]
    public async Task ANewSearchTellsTheMenuItsPagesChanged()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        int ipinfoNotified = 0, abuseNotified = 0;
        vm.OpenInIpinfoCommand.CanExecuteChanged += (_, _) => ipinfoNotified++;
        vm.OpenInAbuseIpDbCommand.CanExecuteChanged += (_, _) => abuseNotified++;
        SearchQuery query = Parsed("8.8.8.8");
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, query, [Row(0)], 1);

        vm.SearchText = "8.8.8.8";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(ipinfoNotified > 0);
        Assert.True(abuseNotified > 0);
        Assert.True(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        // Then an ASN search: AbuseIPDB goes off again
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));
        Assert.True(vm.OpenInIpinfoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ALauncherFailureBecomesTheResultsLine()
    {
        var (vm, ui) = await SearchedAsync("8.8.8.8");
        ui.FailOpenWebPage = true;

        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);

        Assert.Equal(AppText.WebPageOpenFailed, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
        Assert.Empty(ui.OpenedPages);
    }

    [Fact]
    public async Task WhileASearchIsOutNeitherPageOpens()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        SearchQuery asn = Parsed("AS15169");
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, asn, [Row(0)], 1);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.OpenInIpinfoCommand.CanExecute(null));

        // The ASN's rows stay shown while the address search is out, so the menu opens neither search's page
        var gate = new TaskCompletionSource();
        service.SearchGate = gate;
        SearchQuery address = Parsed("8.8.8.8");
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, address, [Row(0)], 1);
        vm.SearchText = "8.8.8.8";
        Task held = vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.HasResults);
        Assert.False(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));
        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);
        Assert.Empty(ui.OpenedPages);

        // Once it answers, both follow the address search, whose rows are now the ones shown
        gate.SetResult();
        await held;
        Assert.True(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.True(vm.OpenInAbuseIpDbCommand.CanExecute(null));
        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        Assert.Equal(["https://ipinfo.io/8.8.8.8"], ui.OpenedPages.Select(page => page.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task AfterDisposeNeitherPageOpens()
    {
        var (vm, ui) = await SearchedAsync("8.8.8.8");
        Assert.True(vm.OpenInAbuseIpDbCommand.CanExecute(null));

        vm.Dispose();

        Assert.False(vm.OpenInIpinfoCommand.CanExecute(null));
        Assert.False(vm.OpenInAbuseIpDbCommand.CanExecute(null));
        await vm.OpenInIpinfoCommand.ExecuteAsync(null);
        await vm.OpenInAbuseIpDbCommand.ExecuteAsync(null);
        Assert.Empty(ui.OpenedPages);
    }

    [Fact]
    public void TheLookupHelpNamesTheRightClickMenu()
    {
        Assert.Contains(
            "Right-click a result, or click it with two fingers on a trackpad, to open the searched address or ASN on "
            + "ipinfo.io, or the address on AbuseIPDB's website.",
            AppText.HelpSearchText(),
            StringComparison.Ordinal);
    }

    // ----- settings -----

    [Fact]
    public async Task ApplySavedSettingsAsyncRefreshesTheFolderAndRerunsTheSearch()
    {
        var defaults = Path.Combine(Path.GetTempPath(), "default-exports");
        var folder = Path.Combine(Path.GetTempPath(), "chosen-exports");
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi(), defaults);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        int checks = service.CheckCalls;
        Assert.Equal(defaults, vm.ExportFolder);

        // Saved elsewhere (the Settings page): the view model only follows it
        service.Settings = new UserSettings { ExportFolder = folder };
        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.Equal(folder, vm.ExportFolder);
        Assert.Equal(2, service.SearchCalls);
        Assert.Equal("AS15169", service.LastSearchInput);
        Assert.Equal(checks, service.CheckCalls);

        await vm.ApplySavedSettingsAsync(ipv6Changed: true);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.Equal(checks + 1, service.CheckCalls);
        Assert.Equal(3, service.SearchCalls);
    }

    [Fact]
    public async Task ApplySavedSettingsAsyncDoesNothingBeforeTheStartOrAfterDispose()
    {
        var defaults = Path.Combine(Path.GetTempPath(), "default-exports");
        var folder = Path.Combine(Path.GetTempPath(), "chosen-exports");
        var service = WithDataset();
        var vm = NewViewModel(service, new FakeUi(), defaults);
        service.Settings = new UserSettings { ExportFolder = folder };

        await vm.ApplySavedSettingsAsync(ipv6Changed: true);
        Assert.Equal(defaults, vm.ExportFolder);
        Assert.Equal(0, service.CheckCalls);

        await vm.InitializeAsync();
        vm.Dispose();
        service.Settings = new UserSettings();
        await vm.ApplySavedSettingsAsync(ipv6Changed: true);
        Assert.Equal(folder, vm.ExportFolder);
    }

    [Fact]
    public async Task StartedIsRaisedOnceAfterALaunchAndTheExportFolderIsResolvedAgain()
    {
        var defaults = Path.Combine(Path.GetTempPath(), "default-exports");
        var folder = Path.Combine(Path.GetTempPath(), "chosen-exports");
        var service = WithDataset();
        var vm = NewViewModel(service, new FakeUi(), defaults);
        Assert.Equal(defaults, vm.ExportFolder);

        // The saved settings become readable only once the service has opened
        service.Settings = new UserSettings { ExportFolder = folder };
        int started = 0;
        string? folderWhenStarted = null;
        bool couldUpdateWhenStarted = false;
        vm.Started += (_, _) =>
        {
            started++;
            folderWhenStarted = vm.ExportFolder;
            couldUpdateWhenStarted = vm.UpdateDatasetCommand.CanExecute(null);
        };

        await vm.InitializeAsync();
        await vm.InitializeAsync();

        Assert.Equal(1, started);
        Assert.Equal(folder, folderWhenStarted);
        Assert.Equal(folder, vm.ExportFolder);
        Assert.True(couldUpdateWhenStarted);
    }

    [Fact]
    public async Task AStopAtStartUpNeverRaisesStarted()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.StorageUnavailable, null, false) };
        var vm = NewViewModel(service, new FakeUi());
        int started = 0;
        vm.Started += (_, _) => started++;

        await vm.InitializeAsync();

        Assert.Equal(0, started);
    }

    // ----- the results line -----

    // An ASN search on a started view model
    private static async Task<(MainViewModel Vm, FakeIpInfoService Service)> SearchedAsync()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        return (vm, service);
    }

    [Fact]
    public async Task ASearchAgainClearsTheLine()
    {
        (MainViewModel vm, FakeIpInfoService service) = await SearchedAsync();
        service.ExportResult = new ExportResult(ExportFailure.ExceptionsUnreadable, [], 0, 0);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(AppText.ExceptionsUnreadable, vm.ResultsActionText);

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);

        // An export result is cleared by the next search too
        service.ExportResult = new ExportResult(ExportFailure.None, ["a"], 1, 0);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal("Exported 1 network to 1 file", vm.ResultsActionText);
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.NoResults, AsnQuery(64500), [], 0);
        vm.SearchText = "AS64500";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
    }

    // A dialog or confirmation that fails to show says so in the results line, and the line is visible
    [Fact]
    public async Task ADialogThatThrowsShowsTheGenericLine()
    {
        var service = WithDataset(hasToken: false);
        var ui = new FakeUi
        {
            TokenToReturn = "tok-123",
            OnDialogOpen = () => throw new InvalidOperationException("no dialog"),
        };
        var vm = await StartAsync(service, ui);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal(new[] { TokenPromptReason.FirstRun }, ui.TokenPrompts);
        Assert.Equal(0, service.SaveTokenCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.Generic, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
    }

    [Fact]
    public async Task AConfirmationThatThrowsShowsTheGenericLine()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        Task run = vm.UpdateDatasetCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        ui.OnDialogOpen = () => throw new InvalidOperationException("no dialog");

        // The close-while-busy confirmation fails to show: the window stays open and the line says so
        Assert.False(await vm.ConfirmCloseAsync());

        Assert.Single(ui.Confirms);
        Assert.True(vm.IsBusy);
        Assert.Equal(AppText.Generic, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);

        gate.SetResult();
        await run;
    }

    [Fact]
    public async Task ATokenThatCannotBeSavedBeforeAnUpdateShowsItsLine()
    {
        var service = WithDataset(hasToken: false);
        service.SaveTokenResult = false;
        var ui = new FakeUi { TokenToReturn = "tok-123" };
        var vm = await StartAsync(service, ui);

        await vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.Equal(new[] { TokenPromptReason.FirstRun }, ui.TokenPrompts);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(AppText.TokenSaveFailed, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
    }

    // ----- IPv6 turned off -----

    [Fact]
    public async Task AnIPv6AddressSearchWithIPv6OffShowsTheSettingsMessage()
    {
        var service = WithDataset();
        service.Settings = new UserSettings { IPv6Enabled = false };
        var vm = await StartAsync(service, new FakeUi());
        Assert.True(SearchQuery.TryParse("2001:db8::1", out SearchQuery? query));
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.IPv6Disabled, query, [], 0);
        vm.SearchText = "2001:db8::1";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("IPv6 is turned off in Settings.", vm.ResultsEmptyText);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task AnIPv4OnlyDatasetSaysSoInTheFactsLine()
    {
        var service = WithoutDataset(hasToken: true);
        service.Dataset = new DatasetInfo(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, false);
        service.StartupResult = new StartupResult(StartupFailure.None, service.Dataset, true);

        var vm = await StartAsync(service, new FakeUi());

        Assert.Equal("Downloaded 8 October 2026" + Dot + "1,234,567 ranges" + Dot + "IPv4 only", vm.DatasetFactsText);
    }

    [Fact]
    public async Task AnUpdateThatOnlyAddsIPv6ShowsTheRebuildDetail()
    {
        var service = WithDataset();
        service.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, null, AddsIPv6: true);

        var vm = await StartAsync(service, new FakeUi());

        Assert.Equal("Update available", vm.PillText);
        Assert.Equal("Update the dataset to add IPv6 networks.", vm.DatasetDetailText);
    }

    // The Settings page saves and says whether IPv6 changed; the view model only follows
    [Fact]
    public async Task SavingIPv6OffRerunsTheDatasetCheck()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        int checksBefore = service.CheckCalls;

        service.Settings = new UserSettings { IPv6Enabled = false };
        await vm.ApplySavedSettingsAsync(ipv6Changed: true);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.Equal(checksBefore + 1, service.CheckCalls);
    }

    [Fact]
    public async Task SavingSettingsWithoutChangingIPv6DoesNotRerunTheCheck()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        int checksBefore = service.CheckCalls;

        service.Settings = new UserSettings { IPv4MinPrefix = 12 };
        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.Equal(checksBefore, service.CheckCalls);
    }

    // ----- reset token -----

    [Fact]
    public async Task ADeclinedResetChangesNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StartAsync(service, ui);

        Assert.Equal(TokenResetOutcome.Declined, await vm.ResetTokenAsync());

        (string title, string message, string confirmText) = Assert.Single(ui.Confirms);
        Assert.Equal("Reset API token?", title);
        Assert.Equal("The saved API token is removed from this computer and you're asked for a new one. The dataset is kept. This cannot be undone.", message);
        Assert.Equal("Reset token", confirmText);
        Assert.Equal(0, service.ClearTokenCalls);
        Assert.Empty(ui.TokenPrompts);
    }

    [Fact]
    public async Task AConfirmedResetWithACancelledPromptLandsInTheNoTokenState()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = null };
        var vm = await StartAsync(service, ui);

        Assert.Equal(TokenResetOutcome.NoNewToken, await vm.ResetTokenAsync());

        Assert.Equal(1, service.ClearTokenCalls);
        Assert.Equal(new[] { TokenPromptReason.Reset }, ui.TokenPrompts);
        Assert.Equal(DatasetState.CheckFailed, vm.State);
        Assert.Equal("No API token could be read. For updates, choose Reset token in Settings.", vm.DatasetDetailText);
        Assert.Equal("Update check failed", vm.PillText);
    }

    [Fact]
    public async Task AConfirmedResetWithANewTokenSavesItAndChecksAgain()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var vm = await StartAsync(service, ui);
        Assert.Equal(1, service.CheckCalls);

        Assert.Equal(TokenResetOutcome.Saved, await vm.ResetTokenAsync());
        await vm.PendingCheck!;

        Assert.Equal("new-token", service.SavedToken);
        Assert.Equal(2, service.CheckCalls);
        Assert.Equal("Dataset up to date", vm.PillText);
        Assert.False(vm.HasResultsAction);
    }

    [Fact]
    public async Task AResetWhoseClearFailsIsClearFailedAndLeavesTheResultsLineAlone()
    {
        var service = WithDataset();
        service.ClearTokenResult = false;
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var vm = await StartAsync(service, ui);

        Assert.Equal(TokenResetOutcome.ClearFailed, await vm.ResetTokenAsync());

        Assert.Equal(1, service.ClearTokenCalls);
        Assert.Empty(ui.TokenPrompts);
        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
        Assert.True(service.HasToken);
    }

    [Fact]
    public async Task EveryResetOutcomeWritesNoResultsLine()
    {
        // Busy: nothing has started yet
        var unstarted = NewViewModel(WithDataset(), new FakeUi());
        Assert.Equal(TokenResetOutcome.Busy, await unstarted.ResetTokenAsync());

        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, OnDialogOpen = () => throw new InvalidOperationException("no dialog") };
        var vm = await StartAsync(service, ui);
        Assert.Equal(TokenResetOutcome.DialogFailed, await vm.ResetTokenAsync());
        Assert.Equal(0, service.ClearTokenCalls);
        Assert.False(vm.HasResultsAction);

        // The confirmation opens, the token prompt doesn't
        int opened = 0;
        ui.OnDialogOpen = () =>
        {
            if (++opened == 2)
                throw new InvalidOperationException("no dialog");
        };
        Assert.Equal(TokenResetOutcome.DialogFailed, await vm.ResetTokenAsync());
        Assert.Equal(1, service.ClearTokenCalls);
        Assert.Equal(DatasetState.CheckFailed, vm.State);
        Assert.False(vm.HasResultsAction);

        ui.OnDialogOpen = null;
        ui.TokenToReturn = "new-token";
        service.SaveTokenResult = false;
        Assert.Equal(TokenResetOutcome.SaveFailed, await vm.ResetTokenAsync());
        Assert.Equal(DatasetState.CheckFailed, vm.State);
        Assert.False(vm.HasResultsAction);
    }

    // A view model on a gate the test can see, started and settled
    private static async Task<MainViewModel> StartOnGateAsync(FakeIpInfoService service, FakeUi ui, WorkGate gate)
    {
        var vm = new MainViewModel(service, ui, null, null, gate)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        return vm;
    }

    // A fake's gate releases its caller inline, but the wait is bounded so a regression fails rather than hangs
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task AResetHoldsTheGateWhileItClearsAndWhileItSaves()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var gate = new WorkGate();
        using var vm = await StartOnGateAsync(service, ui, gate);
        vm.SearchText = "AS15169";
        Assert.True(vm.SearchCommand.CanExecute(null));
        var clear = new TaskCompletionSource();
        var save = new TaskCompletionSource();
        service.ClearTokenGate = clear;
        service.SaveTokenGate = save;

        Task<TokenResetOutcome> reset = vm.ResetTokenAsync();

        await WaitUntilAsync(() => service.ClearTokenCalls == 1);
        Assert.False(gate.CanStart);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));

        clear.SetResult();
        await WaitUntilAsync(() => service.SaveTokenCalls == 1);
        Assert.False(gate.CanStart);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));

        save.SetResult();
        Assert.Equal(TokenResetOutcome.Saved, await reset);
        Assert.True(gate.CanStart);
        Assert.True(vm.SearchCommand.CanExecute(null));
        await vm.PendingCheck!;
        Assert.Equal(2, service.CheckCalls);
    }

    [Fact]
    public async Task AWindowClosedWhileTheNewTokenIsSavedKeepsItAndStartsNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var vm = await StartOnGateAsync(service, ui, new WorkGate());
        int checks = service.CheckCalls;
        var save = new TaskCompletionSource();
        service.SaveTokenGate = save;

        Task<TokenResetOutcome> reset = vm.ResetTokenAsync();
        await WaitUntilAsync(() => service.SaveTokenCalls == 1);
        vm.Dispose();
        save.SetResult();

        Assert.Equal(TokenResetOutcome.Saved, await reset);
        Assert.Equal(checks, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
    }

    [Fact]
    public async Task AWindowClosedWhileTheTokenIsClearedOpensNoPrompt()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var vm = await StartOnGateAsync(service, ui, new WorkGate());
        int prompts = ui.TokenPrompts.Count;
        var clear = new TaskCompletionSource();
        service.ClearTokenGate = clear;

        Task<TokenResetOutcome> reset = vm.ResetTokenAsync();
        await WaitUntilAsync(() => service.ClearTokenCalls == 1);
        vm.Dispose();
        clear.SetResult();

        Assert.Equal(TokenResetOutcome.NoNewToken, await reset);
        Assert.Equal(prompts, ui.TokenPrompts.Count);
        Assert.Equal(0, service.SaveTokenCalls);
    }

    [Fact]
    public async Task AResetWithoutADatasetRunsTheUpdateOnceAfterTheSave()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { TokenToReturn = null };
        var gate = new WorkGate();
        using var vm = await StartOnGateAsync(service, ui, gate);
        Assert.Equal(0, service.UpdateCalls);
        ui.ConfirmResult = true;
        ui.TokenToReturn = "new-token";
        var save = new TaskCompletionSource();
        service.SaveTokenGate = save;

        Task<TokenResetOutcome> reset = vm.ResetTokenAsync();

        await WaitUntilAsync(() => service.SaveTokenCalls == 1);
        Assert.False(gate.CanStart);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.Equal(0, service.UpdateCalls);

        save.SetResult();
        Assert.Equal(TokenResetOutcome.Saved, await reset);
        Assert.Equal(1, service.UpdateCalls);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public async Task TheUpdatePromptHoldsTheGateUntilItsSaveReturns()
    {
        var service = WithoutDataset();
        var ui = new FakeUi { TokenToReturn = null };
        var gate = new WorkGate();
        using var vm = await StartOnGateAsync(service, ui, gate);
        ui.TokenToReturn = "new-token";
        var save = new TaskCompletionSource();
        service.SaveTokenGate = save;

        Task update = vm.UpdateDatasetCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => service.SaveTokenCalls == 1);
        Assert.False(gate.CanStart);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.Equal(TokenResetOutcome.Busy, await vm.ResetTokenAsync());
        Assert.Equal(0, service.UpdateCalls);

        save.SetResult();
        await update;
        Assert.Equal(1, service.UpdateCalls);
        Assert.True(gate.CanStart);
    }

    [Fact]
    public void AKeyPromptBuiltOnAnotherGateIsRefused()
    {
        var ui = new FakeUi();
        var abuse = new FakeAbuseIpDbService();
        var gate = new WorkGate();

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => new MainViewModel(WithDataset(), ui, null, abuse, gate, new AbuseKeyPrompt(ui, abuse, new WorkGate())));
        Assert.Equal("abuseKeys", refused.ParamName);

        using var vm = new MainViewModel(WithDataset(), ui, null, abuse, gate, new AbuseKeyPrompt(ui, abuse, gate));
    }

    // ----- dialogs lock the commands -----

    [Fact]
    public async Task CommandsAreDisabledWhileADialogIsOpen()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = null };
        var vm = await StartAsync(service, ui);
        vm.SearchText = "AS15169";
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        await vm.SearchCommand.ExecuteAsync(null);

        bool updateWhileOpen = true;
        bool searchWhileOpen = true;
        bool exportWhileOpen = true;
        bool copyWhileOpen = true;
        ui.OnDialogOpen = () =>
        {
            updateWhileOpen = vm.UpdateDatasetCommand.CanExecute(null);
            searchWhileOpen = vm.SearchCommand.CanExecute(null);
            exportWhileOpen = vm.ExportCommand.CanExecute(null);
            copyWhileOpen = vm.CopyResultsCommand.CanExecute(null);
        };

        // The reset's confirmation and token prompt are the dialogs
        Assert.Equal(TokenResetOutcome.NoNewToken, await vm.ResetTokenAsync());

        Assert.False(updateWhileOpen);
        Assert.False(searchWhileOpen);
        Assert.False(exportWhileOpen);
        Assert.False(copyWhileOpen);
        Assert.True(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.True(vm.SearchCommand.CanExecute(null));
    }

    // ----- wording -----

    // ----- Check AbuseIPDB -----

    // A made-up key: never a real one, and never in an assertion message
    private const string SentinelKey = "abuseipdb-sentinel-key-0123456789";

    private static readonly SearchQuery AddressQuery = new(SearchKind.IpAddress, "8.8.8.8", 0, new byte[] { 8, 8, 8, 8 });

    private static readonly AbuseCheck ReportedCheck =
        new(87, 12, 5, new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero), false, false, "US");

    private const string ReportedScoreText = "AbuseIPDB score 87 of 100: 12 reports from 5 users, last on 2026-10-18.";

    private static AbuseCheckResult Checked(AbuseCheck? check = null) => new(AbuseIpDbFailure.None, check ?? ReportedCheck, null);

    private static AbuseCheckResult Failed(AbuseIpDbFailure failure, DateTimeOffset? retryAt = null) => new(failure, null, retryAt);

    // A started view model whose last search was the address 8.8.8.8, found or with no network
    private static async Task<(MainViewModel Vm, FakeIpInfoService Service)> AddressSearchedAsync(
        FakeAbuseIpDbService? abuse, FakeUi? ui = null, bool found = true)
    {
        var service = WithDataset();
        service.SearchFunc = (_, _) => found
            ? new SearchResult(SearchOutcome.Found, AddressQuery, [Row(0)], 1)
            : new SearchResult(SearchOutcome.NoResults, AddressQuery, [], 0);
        var vm = await StartAsync(service, ui ?? new FakeUi(), abuse: abuse);
        vm.SearchText = "8.8.8.8";
        await vm.SearchCommand.ExecuteAsync(null);
        return (vm, service);
    }

    [Fact]
    public async Task CheckAbuseIsOffWithoutTheService()
    {
        var (vm, _) = await AddressSearchedAsync(abuse: null);

        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseIsOffAfterAnAsnSearch()
    {
        var service = WithDataset();
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        var vm = await StartAsync(service, new FakeUi(), abuse: new FakeAbuseIpDbService());
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAbuseIsOnAfterAnAddressSearchWithOrWithoutANetwork(bool found)
    {
        var (vm, _) = await AddressSearchedAsync(new FakeAbuseIpDbService(), found: found);

        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseIsOffAfterAnInvalidSearch()
    {
        var (vm, service) = await AddressSearchedAsync(new FakeAbuseIpDbService());
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.InvalidInput, null, [], 0);
        vm.SearchText = "not an address";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseIsOffWhileAnUpdateRuns()
    {
        var (vm, service) = await AddressSearchedAsync(new FakeAbuseIpDbService());
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        Task update = vm.UpdateDatasetCommand.ExecuteAsync(null);

        Assert.False(vm.CheckAbuseCommand.CanExecute(null));

        gate.SetResult();
        await update;
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseIsOffWhileASearchRuns()
    {
        var (vm, service) = await AddressSearchedAsync(new FakeAbuseIpDbService());
        var gate = new TaskCompletionSource();
        service.SearchGate = gate;
        Task search = vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.CheckAbuseCommand.CanExecute(null));

        gate.SetResult();
        await search;
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseShowsCheckingAndIsOffWhileACheckRuns()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, _) = await AddressSearchedAsync(abuse);
        var gate = new TaskCompletionSource();
        abuse.CheckGate = gate;
        abuse.CheckResults.Enqueue(Checked());

        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);
        Assert.Equal("Checking AbuseIPDB" + Ellipsis, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));

        gate.SetResult();
        await held;
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAbuseSendsTheSearchedAddressAndShowsTheScore()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        abuse.CheckResults.Enqueue(Checked());
        var ui = new FakeUi();
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Same(AddressQuery, Assert.Single(abuse.CheckQueries));
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.Empty(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NotPublic, AppText.AbuseNotSent)]
    [InlineData(AbuseIpDbFailure.KeyUnavailable, AppText.AbuseKeyUnavailable)]
    [InlineData(AbuseIpDbFailure.Offline, AppText.AbuseOffline)]
    [InlineData(AbuseIpDbFailure.ServiceError, AppText.AbuseCheckFailed)]
    [InlineData(AbuseIpDbFailure.BadResponse, AppText.AbuseAnswerUnreadable)]
    public async Task EachCheckFailureHasItsLineAndNoPrompt(AbuseIpDbFailure failure, string expected)
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        abuse.CheckResults.Enqueue(Failed(failure));
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.ResultsActionText);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.Empty(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
    }

    [Fact]
    public async Task ARateLimitSaysWhenItLiftsWhenAbuseIpDbSaid()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.RateLimited, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.RateLimited));
        var (vm, _) = await AddressSearchedAsync(abuse);

        await vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again after 2026-10-20 00:00 UTC.", vm.ResultsActionText);

        await vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal("AbuseIPDB's daily limit has been reached. Please try again later.", vm.ResultsActionText);
    }

    [Fact]
    public async Task AnUnexpectedCheckFailureShowsTheCheckFailedLineWithoutItsText()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true, CheckException = new InvalidOperationException("boom") };
        var (vm, _) = await AddressSearchedAsync(abuse);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseCheckFailed, vm.ResultsActionText);
        Assert.DoesNotContain("boom", vm.ResultsActionText, StringComparison.Ordinal);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NoKey, AbuseKeyAsk.Missing)]
    [InlineData(AbuseIpDbFailure.KeyRejected, AbuseKeyAsk.Rejected)]
    public async Task AMissingOrRefusedKeyAsksOnceSavesTheAnswerAndChecksAgain(AbuseIpDbFailure failure, AbuseKeyAsk asked)
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(failure));
        abuse.CheckResults.Enqueue(Checked());
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal([asked], ui.AbuseKeyPrompts);
        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Equal(2, abuse.CheckCalls);
        Assert.All(abuse.CheckQueries, query => Assert.Same(AddressQuery, query));
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.DoesNotContain(SentinelKey, vm.ResultsActionText, StringComparison.Ordinal);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NoKey, null, AppText.AbuseKeyCancelled)]
    [InlineData(AbuseIpDbFailure.NoKey, "   ", AppText.AbuseKeyCancelled)]
    [InlineData(AbuseIpDbFailure.KeyRejected, null, AppText.AbuseKeyReplaceCancelled)]
    [InlineData(AbuseIpDbFailure.KeyRejected, "", AppText.AbuseKeyReplaceCancelled)]
    public async Task ACancelledOrBlankKeySavesNothingAndChecksNoMore(AbuseIpDbFailure failure, string? answer, string expected)
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(failure));
        abuse.CheckResults.Enqueue(Checked());
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(answer);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Single(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.Equal(expected, vm.ResultsActionText);
    }

    [Theory]
    [InlineData(AbuseKeySave.NotAKey, AppText.AbuseKeyNotAKey)]
    [InlineData(AbuseKeySave.Failed, AppText.AbuseKeySaveFailed)]
    public async Task AKeyThatIsNotSavedChecksNoMore(AbuseKeySave save, string expected)
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
        abuse.CheckResults.Enqueue(Checked());
        abuse.SaveResults.Enqueue(save);
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.Equal(expected, vm.ResultsActionText);
        Assert.False(abuse.HasKey);
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NoKey, AppText.AbuseKeyUnreadable)]
    [InlineData(AbuseIpDbFailure.KeyRejected, AppText.AbuseKeyRejected)]
    public async Task TheCheckAfterASavedKeyNeverAsksAgain(AbuseIpDbFailure failure, string expected)
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(failure));
        abuse.CheckResults.Enqueue(Failed(failure));
        abuse.CheckResults.Enqueue(Checked());
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        ui.AbuseKeyAnswers.Enqueue(SentinelKey + "-second");
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Single(ui.AbuseKeyPrompts);
        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Equal(2, abuse.CheckCalls);
        Assert.Equal(expected, vm.ResultsActionText);
    }

    [Fact]
    public async Task AKeyDialogThatFailsToOpenGivesTheGenericLine()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
        var ui = new FakeUi { FailAbuseKeyPrompt = true };
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);

        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Single(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.Equal(AppText.Generic, vm.ResultsActionText);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckThatFinishesAfterANewerSearchIsDropped()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        var gate = new TaskCompletionSource();
        abuse.CheckGate = gate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);

        // The newer search is an ASN search, which clears the line: the held score must not come back over it
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ResultsActionText);

        gate.SetResult();
        await held;

        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Fact]
    public async Task ACheckHeldWhenTheWindowClosesEndsQuietly()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, _) = await AddressSearchedAsync(abuse);
        abuse.CheckGate = new TaskCompletionSource();
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        vm.Dispose();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(held.IsCompletedSuccessfully);
        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NoKey, AppText.AbuseKeyCancelled)]
    [InlineData(AbuseIpDbFailure.KeyRejected, AppText.AbuseKeyReplaceCancelled)]
    public async Task AKeyIsNotAskedForWhileAnotherDialogIsOpen(AbuseIpDbFailure failure, string expected)
    {
        var abuse = new FakeAbuseIpDbService();
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, service) = await AddressSearchedAsync(abuse, ui);
        var checkGate = new TaskCompletionSource();
        abuse.CheckGate = checkGate;
        abuse.CheckResults.Enqueue(Failed(failure));
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        // A token reset holds the gate while the check is out, and the check comes back while the reset still holds it
        var clearGate = new TaskCompletionSource();
        service.ClearTokenGate = clearGate;
        Task<TokenResetOutcome> reset = vm.ResetTokenAsync();
        Assert.Single(ui.Confirms);
        checkGate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(reset.IsCompleted);
        Assert.Empty(ui.AbuseKeyPrompts);
        Assert.Empty(abuse.SavedKeys);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.Equal(expected, vm.ResultsActionText);

        clearGate.SetResult();
        Assert.Equal(TokenResetOutcome.NoNewToken, await reset.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckHeldWhenANewerAddressSearchStartsIsCancelledAndANewCheckRuns()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        abuse.CheckGate = new TaskCompletionSource();
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        var newer = new SearchQuery(SearchKind.IpAddress, "1.1.1.1", 0, new byte[] { 1, 1, 1, 1 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, newer, [Row(1)], 1);
        vm.SearchText = "1.1.1.1";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(held.IsCompletedSuccessfully);
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);

        abuse.CheckResults.Enqueue(Checked(new AbuseCheck(3, 1, 1, new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), false, false, "AU")));
        await vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.Equal(2, abuse.CheckCalls);
        Assert.Equal("1.1.1.1", abuse.CheckQueries[1].Text);
        Assert.Equal("AbuseIPDB score 3 of 100: 1 report from 1 user, last on 2026-10-17.", vm.ResultsActionText);
    }

    [Fact]
    public async Task AWindowClosedWhileTheKeyDialogIsOpenKeepsTheTypedKeyAndChecksNoMore()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
        abuse.CheckResults.Enqueue(Checked());
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, _) = await AddressSearchedAsync(abuse, ui);
        ui.OnDialogOpen = vm.Dispose;

        await vm.CheckAbuseCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

        // The prompt saves what was typed, as it does for the Settings page; only the check after it is dropped
        Assert.Single(ui.AbuseKeyPrompts);
        Assert.Equal([SentinelKey], abuse.SavedKeys);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.NotEqual(ReportedScoreText, vm.ResultsActionText);
    }

    [Fact]
    public async Task ASearchWaitsWhileTheKeySavesAndTheSavedKeyIsCheckedWith()
    {
        var abuse = new FakeAbuseIpDbService();
        abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
        abuse.CheckResults.Enqueue(Checked());
        var saveGate = new TaskCompletionSource();
        abuse.SaveGate = saveGate;
        var ui = new FakeUi();
        ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        var (vm, service) = await AddressSearchedAsync(abuse, ui);
        int searches = service.SearchCalls;
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal([SentinelKey], abuse.SavedKeys);

        // The prompt holds the gate until the save ends, so no search can start meanwhile
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 1, 1);
        vm.SearchText = "AS15169";
        Assert.False(vm.SearchCommand.CanExecute(null));
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(searches, service.SearchCalls);

        saveGate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(abuse.HasKey);
        Assert.Equal(2, abuse.CheckCalls);
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.True(vm.SearchCommand.CanExecute(null));
    }

    // ----- Check AbuseIPDB and the settings rerun -----

    private static readonly SearchQuery IPv6AddressQuery = Parsed("2001:4860::8888");

    // A started view model whose last search found the IPv6 address 2001:4860::8888
    private static async Task<(MainViewModel Vm, FakeIpInfoService Service)> IPv6AddressSearchedAsync(FakeAbuseIpDbService abuse)
    {
        var service = WithDataset();
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, IPv6AddressQuery, [Row(0)], 1);
        var vm = await StartAsync(service, new FakeUi(), abuse: abuse);
        vm.SearchText = IPv6AddressQuery.Text;
        await vm.SearchCommand.ExecuteAsync(null);
        return (vm, service);
    }

    [Fact]
    public async Task ASettingsSaveWhileACheckIsOutLetsItsAnswerLand()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, _) = await AddressSearchedAsync(abuse);
        var gate = new TaskCompletionSource();
        abuse.CheckGate = gate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);

        gate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckAnswerThatLandsDuringTheSettingsRerunStays()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        var checkGate = new TaskCompletionSource();
        abuse.CheckGate = checkGate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        var searchGate = new TaskCompletionSource();
        service.SearchGate = searchGate;
        Task save = vm.ApplySavedSettingsAsync(ipv6Changed: false);
        checkGate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);

        searchGate.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASettingsSaveKeepsAnEarlierCheckAnswer(bool found)
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse, found: found);
        abuse.CheckResults.Enqueue(Checked());
        await vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.Equal(2, service.SearchCalls);
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    // Every line the check writes besides its answer, each reached as its own check test reaches it
    [Theory]
    [InlineData("DialogFailed", AppText.Generic)]
    [InlineData("NotAKey", AppText.AbuseKeyNotAKey)]
    [InlineData("SaveFailed", AppText.AbuseKeySaveFailed)]
    [InlineData("Cancelled", AppText.AbuseKeyCancelled)]
    [InlineData("ReplaceCancelled", AppText.AbuseKeyReplaceCancelled)]
    [InlineData("CheckFailed", AppText.AbuseCheckFailed)]
    public async Task ASettingsSaveKeepsEveryLineTheCheckWrote(string ending, string expected)
    {
        var abuse = new FakeAbuseIpDbService();
        var ui = new FakeUi();
        switch (ending)
        {
            case "DialogFailed":
                abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
                ui.FailAbuseKeyPrompt = true;
                ui.AbuseKeyAnswers.Enqueue(SentinelKey);
                break;
            case "NotAKey":
                abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
                abuse.SaveResults.Enqueue(AbuseKeySave.NotAKey);
                ui.AbuseKeyAnswers.Enqueue(SentinelKey);
                break;
            case "SaveFailed":
                abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
                abuse.SaveResults.Enqueue(AbuseKeySave.Failed);
                ui.AbuseKeyAnswers.Enqueue(SentinelKey);
                break;
            case "Cancelled":
                abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.NoKey));
                ui.AbuseKeyAnswers.Enqueue(null);
                break;
            case "ReplaceCancelled":
                abuse.CheckResults.Enqueue(Failed(AbuseIpDbFailure.KeyRejected));
                ui.AbuseKeyAnswers.Enqueue(null);
                break;
            default:
                abuse.HasKey = true;
                abuse.CheckException = new InvalidOperationException("boom");
                break;
        }

        var (vm, service) = await AddressSearchedAsync(abuse, ui);
        await vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal(expected, vm.ResultsActionText);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.Equal(2, service.SearchCalls);
        Assert.Equal(expected, vm.ResultsActionText);
        Assert.True(vm.HasResultsAction);
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Fact]
    public async Task ASecondSettingsSaveDuringAKeptRerunKeepsTheCheck()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        var checkGate = new TaskCompletionSource();
        abuse.CheckGate = checkGate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        var searchGate = new TaskCompletionSource();
        service.SearchGate = searchGate;
        Task first = vm.ApplySavedSettingsAsync(ipv6Changed: false);

        // The first rerun is still out, so the second save runs it again, keeping the check as the first did
        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);

        searchGate.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        checkGate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.Equal(3, service.SearchCalls);
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Fact]
    public async Task ASettingsSaveDuringAUserSearchThatCancelledACheckEndsOnTheSearchLine()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        abuse.CheckGate = new TaskCompletionSource();
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        // The user's own search of the same address calls the check off; the line stays until a result replaces it
        var searchGate = new TaskCompletionSource();
        service.SearchGate = searchGate;
        Task<bool> user = vm.SearchForAsync("8.8.8.8");
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        searchGate.SetResult();
        Assert.True(await user.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Fact]
    public async Task TurningIPv6OffWhileAnIPv6CheckIsOutCancelsItQuietly()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await IPv6AddressSearchedAsync(abuse);
        // Held without the token, so the answer still arrives after the rerun has called the check off
        var hold = new TaskCompletionSource();
        abuse.CheckHoldIgnoringCancel = hold;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        service.Settings = new UserSettings { IPv6Enabled = false };
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.IPv6Disabled, IPv6AddressQuery, [], 0);
        await vm.ApplySavedSettingsAsync(ipv6Changed: true);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.Equal("", vm.ResultsActionText);
        Assert.False(held.IsCompleted);

        hold.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(abuse.CheckResults);
        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckAnswerThatLandsDuringARerunThatEndsIPv6DisabledIsReplaced()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await IPv6AddressSearchedAsync(abuse);
        var checkGate = new TaskCompletionSource();
        abuse.CheckGate = checkGate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        service.Settings = new UserSettings { IPv6Enabled = false };
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.IPv6Disabled, IPv6AddressQuery, [], 0);
        var searchGate = new TaskCompletionSource();
        service.SearchGate = searchGate;
        Task save = vm.ApplySavedSettingsAsync(ipv6Changed: true);
        checkGate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReportedScoreText, vm.ResultsActionText);

        searchGate.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task AUserSearchDuringAKeptRerunCancelsTheCheckQuietly()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        // Held without the token, so the answer still arrives after the user's search has called the check off
        var hold = new TaskCompletionSource();
        abuse.CheckHoldIgnoringCancel = hold;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        var searchGate = new TaskCompletionSource();
        service.SearchGate = searchGate;
        Task save = vm.ApplySavedSettingsAsync(ipv6Changed: false);

        var newer = new SearchQuery(SearchKind.IpAddress, "1.1.1.1", 0, new byte[] { 1, 1, 1, 1 });
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, newer, [Row(1)], 1);
        Assert.True(await vm.SearchForAsync("1.1.1.1").WaitAsync(TimeSpan.FromSeconds(5)));

        searchGate.SetResult();
        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(held.IsCompleted);
        hold.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(abuse.CheckResults);
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
        Assert.Equal(AppText.IpContained(1, "1.1.1.1"), vm.ResultsSummaryText);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASettingsSaveReplacesALineTheCheckDidNotWrite()
    {
        var (vm, service) = await AddressSearchedAsync(new FakeAbuseIpDbService { HasKey = true });
        await vm.CopyResultsCommand.ExecuteAsync(null);
        Assert.Equal(AppText.Copied(1), vm.ResultsActionText);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);

        Assert.Equal(2, service.SearchCalls);
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
    }

    [Fact]
    public async Task ACopyDuringACheckGivesTheLineBackToTheRerunAndTheAnswerStillLands()
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, _) = await AddressSearchedAsync(abuse);
        var gate = new TaskCompletionSource();
        abuse.CheckGate = gate;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        Assert.True(vm.CopyResultsCommand.CanExecute(null));
        await vm.CopyResultsCommand.ExecuteAsync(null);
        Assert.Equal(AppText.Copied(1), vm.ResultsActionText);

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);

        gate.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ReportedScoreText, vm.ResultsActionText);
        Assert.Equal(1, abuse.CheckCalls);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public async Task AnAnswerThatArrivesAfterAUserSearchCalledTheCheckOffNeverLands(string address)
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        // Held without the token: a real check whose answer is already in returns it even after the cancel
        var hold = new TaskCompletionSource();
        abuse.CheckHoldIgnoringCancel = hold;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);
        Assert.Equal(AppText.CheckingAbuse, vm.ResultsActionText);

        SearchQuery searched = Parsed(address);
        service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Found, searched, [Row(1)], 1);
        Assert.True(await vm.SearchForAsync(address).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
        Assert.False(held.IsCompleted);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));

        hold.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(abuse.CheckResults);
        Assert.Equal(AppText.ExportIpNote, vm.ResultsActionText);
        Assert.Equal(AppText.IpContained(1, address), vm.ResultsSummaryText);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.True(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("thrown")]
    [InlineData("failed")]
    [InlineData("noDataset")]
    public async Task ASettingsRerunThatNoLongerShowsTheAddressCallsTheCheckOff(string ending)
    {
        var abuse = new FakeAbuseIpDbService { HasKey = true };
        var (vm, service) = await AddressSearchedAsync(abuse);
        var hold = new TaskCompletionSource();
        abuse.CheckHoldIgnoringCancel = hold;
        abuse.CheckResults.Enqueue(Checked());
        Task held = vm.CheckAbuseCommand.ExecuteAsync(null);

        // The view model's own catch gives a result with no query; the service's failures carry the query searched
        switch (ending)
        {
            case "thrown":
                service.SearchException = new IOException();
                break;
            case "failed":
                service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.Failed, AddressQuery, [], 0);
                break;
            default:
                service.SearchFunc = (_, _) => new SearchResult(SearchOutcome.NoDataset, AddressQuery, [], 0);
                break;
        }

        await vm.ApplySavedSettingsAsync(ipv6Changed: false);
        Assert.Equal("", vm.ResultsActionText);
        Assert.Equal(ending == "noDataset" ? AppText.ResultsNoDataset : AppText.SearchFailed, vm.ResultsEmptyText);
        Assert.False(held.IsCompleted);

        hold.SetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(abuse.CheckResults);
        Assert.Equal("", vm.ResultsActionText);
        Assert.False(vm.HasResultsAction);
        Assert.Equal(1, abuse.CheckCalls);
        Assert.False(vm.CheckAbuseCommand.CanExecute(null));
    }

    [Fact]
    public void NoUserVisibleStringUsesThreeDotsInsteadOfTheEllipsisCharacter()
    {
        List<FieldInfo> fields = typeof(AppText)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToList();
        Assert.NotEmpty(fields);

        List<string> offenders = fields
            .Where(field => ((string)field.GetRawConstantValue()!).Contains("...", StringComparison.Ordinal))
            .Select(field => field.Name)
            .ToList();

        Assert.Empty(offenders);
        Assert.DoesNotContain("...", AppText.Exported(2, 3), StringComparison.Ordinal);
        Assert.DoesNotContain("...", AppText.RateLimited(10, TimeSpan.FromMinutes(5)), StringComparison.Ordinal);
    }
}
