using System.Numerics;
using System.Reflection;
using PoorMansFeed.Core;

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
    private static MainViewModel NewViewModel(FakeIpInfoService service, FakeUi ui, string? defaultFolder = null) =>
        new(service, ui, defaultFolder)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };

    // Starts the view model and waits for its background check, so the state is settled before the test looks
    private static async Task<MainViewModel> StartAsync(FakeIpInfoService service, FakeUi ui, string? defaultFolder = null)
    {
        var vm = NewViewModel(service, ui, defaultFolder);
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

        Assert.True(vm.ShowStartupError);
        Assert.Equal("PoorMansFeed couldn't start. Its data folder couldn't be created. Please contact the Infra Team for assistance.", vm.StartupErrorText);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.False(vm.SearchCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.False(vm.CopyResultsCommand.CanExecute(null));
        Assert.False(vm.OpenSettingsCommand.CanExecute(null));
        Assert.False(vm.ResetTokenCommand.CanExecute(null));
        Assert.Equal(0, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Empty(ui.Confirms);
    }

    [Fact]
    public async Task ADatabaseNewerThanTheAppShowsItsOwnStartupError()
    {
        var service = new FakeIpInfoService { StartupResult = new StartupResult(StartupFailure.DatabaseNewer, null, false) };
        var vm = NewViewModel(service, new FakeUi());

        await vm.InitializeAsync();

        Assert.True(vm.ShowStartupError);
        Assert.Equal("PoorMansFeed couldn't start. The dataset on this computer was created by a newer version. Please install the latest version.", vm.StartupErrorText);
        Assert.False(vm.UpdateDatasetCommand.CanExecute(null));
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
                "An API token is needed to check for updates. Choose Reset API token to enter one."),
            (new UpdateCheckResult(UpdateCheckOutcome.TokenRejected),
                "The API token wasn't accepted. Choose Reset API token to enter a new one."),
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
        Assert.Equal("An API token is needed to check for updates. Choose Reset API token to enter one.", vm.DatasetDetailText);
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
        Assert.False(vm.OpenSettingsCommand.CanExecute(null));
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
            (UpdateFailure.TokenRejected, "The API token wasn't accepted. Choose Reset API token to enter a new one."),
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

    [Fact]
    public async Task ASearchThatFinishesAfterANewerOneStartedIsDropped()
    {
        var service = WithDataset();
        var ui = new FakeUi { SettingsToReturn = new UserSettings() };
        var vm = await StartAsync(service, ui);
        service.SearchFunc = (input, _) => input == "AS1"
            ? new SearchResult(SearchOutcome.Found, AsnQuery(1),
                [new NetworkRecord("198.51.100.0/24", "US", "United States", "NA", "AS1", "First ISP", "first.example")], 1)
            : new SearchResult(SearchOutcome.Found, AsnQuery(2),
                [new NetworkRecord("192.0.2.0/24", "US", "United States", "NA", "AS2", "Second ISP", "second.example")], 1);

        // AS1 completes, so it is the last completed search that saving settings re-runs
        vm.SearchText = "AS1";
        await vm.SearchCommand.ExecuteAsync(null);

        // AS2 is held open, then saving settings re-runs AS1 and that newer search replaces the held one
        var gate = new TaskCompletionSource();
        service.SearchGate = gate;
        vm.SearchText = "AS2";
        Task held = vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, service.SearchCalls);
        Assert.Equal("Searching" + Ellipsis, vm.ResultsSummaryText);

        await vm.OpenSettingsCommand.ExecuteAsync(null);
        Assert.Equal("AS1", service.LastSearchInput);
        Assert.Equal("198.51.100.0/24", Assert.Single(vm.Results).NetworkText);
        Assert.Equal("1 network for AS1" + Dash + "First ISP", vm.ResultsSummaryText);

        gate.SetResult();
        await held;

        Assert.Equal("198.51.100.0/24", Assert.Single(vm.Results).NetworkText);
        Assert.Equal("1 network for AS1" + Dash + "First ISP", vm.ResultsSummaryText);
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

    // ----- settings -----

    [Fact]
    public async Task SavedSettingsAreStoredRefreshTheFolderAndRerunTheSearch()
    {
        var defaults = Path.Combine(Path.GetTempPath(), "default-exports");
        var folder = Path.Combine(Path.GetTempPath(), "chosen-exports");
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui, defaults);
        service.SearchFunc = (_, _) => FoundResult(AsnQuery(), 2, 2);
        vm.SearchText = "AS15169";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(defaults, vm.ExportFolder);

        ui.SettingsToReturn = new UserSettings { ExportFolder = folder };
        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(defaults, ui.SettingsPrompts[0].DefaultFolder);
        Assert.Equal(folder, service.SavedSettings!.ExportFolder);
        Assert.Equal(folder, vm.ExportFolder);
        Assert.Equal(2, service.SearchCalls);
        Assert.Equal("AS15169", service.LastSearchInput);
    }

    [Fact]
    public async Task ACancelledSettingsDialogChangesNothing()
    {
        var defaults = Path.Combine(Path.GetTempPath(), "default-exports");
        var service = WithDataset();
        var ui = new FakeUi { SettingsToReturn = null };
        var vm = await StartAsync(service, ui, defaults);

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Single(ui.SettingsPrompts);
        Assert.Equal(0, service.SaveSettingsCalls);
        Assert.Equal(defaults, vm.ExportFolder);
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

    [Fact]
    public async Task SavingIPv6OffRerunsTheDatasetCheck()
    {
        var service = WithDataset();
        var ui = new FakeUi { SettingsToReturn = new UserSettings { IPv6Enabled = false } };
        var vm = await StartAsync(service, ui);
        int checksBefore = service.CheckCalls;

        await vm.OpenSettingsCommand.ExecuteAsync(null);
        if (vm.PendingCheck is { } check)
        {
            await check;
        }

        Assert.False(service.SavedSettings!.IPv6Enabled);
        Assert.Equal(checksBefore + 1, service.CheckCalls);
    }

    [Fact]
    public async Task SavingSettingsWithoutChangingIPv6DoesNotRerunTheCheck()
    {
        var service = WithDataset();
        var ui = new FakeUi { SettingsToReturn = new UserSettings { IPv4MinPrefix = 12 } };
        var vm = await StartAsync(service, ui);
        int checksBefore = service.CheckCalls;

        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.Equal(checksBefore, service.CheckCalls);
    }

    // ----- reset token -----

    [Fact]
    public async Task ADeclinedResetChangesNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = false };
        var vm = await StartAsync(service, ui);

        await vm.ResetTokenCommand.ExecuteAsync(null);

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

        await vm.ResetTokenCommand.ExecuteAsync(null);

        Assert.Equal(1, service.ClearTokenCalls);
        Assert.Equal(new[] { TokenPromptReason.Reset }, ui.TokenPrompts);
        Assert.Equal(DatasetState.CheckFailed, vm.State);
        Assert.Equal("An API token is needed to check for updates. Choose Reset API token to enter one.", vm.DatasetDetailText);
        Assert.Equal("Update check failed", vm.PillText);
    }

    [Fact]
    public async Task AConfirmedResetWithANewTokenSavesItAndChecksAgain()
    {
        var service = WithDataset();
        var ui = new FakeUi { ConfirmResult = true, TokenToReturn = "new-token" };
        var vm = await StartAsync(service, ui);
        Assert.Equal(1, service.CheckCalls);

        await vm.ResetTokenCommand.ExecuteAsync(null);
        await vm.PendingCheck!;

        Assert.Equal("new-token", service.SavedToken);
        Assert.Equal(2, service.CheckCalls);
        Assert.Equal("Dataset up to date", vm.PillText);
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
        bool settingsWhileOpen = true;
        bool exportWhileOpen = true;
        bool copyWhileOpen = true;
        ui.OnDialogOpen = () =>
        {
            updateWhileOpen = vm.UpdateDatasetCommand.CanExecute(null);
            searchWhileOpen = vm.SearchCommand.CanExecute(null);
            settingsWhileOpen = vm.OpenSettingsCommand.CanExecute(null);
            exportWhileOpen = vm.ExportCommand.CanExecute(null);
            copyWhileOpen = vm.CopyResultsCommand.CanExecute(null);
        };

        await vm.ResetTokenCommand.ExecuteAsync(null);

        Assert.False(updateWhileOpen);
        Assert.False(searchWhileOpen);
        Assert.False(settingsWhileOpen);
        Assert.False(exportWhileOpen);
        Assert.False(copyWhileOpen);
        Assert.True(vm.UpdateDatasetCommand.CanExecute(null));
        Assert.True(vm.SearchCommand.CanExecute(null));
    }

    // ----- wording -----

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
