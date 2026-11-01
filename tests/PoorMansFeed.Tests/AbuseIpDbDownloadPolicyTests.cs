using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbDownloadPolicyTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextUtcMidnight = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly ServiceTempFolder _temp = new();
    private readonly FakeAbuseAccounting _accounting = new();
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;

    public AbuseIpDbDownloadPolicyTests()
    {
        _handler = new StubHttpHandler((request, _) => Answer(request));
        _http = new HttpClient(_handler);
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public async Task A_stored_limit_must_leave_one_download_for_the_user(int remaining, bool allowed)
    {
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, remaining, NextUtcMidnight), UtcDay(Now), 5 - remaining);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        if (remaining == 0)
            Assert.Equal(AbuseIpDbFailure.LimitReached, result.Failure);
        else
            AssertAllowed(allowed, result);
    }

    [Theory]
    [InlineData(5, 1, 0, true)]
    [InlineData(5, 0, 3, true)]
    [InlineData(5, 1, 4, false)]
    [InlineData(5, 1, 7, false)]
    [InlineData(null, 1, 0, false)]
    [InlineData(null, 2, 4, true)]
    public async Task A_stored_reset_that_has_passed_gives_the_limit_less_todays_count(int? limit, int remaining, int today,
                                                                                       bool allowed)
    {
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(limit, remaining, Now.AddHours(-1)), UtcDay(Now), today);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(allowed, result);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task With_no_stored_limit_todays_count_must_leave_one_download_for_the_user(int today, bool allowed)
    {
        _accounting.Current = new AbuseAccounting(null, UtcDay(Now), today);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(allowed, result);
    }

    [Fact]
    public async Task Yesterdays_count_leaves_the_whole_day_for_the_policy()
    {
        _accounting.Current = new AbuseAccounting(null, UtcDay(Now).AddDays(-1), 5);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(true, result);
    }

    [Theory]
    [InlineData(-5, false)]
    [InlineData(-6, true)]
    [InlineData(-7, true)]
    [InlineData(1, false)]
    public async Task The_last_download_must_be_old_enough(int lastDownloadHours, bool allowed)
    {
        await SeedDownloadAt(Now.AddHours(lastDownloadHours));
        int sent = _handler.Calls;
        string before = Sha(ListPath);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(allowed, result);
        Assert.Equal(sent + (allowed ? 1 : 0), _handler.Calls);
        if (!allowed)
            Assert.Equal(before, Sha(ListPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_counted_answer_that_merged_nothing_starts_the_age_rule(bool clockWrong)
    {
        DateTimeOffset at = Now.AddHours(-1);
        using var unmerged = new HttpClient(new StubHttpHandler((request, _) => Unmerged(request, at, clockWrong)));
        AbuseDownloadResult spent = await Service(new FakeTimeProvider(at), unmerged)
            .DownloadBlacklistAsync(null, CancellationToken.None);
        Assert.Equal(clockWrong ? AbuseIpDbFailure.ClockWrong : AbuseIpDbFailure.BadResponse, spent.Failure);
        Assert.True(spent.Counted);
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 4, NextUtcMidnight), UtcDay(Now), 1);

        AbuseDownloadResult soon = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);
        AbuseDownloadResult later = await Service(new FakeTimeProvider(at.AddHours(6)))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(false, soon);
        AssertAllowed(true, later);
        Assert.Equal(1, _handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_attempt_that_stopped_at_the_key_holds_no_download_back(bool unreadable)
    {
        if (unreadable)
            _store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");
        else
            (_store.Secret, _store.Marker) = (null, false);
        AbuseDownloadResult stopped = await Service(new FakeTimeProvider(Now.AddMinutes(-1)))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);
        Assert.Equal(unreadable ? AbuseIpDbFailure.KeyUnavailable : AbuseIpDbFailure.NoKey, stopped.Failure);
        Assert.Null(Facts().LastSentAt);
        (_store.ReadFailure, _store.Secret, _store.Marker) = (null, Encoding.UTF8.GetBytes(Key), true);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(true, result);
        Assert.Equal(1, _handler.Calls);
        Assert.Equal(Now, Facts().LastSentAt);
    }

    [Fact]
    public async Task A_send_time_that_cannot_be_written_sends_nothing()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File modes are a Unix check here.");
            return;
        }

        UnixFileMode? mode = null;
        // The pre-send write before the key read has made the file; only a later write can fail on it
        _store.BeforeRead = () =>
        {
            if (OperatingSystem.IsWindows())
                return;
            mode = File.GetUnixFileMode(ListPath);
            File.SetUnixFileMode(ListPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        };
        AbuseDownloadResult result;
        try
        {
            result = await Service(new FakeTimeProvider(Now))
                .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);
        }
        finally
        {
            if (mode is { } before)
                File.SetUnixFileMode(ListPath, before);
        }

        Assert.Equal(AbuseIpDbFailure.StorageError, result.Failure);
        Assert.False(result.Counted);
        Assert.Equal(1, _store.Reads);
        Assert.Equal(0, _handler.Calls);
        Assert.Empty(_accounting.Writes);
        Assert.Null(Facts().LastSentAt);
    }

    [Theory]
    [InlineData(48, true)]
    [InlineData(25, true)]
    [InlineData(24, false)]
    [InlineData(2, false)]
    public async Task A_send_time_far_ahead_of_the_clock_is_ignored(int hoursAhead, bool allowed)
    {
        Assert.Equal(AbuseListState.Usable, new AbuseIpDbList(ListPath).RecordSend(Now.AddHours(hoursAhead)));

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(allowed, result);
        Assert.Equal(allowed ? 1 : 0, _handler.Calls);
    }

    [Fact]
    public async Task A_send_time_far_ahead_does_not_hide_a_recent_download()
    {
        await SeedDownloadAt(Now.AddHours(-1));
        Assert.Equal(AbuseListState.Usable, new AbuseIpDbList(ListPath).RecordSend(Now.AddDays(2)));

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(false, result);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task The_policy_judges_the_session_copy_once_the_accounting_write_failed()
    {
        _accounting.Current = new AbuseAccounting(null, UtcDay(Now), 3);
        _accounting.FailWrites = true;
        var time = new FakeTimeProvider(Now.AddHours(-7));
        AbuseIpDbService service = Service(time);
        Assert.True((await service.DownloadBlacklistAsync(null, CancellationToken.None)).Counted);
        time.Advance(TimeSpan.FromHours(7));

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        // The store still says 3 today; only the session copy holds the fourth
        Assert.Equal(new AbuseAccounting(null, UtcDay(Now), 3), _accounting.Current);
        AssertAllowed(false, result);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task No_last_download_is_old_enough()
    {
        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        AssertAllowed(true, result);
    }

    [Fact]
    public async Task A_kept_download_sends_nothing_reads_no_key_and_writes_nothing()
    {
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 1, NextUtcMidnight), UtcDay(Now), 4);
        AbuseAccounting accounting = _accounting.Current;

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.KeptForUser, false, 0, 0, 0, 0, 0, null, null), result);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.Empty(_accounting.Writes);
        Assert.Equal(accounting, _accounting.Current);
        // No attempt was recorded: the pre-send write would have made the list file
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_kept_download_on_an_existing_list_leaves_its_file_as_it_was()
    {
        await SeedDownloadAt(Now.AddHours(-1));
        int sent = _handler.Calls;
        int reads = _store.Reads;
        string before = Sha(ListPath);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.KeptForUser, result.Failure);
        Assert.Equal(sent, _handler.Calls);
        Assert.Equal(reads, _store.Reads);
        Assert.Equal(before, Sha(ListPath));
    }

    [Fact]
    public async Task No_policy_spends_the_last_downloads_as_before()
    {
        await SeedDownloadAt(Now.AddHours(-1));
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 1, NextUtcMidnight), UtcDay(Now), 4);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now)).DownloadBlacklistAsync(null, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, result.Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_limit_refusal_comes_before_the_policy()
    {
        _accounting.Current = new AbuseAccounting(null, UtcDay(Now), 5);

        AbuseDownloadResult result = await Service(new FakeTimeProvider(Now))
            .DownloadBlacklistAsync(AbuseDownloadPolicy.RefreshAll, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.LimitReached, result.Failure);
        Assert.Equal(NextUtcMidnight, result.RetryAt);
    }

    private void AssertAllowed(bool allowed, AbuseDownloadResult result)
    {
        if (allowed)
        {
            Assert.Equal(AbuseIpDbFailure.None, result.Failure);
            Assert.True(result.Counted);
        }
        else
        {
            Assert.Equal(AbuseIpDbFailure.KeptForUser, result.Failure);
            Assert.False(result.Counted);
        }
    }

    // A download with no policy at the given time, then an accounting that leaves the policy downloads to spare
    private async Task SeedDownloadAt(DateTimeOffset at)
    {
        AbuseDownloadResult seeded = await Service(new FakeTimeProvider(at)).DownloadBlacklistAsync(null, CancellationToken.None);
        Assert.Equal(AbuseIpDbFailure.None, seeded.Failure);
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 4, NextUtcMidnight), UtcDay(Now), 1);
        _accounting.Writes.Clear();
    }

    private AbuseIpDbService Service(TimeProvider time, HttpClient? http = null) =>
        new(http ?? _http, _ipinfo, _temp.Paths, new CoreOptions(), time, _store, _accounting, _ => null);

    private string ListPath => _temp.Paths.AbuseIpDbDatabasePath;

    private ListFacts Facts() => new AbuseIpDbList(ListPath).ReadFacts(Now, TimeSpan.FromDays(30)).Value!;

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static DateOnly UtcDay(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    // A 2xx that is counted and merges nothing: a body that isn't the list, or a Date two days from this clock
    private static HttpResponseMessage Unmerged(HttpRequestMessage request, DateTimeOffset at, bool clockWrong)
    {
        if (!clockWrong)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json"), RequestMessage = request };

        HttpResponseMessage response = Answer(request);
        response.Headers.Date = at.AddDays(-2);
        return response;
    }

    private static HttpResponseMessage Answer(HttpRequestMessage request)
    {
        string body = "{\"meta\":{\"generatedAt\":\"" + Now.AddHours(-12).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            + "\"},\"data\":[{\"ipAddress\":\"8.8.8.8\",\"abuseConfidenceScore\":100,\"countryCode\":\"US\","
            + "\"lastReportedAt\":\"2026-10-18T06:00:00+00:00\"}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
    }
}
