using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

/// <summary>The dataset pill in the app bar, its drop-down and the update window it opens.</summary>
public sealed class DatasetPillTests
{
    private static readonly DatasetInfo SampleDataset = new(new DateOnly(2026, 10, 8), 1234567, "abc", DateTimeOffset.UtcNow, true);

    private static FakeIpInfoService WithDataset(UpdateCheckOutcome check = UpdateCheckOutcome.UpToDate) => new()
    {
        Dataset = SampleDataset,
        HasToken = true,
        StartupResult = new StartupResult(StartupFailure.None, SampleDataset, true),
        CheckResult = new UpdateCheckResult(check),
    };

    private static async Task<MainViewModel> StartAsync(FakeIpInfoService service, FakeUi ui, WorkGate? gate = null)
    {
        var vm = new MainViewModel(service, ui, null, null, gate)
        {
            ProgressFactory = handler => new ImmediateProgress<UpdateProgress>(handler),
        };
        await vm.InitializeAsync();
        if (vm.PendingCheck is { } check)
            await check;
        return vm;
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static Flyout OpenFlyout(MainWindow window)
    {
        Button pill = window.FindControl<Button>("DatasetPillButton")!;
        var flyout = Assert.IsType<Flyout>(pill.Flyout);
        flyout.ShowAt(pill);
        Settle(window);
        return flyout;
    }

    private static (bool Check, bool Update, bool UpdateShown, bool Progress, bool ProgressShown) Actions(MainWindow window)
    {
        Button check = window.FindControl<Button>("CheckForUpdatesButton")!;
        Button update = window.FindControl<Button>("UpdateButton")!;
        Button progress = window.FindControl<Button>("ShowProgressButton")!;
        return (check.IsEffectivelyEnabled, update.IsEffectivelyEnabled, update.IsVisible,
            progress.IsEffectivelyEnabled, progress.IsVisible);
    }

    // ----- the view model -----

    [Fact]
    public async Task OnlyAnUpToDateDatasetLightsTheDotGreen()
    {
        var current = await StartAsync(WithDataset(), new FakeUi());
        Assert.Equal((AppText.UpToDate, false, true), (current.PillText, current.IsPillWarning, current.IsPillSuccess));

        foreach (UpdateCheckOutcome outcome in new[] { UpdateCheckOutcome.UpdateAvailable, UpdateCheckOutcome.Offline })
        {
            var vm = await StartAsync(WithDataset(outcome), new FakeUi());
            Assert.True(vm.IsPillWarning, outcome.ToString());
            Assert.False(vm.IsPillSuccess, outcome.ToString());
        }

        var noDataset = await StartAsync(new FakeIpInfoService { HasToken = false }, new FakeUi());
        Assert.Equal((DatasetState.NoDataset, true, false), (noDataset.State, noDataset.IsPillWarning, noDataset.IsPillSuccess));

        // Checking is neutral: no claim yet either way
        var checking = WithDataset();
        var checkGate = new TaskCompletionSource();
        checking.CheckGate = checkGate;
        var vmChecking = new MainViewModel(checking, new FakeUi());
        await vmChecking.InitializeAsync();
        Assert.Equal((DatasetState.Checking, false, false), (vmChecking.State, vmChecking.IsPillWarning, vmChecking.IsPillSuccess));
        checkGate.SetResult();
        await vmChecking.PendingCheck!;
        Assert.True(vmChecking.IsPillSuccess);
    }

    [Fact]
    public async Task RefreshAllsPillOverACurrentDatasetIsNeutral()
    {
        var gate = new WorkGate();
        var service = WithDataset();
        var checkGate = new TaskCompletionSource();
        service.CheckGate = checkGate;
        var vm = new MainViewModel(service, new FakeUi(), null, null, gate);
        await vm.InitializeAsync();
        // The check is still out, so the refresh cancels it and names itself on the pill, with State Current
        gate.SetRefreshing(true);
        Assert.Equal((DatasetState.Current, AppText.DatasetRefreshTitle), (vm.State, vm.PillText));
        Assert.False(vm.IsPillSuccess);
        Assert.False(vm.IsPillWarning);
        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
        Assert.False(vm.UpdateDatasetInWindowCommand.CanExecute(null));
        checkGate.SetResult();
    }

    [Fact]
    public async Task ThePillOpensOnlyOnceStartedAndNotWhileStopped()
    {
        var service = WithDataset();
        var vm = new MainViewModel(service, new FakeUi());
        Assert.False(vm.CanOpenDatasetPill);
        await vm.InitializeAsync();
        Assert.True(vm.CanOpenDatasetPill);

        var stopped = new MainViewModel(new FakeIpInfoService
        {
            StartupResult = new StartupResult(StartupFailure.ConfigLocked, null, false),
        }, new FakeUi());
        await stopped.InitializeAsync();
        Assert.True(stopped.IsStopped);
        Assert.False(stopped.CanOpenDatasetPill);
        Assert.True(stopped.IsPillWarning);

        vm.Dispose();
        Assert.False(vm.CanOpenDatasetPill);
    }

    [Fact]
    public async Task CheckForUpdatesOnlyChecksAndOpensNothing()
    {
        var service = WithDataset();
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        int checks = service.CheckCalls;
        Assert.True(vm.CheckForUpdatesCommand.CanExecute(null));

        service.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        await vm.PendingCheck!;

        Assert.Equal(checks + 1, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
        Assert.Equal(0, ui.DatasetUpdateShows);
        Assert.Equal((DatasetState.UpdateAvailable, AppText.UpdateAvailable, true),
            (vm.State, vm.PillText, vm.IsPillWarning));
    }

    [Fact]
    public async Task CheckForUpdatesAsksForAMissingTokenAndChecksNothingWithoutOne()
    {
        var service = WithDataset();
        service.HasToken = false;
        var ui = new FakeUi { TokenToReturn = null };
        var vm = await StartAsync(service, ui);
        int checks = service.CheckCalls;

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal([TokenPromptReason.FirstRun], ui.TokenPrompts);
        Assert.Equal(checks, service.CheckCalls);
        Assert.Equal(0, service.UpdateCalls);
    }

    [Fact]
    public async Task UpdateDatasetInTheDropDownShowsTheWindowFirstThenUpdates()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        int updatesAtShow = -1;
        ui.OnDatasetUpdateShow = () => updatesAtShow = service.UpdateCalls;

        Assert.True(vm.UpdateDatasetInWindowCommand.CanExecute(null));
        await vm.UpdateDatasetInWindowCommand.ExecuteAsync(null);

        Assert.Equal(1, ui.DatasetUpdateShows);
        Assert.Equal(0, updatesAtShow);
        Assert.Equal(1, service.UpdateCalls);
    }

    [Fact]
    public async Task AnUpdateIsOfferedOnlyWhileTheDatasetIsNotCurrent()
    {
        var current = await StartAsync(WithDataset(), new FakeUi());
        Assert.False(current.UpdateDatasetInWindowCommand.CanExecute(null));

        foreach (UpdateCheckOutcome outcome in new[] { UpdateCheckOutcome.UpdateAvailable, UpdateCheckOutcome.Offline })
        {
            var vm = await StartAsync(WithDataset(outcome), new FakeUi());
            Assert.True(vm.UpdateDatasetInWindowCommand.CanExecute(null), outcome.ToString());
        }

        var noDataset = await StartAsync(new FakeIpInfoService(), new FakeUi());
        Assert.True(noDataset.UpdateDatasetInWindowCommand.CanExecute(null));
        Assert.False(noDataset.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePillShowsThePercentWhileTheBarIsDeterminateAndAnnouncesOnlyTheState()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        var gate = new TaskCompletionSource();
        service.UpdateGate = gate;
        Assert.False(vm.ShowUpdateWindowCommand.CanExecute(null));

        Task update = vm.UpdateDatasetCommand.ExecuteAsync(null);
        Assert.Equal(AppText.Updating, vm.PillText);
        Assert.True(vm.ShowUpdateWindowCommand.CanExecute(null));

        service.LastProgress!.Report(new UpdateProgress(UpdatePhase.Downloading, 42, 100));
        Assert.Equal("Updating 42%", vm.PillText);
        Assert.Equal(AppText.UpdatingPercent(42), vm.PillText);
        Assert.Equal((AppText.UpdatingWord, " 42%"), (vm.PillStateText, vm.PillProgressText));
        Assert.False(vm.IsPillWarning || vm.IsPillSuccess);

        service.LastProgress.Report(new UpdateProgress(UpdatePhase.Importing, 7, 10));
        Assert.Equal("Updating 70%", vm.PillText);
        Assert.Equal(AppText.UpdatingWord, vm.PillStateText);

        service.LastProgress.Report(new UpdateProgress(UpdatePhase.Indexing, 0, 0));
        Assert.Equal("Updating\u2026", vm.PillText);
        Assert.Equal(AppText.UpdatingWord, vm.PillStateText);

        vm.ShowUpdateWindowCommand.Execute(null);
        Assert.Equal(1, ui.DatasetUpdateShows);

        gate.SetResult();
        await update;
        Assert.False(vm.ShowUpdateWindowCommand.CanExecute(null));
        Assert.Equal((AppText.UpToDate, AppText.UpToDate, ""), (vm.PillText, vm.PillStateText, vm.PillProgressText));
    }

    [Fact]
    public async Task AFailedUpdateTurnsThePillYellowAndACancelledOneRestoresIt()
    {
        var failing = WithDataset();
        failing.UpdateResult = new UpdateResult(UpdateFailure.Offline, false, null);
        var failed = await StartAsync(failing, new FakeUi());
        await failed.UpdateDatasetCommand.ExecuteAsync(null);
        Assert.Equal((DatasetState.UpdateFailed, AppText.UpdateFailedPill, true, false),
            (failed.State, failed.PillText, failed.IsPillWarning, failed.IsPillSuccess));
        Assert.True(failed.UpdateDatasetInWindowCommand.CanExecute(null));

        var cancelling = WithDataset();
        var gate = new TaskCompletionSource();
        cancelling.UpdateGate = gate;
        var cancelled = await StartAsync(cancelling, new FakeUi());
        Task update = cancelled.UpdateDatasetCommand.ExecuteAsync(null);
        cancelled.CancelUpdateCommand.Execute(null);
        await update;
        Assert.Equal((DatasetState.UpdateFailed, AppText.UpToDate, false, true),
            (cancelled.State, cancelled.PillText, cancelled.IsPillWarning, cancelled.IsPillSuccess));
        Assert.Equal(AppText.UpdateCancelledTitle, cancelled.DatasetStatusText);
    }

    // ----- the window -----

    [AvaloniaFact]
    public async Task ThePillTakesItsToneAsClassesAndIsNamedForScreenReaders()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Button pill = window.FindControl<Button>("DatasetPillButton")!;
            Border chip = window.FindControl<Border>("DatasetPill")!;
            Assert.Contains("chip", pill.Classes);
            Assert.Equal(AppText.DatasetPillName, Avalonia.Automation.AutomationProperties.GetName(pill));
            Assert.Equal(AppText.UpToDate, Avalonia.Automation.AutomationProperties.GetHelpText(pill));
            Assert.Equal(AppText.DatasetPillTooltip, ToolTip.GetTip(pill));
            Assert.Contains("success", chip.Classes);
            Assert.DoesNotContain("warning", chip.Classes);
            Assert.True(pill.IsEffectivelyEnabled);

            // Only the state word is live; the percent beside it is not announced
            TextBlock state = window.FindControl<TextBlock>("PillState")!;
            TextBlock progress = window.FindControl<TextBlock>("PillProgress")!;
            Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(state));
            Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Off, Avalonia.Automation.AutomationProperties.GetLiveSetting(progress));
        }
        finally
        {
            window.Close();
        }

        var available = await StartAsync(WithDataset(UpdateCheckOutcome.UpdateAvailable), new FakeUi());
        var second = new MainWindow(available);
        second.Show();
        try
        {
            Settle(second);
            Border chip = second.FindControl<Border>("DatasetPill")!;
            Assert.Contains("warning", chip.Classes);
            Assert.DoesNotContain("success", chip.Classes);
        }
        finally
        {
            second.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheDropDownOffersTheActionsForEachState()
    {
        var service = WithDataset();
        var gate = new WorkGate();
        var vm = await StartAsync(service, new FakeUi(), gate);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Flyout flyout = OpenFlyout(window);
            Assert.True(flyout.IsOpen);
            Assert.Equal(AppText.UpToDate, window.FindControl<TextBlock>("DatasetStatus")!.Text);
            // Up to date: check only; the update keeps its place, off
            Assert.Equal((true, false, true, false, false), Actions(window));

            service.CheckResult = new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable);
            await vm.CheckForUpdatesCommand.ExecuteAsync(null);
            await vm.PendingCheck!;
            Settle(window);
            Assert.Equal((true, true, true, false, false), Actions(window));

            // Updating: Show progress takes the update's place
            var update = new TaskCompletionSource();
            service.UpdateGate = update;
            Task running = vm.UpdateDatasetCommand.ExecuteAsync(null);
            Settle(window);
            Assert.Equal((false, false, false, true, true), Actions(window));
            update.SetResult();
            await running;

            // Refresh all: read-only
            gate.SetRefreshing(true);
            Settle(window);
            Assert.Equal((false, false, true, false, false), Actions(window));
            gate.SetRefreshing(false);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EscapeClosesTheDropDownAndFocusGoesBackToThePill()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Settle(window);
            Button pill = window.FindControl<Button>("DatasetPillButton")!;
            pill.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            var flyout = Assert.IsType<Flyout>(pill.Flyout);
            Assert.True(flyout.IsOpen);

            window.FindControl<Button>("CheckForUpdatesButton")!.Focus();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(window);
            Assert.False(flyout.IsOpen);
            Assert.True(pill.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnActionInTheDropDownClosesIt()
    {
        var service = WithDataset();
        var vm = await StartAsync(service, new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            if (vm.PendingCheck is { } startUp)
                await startUp;
            int checks = service.CheckCalls;
            Flyout flyout = OpenFlyout(window);
            Button check = window.FindControl<Button>("CheckForUpdatesButton")!;
            check.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.False(flyout.IsOpen);
            if (vm.PendingCheck is { } pending)
                await pending;
            Assert.Equal(checks + 1, service.CheckCalls);
            Assert.Equal(0, service.UpdateCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheUpdateWindowIsOneWindowOwnedByTheMainWindow()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            window.ShowDatasetUpdateWindow();
            Settle(window);
            DatasetUpdateWindow first = window.UpdateWindow!;
            window.ShowDatasetUpdateWindow();
            Settle(window);
            Assert.Same(first, window.UpdateWindow);
            Assert.Single(window.OwnedWindows);
            Assert.Same(window, first.Owner);
            Assert.True(first.IsVisible);
            Assert.False(first.ShowInTaskbar);
            Assert.False(first.CanResize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheUpdateWindowHidesItAndTheUpdateGoesOn()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var vm = await StartAsync(service, new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var update = new TaskCompletionSource();
            service.UpdateGate = update;
            window.ShowDatasetUpdateWindow();
            Task running = vm.UpdateDatasetCommand.ExecuteAsync(null);
            Settle(window);
            DatasetUpdateWindow shown = window.UpdateWindow!;

            shown.Close();
            Settle(window);
            Assert.False(shown.IsVisible);
            Assert.True(vm.IsBusy);
            Assert.Same(shown, window.UpdateWindow);

            // Esc presses Close, which hides too
            window.ShowDatasetUpdateWindow();
            Settle(window);
            Assert.True(shown.IsVisible);
            Assert.Same(window, shown.Owner);
            shown.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(window);
            Assert.False(shown.IsVisible);
            Assert.True(vm.IsBusy);

            update.SetResult();
            await running;
            Assert.Equal(DatasetState.Current, vm.State);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task UpdateDatasetInTheDropDownOpensTheWindowAndStartsTheDownload()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Flyout flyout = OpenFlyout(window);
            Button update = window.FindControl<Button>("UpdateButton")!;
            update.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.False(flyout.IsOpen);
            if (vm.PendingUpdate is { } pending)
                await pending;

            Assert.Equal(1, ui.DatasetUpdateShows);
            Assert.Equal(1, service.UpdateCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ShowProgressInTheDropDownBringsTheWindowBackWhileTheUpdateRuns()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var ui = new FakeUi();
        var vm = await StartAsync(service, ui);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var update = new TaskCompletionSource();
            service.UpdateGate = update;
            Task running = vm.UpdateDatasetCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(vm.IsBusy);
            int shows = ui.DatasetUpdateShows;

            Flyout flyout = OpenFlyout(window);
            Button progress = window.FindControl<Button>("ShowProgressButton")!;
            Assert.True(progress.IsVisible);
            progress.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.False(flyout.IsOpen);
            Assert.Equal(shows + 1, ui.DatasetUpdateShows);
            Assert.Equal(1, service.UpdateCalls);

            update.SetResult();
            await running;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingTheMainWindowClosesAHiddenUpdateWindow()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        window.ShowDatasetUpdateWindow();
        Settle(window);
        DatasetUpdateWindow shown = window.UpdateWindow!;
        bool shownClosed = false;
        shown.Closed += (_, _) => shownClosed = true;
        shown.Close();
        Settle(window);
        Assert.False(shown.IsVisible);
        Assert.False(shownClosed);

        window.Close();
        Settle(window);

        Assert.True(shownClosed);
        Assert.Null(window.UpdateWindow);
    }

    [AvaloniaFact]
    public async Task TheUpdateWindowDoesNotHoldTheMainWindowsClose()
    {
        var vm = await StartAsync(WithDataset(), new FakeUi());
        var window = new MainWindow(vm);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.ShowDatasetUpdateWindow();
        Settle(window);
        DatasetUpdateWindow shown = window.UpdateWindow!;
        bool shownClosed = false;
        shown.Closed += (_, _) => shownClosed = true;

        window.Close();
        Settle(window);

        Assert.True(closed);
        Assert.True(shownClosed);
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.Undefined, false)]
    public void OnlyTheWindowsOwnCloseHidesIt(WindowCloseReason reason, bool hides) =>
        Assert.Equal(hides, DatasetUpdateWindow.HidesOnClose(reason));

    [AvaloniaFact]
    public async Task TheUpdateWindowsButtonsCancelRetryAndClose()
    {
        var service = WithDataset(UpdateCheckOutcome.UpdateAvailable);
        var vm = await StartAsync(service, new FakeUi());
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            window.ShowDatasetUpdateWindow();
            Settle(window);
            DatasetUpdateWindow shown = window.UpdateWindow!;
            Button cancel = shown.FindControl<Button>("CancelUpdateButton")!;
            Button retry = shown.FindControl<Button>("RetryUpdateButton")!;
            Button close = shown.FindControl<Button>("CloseButton")!;
            Assert.Same(vm.CancelUpdateCommand, cancel.Command);
            Assert.Same(vm.UpdateDatasetInWindowCommand, retry.Command);
            Assert.Contains("accent", retry.Classes);
            Assert.True(close.IsCancel);
            Assert.False(cancel.IsEffectivelyEnabled);
            Assert.True(retry.IsEffectivelyEnabled);
            Assert.Equal(AppText.UpdateAvailable, shown.FindControl<TextBlock>("UpdateStatus")!.Text);

            var update = new TaskCompletionSource();
            service.UpdateGate = update;
            Task running = vm.UpdateDatasetCommand.ExecuteAsync(null);
            Settle(window);
            Assert.True(cancel.IsEffectivelyEnabled);
            Assert.False(retry.IsEffectivelyEnabled);

            cancel.Focus();
            shown.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await running;
            Settle(window);
            Assert.False(vm.IsBusy);
            Assert.Equal(AppText.UpdateCancelledTitle, vm.DatasetStatusText);
            Assert.True(retry.IsEffectivelyEnabled);

            close.Focus();
            shown.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle(window);
            Assert.False(shown.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
