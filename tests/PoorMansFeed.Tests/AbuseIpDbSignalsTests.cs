using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbSignalsTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);
    // No feed type ticked, so a feed write from a handler writes nothing and still takes the gate
    private readonly FakeIpInfoService _ipinfo = new() { Settings = new UserSettings() };
    private readonly ServiceTempFolder _temp = new();
    private readonly FakeAbuseAccounting _accounting = new();

    public AbuseIpDbSignalsTests()
    {
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_merging_download_raises_ListChanged_once_after_the_gate_is_free()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        var changes = new List<AbuseListChange>();
        bool? writeFinished = null;
        AbuseWork? workSeen = new(AbuseWorkKind.Download);
        service.ListChanged += (_, change) =>
        {
            changes.Add(change);
            workSeen = service.WorkUnderWay;
            // Blocks this thread, so a gate still held by the download would never let the write in
            writeFinished = Task.Run(() => service.WriteFeedAsync(CancellationToken.None)).Wait(Bound);
        };
        long before = service.ListVersion;

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(null, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, result.Failure);
        AbuseListChange only = Assert.Single(changes);
        Assert.Equal(AbuseListChangeKind.Merged, only.Kind);
        Assert.True(only.Version > before);
        Assert.Equal(only.Version, service.ListVersion);
        Assert.True(writeFinished, "The handler's feed write didn't finish, so the gate was still held.");
        Assert.Null(workSeen);
    }

    [Fact]
    public async Task A_WorkChanged_listener_on_the_clear_can_take_the_gate()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        int taken = 0;
        bool? writeFinished = null;
        service.WorkChanged += (_, _) =>
        {
            if (service.WorkUnderWay is not null || Interlocked.Exchange(ref taken, 1) == 1)
                return;
            // Blocks this thread, so a gate still held by the download would never let the write in
            writeFinished = Task.Run(() => service.WriteFeedAsync(CancellationToken.None)).Wait(Bound);
        };

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(null, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, result.Failure);
        Assert.True(writeFinished, "The handler's feed write didn't finish, so the gate was still held.");
    }

    [Fact]
    public async Task A_download_that_merges_nothing_raises_no_ListChanged()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request }));
        AbuseIpDbService service = Service(http);
        int raised = 0;
        service.ListChanged += (_, _) => raised++;

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(null, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.ServiceError, result.Failure);
        Assert.Equal(0, raised);
        Assert.Equal(0, service.ListVersion);
    }

    [Fact]
    public async Task Clear_list_raises_ListChanged_once_with_a_higher_version()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(null, CancellationToken.None)).Failure);
        long merged = service.ListVersion;
        var changes = new List<AbuseListChange>();
        bool? writeFinished = null;
        service.ListChanged += (_, change) =>
        {
            changes.Add(change);
            writeFinished = Task.Run(() => service.WriteFeedAsync(CancellationToken.None)).Wait(Bound);
        };

        Assert.Equal(AbuseIpDbFailure.None, await service.ClearListAsync(CancellationToken.None));

        AbuseListChange only = Assert.Single(changes);
        Assert.Equal(AbuseListChangeKind.Cleared, only.Kind);
        Assert.True(only.Version > merged);
        Assert.Equal(only.Version, service.ListVersion);
        Assert.True(writeFinished, "The handler's feed write didn't finish, so the gate was still held.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_download_is_under_way_while_it_holds_the_gate(bool policy)
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        List<AbuseWork?> seen = Watch(service);

        await service.DownloadBlacklistAsync(policy ? AbuseDownloadPolicy.RefreshAll : null, CancellationToken.None);

        Assert.Equal([new AbuseWork(AbuseWorkKind.Download), null], seen);
        Assert.Null(service.WorkUnderWay);
    }

    [Fact]
    public async Task Each_gated_call_names_its_work_and_clears_it()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        List<AbuseWork?> seen = Watch(service);

        await service.ClearListAsync(CancellationToken.None);
        await service.WriteFeedAsync(CancellationToken.None);
        await service.EmptyFeedAsync(CancellationToken.None);
        await service.RefreshFeedAsync(CancellationToken.None);

        Assert.Equal(
        [
            new AbuseWork(AbuseWorkKind.ClearList), null,
            new AbuseWork(AbuseWorkKind.WriteFeed), null,
            new AbuseWork(AbuseWorkKind.RetireFeed), null,
            new AbuseWork(AbuseWorkKind.RefreshFeed), null,
        ], seen);
        Assert.Null(service.WorkUnderWay);
    }

    [Fact]
    public async Task A_throwing_listener_keeps_the_result_and_the_listeners_after_it()
    {
        using var http = new HttpClient(new StubHttpHandler((request, _) => Answer(request, ListBody())));
        AbuseIpDbService service = Service(http);
        int workHeard = 0, listHeard = 0;
        service.WorkChanged += (_, _) => throw new InvalidOperationException("listener");
        service.WorkChanged += (_, _) => workHeard++;
        service.ListChanged += (_, _) => throw new InvalidOperationException("listener");
        service.ListChanged += (_, _) => listHeard++;

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(null, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, result.Failure);
        Assert.Equal(2, workHeard);
        Assert.Equal(1, listHeard);
        Assert.Null(service.WorkUnderWay);
    }

    private static List<AbuseWork?> Watch(AbuseIpDbService service)
    {
        var seen = new List<AbuseWork?>();
        service.WorkChanged += (_, _) =>
        {
            lock (seen)
                seen.Add(service.WorkUnderWay);
        };
        return seen;
    }

    private AbuseIpDbService Service(HttpClient http) =>
        new(http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, _accounting, _ => null);

    private static string ListBody() =>
        "{\"meta\":{\"generatedAt\":\"" + Now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        + "\"},\"data\":[{\"ipAddress\":\"8.8.8.8\",\"abuseConfidenceScore\":100,\"countryCode\":\"US\","
        + "\"lastReportedAt\":\"2026-10-19T06:00:00+00:00\"}]}";

    private static HttpResponseMessage Answer(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
}
