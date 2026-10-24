using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private const string SentinelKey = "sentinel-abuse-key-0123456789";
    private const int KeepDays = 30;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ipinfo-abuse-vm", Guid.NewGuid().ToString("N"));
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly FakeUi _ui = new();
    private readonly FakeAbuseIpDbService _abuse = new();
    private readonly WorkGate _gate = new();
    private readonly FakeTimeProvider _time = new(Now);

    public AbuseIpDbViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _ipinfo.DataFolder = Path.Combine(_folder, "data");
        _gate.SetStarted(true);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Exports => Path.Combine(_folder, "exports");

    private AbuseIpDbViewModel Create(IUnsavedSettings? unsaved = null, IWorkGate? gate = null) =>
        Immediate(new(_abuse, _ipinfo, _ui, gate ?? _gate, null, _time, Exports, unsaved));

    private AbuseIpDbViewModel CreateWithPrompt() =>
        Immediate(new(_abuse, _ipinfo, _ui, _gate, new AbuseKeyPrompt(_ui, _abuse, _gate), _time, Exports));

    // The default sink posts to the thread pool in a test, so a late report would race the assertions
    private static AbuseIpDbViewModel Immediate(AbuseIpDbViewModel vm)
    {
        vm.ProgressFactory = handler => new ImmediateProgress<GroupingProgress>(handler);
        return vm;
    }

    private static AbuseListStatus Status(AbuseListState state = AbuseListState.Usable, DateTimeOffset? last = null,
                                          DateTimeOffset? refusedUntil = null, int ipv4 = 120) =>
        new(state, ipv4, 4, last, last, new AbuseRateLimit(5, 3, Now.AddHours(16)), 2, refusedUntil);

    private static AbuseDownloadResult Downloaded(AbuseIpDbFailure failure = AbuseIpDbFailure.None,
                                                  DateTimeOffset? retryAt = null) =>
        new(failure, true, 10_000, 0, 50, 3, 0, Now, retryAt);

    private static AbuseFeedWriteResult Feed(AbuseFeedWriteFailure failure = AbuseFeedWriteFailure.None, int removals = 0) =>
        new(failure, ["AbuseIPDB_rolling_ipv4.txt"], 100, 2, 0, removals, []);

    private static readonly AbuseFeedWriteResult ThrownWrite = new(AbuseFeedWriteFailure.WriteFailed, [], 0, 0, 0, 0, []);

    private static IEnumerable<IAsyncRelayCommand> ActionCommands(AbuseIpDbViewModel vm) =>
        [vm.DownloadCommand, vm.ClearListCommand, vm.WriteFeedCommand, vm.EmptyFeedCommand];

    private static IEnumerable<IAsyncRelayCommand> AllCommands(AbuseIpDbViewModel vm) =>
        [.. ActionCommands(vm), vm.OpenFolderCommand];

    private string Fortigate(string root) => Path.Combine(root, ExportWriter.FortigateFolderName);

    // ---- construction and the unavailable page ----

    // The facts row's two lines and its whole line, from the same status read
    private static void AssertFacts(AbuseIpDbViewModel vm, AbuseListStatus status, int keepDays)
    {
        Assert.Equal(AppText.AbuseFactsHead(status, Now), vm.FactsHeadText);
        Assert.Equal(AppText.AbuseFactsDetail(status, keepDays, Now), vm.FactsDetailText);
        Assert.Equal(AppText.AbuseFacts(status, keepDays, Now), vm.FactsText);
    }

    // A line of its own: the head is the whole line and the detail is empty
    private static void AssertFactsLine(AbuseIpDbViewModel vm, string line)
    {
        Assert.Equal(line, vm.FactsHeadText);
        Assert.Equal("", vm.FactsDetailText);
        Assert.Equal(line, vm.FactsText);
    }

    [Fact]
    public async Task Unavailable_Page_Shows_Its_Line_Disables_Every_Command_And_Activates_Quietly()
    {
        using var vm = new AbuseIpDbViewModel(null, _ipinfo, _ui, _gate, null, _time, Exports);

        Assert.False(vm.IsAvailable);
        Assert.Equal(AppText.AbuseKeysUnavailable, vm.FactsText);
        AssertFactsLine(vm, AppText.AbuseKeysUnavailable);
        Assert.Equal("", vm.StatusText);
        Assert.Equal("", vm.ActionText);
        Assert.All(AllCommands(vm), command => Assert.False(command.CanExecute(null)));

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseKeysUnavailable, vm.FactsText);
        AssertFactsLine(vm, AppText.AbuseKeysUnavailable);
        Assert.Equal(AppText.WriteAbuseFeedTooltip(true), vm.WriteFeedTooltip);
        Assert.False(vm.IsGrouping);
        Assert.Null(vm.PendingGrouping);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Available_Page_Starts_Reading_With_Empty_Lines()
    {
        using AbuseIpDbViewModel vm = Create();

        Assert.True(vm.IsAvailable);
        Assert.False(vm.IsWorking);
        Assert.Equal(AppText.ReadingAbuseList, vm.FactsText);
        AssertFactsLine(vm, AppText.ReadingAbuseList);
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);
        Assert.Equal("", vm.ActionText);
        Assert.False(vm.HasActionText);
        Assert.Equal(0, _abuse.StatusCalls);
    }

    [Fact]
    public void Required_Arguments_Are_Checked()
    {
        Assert.Throws<ArgumentNullException>(() => new AbuseIpDbViewModel(_abuse, null!, _ui, _gate, null, _time, Exports));
        Assert.Throws<ArgumentNullException>(() => new AbuseIpDbViewModel(_abuse, _ipinfo, null!, _gate, null, _time, Exports));
        Assert.Throws<ArgumentNullException>(() => new AbuseIpDbViewModel(_abuse, _ipinfo, _ui, null!, null, _time, Exports));
        Assert.Throws<ArgumentNullException>(() => new AbuseIpDbViewModel(_abuse, _ipinfo, _ui, _gate, null, null!, Exports));
    }

    [Fact]
    public void A_Key_Prompt_On_Another_Gate_Is_Refused()
    {
        var other = new WorkGate();

        ArgumentException thrown = Assert.Throws<ArgumentException>(() =>
            new AbuseIpDbViewModel(_abuse, _ipinfo, _ui, _gate, new AbuseKeyPrompt(_ui, _abuse, other), _time, Exports));

        Assert.Equal("abuseKeys", thrown.ParamName);
    }

    [Fact]
    public async Task HasActionText_Follows_ActionText()
    {
        _abuse.FeedResults.Enqueue(Feed());
        using AbuseIpDbViewModel vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.HasActionText);

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.NotEqual("", vm.ActionText);
        Assert.True(vm.HasActionText);
        Assert.Contains(nameof(vm.HasActionText), raised);
    }

    [Fact]
    public async Task The_Write_Feed_Tooltip_Follows_The_IPv6_Switch_On_Each_Activation()
    {
        using AbuseIpDbViewModel vm = Create();
        int raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.WriteFeedTooltip))
                raised++;
        };

        Assert.Equal(AppText.WriteAbuseFeedTooltip(true), vm.WriteFeedTooltip);

        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        await vm.ActivateAsync();
        Assert.Equal(AppText.WriteAbuseFeedTooltip(false), vm.WriteFeedTooltip);
        Assert.Equal(1, raised);

        await vm.ActivateAsync();
        Assert.Equal(1, raised);

        // Refreshed even while a run holds the page, because it reads only the settings
        _abuse.DownloadResults.Enqueue(Downloaded());
        Task download = StartHeldDownload(vm);
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = true };
        await vm.ActivateAsync();
        Assert.Equal(AppText.WriteAbuseFeedTooltip(true), vm.WriteFeedTooltip);
        await ReleaseAsync(download);
    }

    // Releases the download StartHeldDownload held (the fake clears its own gate property when the call takes it)
    private Task ReleaseAsync(Task running)
    {
        _heldDownload?.TrySetResult();
        return running;
    }

    private TaskCompletionSource? _heldDownload;

    private Task StartHeldDownload(AbuseIpDbViewModel vm)
    {
        _heldDownload = new TaskCompletionSource();
        _abuse.DownloadGate = _heldDownload;
        return vm.DownloadCommand.ExecuteAsync(null);
    }

    // ---- commands ----

    [Fact]
    public void The_Commands_Need_The_App_Started_And_Ignore_Updates_And_Holds()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);
        int changed = 0;
        vm.DownloadCommand.CanExecuteChanged += (_, _) => changed++;

        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));

        gate.SetStarted(true);
        Assert.True(changed > 0);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));

        gate.SetBusy(true);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
        gate.SetBusy(false);

        using (gate.Hold())
        {
            Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
        }
    }

    [Fact]
    public async Task A_Command_Run_Before_Start_Does_Nothing()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);

        await vm.DownloadCommand.ExecuteAsync(null);
        await vm.ClearListCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);
        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Empty(_abuse.Calls);
        Assert.Empty(_ui.Confirms);
    }

    [Fact]
    public async Task A_Show_Before_Start_Reads_Nothing()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);

        await vm.ActivateAsync();

        Assert.DoesNotContain(nameof(IAbuseIpDbService.GetStatusAsync), _abuse.Calls);
        Assert.DoesNotContain(nameof(IAbuseIpDbService.FindOffendingNetworksAsync), _abuse.Calls);
        Assert.Equal(AppText.ReadingAbuseList, vm.FactsText);
        Assert.False(vm.IsGrouping);
    }

    [Fact]
    public async Task Starting_The_Gate_Runs_One_Activation_For_The_Shows_Before_It()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);
        await vm.ActivateAsync();
        await vm.ActivateAsync();

        gate.SetStarted(true);
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(1, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Missing), vm.StatusText);
    }

    [Fact]
    public async Task A_Show_Right_After_The_Start_Joins_The_Remembered_Read()
    {
        // The window's start order: the gate starts, then the dataset change shows the page again at once
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);
        await vm.ActivateAsync();
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;

        gate.SetStarted(true);
        Task again = vm.ActivateAsync();
        held.SetResult();
        await again;
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(1, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Later_Gate_Change_Does_Not_Activate_Again()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);
        await vm.ActivateAsync();
        gate.SetStarted(true);
        if (vm.PendingGrouping is { } pending)
            await pending;

        using (gate.Hold())
        {
        }
        gate.SetBusy(true);
        gate.SetBusy(false);

        Assert.Equal(1, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public void A_Gate_Start_With_No_Show_Before_It_Reads_Nothing()
    {
        var gate = new WorkGate();
        using AbuseIpDbViewModel vm = Create(gate: gate);

        gate.SetStarted(true);

        Assert.Empty(_abuse.Calls);
    }

    [Fact]
    public async Task A_Page_Disposed_Before_Start_Reads_Nothing_When_The_Gate_Starts()
    {
        var gate = new WorkGate();
        AbuseIpDbViewModel vm = Create(gate: gate);
        await vm.ActivateAsync();

        vm.Dispose();
        gate.SetStarted(true);

        Assert.Empty(_abuse.Calls);
    }

    [Fact]
    public async Task With_No_Service_A_Show_Before_Start_Does_Nothing()
    {
        var gate = new WorkGate();
        using var vm = new AbuseIpDbViewModel(null, _ipinfo, _ui, gate, null, _time, Exports);

        await vm.ActivateAsync();
        gate.SetStarted(true);
        await vm.ActivateAsync();

        Assert.Empty(_abuse.Calls);
        Assert.Equal(AppText.AbuseKeysUnavailable, vm.FactsText);
    }

    [Fact]
    public async Task While_A_Run_Holds_The_Page_The_Action_Commands_Are_Disabled()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        using AbuseIpDbViewModel vm = Create();
        Task download = StartHeldDownload(vm);

        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));

        await ReleaseAsync(download);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
    }

    // ---- the status ----

    [Fact]
    public async Task Each_Activation_Reads_The_Status()
    {
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Equal(2, _abuse.StatusCalls);
    }

    [Fact]
    public async Task The_Facts_Row_Is_The_Status_Within_The_Keep_Days()
    {
        AbuseListStatus status = Status(last: Now.AddHours(-3));
        _abuse.StatusResults.Enqueue(status);
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseFacts(status, KeepDays, Now), vm.FactsText);
        AssertFacts(vm, status, KeepDays);
        Assert.NotEqual("", vm.FactsDetailText);
    }

    [Fact]
    public async Task The_Keep_Days_Are_Clamped_For_The_Facts_Row()
    {
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 500 };
        AbuseListStatus status = Status(last: Now.AddHours(-3));
        _abuse.StatusResults.Enqueue(status);
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseFacts(status, AbuseIpDbLimits.MaxKeepDays, Now), vm.FactsText);
        AssertFacts(vm, status, AbuseIpDbLimits.MaxKeepDays);
    }

    [Fact]
    public async Task An_Activation_While_A_Download_Runs_Reads_Nothing()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        using AbuseIpDbViewModel vm = Create();
        Task download = StartHeldDownload(vm);

        await vm.ActivateAsync();
        Assert.Equal(0, _abuse.StatusCalls);

        await ReleaseAsync(download);
        Assert.Equal(1, _abuse.StatusCalls);
    }

    [Fact]
    public async Task An_Older_Reads_Answer_Arriving_After_A_Newer_One_Is_Dropped()
    {
        // Activations join one read, so the newer read is the one Write feed starts after its run
        AbuseListStatus newer = Status(last: Now.AddHours(-1), ipv4: 10);
        AbuseListStatus older = Status(AbuseListState.StorageError, Now.AddHours(-2), ipv4: 20);
        _abuse.StatusResults.Enqueue(newer);
        _abuse.StatusResults.Enqueue(older);
        _abuse.FeedResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        using AbuseIpDbViewModel vm = Create();

        Task first = vm.ActivateAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.AbuseFacts(newer, KeepDays, Now), vm.FactsText);

        held.SetResult();
        await first;

        Assert.Equal(2, _abuse.StatusCalls);
        Assert.Equal(AppText.AbuseFacts(newer, KeepDays, Now), vm.FactsText);
        Assert.Equal("", vm.StatusText);
    }

    [Fact]
    public async Task Activations_Join_The_Read_Still_Out()
    {
        AbuseListStatus status = Status(last: Now.AddHours(-1));
        _abuse.StatusResults.Enqueue(status);
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        using AbuseIpDbViewModel vm = Create();

        Task first = vm.ActivateAsync();
        Task second = vm.ActivateAsync();
        Assert.Equal(1, _abuse.StatusCalls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        held.SetResult();
        await first;
        await second;
        Assert.Equal(AppText.AbuseFacts(status, KeepDays, Now), vm.FactsText);

        await vm.ActivateAsync();
        Assert.Equal(2, _abuse.StatusCalls);
    }

    [Fact]
    public async Task A_Failed_Status_Read_Shows_The_Generic_Line()
    {
        var failing = new TaskCompletionSource();
        failing.SetException(new InvalidOperationException("broken"));
        _abuse.StatusGate = failing;
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.Generic, vm.FactsText);
        AssertFactsLine(vm, AppText.Generic);
    }

    [Fact]
    public async Task A_Failed_Status_Read_After_A_Good_One_Clears_The_Second_Line()
    {
        AbuseListStatus status = Status(last: Now.AddHours(-3));
        _abuse.StatusResults.Enqueue(status);
        using AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();
        AssertFacts(vm, status, KeepDays);
        Assert.NotEqual("", vm.FactsDetailText);

        var failing = new TaskCompletionSource();
        failing.SetException(new InvalidOperationException("broken"));
        _abuse.StatusGate = failing;
        await vm.ActivateAsync();

        Assert.Equal(2, _abuse.StatusCalls);
        AssertFactsLine(vm, AppText.Generic);
    }

    [Fact]
    public async Task The_Status_Line_Names_A_List_That_Cant_Be_Used_First()
    {
        _abuse.StatusResults.Enqueue(Status(AbuseListState.Unreadable, Now.AddDays(-40), Now.AddHours(5)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task The_Status_Line_Names_The_Refusal_Before_A_Stale_List()
    {
        DateTimeOffset refused = Now.AddHours(5);
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40), refusedUntil: refused));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseNextDownloadAfter(refused), vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    // A clock more than a day behind the last download: the counts are withheld and the rest is kept
    private static AbuseListStatus ClockBehind(DateTimeOffset? refusedUntil = null) =>
        Status(last: Now.AddDays(2), refusedUntil: refusedUntil, ipv4: 0) with { IPv6Count = 0, ClockBehind = true };

    [Fact]
    public async Task The_Status_Line_Warns_Of_A_Clock_Behind_The_Last_Download_Before_The_Refusal()
    {
        _abuse.StatusResults.Enqueue(ClockBehind(refusedUntil: Now.AddHours(5)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseClockBehind, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task The_Facts_Row_Shows_Only_The_Last_Download_While_The_Clock_Is_Behind()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal("Downloaded 2026-10-22 08:00 UTC", vm.FactsHeadText);
        Assert.Equal("", vm.FactsDetailText);
        Assert.Equal("Downloaded 2026-10-22 08:00 UTC", vm.FactsText);
    }

    [Fact]
    public async Task The_Status_Line_Warns_Of_A_Download_Older_Than_The_Keep_Days()
    {
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-KeepDays).AddMinutes(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Otherwise_The_Status_Line_Keeps_The_Last_Actions_Line()
    {
        using AbuseIpDbViewModel vm = Create();
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal(AppText.AbuseListCleared, vm.StatusText);

        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-KeepDays).AddMinutes(1)));
        await vm.ActivateAsync();

        Assert.Equal(AppText.AbuseListCleared, vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task With_Nothing_To_Say_The_Status_Line_Stays_Empty()
    {
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task A_State_Line_Gives_Way_When_The_List_Can_Be_Used_Again()
    {
        _abuse.StatusResults.Enqueue(Status(AbuseListState.StorageError, Now.AddHours(-1)));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.StorageError), vm.StatusText);
        Assert.True(vm.IsStatusWarning);

        await vm.ActivateAsync();
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task A_State_Line_Gives_Way_To_The_Last_Downloads_Line()
    {
        AbuseDownloadResult result = Downloaded();
        _abuse.DownloadResults.Enqueue(result);
        _abuse.StatusResults.Enqueue(Status(last: Now));
        _abuse.StatusResults.Enqueue(Status(AbuseListState.StorageError, Now));
        _abuse.StatusResults.Enqueue(Status(last: Now));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);
        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.StorageError), vm.StatusText);

        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseDownloaded(result, KeepDays), vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task The_Stale_Warning_Gives_Way_When_The_Keep_Days_Are_Raised()
    {
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
        Assert.True(vm.IsStatusWarning);

        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 60 };
        await vm.ActivateAsync();
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task The_Refusal_Line_Gives_Way_Once_The_Refusal_Is_Gone()
    {
        DateTimeOffset refused = Now.AddHours(5);
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1), refusedUntil: refused));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseNextDownloadAfter(refused), vm.StatusText);

        await vm.ActivateAsync();
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task A_Download_Keeps_Its_Line_While_An_Activation_Read_Returns()
    {
        AbuseDownloadResult result = Downloaded();
        AbuseListStatus stale = Status(last: Now.AddDays(-40));
        _abuse.DownloadResults.Enqueue(result);
        _abuse.StatusResults.Enqueue(stale);
        _abuse.StatusResults.Enqueue(Status(last: Now));
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        using AbuseIpDbViewModel vm = Create();

        Task activation = vm.ActivateAsync();
        Task download = StartHeldDownload(vm);
        held.SetResult();
        await activation;

        Assert.Equal(AppText.AbuseFacts(stale, KeepDays, Now), vm.FactsText);
        Assert.Equal(AppText.DownloadingBlacklist, vm.StatusText);
        Assert.False(vm.IsStatusWarning);

        await ReleaseAsync(download);
        Assert.Equal(AppText.AbuseDownloaded(result, KeepDays), vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Clear_List_Keeps_The_Line_While_An_Activation_Read_Returns()
    {
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        var asking = new TaskCompletionSource();
        _ui.ConfirmGate = asking;
        using AbuseIpDbViewModel vm = Create();

        Task activation = vm.ActivateAsync();
        Task clear = vm.ClearListCommand.ExecuteAsync(null);
        held.SetResult();
        await activation;
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.IsStatusWarning);

        asking.SetResult();
        await clear;
        Assert.Equal(AppText.AbuseListCleared, vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task No_To_Clear_List_Shows_The_Line_A_Read_Chose_Meanwhile()
    {
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        var asking = new TaskCompletionSource();
        _ui.ConfirmGate = asking;
        _ui.ConfirmResult = false;
        using AbuseIpDbViewModel vm = Create();

        Task activation = vm.ActivateAsync();
        Task clear = vm.ClearListCommand.ExecuteAsync(null);
        held.SetResult();
        await activation;
        Assert.Equal("", vm.StatusText);

        asking.SetResult();
        await clear;
        Assert.Equal(0, _abuse.ClearCalls);
        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task The_Read_After_Write_Feed_Chooses_The_Line()
    {
        AbuseFeedWriteResult result = Feed();
        _abuse.FeedResults.Enqueue(result);
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        using AbuseIpDbViewModel vm = Create();

        Task activation = vm.ActivateAsync();
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.Equal(AppText.AbuseFeedWritten(result, Exports), vm.ActionText);

        held.SetResult();
        await activation;
        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
    }

    [Fact]
    public async Task The_Read_After_Empty_Feed_Chooses_The_Line()
    {
        _abuse.EmptyResults.Enqueue(Feed());
        _abuse.StatusResults.Enqueue(Status(last: Now.AddDays(-40)));
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseListStale(KeepDays), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    // ---- Download blacklist ----

    [Fact]
    public async Task A_Download_Shows_Working_And_Its_Line_While_It_Runs()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        using AbuseIpDbViewModel vm = Create();
        var working = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsWorking))
                working.Add(vm.IsWorking);
        };

        Task download = StartHeldDownload(vm);

        Assert.True(vm.IsWorking);
        Assert.Equal(AppText.DownloadingBlacklist, vm.StatusText);
        Assert.False(vm.IsStatusWarning);

        await ReleaseAsync(download);

        Assert.False(vm.IsWorking);
        Assert.Equal([true, false], working);
    }

    [Fact]
    public async Task A_Download_Reads_The_Status_After_It_And_Shows_What_It_Added()
    {
        AbuseDownloadResult result = Downloaded();
        AbuseListStatus status = Status(last: Now);
        _abuse.DownloadResults.Enqueue(result);
        _abuse.StatusResults.Enqueue(status);
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                nameof(IAbuseIpDbService.DownloadBlacklistAsync), nameof(IAbuseIpDbService.GetStatusAsync),
                nameof(IAbuseIpDbService.FindOffendingNetworksAsync),
            ],
            _abuse.Calls);
        Assert.Equal(AppText.AbuseDownloaded(result, KeepDays), vm.StatusText);
        Assert.False(vm.IsStatusWarning);
        Assert.Equal(AppText.AbuseFacts(status, KeepDays, Now), vm.FactsText);
        Assert.Equal(_abuse.DownloadTokens[0], _abuse.StatusTokens[0]);
        Assert.True(_abuse.DownloadTokens[0].CanBeCanceled);
    }

    [Theory]
    [InlineData((int)AbuseIpDbFailure.LimitReached)]
    [InlineData((int)AbuseIpDbFailure.RateLimited)]
    public async Task A_Refusal_Names_The_Status_Refusal_First(int failureValue)
    {
        var failure = (AbuseIpDbFailure)failureValue;
        DateTimeOffset refused = Now.AddHours(7);
        _abuse.DownloadResults.Enqueue(Downloaded(failure, Now.AddHours(2)));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1), refusedUntil: refused));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(failure, refused), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Theory]
    [InlineData((int)AbuseIpDbFailure.LimitReached)]
    [InlineData((int)AbuseIpDbFailure.RateLimited)]
    public async Task A_Refusal_Names_The_Retry_Time_When_The_Status_Has_None(int failureValue)
    {
        var failure = (AbuseIpDbFailure)failureValue;
        DateTimeOffset retry = Now.AddHours(2);
        _abuse.DownloadResults.Enqueue(Downloaded(failure, retry));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(failure, retry), vm.StatusText);
    }

    [Fact]
    public async Task A_Refusal_Names_The_Retry_Time_When_The_Status_Read_Fails()
    {
        DateTimeOffset retry = Now.AddHours(2);
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.RateLimited, retry));
        using AbuseIpDbViewModel vm = Create();
        var failing = new TaskCompletionSource();
        failing.SetException(new InvalidOperationException("broken"));
        _abuse.StatusGate = failing;

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(AbuseIpDbFailure.RateLimited, retry), vm.StatusText);
        Assert.Equal(AppText.Generic, vm.FactsText);
    }

    [Fact]
    public async Task A_List_That_Fails_After_A_Counted_Download_Says_It_Counts()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.StorageError, null) with { Counted = true });
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1)));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(AbuseIpDbFailure.StorageError, null, counted: true), vm.StatusText);
        Assert.Contains("still counts", vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Another_Failure_Names_No_Time()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.ServiceError, Now.AddHours(2)));
        _abuse.StatusResults.Enqueue(Status(last: Now.AddHours(-1), refusedUntil: Now.AddHours(7)));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(AbuseIpDbFailure.ServiceError, null), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task A_Download_That_Throws_Shows_The_Generic_Line_And_Still_Reads_The_Status()
    {
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.Generic, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.Equal(1, _abuse.StatusCalls);
        Assert.False(vm.IsWorking);
    }

    // ---- the key prompt ----

    [Fact]
    public async Task No_Key_Asks_Once_Saves_It_And_Downloads_Once_More()
    {
        AbuseDownloadResult second = Downloaded();
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(second);
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal([AbuseKeyAsk.Missing], _ui.AbuseKeyPrompts);
        Assert.Equal([SentinelKey], _abuse.SavedKeys);
        Assert.Equal(
            [
                nameof(IAbuseIpDbService.DownloadBlacklistAsync), nameof(IAbuseIpDbService.SaveKeyAsync),
                nameof(IAbuseIpDbService.DownloadBlacklistAsync), nameof(IAbuseIpDbService.GetStatusAsync),
                nameof(IAbuseIpDbService.FindOffendingNetworksAsync),
            ],
            _abuse.Calls);
        Assert.Equal(AppText.AbuseDownloaded(second, KeepDays), vm.StatusText);
        Assert.DoesNotContain(SentinelKey, vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, vm.FactsText, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelKey, vm.ActionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Prompt_Is_Built_When_None_Is_Given()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(Downloaded());
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal([AbuseKeyAsk.Missing], _ui.AbuseKeyPrompts);
        Assert.Equal(2, _abuse.DownloadCalls);
    }

    [Fact]
    public async Task A_Rejected_Key_Asks_For_A_Replacement()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.KeyRejected));
        _abuse.DownloadResults.Enqueue(Downloaded());
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal([AbuseKeyAsk.Rejected], _ui.AbuseKeyPrompts);
        Assert.Equal(2, _abuse.DownloadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Cancelled_Prompt_Says_Nothing_Was_Downloaded(bool rejected)
    {
        _abuse.DownloadResults.Enqueue(Downloaded(rejected ? AbuseIpDbFailure.KeyRejected : AbuseIpDbFailure.NoKey));
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Single(_ui.AbuseKeyPrompts);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.Equal(rejected ? AppText.AbuseDownloadKeyReplaceCancelled : AppText.AbuseDownloadKeyCancelled, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.Equal(1, _abuse.StatusCalls);
    }

    [Fact]
    public async Task During_An_Update_A_Missing_Key_Shows_No_Prompt()
    {
        _gate.SetBusy(true);
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        Assert.True(vm.DownloadCommand.CanExecute(null));
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Empty(_ui.AbuseKeyPrompts);
        Assert.Empty(_abuse.SavedKeys);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.Equal(AppText.AbuseDownloadKeyCancelled, vm.StatusText);
    }

    [Fact]
    public async Task A_Prompt_That_Fails_To_Open_Shows_The_Generic_Line()
    {
        _ui.FailAbuseKeyPrompt = true;
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.Generic, vm.StatusText);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.True(_gate.CanStart);
    }

    [Theory]
    [InlineData((int)AbuseKeySave.NotAKey)]
    [InlineData((int)AbuseKeySave.Failed)]
    public async Task A_Key_That_Isnt_Saved_Gives_Its_Line(int saveValue)
    {
        var save = (AbuseKeySave)saveValue;
        _abuse.SaveResults.Enqueue(save);
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(save == AbuseKeySave.NotAKey ? AppText.AbuseKeyNotAKey : AppText.AbuseKeySaveFailed, vm.StatusText);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.DoesNotContain(SentinelKey, vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Retry_Answered_No_Key_Says_The_Key_Couldnt_Be_Read()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(Downloaded());
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseKeyUnreadable, vm.StatusText);
        Assert.Equal(2, _abuse.DownloadCalls);
        Assert.Single(_ui.AbuseKeyPrompts);
    }

    [Fact]
    public async Task A_Retry_Answered_Key_Rejected_Asks_Nothing_More()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.KeyRejected));
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.KeyRejected));
        _abuse.DownloadResults.Enqueue(Downloaded());
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(AbuseIpDbFailure.KeyRejected, null), vm.StatusText);
        Assert.Equal(2, _abuse.DownloadCalls);
        Assert.Single(_ui.AbuseKeyPrompts);
    }

    // ---- Clear list ----

    [Fact]
    public async Task Clear_List_Holds_The_Gate_Only_While_It_Asks()
    {
        using AbuseIpDbViewModel vm = Create();
        bool? canStartWhileAsking = null;
        bool? workingWhileAsking = null;
        _ui.OnDialogOpen = () =>
        {
            canStartWhileAsking = _gate.CanStart;
            workingWhileAsking = vm.IsWorking;
        };
        var clearing = new TaskCompletionSource();
        _abuse.ClearGate = clearing;

        Task clear = vm.ClearListCommand.ExecuteAsync(null);

        Assert.Equal([(AppText.ClearAbuseListTitle, AppText.ClearAbuseListBody, AppText.ClearAbuseListButton)], _ui.Confirms);
        Assert.False(canStartWhileAsking);
        Assert.True(workingWhileAsking);
        Assert.Equal(1, _abuse.ClearCalls);
        Assert.True(_gate.CanStart);
        Assert.True(vm.IsWorking);
        Assert.Equal(AppText.ClearingAbuseList, vm.StatusText);
        Assert.False(vm.IsStatusWarning);

        clearing.SetResult();
        await clear;
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Clear_List_Is_Working_While_The_Confirmation_Is_Open()
    {
        using AbuseIpDbViewModel vm = Create();
        var asking = new TaskCompletionSource();
        _ui.ConfirmGate = asking;

        Task clear = vm.ClearListCommand.ExecuteAsync(null);

        Assert.True(vm.IsWorking);
        Assert.False(_gate.CanStart);
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.Equal(0, _abuse.ClearCalls);

        asking.SetResult();
        await clear;
        Assert.Equal(1, _abuse.ClearCalls);
    }

    // Something else holds the gate with no dialog open yet (Reset token while it removes the token), so a question now
    // could end up under or over its dialog
    [Fact]
    public async Task Clear_List_Asks_Nothing_While_Something_Else_Holds_The_Gate()
    {
        using AbuseIpDbViewModel vm = Create();
        IDisposable other = _gate.Hold();

        Assert.True(vm.ClearListCommand.CanExecute(null));
        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Empty(_ui.Confirms);
        Assert.Empty(_abuse.Calls);
        Assert.Equal(AppText.AbuseClearListBusy, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.False(vm.IsWorking);

        other.Dispose();
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Single(_ui.Confirms);
        Assert.Equal(1, _abuse.ClearCalls);
    }

    // The busy line isn't an end line, so it doesn't come back at every later read in place of the download's
    [Fact]
    public async Task Clear_Lists_Busy_Line_Gives_Way_To_The_Last_Downloads_Line()
    {
        AbuseDownloadResult result = Downloaded();
        _abuse.DownloadResults.Enqueue(result);
        _abuse.StatusResults.Enqueue(Status(last: Now));
        _abuse.StatusResults.Enqueue(Status(last: Now));
        using AbuseIpDbViewModel vm = Create();

        await vm.DownloadCommand.ExecuteAsync(null);
        IDisposable other = _gate.Hold();
        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal(AppText.AbuseClearListBusy, vm.StatusText);
        other.Dispose();

        await vm.ActivateAsync();
        Assert.Equal(AppText.AbuseDownloaded(result, KeepDays), vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Empty_Feed_Asks_Nothing_While_Something_Else_Holds_The_Gate()
    {
        _abuse.EmptyResults.Enqueue(Feed());
        using AbuseIpDbViewModel vm = Create();
        IDisposable other = _gate.Hold();

        Assert.True(vm.EmptyFeedCommand.CanExecute(null));
        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Empty(_ui.Confirms);
        Assert.Empty(_abuse.Calls);
        Assert.Equal(AppText.AbuseEmptyFeedBusy, vm.ActionText);
        Assert.False(vm.IsWorking);

        other.Dispose();
        await vm.EmptyFeedCommand.ExecuteAsync(null);
        Assert.Single(_ui.Confirms);
        Assert.Equal(1, _abuse.EmptyCalls);
    }

    // The close must be asked, whatever holds the gate
    [Fact]
    public async Task The_Close_Question_Asks_While_Something_Else_Holds_The_Gate()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);
        IDisposable other = _gate.Hold();
        _ui.ConfirmResult = false;

        Assert.False(await vm.AskBeforeCloseAsync());

        Assert.Equal(AppText.CloseAbuseWorkTitle, Assert.Single(_ui.Confirms).Title);
        other.Dispose();
        held.SetResult();
        await run;
    }

    [Fact]
    public async Task No_To_Clear_List_Runs_Nothing()
    {
        _ui.ConfirmResult = false;
        using AbuseIpDbViewModel vm = Create();

        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Empty(_abuse.Calls);
        Assert.False(vm.IsWorking);
        Assert.Equal("", vm.StatusText);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task A_Clear_Confirmation_That_Fails_Shows_The_Generic_Line()
    {
        _ui.ConfirmException = new InvalidOperationException("dialog failed");
        using AbuseIpDbViewModel vm = Create();

        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Empty(_abuse.Calls);
        Assert.Equal(AppText.Generic, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.Equal("", vm.ActionText);
        Assert.True(_gate.CanStart);
        Assert.False(vm.IsWorking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Clear_List_Gives_Its_Line_And_Reads_The_Status(bool succeeds)
    {
        if (!succeeds)
            _abuse.ClearResults.Enqueue(AbuseIpDbFailure.StorageError);
        AbuseListStatus status = Status();
        _abuse.StatusResults.Enqueue(status);
        using AbuseIpDbViewModel vm = Create();

        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Equal(
            [
                nameof(IAbuseIpDbService.ClearListAsync), nameof(IAbuseIpDbService.GetStatusAsync),
                nameof(IAbuseIpDbService.FindOffendingNetworksAsync),
            ],
            _abuse.Calls);
        Assert.Equal(succeeds ? AppText.AbuseListCleared : AppText.AbuseListClearFailed, vm.StatusText);
        Assert.Equal(!succeeds, vm.IsStatusWarning);
        Assert.Equal(AppText.AbuseFacts(status, KeepDays, Now), vm.FactsText);
    }

    // ---- Empty feed ----

    [Fact]
    public async Task Empty_Feed_Holds_The_Gate_Only_While_It_Asks()
    {
        _abuse.EmptyResults.Enqueue(Feed());
        using AbuseIpDbViewModel vm = Create();
        bool? canStartWhileAsking = null;
        bool? workingWhileAsking = null;
        _ui.OnDialogOpen = () =>
        {
            canStartWhileAsking = _gate.CanStart;
            workingWhileAsking = vm.IsWorking;
        };
        var emptying = new TaskCompletionSource();
        _abuse.EmptyGate = emptying;

        Task empty = vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal([(AppText.EmptyAbuseFeedTitle, AppText.EmptyAbuseFeedBody, AppText.EmptyAbuseFeedButton)], _ui.Confirms);
        Assert.False(canStartWhileAsking);
        Assert.True(workingWhileAsking);
        Assert.Equal(1, _abuse.EmptyCalls);
        Assert.True(_gate.CanStart);
        Assert.True(vm.IsWorking);
        Assert.Equal(AppText.EmptyingAbuseFeed, vm.ActionText);

        emptying.SetResult();
        await empty;
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task No_To_Empty_Feed_Runs_Nothing()
    {
        _ui.ConfirmResult = false;
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Empty(_abuse.Calls);
        Assert.False(vm.IsWorking);
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task An_Empty_Confirmation_That_Fails_Shows_The_Generic_Line()
    {
        _ui.ConfirmException = new InvalidOperationException("dialog failed");
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Empty(_abuse.Calls);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.Equal("", vm.StatusText);
        Assert.True(_gate.CanStart);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Empty_Feed_Names_The_Folder_And_Never_The_Unsaved_Note()
    {
        string root = Path.Combine(_folder, "chosen");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = root };
        AbuseFeedWriteResult result = Feed();
        _abuse.EmptyResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(hasUnsavedChanges: true));

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal([nameof(IAbuseIpDbService.EmptyFeedAsync), nameof(IAbuseIpDbService.GetStatusAsync)], _abuse.Calls);
        Assert.Equal(AppText.AbuseFeedEmptied(result, root), vm.ActionText);
        Assert.DoesNotContain(AppText.UnsavedSettingsNote, vm.ActionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_Feed_With_No_File_Names_The_Default_Folder()
    {
        AbuseFeedWriteResult result = new(AbuseFeedWriteFailure.NothingToWrite, [], 0, 0, 0, 0, []);
        _abuse.EmptyResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(hasUnsavedChanges: true));

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseFeedEmptyFailureLine(result, Exports), vm.ActionText);
    }

    [Fact]
    public async Task Empty_Feed_That_Throws_Gives_The_Failed_Line()
    {
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseFeedEmptyFailureLine(ThrownWrite, Exports), vm.ActionText);
        Assert.Equal(1, _abuse.StatusCalls);
    }

    // ---- Write feed ----

    private void TickNoFeedType() =>
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = false };

    [Fact]
    public async Task Write_Feed_With_No_Feed_Type_Asks_And_Writes_Nothing()
    {
        TickNoFeedType();
        using AbuseIpDbViewModel vm = Create();
        string before = vm.ActionText;

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.Equal(0, _ui.SettingsPageShows);
        Assert.Equal(before, vm.ActionText);
        Assert.NotEqual(AppText.WritingAbuseFeed, vm.ActionText);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Open_Settings_From_The_Configure_Dialog_Shows_The_Settings_Page_And_Writes_Nothing()
    {
        TickNoFeedType();
        _ui.ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings;
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.Equal(1, _ui.SettingsPageShows);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Every_Write_Feed_Press_With_No_Feed_Type_Asks()
    {
        TickNoFeedType();
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);
        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(2, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _abuse.FeedCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Write_Feed_With_A_Feed_Type_Ticked_Writes_Without_Asking(bool fortigate, bool modSecurity)
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = fortigate, WriteModSecurityLists = modSecurity };
        _abuse.FeedResults.Enqueue(Feed());
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(0, _ui.ConfigureFeedsAsks);
        Assert.Equal(1, _abuse.FeedCalls);
    }

    [Fact]
    public async Task Write_Feed_With_No_Feed_Type_While_The_Gate_Is_Held_Shows_The_Busy_Line()
    {
        TickNoFeedType();
        using AbuseIpDbViewModel vm = Create();
        using (IDisposable hold = _gate.Hold())
            await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.ConfigureFeedsBusy, vm.ActionText);
        Assert.Equal(0, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task The_Configure_Dialog_Holds_The_Gate_Only_While_It_Is_Open()
    {
        TickNoFeedType();
        using AbuseIpDbViewModel vm = Create();
        bool? heldWhileAsking = null;
        bool? workingWhileAsking = null;
        _ui.OnDialogOpen = () =>
        {
            heldWhileAsking = _gate.IsHeld;
            workingWhileAsking = vm.IsWorking;
        };

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.True(heldWhileAsking);
        Assert.True(workingWhileAsking);
        Assert.False(_gate.IsHeld);
        Assert.True(_gate.CanStart);
        Assert.True(vm.WriteFeedCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_Configure_Dialog_That_Fails_To_Show_Gives_The_Generic_Line_And_No_Settings_Page()
    {
        TickNoFeedType();
        _ui.ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings;
        _ui.ConfigureFeedsException = new InvalidOperationException("dialog broken");
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.Equal(AppText.Generic, vm.ActionText);
        Assert.Equal(0, _ui.SettingsPageShows);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.False(_gate.IsHeld);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task A_Page_Disposed_While_The_Configure_Dialog_Is_Open_Shows_No_Settings_Page()
    {
        TickNoFeedType();
        _ui.ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings;
        var open = new TaskCompletionSource();
        _ui.ConfigureFeedsGate = open;
        AbuseIpDbViewModel vm = Create();

        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        vm.Dispose();
        open.SetResult();
        await write;

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _ui.SettingsPageShows);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task A_Close_While_The_Configure_Dialog_Is_Open_Shows_No_Settings_Page()
    {
        TickNoFeedType();
        _ui.ConfigureFeedsAnswer = ConfigureFeedsChoice.OpenSettings;
        var open = new TaskCompletionSource();
        _ui.ConfigureFeedsGate = open;
        using AbuseIpDbViewModel vm = Create();

        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        await vm.StopForCloseAsync();
        open.SetResult();
        await write;

        Assert.Equal(1, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _ui.SettingsPageShows);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task Empty_Feed_With_No_Feed_Type_Never_Asks_To_Configure()
    {
        TickNoFeedType();
        _abuse.EmptyResults.Enqueue(Feed());
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Single(_ui.Confirms);
        Assert.Equal(0, _ui.ConfigureFeedsAsks);
        Assert.Equal(1, _abuse.EmptyCalls);
    }

    [Fact]
    public async Task A_Write_Feed_Whose_Settings_Read_Throws_Never_Asks_And_Gives_The_Failed_Line()
    {
        using AbuseIpDbViewModel vm = Create();
        _ipinfo.SettingsFailure = new InvalidOperationException("settings broken");

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(0, _ui.ConfigureFeedsAsks);
        Assert.Equal(0, _abuse.FeedCalls);
        Assert.Equal(AppText.AbuseFeedWriteFailureLine(ThrownWrite, AbuseIpDbLimits.DefaultKeepDays, true), vm.ActionText);
        Assert.False(vm.IsWorking);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Written_Feed_Carries_The_Note_Only_With_Unsaved_Changes(bool unsaved)
    {
        AbuseFeedWriteResult result = Feed();
        _abuse.FeedResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(unsaved));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        string line = AppText.AbuseFeedWritten(result, Exports);
        Assert.Equal(unsaved ? line + AppText.UnsavedSettingsNote : line, vm.ActionText);
        Assert.Equal([nameof(IAbuseIpDbService.WriteFeedAsync), nameof(IAbuseIpDbService.GetStatusAsync)], _abuse.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Failed_Write_After_A_File_Carries_The_Unsaved_Sentence_Only_With_Unsaved_Changes(bool unsaved)
    {
        string ipv4 = Path.Combine(Fortigate(Exports), "AbuseIPDB_rolling_ipv4.txt");
        string ipv6 = Path.Combine(Fortigate(Exports), "AbuseIPDB_rolling_ipv6.txt");
        _abuse.FeedResults.Enqueue(new AbuseFeedWriteResult(AbuseFeedWriteFailure.WriteFailed, [ipv4], 1, 0, 0, 0, [],
                                                            FailedFile: ipv6));
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(unsaved));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine("fortigate", "AbuseIPDB_rolling_ipv6.txt") + " couldn't be written. "
                     + Path.Combine("fortigate", "AbuseIPDB_rolling_ipv4.txt") + " was written before the failure."
                     + (unsaved ? " Unsaved changes on the Settings page weren't used." : "")
                     + " Please try again or contact the Infra Team.", vm.ActionText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_Note_Is_Decided_When_The_Write_Starts(bool unsavedAtStart)
    {
        AbuseFeedWriteResult result = Feed();
        _abuse.FeedResults.Enqueue(result);
        var unsaved = new FakeUnsavedSettings(unsavedAtStart);
        using AbuseIpDbViewModel vm = Create(unsaved);
        var writing = new TaskCompletionSource();
        _abuse.FeedGate = writing;

        Task write = vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(AppText.WritingAbuseFeed, vm.ActionText);
        Assert.True(vm.IsWorking);
        unsaved.HasUnsavedChanges = !unsavedAtStart;
        writing.SetResult();
        await write;

        string line = AppText.AbuseFeedWritten(result, Exports);
        Assert.Equal(unsavedAtStart ? line + AppText.UnsavedSettingsNote : line, vm.ActionText);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task Nothing_To_Write_Carries_The_Note_Only_When_Exceptions_Removed_Addresses(int removals)
    {
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false, AbuseIpDbKeepDays = 7 };
        AbuseFeedWriteResult result = new(AbuseFeedWriteFailure.NothingToWrite, [], 0, 0, 0, removals, []);
        _abuse.FeedResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(hasUnsavedChanges: true));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        string line = AppText.AbuseFeedWriteFailureLine(result, 7, false);
        Assert.Equal(removals > 0 ? line + AppText.UnsavedSettingsNote : line, vm.ActionText);
    }

    [Fact]
    public async Task A_Failed_Write_Carries_No_Note()
    {
        AbuseFeedWriteResult result = new(AbuseFeedWriteFailure.FolderUnavailable, [], 0, 0, 0, 5, []);
        _abuse.FeedResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(hasUnsavedChanges: true));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseFeedWriteFailureLine(result, KeepDays, true), vm.ActionText);
    }

    [Fact]
    public async Task A_Write_That_Throws_Gives_The_Failed_Line_And_Reads_The_Status()
    {
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(hasUnsavedChanges: true));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseFeedWriteFailureLine(ThrownWrite, KeepDays, true), vm.ActionText);
        Assert.Equal(1, _abuse.StatusCalls);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task The_Written_Feed_Names_The_Chosen_Export_Folder()
    {
        string root = Path.Combine(_folder, "chosen");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = root };
        AbuseFeedWriteResult result = Feed() with { Files = [Path.Combine(Fortigate(root), "AbuseIPDB_rolling_ipv4.txt")] };
        _abuse.FeedResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Feed written: 100 IPv4 and 2 IPv6 addresses to the fortigate folder of " + root, vm.ActionText);
    }

    // The settings ticked a type, but the write found none ticked: its own line, with the unsaved note
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Write_That_Finds_No_Feed_Type_Says_So(bool unsaved)
    {
        _abuse.FeedResults.Enqueue(new AbuseFeedWriteResult(AbuseFeedWriteFailure.NoFeedType, [], 0, 0, 0, 0, []));
        using AbuseIpDbViewModel vm = Create(new FakeUnsavedSettings(unsaved));

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.NothingWrittenNoFeedType + (unsaved ? AppText.UnsavedSettingsNote : ""), vm.ActionText);
    }

    [Fact]
    public async Task A_ModSecurity_Only_Write_Names_The_ModSecurity_Folder()
    {
        _ipinfo.Settings = _ipinfo.Settings with { WriteFortigateFeeds = false, WriteModSecurityLists = true };
        string file = Path.Combine(Exports, "modsecurity", "AbuseIPDB_rolling_ipv4.txt");
        _abuse.FeedResults.Enqueue(Feed() with { Files = [file], ModSecurityFiles = [file] });
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal("Feed written: 100 IPv4 and 2 IPv6 addresses to the modsecurity folder of " + Exports, vm.ActionText);
    }

    [Fact]
    public async Task Empty_Feed_Over_Both_Folders_Names_Each_File_With_Its_Folder()
    {
        string fortigate = Path.Combine(Fortigate(Exports), "AbuseIPDB_rolling_ipv6.txt");
        string modSecurity = Path.Combine(Exports, "modsecurity", "AbuseIPDB_rolling_ipv6.txt");
        _abuse.EmptyResults.Enqueue(new AbuseFeedWriteResult(AbuseFeedWriteFailure.None, [fortigate, modSecurity], 0, 0, 0, 0,
            [fortigate, modSecurity]));
        using AbuseIpDbViewModel vm = Create();

        await vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.Equal("Feed emptied: " + Path.Combine("fortigate", "AbuseIPDB_rolling_ipv6.txt") + " and "
            + Path.Combine("modsecurity", "AbuseIPDB_rolling_ipv6.txt") + " in " + Exports, vm.ActionText);
    }

    [Fact]
    public async Task A_Status_Read_After_A_Write_Names_A_List_That_Cant_Be_Used()
    {
        _abuse.FeedResults.Enqueue(Feed());
        _abuse.StatusResults.Enqueue(Status(AbuseListState.Unreadable));
        using AbuseIpDbViewModel vm = Create();

        await vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.Equal(AppText.AbuseFacts(Status(AbuseListState.Unreadable), KeepDays, Now), vm.FactsText);
        AssertFacts(vm, Status(AbuseListState.Unreadable), KeepDays);
        Assert.Equal("", vm.FactsDetailText);
    }

    // ---- Open folder ----

    [Fact]
    public async Task Open_Folder_Opens_The_Default_Export_Folder()
    {
        using AbuseIpDbViewModel vm = Create();

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([Exports], _ui.OpenedFolders);
        Assert.True(Directory.Exists(Exports));
    }

    [Fact]
    public async Task Open_Folder_Opens_The_Chosen_Export_Folder()
    {
        string root = Path.Combine(_folder, "chosen");
        _ipinfo.Settings = _ipinfo.Settings with { ExportFolder = root };
        using AbuseIpDbViewModel vm = Create();

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([root], _ui.OpenedFolders);
    }

    [Fact]
    public async Task A_Folder_That_Wont_Open_Gives_Its_Line()
    {
        _ui.FailOpenFolder = true;
        using AbuseIpDbViewModel vm = Create();

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(AppText.FolderOpenFailed, vm.ActionText);
    }

    // ---- dispose ----

    [Fact]
    public async Task Dispose_Cancels_A_Held_Call_And_Changes_Nothing_After()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        AbuseIpDbViewModel vm = Create();
        Task download = StartHeldDownload(vm);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Dispose();
        await download;

        Assert.True(_abuse.DownloadTokens[0].IsCancellationRequested);
        Assert.Equal(AppText.DownloadingBlacklist, vm.StatusText);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.Empty(raised);
        Assert.All(AllCommands(vm), command => Assert.False(command.CanExecute(null)));
    }

    [Fact]
    public async Task A_Key_Saved_After_Dispose_Downloads_Nothing_More()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(Downloaded());
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        AbuseIpDbViewModel vm = CreateWithPrompt();
        var raised = new List<string?>();
        _ui.OnDialogOpen = () =>
        {
            vm.Dispose();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        };

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal([SentinelKey], _abuse.SavedKeys);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.Empty(raised);
        Assert.Equal(AppText.DownloadingBlacklist, vm.StatusText);
    }

    [Fact]
    public async Task A_Confirmation_Answered_After_Dispose_Runs_Nothing()
    {
        AbuseIpDbViewModel vm = Create();
        var asking = new TaskCompletionSource();
        _ui.ConfirmGate = asking;
        Task clear = vm.ClearListCommand.ExecuteAsync(null);

        vm.Dispose();
        asking.SetResult();
        await clear;

        Assert.Empty(_abuse.Calls);
        Assert.Equal("", vm.StatusText);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task After_Dispose_A_Gate_Change_Raises_Nothing_And_Activation_Reads_Nothing()
    {
        var gate = new WorkGate();
        AbuseIpDbViewModel vm = Create(gate: gate);
        int changed = 0;
        foreach (IAsyncRelayCommand command in AllCommands(vm))
            command.CanExecuteChanged += (_, _) => changed++;

        vm.Dispose();
        changed = 0;
        gate.SetStarted(true);
        await vm.ActivateAsync();
        vm.Dispose();

        Assert.Equal(0, changed);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.All(AllCommands(vm), command => Assert.False(command.CanExecute(null)));
    }

    // ---- the offending networks: helpers ----

    private static readonly DatasetInfo DatasetA =
        new(new DateOnly(2026, 10, 19), 4_170_047, "aa11", new DateTimeOffset(2026, 10, 19, 17, 0, 0, TimeSpan.Zero), true);

    private static AbuseNetworkGroup Group(uint asn, int addresses = 10, string? name = "Example",
                                           string? domain = "example.com", params AbuseCountryCount[] countries) =>
        new(asn, name, domain, addresses, countries);

    // A None answer naming the inputs it read: by default the ones the fake service holds now, so a show after it is
    // not stale
    private AbuseNetworksResult Grouped(IReadOnlyList<AbuseNetworkGroup> groups, int? keepDays = null, bool? ipv6 = null,
                                        DatasetInfo? dataset = null) =>
        new(AbuseNetworksFailure.None, groups, 2, [], 1, groups.Sum(g => g.Addresses), 0,
            ipv6 ?? _ipinfo.Settings.IPv6Enabled,
            keepDays ?? Math.Clamp(_ipinfo.Settings.AbuseIpDbKeepDays, AbuseIpDbLimits.MinKeepDays, AbuseIpDbLimits.MaxKeepDays),
            Now.AddHours(-1), dataset ?? _ipinfo.Dataset, 0);

    private static AbuseNetworksResult NotGrouped(AbuseNetworksFailure failure) =>
        new(failure, [], 0, [], 0, 0, 0, true, KeepDays, null, null, 0);

    private static IEnumerable<string> RowAsns(AbuseIpDbViewModel vm) => vm.Rows.Select(r => r.AsnText);

    private static async Task GroupedAsync(AbuseIpDbViewModel vm)
    {
        await vm.ActivateAsync();
        if (vm.PendingGrouping is { } pending)
            await pending;
    }

    /// <summary>
    /// The fake service with offending-networks calls that answer only when the test says so, whatever their token:
    /// the fake's own gate ends a cancelled call, so it can't show a superseded answer arriving late.
    /// </summary>
    private sealed class HeldNetworks(FakeAbuseIpDbService inner) : IAbuseIpDbService
    {
        public List<(IProgress<GroupingProgress>? Progress, CancellationToken Token, TaskCompletionSource<AbuseNetworksResult> Answer)> Runs { get; } = [];

        public bool HasKey => inner.HasKey;
        public Task<AbuseKeySave> SaveKeyAsync(string key) => inner.SaveKeyAsync(key);
        public Task<bool> ClearKeyAsync() => inner.ClearKeyAsync();
        public Task<AbuseCheckResult> CheckAsync(SearchQuery query, CancellationToken ct) => inner.CheckAsync(query, ct);
        public Task<AbuseListStatus> GetStatusAsync(CancellationToken ct) => inner.GetStatusAsync(ct);
        public Task<AbuseNewAddresses> GetNewAddressesAsync(CancellationToken ct) => inner.GetNewAddressesAsync(ct);
        public Task<AbuseDownloadResult> DownloadBlacklistAsync(CancellationToken ct) => inner.DownloadBlacklistAsync(ct);
        public Task<AbuseIpDbFailure> ClearListAsync(CancellationToken ct) => inner.ClearListAsync(ct);
        public Task<AbuseFeedWriteResult> WriteFeedAsync(CancellationToken ct) => inner.WriteFeedAsync(ct);
        public Task<AbuseFeedWriteResult> EmptyFeedAsync(CancellationToken ct) => inner.EmptyFeedAsync(ct);

        public Task<AbuseNetworksResult> FindOffendingNetworksAsync(IProgress<GroupingProgress>? progress, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<AbuseNetworksResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Runs.Add((progress, ct, answer));
            return answer.Task;
        }
    }

    private AbuseIpDbViewModel CreateHeld(HeldNetworks held) =>
        Immediate(new(held, _ipinfo, _ui, _gate, null, _time, Exports));

    // ---- the offending networks: rows ----

    [Fact]
    public void A_Row_Formats_Each_Cell_In_The_Invariant_Culture()
    {
        AbuseCountryCount[] countries = [new("SG", 120), new("US", 40)];
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            OffendingNetworkRow row = OffendingNetworkRow.From(Group(15169, 1_234_567, "Google LLC", "google.com", countries));

            Assert.Equal("AS15169", row.AsnText);
            Assert.Equal("Google LLC", row.NameText);
            Assert.Equal("google.com", row.DomainText);
            Assert.Equal("1,234,567", row.AddressesText);
            Assert.Equal(AppText.AbuseNetworkCountries(countries), row.CountriesText);
            Assert.Equal(AppText.LookUpNetworkName(15169), row.LookUpName);
            Assert.Equal(string.Join(", ", "AS15169", "Google LLC", "google.com", "1,234,567", row.CountriesText),
                row.AccessibleText);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Row_Without_A_Name_Or_Domain_Shows_The_Missing_Mark(string? missing)
    {
        OffendingNetworkRow row = OffendingNetworkRow.From(Group(64500, 3, missing, missing));

        Assert.Equal(AppText.Missing, row.NameText);
        Assert.Equal(AppText.Missing, row.DomainText);
        Assert.Equal(AppText.Missing, row.CountriesText);
        Assert.Equal(string.Join(", ", "AS64500", AppText.Missing, AppText.Missing, "3", AppText.Missing), row.AccessibleText);
    }

    [Fact]
    public async Task Over_300_Networks_Show_The_First_300_Rows_In_Order()
    {
        AbuseNetworkGroup[] groups = Enumerable.Range(1, 301).Select(i => Group((uint)i)).ToArray();
        AbuseNetworksResult result = Grouped(groups);
        _abuse.NetworksResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);

        Assert.Equal(AbuseIpDbViewModel.RowLimit, vm.Rows.Count);
        Assert.Equal(Enumerable.Range(1, 300).Select(i => "AS" + i), RowAsns(vm));
        Assert.True(vm.HasRows);
        Assert.Equal(AppText.AbuseNetworksSummary(result, 300, true), vm.SummaryText);
        Assert.Equal(AppText.AbuseNetworksEmpty, vm.EmptyText);
        Assert.False(vm.IsGrouping);
    }

    [Fact]
    public async Task No_Networks_Give_No_Rows_And_The_Summary()
    {
        // As the service answers a list with no address to look up: no networks and both buckets empty
        AbuseNetworksResult result = Grouped([]) with { NoAsn = 0, NotInIpinfo = 0 };
        _abuse.NetworksResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal(AppText.AbuseNetworksSummary(result, 0, true), vm.SummaryText);
        Assert.Equal(AppText.AbuseNetworksNoAddresses, vm.EmptyText);
    }

    [Fact]
    public async Task Addresses_With_No_Network_Say_None_Has_An_ASN()
    {
        // Every address looked up is in the no-ASN or the not-in-IPinfo bucket
        AbuseNetworksResult result = Grouped([]) with { NoAsn = 3, NotInIpinfo = 2, IPv4Addresses = 4, IPv6Addresses = 1 };
        _abuse.NetworksResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal(AppText.AbuseNetworksSummary(result, 0, true), vm.SummaryText);
        Assert.Equal(AppText.AbuseNetworksNoAsn, vm.EmptyText);
    }

    [Fact]
    public async Task Only_IPv6_Addresses_Still_Have_Addresses_To_Name()
    {
        AbuseNetworksResult result = Grouped([]) with { NoAsn = 1, IPv4Addresses = 0, IPv6Addresses = 1 };
        _abuse.NetworksResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);

        Assert.Equal(AppText.AbuseNetworksNoAsn, vm.EmptyText);
    }

    [Fact]
    public async Task A_Regroup_With_No_Rows_Says_The_Networks_Will_Appear()
    {
        // The first grouping ends on the fake's default, a list with no download
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(AppText.AbuseNetworksFailureLine(AbuseNetworksFailure.NoDownload), vm.EmptyText);

        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        var held = new TaskCompletionSource();
        _abuse.NetworksGate = held;
        await vm.ActivateAsync();

        Assert.True(vm.IsGrouping);
        Assert.False(vm.HasRows);
        Assert.Equal(AppText.AbuseNetworksEmpty, vm.EmptyText);

        held.SetResult();
        await vm.PendingGrouping!;
    }

    [Fact]
    public async Task The_Summary_Uses_The_IPv6_Switch_Read_At_The_Start()
    {
        _ipinfo.Dataset = DatasetA with { IncludesIPv6 = false };
        AbuseNetworksResult result = Grouped([Group(1)]);
        _abuse.NetworksResults.Enqueue(result);
        var held = new TaskCompletionSource();
        _abuse.NetworksGate = held;
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
        held.SetResult();
        await vm.PendingGrouping!;

        Assert.Equal(AppText.AbuseNetworksSummary(result, 1, true), vm.SummaryText);
        Assert.NotEqual(AppText.AbuseNetworksSummary(result, 1, false), vm.SummaryText);
    }

    [Fact]
    public async Task The_Rows_Stay_While_A_Rerun_Runs_And_Are_Replaced_In_Place()
    {
        _abuse.NetworksResults.Enqueue(Grouped([Group(1), Group(2)]));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        var rows = vm.Rows;

        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        _abuse.NetworksResults.Enqueue(Grouped([Group(3)]));
        var held = new TaskCompletionSource();
        _abuse.NetworksGate = held;
        await vm.ActivateAsync();

        Assert.True(vm.IsGrouping);
        Assert.Equal(["AS1", "AS2"], RowAsns(vm));
        Assert.True(vm.HasRows);

        held.SetResult();
        await vm.PendingGrouping!;

        Assert.Same(rows, vm.Rows);
        Assert.Equal(["AS3"], RowAsns(vm));
    }

    // ---- the offending networks: a grouping ----

    [Fact]
    public async Task A_Held_Grouping_Is_Grouping_With_The_Starting_Line_And_Never_Working()
    {
        var held = new TaskCompletionSource();
        _abuse.NetworksGate = held;
        using AbuseIpDbViewModel vm = Create();
        var working = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsWorking))
                working.Add(vm.IsWorking);
        };

        await vm.ActivateAsync();

        Assert.True(vm.IsGrouping);
        Assert.False(vm.IsWorking);
        Assert.Equal(AppText.GroupingNetworks(null), vm.SummaryText);
        Assert.Equal(0, vm.GroupingProgress);

        held.SetResult();
        await vm.PendingGrouping!;

        Assert.False(vm.IsGrouping);
        Assert.False(vm.IsWorking);
        Assert.Empty(working);
    }

    [Fact]
    public async Task Progress_Reports_Set_The_Summary_And_A_Lower_One_Keeps_The_Value()
    {
        _abuse.NetworksProgress.AddRange([new GroupingProgress(0, 0), new GroupingProgress(250, 1000), new GroupingProgress(100, 1000)]);
        _abuse.NetworksGate = new TaskCompletionSource();
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(AppText.GroupingNetworks(new GroupingProgress(100, 1000)), vm.SummaryText);
        Assert.Equal(0.25, vm.GroupingProgress);
    }

    [Fact]
    public async Task A_Progress_Report_Sets_The_Value()
    {
        _abuse.NetworksProgress.Add(new GroupingProgress(1500, 1000));
        _abuse.NetworksProgress.Add(new GroupingProgress(1, 0));
        _abuse.NetworksGate = new TaskCompletionSource();
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();

        Assert.Equal(1, vm.GroupingProgress);
        Assert.Equal(AppText.GroupingNetworks(new GroupingProgress(1, 0)), vm.SummaryText);
    }

    [Fact]
    public async Task A_New_Grouping_Starts_At_Zero()
    {
        _abuse.NetworksProgress.Add(new GroupingProgress(500, 1000));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(0.5, vm.GroupingProgress);

        _abuse.NetworksProgress.Clear();
        _abuse.NetworksGate = new TaskCompletionSource();
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        await vm.ActivateAsync();

        Assert.True(vm.IsGrouping);
        Assert.Equal(0, vm.GroupingProgress);
        Assert.Equal(AppText.GroupingNetworks(null), vm.SummaryText);
    }

    [Fact]
    public async Task A_Report_After_The_Grouping_Ended_Changes_Nothing()
    {
        AbuseNetworksResult result = Grouped([Group(1)]);
        _abuse.NetworksResults.Enqueue(result);
        using AbuseIpDbViewModel vm = Create();
        IProgress<GroupingProgress>? sink = null;
        vm.ProgressFactory = handler => sink = new ImmediateProgress<GroupingProgress>(handler);

        await GroupedAsync(vm);
        sink!.Report(new GroupingProgress(5, 10));

        Assert.Equal(AppText.AbuseNetworksSummary(result, 1, true), vm.SummaryText);
        Assert.Equal(0, vm.GroupingProgress);
    }

    // ---- the offending networks: one at a time ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Second_Grouping_Cancels_The_First_And_Drops_Its_Answer_And_Reports(bool firstAnswersLast)
    {
        var held = new HeldNetworks(_abuse);
        using AbuseIpDbViewModel vm = CreateHeld(held);
        await vm.ActivateAsync();
        Task firstRun = vm.PendingGrouping!;
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        await vm.ActivateAsync();
        Assert.NotSame(firstRun, vm.PendingGrouping);

        Assert.Equal(2, held.Runs.Count);
        Assert.True(held.Runs[0].Token.IsCancellationRequested);
        Assert.False(held.Runs[1].Token.IsCancellationRequested);

        held.Runs[0].Progress!.Report(new GroupingProgress(5, 10));
        Assert.Equal(AppText.GroupingNetworks(null), vm.SummaryText);
        Assert.Equal(0, vm.GroupingProgress);

        AbuseNetworksResult second = Grouped([Group(2)]);
        if (firstAnswersLast)
        {
            held.Runs[1].Answer.SetResult(second);
            await vm.PendingGrouping!;
            held.Runs[0].Answer.SetResult(Grouped([Group(1)]));
            await firstRun;

            Assert.False(vm.IsGrouping);
            Assert.Equal(["AS2"], RowAsns(vm));
            Assert.Equal(AppText.AbuseNetworksSummary(second, 1, true), vm.SummaryText);
        }
        else
        {
            held.Runs[0].Answer.SetResult(Grouped([Group(1)]));
            await firstRun;

            Assert.True(vm.IsGrouping);
            Assert.Empty(vm.Rows);
            Assert.Equal(AppText.GroupingNetworks(null), vm.SummaryText);

            held.Runs[1].Answer.SetResult(second);
            await vm.PendingGrouping!;
            Assert.False(vm.IsGrouping);
            Assert.Equal(["AS2"], RowAsns(vm));
        }
    }

    // ---- the offending networks: failures ----

    [Theory]
    [InlineData((int)AbuseNetworksFailure.ListUnreadable)]
    [InlineData((int)AbuseNetworksFailure.ListNewer)]
    [InlineData((int)AbuseNetworksFailure.StorageError)]
    [InlineData((int)AbuseNetworksFailure.NoDownload)]
    [InlineData((int)AbuseNetworksFailure.NoDataset)]
    [InlineData((int)AbuseNetworksFailure.LookupFailed)]
    [InlineData((int)AbuseNetworksFailure.DatasetChanged)]
    [InlineData((int)AbuseNetworksFailure.ClockWrong)]
    public async Task A_Failure_Gives_Its_Line_Clears_The_Rows_And_Empties_The_Summary(int failureValue)
    {
        var failure = (AbuseNetworksFailure)failureValue;
        _abuse.NetworksResults.Enqueue(Grouped([Group(1), Group(2)]));
        _abuse.NetworksResults.Enqueue(NotGrouped(failure));
        // Both shows read a clock still behind, so a failure for the clock isn't grouped again by the read
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.StatusResults.Enqueue(ClockBehind());
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.True(vm.HasRows);

        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        await GroupedAsync(vm);

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.Equal("", vm.SummaryText);
        Assert.Equal(AppText.AbuseNetworksFailureLine(failure), vm.EmptyText);
        Assert.False(vm.IsGrouping);
    }

    [Fact]
    public async Task A_Grouping_That_Throws_Gives_The_Lookup_Failed_Line()
    {
        var held = new HeldNetworks(_abuse);
        using AbuseIpDbViewModel vm = CreateHeld(held);
        await vm.ActivateAsync();

        held.Runs[0].Answer.SetException(new InvalidOperationException("broken"));
        await vm.PendingGrouping!;

        Assert.False(vm.IsGrouping);
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.SummaryText);
        Assert.Equal(AppText.AbuseNetworksFailureLine(AbuseNetworksFailure.LookupFailed), vm.EmptyText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Grouping_Answer_Touches_None_Of_The_Run_Lines(bool fails)
    {
        var held = new HeldNetworks(_abuse);
        using AbuseIpDbViewModel vm = CreateHeld(held);
        await vm.ActivateAsync();
        string facts = vm.FactsText;
        string status = vm.StatusText;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        if (fails)
            held.Runs[0].Answer.SetResult(NotGrouped(AbuseNetworksFailure.StorageError));
        else
            held.Runs[0].Answer.SetResult(Grouped([Group(1)]));
        await vm.PendingGrouping!;

        Assert.Equal(facts, vm.FactsText);
        Assert.Equal(status, vm.StatusText);
        Assert.Contains(nameof(vm.IsGrouping), raised);
        Assert.DoesNotContain(nameof(vm.FactsText), raised);
        Assert.DoesNotContain(nameof(vm.StatusText), raised);
        Assert.DoesNotContain(nameof(vm.IsStatusWarning), raised);
        Assert.DoesNotContain(nameof(vm.ActionText), raised);
        Assert.DoesNotContain(nameof(vm.IsWorking), raised);
    }

    // ---- the offending networks: when they rerun ----

    [Fact]
    public async Task The_First_Activation_Groups_And_A_Second_With_The_Same_Inputs_Does_Not()
    {
        _ipinfo.Dataset = DatasetA;
        _abuse.NetworksResults.Enqueue(Grouped([Group(1)]));
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.True(_abuse.NetworksTokens[0].CanBeCanceled);

        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.Equal(2, _abuse.StatusCalls);
    }

    [Theory]
    [InlineData("other dataset", 2)]
    [InlineData("equal dataset", 1)]
    [InlineData("no dataset", 2)]
    [InlineData("ipv6 off", 2)]
    [InlineData("keep days", 2)]
    [InlineData("keep days clamped the same", 1)]
    public async Task Changed_Inputs_Regroup_And_Equal_Ones_Do_Not(string change, int calls)
    {
        _ipinfo.Dataset = DatasetA;
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 365 };
        _abuse.NetworksResults.Enqueue(Grouped([Group(1)]));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        switch (change)
        {
            case "other dataset":
                _ipinfo.Dataset = DatasetA with { Checksum = "bb22" };
                break;
            case "equal dataset":
                _ipinfo.Dataset = DatasetA with { };
                Assert.NotSame(DatasetA, _ipinfo.Dataset);
                break;
            case "no dataset":
                _ipinfo.Dataset = null;
                break;
            case "ipv6 off":
                _ipinfo.Settings = _ipinfo.Settings with { IPv6Enabled = false };
                break;
            case "keep days":
                _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
                break;
            case "keep days clamped the same":
                _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 500 };
                break;
        }

        await GroupedAsync(vm);

        Assert.Equal(calls, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Finished_Grouping_Keeps_The_Inputs_Its_Answer_Names()
    {
        // The settings changed between the start and the service's read: the answer names what it grouped
        _ipinfo.Dataset = DatasetA;
        _abuse.NetworksResults.Enqueue(Grouped([Group(1)], keepDays: 7));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        await GroupedAsync(vm);
        Assert.Equal(2, _abuse.NetworksCalls);

        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        _abuse.NetworksResults.Enqueue(Grouped([Group(1)], keepDays: 7));
        await GroupedAsync(vm);
        await GroupedAsync(vm);
        Assert.Equal(3, _abuse.NetworksCalls);
    }

    [Theory]
    [InlineData((int)AbuseNetworksFailure.StorageError, 2)]
    [InlineData((int)AbuseNetworksFailure.LookupFailed, 2)]
    [InlineData((int)AbuseNetworksFailure.DatasetChanged, 2)]
    [InlineData((int)AbuseNetworksFailure.ClockWrong, 1)]
    [InlineData((int)AbuseNetworksFailure.NoDownload, 1)]
    [InlineData((int)AbuseNetworksFailure.NoDataset, 1)]
    [InlineData((int)AbuseNetworksFailure.ListNewer, 1)]
    [InlineData((int)AbuseNetworksFailure.ListUnreadable, 1)]
    public async Task Only_A_Failure_That_Can_Heal_Regroups_On_A_Show_With_The_Same_Inputs(int failureValue, int calls)
    {
        _ipinfo.Dataset = DatasetA;
        _abuse.NetworksResults.Enqueue(NotGrouped((AbuseNetworksFailure)failureValue));
        // Both shows read a clock still behind, so only the show itself can group again
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.StatusResults.Enqueue(ClockBehind());
        using AbuseIpDbViewModel vm = Create();

        await GroupedAsync(vm);
        await GroupedAsync(vm);

        Assert.Equal(calls, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Show_After_A_Grouping_That_Threw_Regroups()
    {
        var held = new HeldNetworks(_abuse);
        using AbuseIpDbViewModel vm = CreateHeld(held);
        await vm.ActivateAsync();
        held.Runs[0].Answer.SetException(new InvalidOperationException("broken"));
        await vm.PendingGrouping!;

        await vm.ActivateAsync();

        Assert.Equal(2, held.Runs.Count);
    }

    [Fact]
    public async Task A_Failure_That_Can_Heal_Is_Left_Alone_While_Its_Rerun_Runs()
    {
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.StorageError));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        _abuse.NetworksGate = new TaskCompletionSource();
        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.False(_abuse.NetworksTokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task No_Dataset_Waits_For_A_Dataset_To_Arrive()
    {
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.NoDataset));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        _ipinfo.Dataset = DatasetA;
        await GroupedAsync(vm);

        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Show_During_A_Grouping_With_The_Same_Inputs_Leaves_It_Running()
    {
        _abuse.NetworksGate = new TaskCompletionSource();
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        await vm.ActivateAsync();

        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.False(_abuse.NetworksTokens[0].IsCancellationRequested);
        Assert.True(vm.IsGrouping);
    }

    [Fact]
    public async Task A_Show_While_A_Download_Is_Held_Still_Groups_When_Stale()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.ServiceError));
        using AbuseIpDbViewModel vm = Create();
        Task download = StartHeldDownload(vm);

        await vm.ActivateAsync();

        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.True(vm.IsWorking);
        await ReleaseAsync(download);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Show_That_Joins_A_Status_Read_Still_Groups_When_Stale()
    {
        var held = new TaskCompletionSource();
        _abuse.StatusGate = held;
        using AbuseIpDbViewModel vm = Create();

        Task first = vm.ActivateAsync();
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        Task second = vm.ActivateAsync();

        Assert.Equal(1, _abuse.StatusCalls);
        Assert.Equal(2, _abuse.NetworksCalls);
        held.SetResult();
        await first;
        await second;
    }

    [Fact]
    public async Task A_Show_Whose_Settings_Read_Throws_Never_Throws_And_Skips_The_Grouping()
    {
        _ipinfo.SettingsFailure = new InvalidOperationException("settings broken");
        using AbuseIpDbViewModel vm = Create();

        Task activation = vm.ActivateAsync();
        await activation;

        Assert.True(activation.IsCompletedSuccessfully);
        Assert.Equal(0, _abuse.NetworksCalls);
        Assert.Null(vm.PendingGrouping);
        Assert.False(vm.IsGrouping);
    }

    [Theory]
    [InlineData((int)AbuseIpDbFailure.None, 2)]
    [InlineData((int)AbuseIpDbFailure.ServiceError, 1)]
    [InlineData((int)AbuseIpDbFailure.LimitReached, 1)]
    public async Task Only_A_Download_That_Ends_None_Regroups(int failureValue, int calls)
    {
        _abuse.DownloadResults.Enqueue(Downloaded((AbuseIpDbFailure)failureValue));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(calls, _abuse.NetworksCalls);
        if (calls == 2)
            Assert.Equal(nameof(IAbuseIpDbService.FindOffendingNetworksAsync), _abuse.Calls[^1]);
    }

    [Fact]
    public async Task A_Download_Refused_For_A_Clock_Behind_Gives_Its_Line()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.ClockBehind));
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.StatusResults.Enqueue(ClockBehind());
        using AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseDownloadFailure(AbuseIpDbFailure.ClockBehind, null), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.True(vm.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_Status_Read_That_Finds_The_Clock_Put_Right_Groups_Again_After_A_Clock_Behind_Grouping()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.ClockWrong));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);
        Assert.Equal(AppText.AbuseNetworksFailureLine(AbuseNetworksFailure.ClockWrong), vm.EmptyText);

        // Write feed reads the status again: while the clock is still behind, nothing groups
        _abuse.FeedResults.Enqueue(Feed(AbuseFeedWriteFailure.ClockWrong));
        _abuse.StatusResults.Enqueue(ClockBehind());
        await vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.Equal(1, _abuse.NetworksCalls);

        // Once a read says the clock is right, the networks group again with no show
        _abuse.FeedResults.Enqueue(Feed());
        _abuse.StatusResults.Enqueue(Status(last: Now.AddMinutes(-5)));
        await vm.WriteFeedCommand.ExecuteAsync(null);
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.Equal(3, _abuse.StatusCalls);
    }

    [Fact]
    public async Task A_Show_That_Finds_The_Clock_Put_Right_Groups_Again_After_A_Clock_Behind_Grouping()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.ClockWrong));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        _abuse.StatusResults.Enqueue(Status(last: Now.AddMinutes(-5)));
        await GroupedAsync(vm);

        Assert.Equal(2, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Successful_Download_After_A_Clock_Behind_Grouping_Groups_Once()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.ClockWrong));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        // The read after the download finds the clock put right; the download groups again itself, so the read doesn't
        _abuse.DownloadResults.Enqueue(Downloaded());
        _abuse.StatusResults.Enqueue(Status(last: Now));
        await vm.DownloadCommand.ExecuteAsync(null);
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.Equal(2, _abuse.StatusCalls);
    }

    [Fact]
    public async Task Clear_List_After_A_Clock_Behind_Grouping_Groups_Once()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.ClockWrong));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        // The read after the clear finds the clock put right (no download is left to be ahead of); Clear list groups
        // again itself, so the read doesn't
        _abuse.StatusResults.Enqueue(Status(AbuseListState.Missing));
        await vm.ClearListCommand.ExecuteAsync(null);
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(1, _abuse.ClearCalls);
        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.Equal(2, _abuse.StatusCalls);
    }

    [Fact]
    public async Task A_Status_Read_Answered_After_The_Close_Stopped_The_Runs_Starts_No_Grouping()
    {
        _abuse.StatusResults.Enqueue(ClockBehind());
        _abuse.NetworksResults.Enqueue(NotGrouped(AbuseNetworksFailure.ClockWrong));
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);
        Assert.Equal(1, _abuse.NetworksCalls);

        // A show's read is out when the close stops the runs, and then finds the clock put right
        var reading = new TaskCompletionSource();
        _abuse.StatusGate = reading;
        _abuse.StatusResults.Enqueue(Status(last: Now.AddMinutes(-5)));
        Task show = vm.ActivateAsync();
        await vm.StopForCloseAsync();
        reading.SetResult();
        await show;
        if (vm.PendingGrouping is { } pending)
            await pending;

        Assert.Equal(2, _abuse.StatusCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Download_Supersedes_A_Grouping_Still_Running()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        _abuse.NetworksGate = new TaskCompletionSource();
        using AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.True(_abuse.NetworksTokens[0].IsCancellationRequested);
        Assert.False(vm.IsGrouping);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("storage error")]
    [InlineData("throws")]
    public async Task A_Clear_List_That_Ran_Regroups_Whatever_It_Gave(string outcome)
    {
        if (outcome == "storage error")
            _abuse.ClearResults.Enqueue(AbuseIpDbFailure.StorageError);
        if (outcome == "throws")
        {
            var failing = new TaskCompletionSource();
            failing.SetException(new InvalidOperationException("broken"));
            _abuse.ClearGate = failing;
        }

        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Equal(1, _abuse.ClearCalls);
        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.Equal(nameof(IAbuseIpDbService.FindOffendingNetworksAsync), _abuse.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Clear_List_Answered_No_Or_Whose_Dialog_Failed_Does_Not_Regroup(bool dialogFails)
    {
        if (dialogFails)
            _ui.ConfirmException = new InvalidOperationException("dialog failed");
        else
            _ui.ConfirmResult = false;
        using AbuseIpDbViewModel vm = Create();
        await GroupedAsync(vm);

        await vm.ClearListCommand.ExecuteAsync(null);

        Assert.Equal(0, _abuse.ClearCalls);
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    // ---- the offending networks: independence ----

    [Fact]
    public async Task The_Runs_Go_Ahead_While_A_Grouping_Is_Held()
    {
        _abuse.NetworksGate = new TaskCompletionSource();
        _abuse.FeedResults.Enqueue(Feed());
        _abuse.EmptyResults.Enqueue(Feed());
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.ServiceError));
        using AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();

        Assert.True(vm.IsGrouping);
        Assert.False(vm.IsWorking);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));

        await vm.WriteFeedCommand.ExecuteAsync(null);
        await vm.EmptyFeedCommand.ExecuteAsync(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.IsGrouping);
        Assert.Equal(1, _abuse.NetworksCalls);

        await vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal(1, _abuse.FeedCalls);
        Assert.Equal(1, _abuse.EmptyCalls);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.Equal(1, _abuse.ClearCalls);
        Assert.False(vm.IsWorking);
    }

    // ---- the offending networks: Look up ----

    [Fact]
    public void Look_Up_Raises_The_Rows_ASN()
    {
        using AbuseIpDbViewModel vm = Create();
        var asked = new List<string>();
        vm.LookUpRequested += (_, asn) => asked.Add(asn);

        vm.LookUpCommand.Execute(OffendingNetworkRow.From(Group(15169)));

        Assert.Equal(["AS15169"], asked);
    }

    [Fact]
    public async Task Look_Up_Needs_A_Row_And_An_Available_Page_But_Not_An_Idle_One()
    {
        OffendingNetworkRow row = OffendingNetworkRow.From(Group(15169));
        using var unavailable = new AbuseIpDbViewModel(null, _ipinfo, _ui, _gate, null, _time, Exports);
        Assert.False(unavailable.LookUpCommand.CanExecute(row));

        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.ServiceError));
        _abuse.NetworksGate = new TaskCompletionSource();
        AbuseIpDbViewModel vm = Create();
        Assert.False(vm.LookUpCommand.CanExecute(null));
        Assert.True(vm.LookUpCommand.CanExecute(row));

        await vm.ActivateAsync();
        Task download = StartHeldDownload(vm);
        using (_gate.Hold())
        {
            Assert.True(vm.IsGrouping);
            Assert.True(vm.IsWorking);
            Assert.True(vm.LookUpCommand.CanExecute(row));
        }

        await ReleaseAsync(download);
        int changed = 0;
        vm.LookUpCommand.CanExecuteChanged += (_, _) => changed++;
        vm.Dispose();
        Assert.False(vm.LookUpCommand.CanExecute(row));
        Assert.True(changed > 0);

        var asked = new List<string>();
        vm.LookUpRequested += (_, asn) => asked.Add(asn);
        vm.LookUpCommand.Execute(row);
        Assert.Empty(asked);
    }

    // ---- the offending networks: Open in ipinfo.io ----

    [Fact]
    public async Task Open_In_Ipinfo_Opens_The_Rows_AS_Page()
    {
        using AbuseIpDbViewModel vm = Create();
        OffendingNetworkRow row = OffendingNetworkRow.From(Group(15169));
        Assert.Equal(15169u, row.Asn);

        Assert.True(vm.OpenNetworkInIpinfoCommand.CanExecute(row));
        await vm.OpenNetworkInIpinfoCommand.ExecuteAsync(row);

        Assert.Equal(["https://ipinfo.io/AS15169"], _ui.OpenedPages.Select(p => p.Uri.AbsoluteUri));
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task Open_In_Ipinfo_Needs_A_Row_And_An_Available_Page()
    {
        OffendingNetworkRow row = OffendingNetworkRow.From(Group(15169));
        using var unavailable = new AbuseIpDbViewModel(null, _ipinfo, _ui, _gate, null, _time, Exports);
        Assert.False(unavailable.OpenNetworkInIpinfoCommand.CanExecute(row));

        AbuseIpDbViewModel vm = Create();
        Assert.False(vm.OpenNetworkInIpinfoCommand.CanExecute(null));
        await vm.OpenNetworkInIpinfoCommand.ExecuteAsync(null);
        Assert.Empty(_ui.OpenedPages);

        // Not tied to the work gate: a browser is no page run
        using (_gate.Hold())
        {
            Assert.True(vm.OpenNetworkInIpinfoCommand.CanExecute(row));
        }

        int changed = 0;
        vm.OpenNetworkInIpinfoCommand.CanExecuteChanged += (_, _) => changed++;
        vm.Dispose();
        Assert.False(vm.OpenNetworkInIpinfoCommand.CanExecute(row));
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task A_Page_That_Wont_Open_Gives_Its_Line()
    {
        _ui.FailOpenWebPage = true;
        using AbuseIpDbViewModel vm = Create();

        await vm.OpenNetworkInIpinfoCommand.ExecuteAsync(OffendingNetworkRow.From(Group(15169)));

        Assert.Equal(AppText.WebPageOpenFailed, vm.ActionText);
        Assert.Empty(_ui.OpenedPages);
    }

    // ---- the offending networks: dispose ----

    [Fact]
    public async Task Dispose_Cancels_The_Grouping_And_Its_Answer_After_Changes_Nothing()
    {
        var held = new HeldNetworks(_abuse);
        AbuseIpDbViewModel vm = CreateHeld(held);
        await vm.ActivateAsync();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Dispose();
        held.Runs[0].Answer.SetResult(Grouped([Group(1)]));
        await vm.PendingGrouping!;
        held.Runs[0].Progress!.Report(new GroupingProgress(5, 10));

        Assert.True(held.Runs[0].Token.IsCancellationRequested);
        Assert.Empty(vm.Rows);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task Dispose_Cancels_The_Fakes_Grouping_Token()
    {
        _abuse.NetworksGate = new TaskCompletionSource();
        AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();

        vm.Dispose();
        await vm.PendingGrouping!;

        Assert.True(_abuse.NetworksTokens[0].IsCancellationRequested);
        await vm.ActivateAsync();
        Assert.Equal(1, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task Dispose_Raises_Nothing_When_The_Grouping_Ends_Inside_The_Cancel()
    {
        // The fake's call ends inside Cancel, so the run's end runs while it is still the newest grouping
        _abuse.NetworksGate = new TaskCompletionSource();
        AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();
        Assert.True(vm.IsGrouping);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Task pending = vm.PendingGrouping!;
        vm.Dispose();

        // Ended inside the cancel, or the test would never reach the guard it pins
        Assert.True(pending.IsCompleted);
        await pending;
        Assert.True(_abuse.NetworksTokens[0].IsCancellationRequested);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task Grouping_Stays_On_Between_Two_Groupings()
    {
        _abuse.NetworksGate = new TaskCompletionSource();
        using AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();
        Assert.True(vm.IsGrouping);
        var grouping = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsGrouping))
                grouping.Add(vm.IsGrouping);
        };

        // The first grouping's call ends inside the cancel that supersedes it
        _ipinfo.Settings = _ipinfo.Settings with { AbuseIpDbKeepDays = 7 };
        var second = new TaskCompletionSource();
        _abuse.NetworksGate = second;
        Task first = vm.PendingGrouping!;
        Task activation = vm.ActivateAsync();

        // Ended inside the cancel, or no false could show between the two groupings
        Assert.True(first.IsCompleted);
        await activation;
        Assert.True(_abuse.NetworksTokens[0].IsCancellationRequested);
        Assert.Equal(2, _abuse.NetworksCalls);
        Assert.DoesNotContain(false, grouping);
        Assert.True(vm.IsGrouping);

        second.SetResult();
        await vm.PendingGrouping!;
        Assert.False(vm.IsGrouping);
    }

    // ---- the close ----

    [Fact]
    public async Task Asks_Before_Close_Only_While_A_Download_Is_With_The_Service()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        var reading = new TaskCompletionSource();
        _abuse.StatusGate = reading;
        using AbuseIpDbViewModel vm = Create();
        Assert.False(vm.AsksBeforeClose);

        Task run = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.AsksBeforeClose);

        // The call has ended; the status read after it is still out, and that never asks
        held.SetResult();
        await WaitUntilAsync(() => _abuse.StatusCalls == 1);
        Assert.True(vm.IsWorking);
        Assert.False(vm.AsksBeforeClose);

        reading.SetResult();
        await run;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task Asks_Before_Close_For_Both_Download_Calls_But_Not_While_The_Key_Prompt_Is_Open()
    {
        _abuse.DownloadResults.Enqueue(Downloaded(AbuseIpDbFailure.NoKey));
        _abuse.DownloadResults.Enqueue(Downloaded());
        var first = new TaskCompletionSource();
        _abuse.DownloadGate = first;
        var second = new TaskCompletionSource();
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();
        var whilePrompting = new List<bool>();
        _ui.OnDialogOpen = () =>
        {
            whilePrompting.Add(vm.AsksBeforeClose);
            _abuse.DownloadGate = second;
        };

        Task run = vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.AsksBeforeClose);

        first.SetResult();
        await WaitUntilAsync(() => _abuse.DownloadCalls == 2);
        Assert.Equal([false], whilePrompting);
        Assert.True(vm.AsksBeforeClose);

        second.SetResult();
        await run;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task Asks_Before_Close_While_Clear_List_Is_With_The_Service_But_Not_While_It_Confirms()
    {
        var held = new TaskCompletionSource();
        _abuse.ClearGate = held;
        using AbuseIpDbViewModel vm = Create();
        var whileConfirming = new List<bool>();
        _ui.OnDialogOpen = () => whileConfirming.Add(vm.AsksBeforeClose);

        Task run = vm.ClearListCommand.ExecuteAsync(null);
        Assert.Equal([false], whileConfirming);
        Assert.Equal(1, _abuse.ClearCalls);
        Assert.True(vm.AsksBeforeClose);

        held.SetResult();
        await run;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task Asks_Before_Close_While_Write_Feed_Is_With_The_Service()
    {
        _abuse.FeedResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.FeedGate = held;
        using AbuseIpDbViewModel vm = Create();
        Assert.False(vm.AsksBeforeClose);

        Task run = vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(vm.AsksBeforeClose);

        held.SetResult();
        await run;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task Asks_Before_Close_While_Empty_Feed_Is_With_The_Service_But_Not_While_It_Confirms()
    {
        _abuse.EmptyResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.EmptyGate = held;
        using AbuseIpDbViewModel vm = Create();
        var whileConfirming = new List<bool>();
        _ui.OnDialogOpen = () => whileConfirming.Add(vm.AsksBeforeClose);

        Task run = vm.EmptyFeedCommand.ExecuteAsync(null);
        Assert.Equal([false], whileConfirming);
        Assert.Equal(1, _abuse.EmptyCalls);
        Assert.True(vm.AsksBeforeClose);

        held.SetResult();
        await run;
        Assert.False(vm.AsksBeforeClose);
    }

    [Fact]
    public async Task The_Grouping_Alone_Never_Asks_Before_Close()
    {
        var held = new TaskCompletionSource();
        _abuse.NetworksGate = held;
        using AbuseIpDbViewModel vm = Create();

        await vm.ActivateAsync();
        Assert.True(vm.IsGrouping);
        Assert.False(vm.AsksBeforeClose);
        Assert.True(await vm.AskBeforeCloseAsync());
        Assert.True(vm.StopForCloseAsync().IsCompleted);
        Assert.Empty(_ui.Confirms);
        Assert.False(_abuse.NetworksTokens[0].IsCancellationRequested);

        held.SetResult();
        await vm.PendingGrouping!;
    }

    [Fact]
    public async Task Asking_With_Nothing_Out_Agrees_With_No_Question()
    {
        using AbuseIpDbViewModel vm = Create();

        Assert.True(await vm.AskBeforeCloseAsync());
        Assert.Empty(_ui.Confirms);
    }

    [Fact]
    public async Task Asking_During_A_Download_Names_The_Download_Holds_The_Gate_And_A_No_Cancels_Nothing()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);
        var canStart = new List<bool>();
        _ui.OnDialogOpen = () => canStart.Add(_gate.CanStart);
        _ui.ConfirmResult = false;

        Assert.False(await vm.AskBeforeCloseAsync());

        var (title, message, confirmText) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.CloseAbuseWorkTitle, title);
        Assert.Equal(AppText.CloseAbuseDownloadBody, message);
        Assert.Equal(AppText.CloseAbuseWorkButton, confirmText);
        Assert.Equal([false], canStart);
        Assert.True(_gate.CanStart);
        Assert.False(_abuse.DownloadTokens[0].IsCancellationRequested);
        Assert.True(vm.IsWorking);
        Assert.True(vm.AsksBeforeClose);

        held.SetResult();
        await run;
        Assert.Equal(AppText.AbuseDownloaded(Downloaded(), KeepDays), vm.StatusText);
    }

    [Fact]
    public async Task Asking_During_Write_Feed_Names_The_Other_Work_And_A_Yes_Cancels_Nothing()
    {
        _abuse.FeedResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.FeedGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.WriteFeedCommand.ExecuteAsync(null);

        Assert.True(await vm.AskBeforeCloseAsync());

        var (title, message, confirmText) = Assert.Single(_ui.Confirms);
        Assert.Equal(AppText.CloseAbuseWorkTitle, title);
        Assert.Equal(AppText.CloseAbuseWorkBody, message);
        Assert.Equal(AppText.CloseAbuseWorkButton, confirmText);
        Assert.False(_abuse.FeedTokens[0].IsCancellationRequested);

        held.SetResult();
        await run;
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("empty")]
    public async Task Asking_During_Clear_List_Or_Empty_Feed_Names_The_Other_Work(string which)
    {
        _abuse.EmptyResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        if (which == "clear")
            _abuse.ClearGate = held;
        else
            _abuse.EmptyGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = which == "clear" ? vm.ClearListCommand.ExecuteAsync(null) : vm.EmptyFeedCommand.ExecuteAsync(null);

        Assert.True(await vm.AskBeforeCloseAsync());

        Assert.Equal(2, _ui.Confirms.Count);
        Assert.Equal(AppText.CloseAbuseWorkTitle, _ui.Confirms[1].Title);
        Assert.Equal(AppText.CloseAbuseWorkBody, _ui.Confirms[1].Message);

        held.SetResult();
        await run;
    }

    [Fact]
    public async Task A_Close_Question_That_Fails_To_Show_Keeps_The_Window_Open()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);
        _ui.ConfirmException = new InvalidOperationException("dialog failed");

        Assert.False(await vm.AskBeforeCloseAsync());
        Assert.True(_gate.CanStart);
        Assert.False(_abuse.DownloadTokens[0].IsCancellationRequested);

        held.SetResult();
        await run;
    }

    [Fact]
    public async Task Stopping_For_The_Close_Cancels_A_Download_Not_Yet_Answered_And_Waits_For_The_Run()
    {
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        Task stop = vm.StopForCloseAsync();

        Assert.True(_abuse.DownloadTokens[0].IsCancellationRequested);
        await stop;
        Assert.True(run.IsCompleted);
        Assert.False(vm.IsWorking);
        Assert.False(vm.AsksBeforeClose);
        Assert.Equal(1, _abuse.DownloadCalls);
    }

    [Fact]
    public async Task Stopping_For_The_Close_Waits_For_A_Merge_Already_Under_Way()
    {
        AbuseDownloadResult result = Downloaded();
        _abuse.DownloadResults.Enqueue(result);
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadIgnoresToken = true;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        Task stop = vm.StopForCloseAsync();

        Assert.True(_abuse.DownloadTokens[0].IsCancellationRequested);
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(200)));
        Assert.False(run.IsCompleted);

        held.SetResult();
        await stop;
        Assert.True(run.IsCompleted);
        Assert.False(vm.IsWorking);
    }

    [Fact]
    public async Task Stopping_For_The_Close_With_Nothing_Out_Is_Already_Done()
    {
        using AbuseIpDbViewModel vm = Create();

        Task stop = vm.StopForCloseAsync();

        Assert.True(stop.IsCompletedSuccessfully);
        await stop;
        Assert.Equal(0, _abuse.StatusCalls);
    }

    [Fact]
    public async Task Stopping_For_The_Close_Never_Throws()
    {
        // A merge under way that then fails, with the page disposed before it ends
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadIgnoresToken = true;
        AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        Task stop = vm.StopForCloseAsync();
        vm.Dispose();
        held.SetResult();

        await stop;
        Assert.True(run.IsCompleted);
        Assert.True(vm.StopForCloseAsync().IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(AbuseIpDbFailure.NoKey)]
    [InlineData(AbuseIpDbFailure.KeyRejected)]
    public async Task A_Download_Answered_Without_A_Usable_Key_After_The_Stop_Began_Asks_Nothing_And_Retries_Nothing(
        AbuseIpDbFailure failure)
    {
        _abuse.DownloadResults.Enqueue(Downloaded(failure));
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadIgnoresToken = true;
        _ui.AbuseKeyAnswers.Enqueue(SentinelKey);
        using AbuseIpDbViewModel vm = CreateWithPrompt();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        Task stop = vm.StopForCloseAsync();
        held.SetResult();
        await stop;
        await run;

        Assert.Empty(_ui.AbuseKeyPrompts);
        Assert.Empty(_abuse.SavedKeys);
        Assert.Equal(1, _abuse.DownloadCalls);
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.Equal(0, _abuse.NetworksCalls);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("feed")]
    [InlineData("empty")]
    public async Task Stopping_For_The_Close_Waits_For_Other_Work_And_Cancels_None_Of_It(string which)
    {
        _abuse.FeedResults.Enqueue(Feed());
        _abuse.EmptyResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        switch (which)
        {
            case "clear":
                _abuse.ClearGate = held;
                break;
            case "feed":
                _abuse.FeedGate = held;
                break;
            default:
                _abuse.EmptyGate = held;
                break;
        }

        using AbuseIpDbViewModel vm = Create();
        Task run = which switch
        {
            "clear" => vm.ClearListCommand.ExecuteAsync(null),
            "feed" => vm.WriteFeedCommand.ExecuteAsync(null),
            _ => vm.EmptyFeedCommand.ExecuteAsync(null),
        };
        Assert.True(vm.AsksBeforeClose);
        CancellationToken token = which switch
        {
            "clear" => Assert.Single(_abuse.ClearTokens),
            "feed" => Assert.Single(_abuse.FeedTokens),
            _ => Assert.Single(_abuse.EmptyTokens),
        };

        Task stop = vm.StopForCloseAsync();

        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(200)));
        Assert.False(token.IsCancellationRequested);
        Assert.False(run.IsCompleted);

        held.SetResult();
        await stop;
        await run;
        Assert.False(token.IsCancellationRequested);

        // A closing page reads nothing more once its call has ended
        Assert.Equal(0, _abuse.StatusCalls);
        Assert.Equal(0, _abuse.NetworksCalls);
    }

    [Fact]
    public async Task A_Refused_Press_While_A_Download_Is_Out_Leaves_The_Stop_Waiting_For_The_Download()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadIgnoresToken = true;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        // Refused at once: the download is working
        vm.ClearListCommand.Execute(null);
        vm.WriteFeedCommand.Execute(null);
        Assert.Equal(0, _abuse.ClearCalls);
        Assert.Equal(0, _abuse.FeedCalls);

        Task stop = vm.StopForCloseAsync();

        Assert.True(_abuse.DownloadTokens[0].IsCancellationRequested);
        Assert.NotSame(stop, await Task.WhenAny(stop, Task.Delay(200)));
        held.SetResult();
        await stop;
        await run;
    }

    [Fact]
    public async Task A_Download_That_Faults_After_The_Stop_Began_Leaves_The_Stop_Completed()
    {
        // Nothing queued, so the held download throws once it is released
        var held = new TaskCompletionSource();
        _abuse.DownloadGate = held;
        _abuse.DownloadIgnoresToken = true;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);

        Task stop = vm.StopForCloseAsync();
        held.SetResult();

        await stop;
        Assert.True(stop.IsCompletedSuccessfully);
        await run;
        Assert.Equal(0, _abuse.StatusCalls);
    }

    [Fact]
    public async Task Stopping_For_The_Close_Past_The_Call_Returns_At_Once_And_Cancels_Nothing()
    {
        _abuse.DownloadResults.Enqueue(Downloaded());
        var reading = new TaskCompletionSource();
        _abuse.StatusGate = reading;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.DownloadCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _abuse.StatusCalls == 1);
        Assert.True(vm.IsWorking);

        Task stop = vm.StopForCloseAsync();

        Assert.True(stop.IsCompletedSuccessfully);
        Assert.False(_abuse.DownloadTokens[0].IsCancellationRequested);
        Assert.False(_abuse.StatusTokens[0].IsCancellationRequested);

        reading.SetResult();
        await run;
    }

    [Fact]
    public async Task Holding_The_Runs_For_The_Close_Turns_The_Run_Commands_Off_Until_It_Is_Released()
    {
        using AbuseIpDbViewModel vm = Create();
        int changes = 0;
        vm.DownloadCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));

        IDisposable hold = vm.HoldRunsForClose();

        Assert.True(changes > 0);
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(0, _abuse.DownloadCalls);

        int changesHeld = changes;
        hold.Dispose();

        Assert.True(changes > changesHeld);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));

        // A second release does nothing, and holds nest
        IDisposable first = vm.HoldRunsForClose();
        IDisposable second = vm.HoldRunsForClose();
        hold.Dispose();
        first.Dispose();
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        second.Dispose();
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
    }

    [Fact]
    public async Task Holding_The_Runs_For_The_Close_Leaves_A_Run_Already_Going()
    {
        _abuse.FeedResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.FeedGate = held;
        using AbuseIpDbViewModel vm = Create();
        Task run = vm.WriteFeedCommand.ExecuteAsync(null);

        using IDisposable hold = vm.HoldRunsForClose();
        held.SetResult();
        await run;

        Assert.False(_abuse.FeedTokens[0].IsCancellationRequested);
        Assert.Equal(1, _abuse.StatusCalls);
        Assert.False(vm.IsWorking);
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
    }

    // ---- the New addresses button ----

    private static readonly DateTimeOffset NewAsOf = new(2026, 10, 20, 7, 30, 45, TimeSpan.Zero);

    private static AbuseListStatus WithNew(int newCount, AbuseListState state = AbuseListState.Usable) =>
        Status(state) with { NewCount = newCount };

    private static AbuseNewAddresses NewSet(int count) =>
        new(AbuseNewAddressesFailure.None,
            Enumerable.Range(0, count)
                .Select(i => new AbuseNewAddress(Address($"8.8.{i / 256}.{i % 256}"), i % 101,
                    i % 2 == 0 ? new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero) : null))
                .ToList(),
            NewAsOf);

    private static PublicAddress Address(string text)
    {
        Assert.True(IpAddressText.TryParse(text, out IPAddress? address));
        Assert.True(PublicAddress.TryCreate(address.GetAddressBytes(), out PublicAddress? value));
        return value;
    }

    // A page that has read a status naming new addresses, so its button is on
    private async Task<AbuseIpDbViewModel> CreateWithNewAsync(int newCount = 3)
    {
        _abuse.StatusResults.Enqueue(WithNew(newCount));
        AbuseIpDbViewModel vm = Create();
        await vm.ActivateAsync();
        return vm;
    }

    [Fact]
    public async Task New_addresses_is_enabled_only_after_a_status_that_names_new_addresses()
    {
        using AbuseIpDbViewModel vm = Create();
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));

        _abuse.StatusResults.Enqueue(WithNew(0));
        await vm.ActivateAsync();
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));

        _abuse.StatusResults.Enqueue(WithNew(2));
        await vm.ActivateAsync();
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        // A later status without them turns it off again
        _abuse.StatusResults.Enqueue(WithNew(0));
        await vm.ActivateAsync();
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_addresses_is_off_before_start()
    {
        var gate = new WorkGate();
        _abuse.StatusResults.Enqueue(WithNew(3));
        using AbuseIpDbViewModel vm = Immediate(new(_abuse, _ipinfo, _ui, gate, null, _time, Exports));
        await vm.ActivateAsync();

        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
        Assert.Equal(0, _abuse.StatusCalls);

        gate.SetStarted(true);
        await WaitUntilAsync(() => vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_addresses_is_off_while_a_run_works_under_a_close_hold_and_after_dispose()
    {
        AbuseIpDbViewModel vm = await CreateWithNewAsync();
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        _abuse.FeedResults.Enqueue(Feed());
        var held = new TaskCompletionSource();
        _abuse.FeedGate = held;
        _abuse.StatusResults.Enqueue(WithNew(3));
        Task run = vm.WriteFeedCommand.ExecuteAsync(null);
        Assert.True(vm.IsWorking);
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
        held.SetResult();
        await run;
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        IDisposable hold = vm.HoldRunsForClose();
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
        hold.Dispose();
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        vm.Dispose();
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_press_holds_the_gate_only_while_the_dialog_is_open()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(3);
        var read = new TaskCompletionSource();
        _abuse.NewAddressesGate = read;
        var dialog = new TaskCompletionSource();
        _ui.NewAddressesAnswer = _ => dialog.Task;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);

        // The read is out: the gate is free for Lookup and the Settings page, but every page command is off and the
        // close doesn't ask
        Assert.True(_gate.CanStart);
        Assert.False(_gate.IsHeld);
        Assert.False(vm.DownloadCommand.CanExecute(null));
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
        Assert.False(vm.IsWorking);
        Assert.False(vm.AsksBeforeClose);
        Assert.Equal(_abuse.StatusTokens[0], _abuse.NewAddressesTokens[0]);

        read.SetResult();
        await WaitUntilAsync(() => _ui.NewAddressesPrompts.Count == 1);

        // The dialog is open: the gate is held and the commands are off, and still nothing for the close to ask about
        Assert.False(_gate.CanStart);
        Assert.All(ActionCommands(vm), command => Assert.False(command.CanExecute(null)));
        Assert.False(vm.AsksBeforeClose);
        Assert.True(await vm.AskBeforeCloseAsync());
        Assert.Empty(_ui.Confirms);

        dialog.SetResult();
        await press;

        Assert.True(_gate.CanStart);
        Assert.True(vm.DownloadCommand.CanExecute(null));
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));
        Assert.Equal(1, _abuse.StatusCalls);
    }

    [Fact]
    public async Task Another_hold_taken_during_the_read_shows_no_dialog_and_says_why()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(3);
        var read = new TaskCompletionSource();
        _abuse.NewAddressesGate = read;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);

        // Another dialog opens while the read is out
        IDisposable other = _gate.Hold();
        read.SetResult();
        await press;

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(AppText.AbuseNewAddressesBusy, vm.StatusText);
        Assert.True(vm.IsStatusWarning);

        other.Dispose();
        Assert.True(_gate.CanStart);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_close_hold_taken_during_the_read_shows_no_dialog()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(3);
        var read = new TaskCompletionSource();
        _abuse.NewAddressesGate = read;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        using IDisposable hold = vm.HoldRunsForClose();
        read.SetResult();
        await press;

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task An_update_alone_doesnt_refuse_the_dialog()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(3);

        // An update makes the gate busy but holds nothing
        _gate.SetBusy(true);
        Assert.False(_gate.CanStart);
        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Single(_ui.NewAddressesPrompts);
        Assert.NotEqual(AppText.AbuseNewAddressesBusy, vm.StatusText);
    }

    [Fact]
    public async Task Look_up_is_off_from_the_press_until_the_dialog_closes()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        OffendingNetworkRow row = OffendingNetworkRow.From(Group(15169));
        Assert.True(vm.LookUpCommand.CanExecute(row));
        _abuse.NewAddresses = NewSet(3);
        var read = new TaskCompletionSource();
        _abuse.NewAddressesGate = read;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        Assert.False(vm.LookUpCommand.CanExecute(row));

        read.SetResult();
        await press;

        Assert.Single(_ui.NewAddressesPrompts);
        Assert.True(vm.LookUpCommand.CanExecute(row));
    }

    [Fact]
    public async Task A_show_during_a_failures_status_read_joins_it_and_keeps_the_line()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = new AbuseNewAddresses(AbuseNewAddressesFailure.StorageError, [], null);
        var status = new TaskCompletionSource();
        _abuse.StatusGate = status;
        _abuse.StatusResults.Enqueue(WithNew(3));

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _abuse.StatusCalls == 2);

        // The page is shown again while the press's status read is held
        Task shown = vm.ActivateAsync();
        Assert.Equal(2, _abuse.StatusCalls);

        status.SetResult();
        await press;
        await shown;

        Assert.Equal(2, _abuse.StatusCalls);
        Assert.Equal(AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.StorageError), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_read_that_throws_shows_the_storage_line()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddressesException = new InvalidOperationException("broken");
        _abuse.StatusResults.Enqueue(WithNew(3));

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.StorageError), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Rows_with_no_download_time_show_no_dialog_and_the_not_known_line()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(2) with { AsOf = null };
        _abuse.StatusResults.Enqueue(WithNew(3));

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.NotKnown), vm.StatusText);
    }

    [Fact]
    public async Task A_close_during_a_failures_status_read_shows_no_line()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = new AbuseNewAddresses(AbuseNewAddressesFailure.StorageError, [], null);
        var status = new TaskCompletionSource();
        _abuse.StatusGate = status;
        _abuse.StatusResults.Enqueue(WithNew(3));

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _abuse.StatusCalls == 2);
        await vm.StopForCloseAsync();
        status.SetResult();
        await press;

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.NotEqual(AppText.AbuseNewAddressesFailureLine(AbuseNewAddressesFailure.StorageError), vm.StatusText);
    }

    [Fact]
    public async Task A_status_read_that_fails_turns_the_button_off()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        _abuse.StatusException = new InvalidOperationException("broken");
        await vm.ActivateAsync();

        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_prompt_lists_at_most_300_and_copies_every_address()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync(301);
        AbuseNewAddresses set = NewSet(301);
        _abuse.NewAddresses = set;

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        NewAddressesPrompt prompt = Assert.Single(_ui.NewAddressesPrompts);
        Assert.Equal("New addresses", prompt.Title);
        Assert.Equal("A report, so exceptions aren't applied. Added to the list by the download of 2026-10-20 07:30 UTC."
            + " Each line gives the address, its AbuseIPDB score out of 100 and the day of its last report.",
            prompt.Message);
        Assert.Equal("Showing 300 of 301 new addresses", prompt.CountText);
        Assert.Equal(AbuseIpDbViewModel.NewAddressRowLimit, prompt.Rows.Count);
        // WebPage has no value equality, so the rows are compared field by field
        Assert.Equal(("8.8.0.0", "0", "2026-10-19"), (prompt.Rows[0].Address, prompt.Rows[0].Score, prompt.Rows[0].LastReported));
        Assert.Equal(("8.8.0.1", "1", "Unknown"), (prompt.Rows[1].Address, prompt.Rows[1].Score, prompt.Rows[1].LastReported));
        Assert.Equal(set.Addresses.Take(300).Select(a => a.Address.Text), prompt.Rows.Select(r => r.Address));

        string[] copied = prompt.CopyText.Split(Environment.NewLine);
        Assert.Equal(set.Addresses.Select(a => a.Address.Text), copied);
        Assert.Equal("8.8.1.44", copied[^1]);
        Assert.Equal("Copied 301 addresses", prompt.CopiedText);

        // The press touched no line
        Assert.Equal("", vm.StatusText);
        Assert.Equal("", vm.ActionText);
    }

    [Fact]
    public async Task Each_prompt_row_carries_the_two_pages_of_its_address()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync(2);
        _abuse.NewAddresses = NewSet(2);

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        NewAddressesPrompt prompt = Assert.Single(_ui.NewAddressesPrompts);
        Assert.Equal("https://ipinfo.io/8.8.0.0", prompt.Rows[0].IpinfoPage.Uri.AbsoluteUri);
        Assert.Equal("https://www.abuseipdb.com/check/8.8.0.0", prompt.Rows[0].AbuseIpDbPage.Uri.AbsoluteUri);
        Assert.Equal("https://ipinfo.io/8.8.0.1", prompt.Rows[1].IpinfoPage.Uri.AbsoluteUri);
        Assert.Equal("https://www.abuseipdb.com/check/8.8.0.1", prompt.Rows[1].AbuseIpDbPage.Uri.AbsoluteUri);
        Assert.Equal("8.8.0.0" + Environment.NewLine + "8.8.0.1", prompt.CopyText);
        Assert.Empty(_ui.OpenedPages);
    }

    [Fact]
    public async Task One_new_address_reads_in_the_singular()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync(1);
        _abuse.NewAddresses = NewSet(1);

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        NewAddressesPrompt prompt = Assert.Single(_ui.NewAddressesPrompts);
        Assert.Equal("1 new address", prompt.CountText);
        Assert.Equal("8.8.0.0", prompt.CopyText);
        Assert.Equal("Copied 1 address", prompt.CopiedText);
    }

    // A download's end line, set before the press, with a status after it that names new addresses
    private async Task<(AbuseIpDbViewModel vm, string downloadLine)> DownloadedWithNewAsync()
    {
        AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.DownloadResults.Enqueue(Downloaded());
        _abuse.StatusResults.Enqueue(WithNew(3));
        await vm.DownloadCommand.ExecuteAsync(null);
        string downloadLine = vm.StatusText;
        Assert.Equal(AppText.AbuseDownloaded(Downloaded(), KeepDays), downloadLine);
        return (vm, downloadLine);
    }

    [Theory]
    [InlineData(AbuseNewAddressesFailure.NotKnown)]
    [InlineData(AbuseNewAddressesFailure.StorageError)]
    [InlineData(AbuseNewAddressesFailure.None)]
    public async Task A_failure_reads_the_status_again_and_leaves_the_action_line(AbuseNewAddressesFailure failure)
    {
        (AbuseIpDbViewModel vm, string downloadLine) = await DownloadedWithNewAsync();
        using AbuseIpDbViewModel page = vm;
        int statusCalls = _abuse.StatusCalls;

        // None with no rows reads as NotKnown
        _abuse.NewAddresses = new AbuseNewAddresses(failure, [], failure == AbuseNewAddressesFailure.None ? NewAsOf : null);
        _abuse.StatusResults.Enqueue(WithNew(3));
        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(statusCalls + 1, _abuse.StatusCalls);
        AbuseNewAddressesFailure shown = failure == AbuseNewAddressesFailure.None ? AbuseNewAddressesFailure.NotKnown : failure;
        Assert.Equal(AppText.AbuseNewAddressesFailureLine(shown), vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.True(_gate.CanStart);
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));

        // The transient line gives way at the next read, back to the download's line
        _abuse.StatusResults.Enqueue(WithNew(3));
        await vm.ActivateAsync();
        Assert.Equal(downloadLine, vm.StatusText);
        Assert.False(vm.IsStatusWarning);
    }

    [Fact]
    public async Task Not_known_with_the_button_now_off_shows_no_transient_line()
    {
        (AbuseIpDbViewModel vm, string downloadLine) = await DownloadedWithNewAsync();
        using AbuseIpDbViewModel page = vm;

        _abuse.StatusResults.Enqueue(WithNew(0));
        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(downloadLine, vm.StatusText);
        Assert.False(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failure_on_a_list_now_unusable_shows_the_lists_own_line()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = new AbuseNewAddresses(AbuseNewAddressesFailure.ListUnreadable, [], null);
        _abuse.StatusResults.Enqueue(WithNew(3, AbuseListState.Unreadable));

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Equal(AppText.AbuseListStateLine(AbuseListState.Unreadable), vm.StatusText);
        Assert.Empty(_ui.NewAddressesPrompts);
    }

    [Fact]
    public async Task A_dialog_that_fails_to_show_says_so_and_releases_the_gate()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(2);
        _ui.NewAddressesAnswer = _ => throw new InvalidOperationException("no window");

        await vm.ShowNewAddressesCommand.ExecuteAsync(null);

        Assert.Single(_ui.NewAddressesPrompts);
        Assert.Equal(AppText.AbuseNewAddressesNotShown, vm.StatusText);
        Assert.True(vm.IsStatusWarning);
        Assert.True(_gate.CanStart);
        Assert.All(ActionCommands(vm), command => Assert.True(command.CanExecute(null)));
        Assert.True(vm.ShowNewAddressesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispose_during_the_read_shows_nothing_and_releases_the_gate()
    {
        AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(2);
        _abuse.NewAddressesGate = new TaskCompletionSource();
        int statusCalls = _abuse.StatusCalls;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);
        vm.Dispose();
        await press;

        Assert.True(_abuse.NewAddressesTokens[0].IsCancellationRequested);
        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.Equal(statusCalls, _abuse.StatusCalls);
        Assert.Equal("", vm.StatusText);
        Assert.True(_gate.CanStart);
    }

    [Fact]
    public async Task A_read_that_ends_after_the_stop_began_shows_nothing()
    {
        using AbuseIpDbViewModel vm = await CreateWithNewAsync();
        _abuse.NewAddresses = NewSet(2);
        var read = new TaskCompletionSource();
        _abuse.NewAddressesGate = read;

        Task press = vm.ShowNewAddressesCommand.ExecuteAsync(null);

        // The stop doesn't wait for the read: nothing is with the service
        await vm.StopForCloseAsync();
        Assert.False(press.IsCompleted);
        read.SetResult();
        await press;

        Assert.Empty(_ui.NewAddressesPrompts);
        Assert.True(_gate.CanStart);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
