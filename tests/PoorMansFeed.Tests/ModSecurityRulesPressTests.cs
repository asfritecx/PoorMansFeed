using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;

namespace PoorMansFeed.Tests;

/// <summary>The rules press every page shares, over the fakes and a real work gate.</summary>
public sealed class ModSecurityRulesPressTests
{
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly FakeUi _ui = new();
    private readonly WorkGate _gate = new();

    public ModSecurityRulesPressTests() => _gate.SetStarted(true);

    private ModSecurityRulesPress Press() => new(_ipinfo, _ui, _gate);

    private static readonly IReadOnlyList<string> Files =
        [Path.Combine("exports", "modsecurity", "AS15169_ipv4.txt"), Path.Combine("exports", "modsecurity", "AS15169_ipv6.txt")];

    [Fact]
    public void Every_part_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPress(null!, _ui, _gate));
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPress(_ipinfo, null!, _gate));
        Assert.Throws<ArgumentNullException>(() => new ModSecurityRulesPress(_ipinfo, _ui, null!));
    }

    [Fact]
    public async Task A_held_gate_gives_the_busy_line_and_assigns_nothing()
    {
        using IDisposable other = _gate.Hold();

        string? line = await Press().RunAsync(Files, () => true, CancellationToken.None);

        Assert.Equal(AppText.ModSecurityRulesBusy, line);
        Assert.Empty(_ipinfo.AssignRuleIdsCalls);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
    }

    [Fact]
    public async Task The_names_are_the_distinct_file_names_compared_ordinally_in_first_seen_order()
    {
        string[] files =
        [
            Path.Combine("one", "modsecurity", "b.txt"), Path.Combine("one", "modsecurity", "a.txt"),
            Path.Combine("two", "modsecurity", "A.txt"), Path.Combine("three", "a.txt"), "b.txt",
        ];

        string? line = await Press().RunAsync(files, () => true, CancellationToken.None);

        Assert.Null(line);
        Assert.Equal(["b.txt", "a.txt", "A.txt"], Assert.Single(_ipinfo.AssignRuleIdsCalls));
        Assert.Equal(["b.txt", "a.txt", "A.txt"], Assert.Single(_ui.ModSecurityRulesPrompts).FileNames);
    }

    [Theory]
    [InlineData(ModSecurityRuleIdsFailure.StorageError)]
    [InlineData(ModSecurityRuleIdsFailure.Unavailable)]
    [InlineData(ModSecurityRuleIdsFailure.RangeFull)]
    [InlineData(ModSecurityRuleIdsFailure.TooMany)]
    [InlineData(ModSecurityRuleIdsFailure.FileNameInvalid)]
    public async Task A_failed_assignment_gives_its_line_and_no_dialog(ModSecurityRuleIdsFailure failure)
    {
        _ipinfo.AssignRuleIdsResult = new ModSecurityRuleIdsResult(failure, []);

        string? line = await Press().RunAsync(Files, () => true, CancellationToken.None);

        Assert.Equal(AppText.ModSecurityRuleIdsLine(failure), line);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task The_assignment_holds_no_gate_and_a_hold_taken_meanwhile_gives_the_busy_line()
    {
        var pending = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = pending;
        Task<string?> press = Press().RunAsync(Files, () => true, CancellationToken.None);
        Assert.False(_gate.IsHeld);
        Assert.True(_gate.CanStart);

        IDisposable other = _gate.Hold();
        pending.SetResult();
        string? line = await press;
        other.Dispose();

        Assert.Equal(AppText.ModSecurityRulesBusy, line);
        Assert.Single(_ipinfo.AssignRuleIdsCalls);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task A_page_that_may_not_show_gives_the_busy_line_after_assigning()
    {
        string? line = await Press().RunAsync(Files, () => false, CancellationToken.None);

        Assert.Equal(AppText.ModSecurityRulesBusy, line);
        Assert.Single(_ipinfo.AssignRuleIdsCalls);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
    }

    [Fact]
    public async Task The_dialog_gets_the_names_ids_label_location_and_action_and_holds_the_gate_while_open()
    {
        _ipinfo.Settings = new UserSettings
        {
            WriteModSecurityLists = true,
            ModSecurityFirstRuleId = 20_000,
            ModSecurityLocation = "/etc/modsecurity/lists",
            ModSecurityAction = ModSecurityAction.Deny,
        };
        bool? heldWhileOpen = null;
        _ui.OnDialogOpen = () => heldWhileOpen = _gate.IsHeld;
        var open = new TaskCompletionSource();
        _ui.ModSecurityRulesAnswer = _ => open.Task;

        Task<string?> press = Press().RunAsync(Files, () => true, CancellationToken.None);
        Assert.True(_gate.IsHeld);
        open.SetResult();
        string? line = await press;

        Assert.Null(line);
        Assert.True(heldWhileOpen);
        Assert.False(_gate.IsHeld);
        ModSecurityRulesPrompt prompt = Assert.Single(_ui.ModSecurityRulesPrompts);
        Assert.Equal(["AS15169_ipv4.txt", "AS15169_ipv6.txt"], prompt.FileNames);
        Assert.Equal([20_000, 20_001], prompt.RuleIds);
        Assert.Equal(AppText.ModSecurityRuleLabel, prompt.FeedLabel);
        Assert.True(prompt.Location == "/etc/modsecurity/lists");
        Assert.Equal(ModSecurityAction.Deny, prompt.Action);
    }

    [Fact]
    public async Task A_dialog_that_fails_gives_the_generic_line_and_releases_the_gate()
    {
        _ui.ModSecurityRulesAnswer = _ => Task.FromException(new InvalidOperationException("no owner"));

        string? line = await Press().RunAsync(Files, () => true, CancellationToken.None);

        Assert.Equal(AppText.Generic, line);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task An_assignment_that_throws_gives_the_generic_line()
    {
        var pending = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = pending;
        Task<string?> press = Press().RunAsync(Files, () => true, CancellationToken.None);
        pending.SetException(new IOException("disk"));

        Assert.Equal(AppText.Generic, await press);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
    }

    [Fact]
    public async Task A_cancel_that_is_not_the_callers_gives_the_generic_line()
    {
        var pending = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = pending;
        Task<string?> press = Press().RunAsync(Files, () => true, CancellationToken.None);
        pending.SetCanceled();

        Assert.Equal(AppText.Generic, await press);
    }

    [Fact]
    public async Task The_callers_cancel_gives_nothing_to_say()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        string? line = await Press().RunAsync(Files, () => true, cts.Token);

        Assert.Null(line);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task Settings_that_cannot_be_read_give_the_generic_line_and_show_nothing()
    {
        // A fixed answer, so the assignment reads no settings and the press's own read is the one that fails
        _ipinfo.AssignRuleIdsResult = new ModSecurityRuleIdsResult(ModSecurityRuleIdsFailure.None, [10_000, 10_001]);
        _ipinfo.SettingsFailure = new InvalidOperationException("settings");

        string? line = await Press().RunAsync(Files, () => true, CancellationToken.None);

        Assert.Single(_ipinfo.AssignRuleIdsCalls);
        Assert.Equal(AppText.Generic, line);
        Assert.Empty(_ui.ModSecurityRulesPrompts);
        Assert.False(_gate.IsHeld);
    }

    [Fact]
    public async Task The_settings_are_read_after_the_assignment()
    {
        _ipinfo.Settings = new UserSettings { WriteModSecurityLists = true, ModSecurityLocation = "/srv/old" };
        var pending = new TaskCompletionSource();
        _ipinfo.AssignRuleIdsPending = pending;
        Task<string?> press = Press().RunAsync(Files, () => true, CancellationToken.None);

        _ipinfo.Settings = new UserSettings
        {
            WriteModSecurityLists = true,
            ModSecurityLocation = "/srv/new",
            ModSecurityAction = ModSecurityAction.Deny,
        };
        pending.SetResult();

        Assert.Null(await press);
        ModSecurityRulesPrompt prompt = Assert.Single(_ui.ModSecurityRulesPrompts);
        Assert.True(prompt.Location == "/srv/new");
        Assert.Equal(ModSecurityAction.Deny, prompt.Action);
    }

    // ----- the files a page's button offers -----

    [Fact]
    public void Files_set_with_the_mark_of_their_clear_show()
    {
        var files = new ModSecurityRulesFiles();
        Assert.False(files.Shows);

        int mark = files.Clear();
        files.Set(mark, Files);

        Assert.True(files.Shows);
        Assert.Same(Files, files.Files);
    }

    [Fact]
    public void A_second_clear_before_the_set_drops_that_set()
    {
        var files = new ModSecurityRulesFiles();
        int mark = files.Clear();
        files.Clear();

        files.Set(mark, Files);

        Assert.False(files.Shows);
        Assert.Null(files.Files);
    }

    [Fact]
    public void An_empty_or_null_list_does_not_show()
    {
        var files = new ModSecurityRulesFiles();
        files.Set(files.Clear(), []);
        Assert.False(files.Shows);

        files.Set(files.Clear(), null);
        Assert.False(files.Shows);
    }

    [Fact]
    public void ShowsChanged_is_raised_only_when_Shows_flips()
    {
        var files = new ModSecurityRulesFiles();
        int raised = 0;
        files.ShowsChanged += (_, _) => raised++;

        files.Clear();
        files.Set(files.Clear(), []);
        Assert.Equal(0, raised);

        int mark = files.Clear();
        files.Set(mark, Files);
        Assert.Equal(1, raised);
        files.Set(mark, ["other.txt"]);
        Assert.Equal(1, raised);

        files.Clear();
        Assert.Equal(2, raised);
        files.Clear();
        Assert.Equal(2, raised);
    }
}
