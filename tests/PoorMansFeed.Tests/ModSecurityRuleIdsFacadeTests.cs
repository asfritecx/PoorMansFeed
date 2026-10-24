using System.Net;
using PoorMansFeed.Core;
using PoorMansFeed.Core.ModSecurity;
using PoorMansFeed.Core.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace PoorMansFeed.Tests;

public sealed class ModSecurityRuleIdsFacadeTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ServiceTempFolder _temp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly HttpClient _http = new(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    private readonly InMemorySecretStore _secrets = new();
    private readonly InMemorySecretStore _configKeys = new();
    private readonly List<IpInfoService> _services = [];

    public void Dispose()
    {
        foreach (IpInfoService service in _services)
            service.Dispose();
        _http.Dispose();
        _temp.Dispose();
    }

    private IpInfoService NewService()
    {
        var service = new IpInfoService(_temp.Paths, _http, new CoreOptions(), _time, _secrets, _configKeys,
            () => false, (duration, token) => Task.CompletedTask);
        _services.Add(service);
        return service;
    }

    private async Task<IpInfoService> LaunchedServiceAsync()
    {
        IpInfoService service = NewService();
        StartupResult result = await service.InitializeAsync(Ct);
        Assert.Equal(StartupFailure.None, result.Failure);
        return service;
    }

    private static void AssertIds(ModSecurityRuleIdsResult result, params int[] expected)
    {
        Assert.Equal(ModSecurityRuleIdsFailure.None, result.Failure);
        Assert.Equal(expected, result.RuleIds);
    }

    private static void AssertStorageError(ModSecurityRuleIdsResult result)
    {
        Assert.Equal(ModSecurityRuleIdsFailure.StorageError, result.Failure);
        Assert.Empty(result.RuleIds);
    }

    // ----- The facade -----

    [Fact]
    public async Task New_names_get_ids_from_the_default_first_id_and_keep_them()
    {
        IpInfoService service = await LaunchedServiceAsync();

        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt", "AS1_ipv6.txt"], Ct),
            ModSecurityLimits.DefaultFirstRuleId, ModSecurityLimits.DefaultFirstRuleId + 1);
        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt", "AS1_ipv6.txt"], Ct), 10_000, 10_001);
        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv6.txt", "AS2_ipv4.txt"], Ct), 10_001, 10_002);
    }

    [Fact]
    public async Task A_raised_first_id_applies_to_new_names_only()
    {
        IpInfoService service = await LaunchedServiceAsync();
        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], Ct), 10_000);

        Assert.True(await service.SaveSettingsAsync(service.Settings with { ModSecurityFirstRuleId = 20_000 }));

        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt", "AS2_ipv4.txt"], Ct), 10_000, 20_000);
    }

    [Fact]
    public async Task Before_a_launch_it_is_a_storage_error()
    {
        IpInfoService service = NewService();

        AssertStorageError(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], Ct));
    }

    [Fact]
    public async Task After_dispose_it_is_a_storage_error()
    {
        IpInfoService service = await LaunchedServiceAsync();
        service.Dispose();

        AssertStorageError(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], Ct));
    }

    [Fact]
    public async Task A_cancelled_call_throws_and_assigns_nothing()
    {
        IpInfoService service = await LaunchedServiceAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], cts.Token));

        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS2_ipv4.txt"], Ct), 10_000);
    }

    [Fact]
    public async Task An_unexpected_store_failure_is_a_storage_error_and_keeps_nothing()
    {
        IpInfoService service = await LaunchedServiceAsync();
        service.Config!.BeforeCommit = () => throw new InvalidOperationException("test");

        AssertStorageError(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], Ct));

        service.Config.BeforeCommit = null;
        AssertIds(await service.AssignModSecurityRuleIdsAsync(["AS2_ipv4.txt"], Ct), 10_000);
    }

    [Fact]
    public async Task Null_names_throw()
    {
        IpInfoService service = await LaunchedServiceAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AssignModSecurityRuleIdsAsync(null!, Ct));
    }

    [Fact]
    public async Task The_unavailable_service_gives_a_storage_error()
    {
        using var service = new UnavailableIpInfoService();

        AssertStorageError(await service.AssignModSecurityRuleIdsAsync(["AS1_ipv4.txt"], Ct));
    }

    // ----- The lines -----

    [Fact]
    public void Every_failure_has_a_line()
    {
        foreach (ModSecurityRuleIdsFailure failure in Enum.GetValues<ModSecurityRuleIdsFailure>())
            Assert.False(string.IsNullOrWhiteSpace(AppText.ModSecurityRuleIdsLine(failure)));
    }

    [Fact]
    public void The_real_failures_have_their_own_lines()
    {
        string[] lines =
        [
            AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.StorageError),
            AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.Unavailable),
            AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.RangeFull),
            AppText.ModSecurityRuleIdsLine(ModSecurityRuleIdsFailure.TooMany),
        ];

        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(AppText.Generic, lines);
        Assert.Contains("99,999", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ModSecurityRuleIdsFailure.None)]
    [InlineData(ModSecurityRuleIdsFailure.FileNameInvalid)]
    public void The_other_values_give_the_generic_line(ModSecurityRuleIdsFailure failure)
    {
        Assert.Equal(AppText.Generic, AppText.ModSecurityRuleIdsLine(failure));
    }

    // ----- The fake -----

    [Fact]
    public async Task The_fake_records_calls_and_gives_its_scripted_result()
    {
        var fake = new FakeIpInfoService
        {
            AssignRuleIdsResult = new ModSecurityRuleIdsResult(ModSecurityRuleIdsFailure.RangeFull, []),
        };

        ModSecurityRuleIdsResult result = await fake.AssignModSecurityRuleIdsAsync(["a.txt", "b.txt"], Ct);

        Assert.Equal(ModSecurityRuleIdsFailure.RangeFull, result.Failure);
        Assert.Equal(["a.txt", "b.txt"], Assert.Single(fake.AssignRuleIdsCalls));
    }

    [Fact]
    public async Task The_fake_records_no_cancelled_call()
    {
        var fake = new FakeIpInfoService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fake.AssignModSecurityRuleIdsAsync(["a.txt"], cts.Token));

        Assert.Empty(fake.AssignRuleIdsCalls);
    }

    [Fact]
    public async Task The_fake_waits_for_its_pending_gate()
    {
        var fake = new FakeIpInfoService { AssignRuleIdsPending = new TaskCompletionSource() };

        Task<ModSecurityRuleIdsResult> call = fake.AssignModSecurityRuleIdsAsync(["a.txt"], Ct);
        Assert.False(call.IsCompleted);
        fake.AssignRuleIdsPending.SetResult();

        AssertIds(await call, fake.Settings.ModSecurityFirstRuleId);
    }
}
