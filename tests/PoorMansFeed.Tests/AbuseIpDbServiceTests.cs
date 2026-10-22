using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbServiceTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

    private readonly InMemorySecretStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeIpInfoService _ipinfo = new();
    private readonly ServiceTempFolder _temp = new();
    // One accounting per test, shared by every service a test makes, as the list file is
    private readonly FakeAbuseAccounting _accounting = new();
    // Null measures the real disk
    private Func<string, long?>? _freeBytes;
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = request => Answer(request, HttpStatusCode.OK, CheckBody("8.8.8.8"));

    public AbuseIpDbServiceTests()
    {
        _handler = new StubHttpHandler((request, _) => _respond(request));
        _http = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _http.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void HasKey_follows_the_marker_without_reading_the_store()
    {
        AbuseIpDbService service = Service();

        Assert.False(service.HasKey);
        _store.Secret = Encoding.UTF8.GetBytes(Key);
        _store.Marker = true;
        Assert.True(service.HasKey);
        _store.Marker = false;
        Assert.False(service.HasKey);

        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task SaveKeyAsync_trims_and_saves_the_key()
    {
        AbuseIpDbService service = Service();

        Assert.Equal(AbuseKeySave.Saved, await service.SaveKeyAsync("  " + Key + "  "));

        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
        Assert.True(service.HasKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short-key-01234")]
    [InlineData("abuseipdb sentinel key 0123456789")]
    [InlineData("abuseipdb-sentinel-k\u00E9y-0123456789")]
    [InlineData("abuseipdb-sentinel-key-\u00A00123456789")]
    public async Task SaveKeyAsync_gives_NotAKey_without_touching_the_store(string key)
    {
        Assert.Equal(AbuseKeySave.NotAKey, await Service().SaveKeyAsync(key));

        AssertStoreUntouched();
    }

    [Fact]
    public async Task SaveKeyAsync_gives_NotAKey_for_an_over_long_key_and_null()
    {
        AbuseIpDbService service = Service();

        Assert.Equal(AbuseKeySave.NotAKey, await service.SaveKeyAsync(new string('k', 257)));
        Assert.Equal(AbuseKeySave.NotAKey, await service.SaveKeyAsync(null!));

        AssertStoreUntouched();
    }

    [Fact]
    public async Task SaveKeyAsync_gives_Failed_when_the_store_write_fails()
    {
        _store.WriteFailure = new SecretStoreException("The Keychain couldn't write the secret.");

        Assert.Equal(AbuseKeySave.Failed, await Service().SaveKeyAsync(Key));
    }

    [Fact]
    public async Task ClearKeyAsync_removes_the_key_and_leaves_the_list_and_accounting()
    {
        StoreKey(Key);
        AbuseAccounting before = _accounting.Current;
        AbuseIpDbService service = Service();

        Assert.True(await service.ClearKeyAsync());

        Assert.False(service.HasKey);
        Assert.Null(_store.Secret);
        Assert.False(_store.Marker);
        Assert.Equal(0, _store.Reads);
        Assert.Empty(_accounting.Writes);
        Assert.Equal(before, _accounting.Current);
        Assert.False(File.Exists(ListPath));
        Assert.Equal(0, _handler.Calls);
    }

    public static TheoryData<Exception> DeleteFailures =>
    [
        new SecretStoreException("The Keychain couldn't remove the secret."),
        new IOException("The file couldn't be deleted."),
        new InvalidOperationException("Something unexpected."),
    ];

    [Theory]
    [MemberData(nameof(DeleteFailures))]
    public async Task A_clear_that_fails_keeps_HasKey_true(Exception failure)
    {
        StoreKey(Key);
        _store.DeleteFailure = failure;
        AbuseIpDbService service = Service();

        Assert.False(await service.ClearKeyAsync());

        Assert.True(service.HasKey);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
        Assert.Empty(_accounting.Writes);
        Assert.False(File.Exists(ListPath));
    }

    public static TheoryData<SearchQuery> NotAddressQueries =>
    [
        new SearchQuery(SearchKind.Asn, "AS15169", 15169, null),
        new SearchQuery(SearchKind.IpAddress, "8.8.8.8", 0, null),
        new SearchQuery(SearchKind.Asn, "AS15169", 15169, [8, 8, 8, 8]),
        new SearchQuery(SearchKind.IpAddress, "10.0.0.1", 0, [10, 0, 0, 1]),
        new SearchQuery(SearchKind.IpAddress, "::ffff:8.8.8.8", 0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 8, 8, 8, 8]),
        new SearchQuery(SearchKind.IpAddress, "8.8.8", 0, [8, 8, 8]),
    ];

    [Theory]
    [MemberData(nameof(NotAddressQueries))]
    public async Task Anything_but_a_public_address_gives_NotPublic_with_no_key_read_and_no_request(SearchQuery query)
    {
        StoreKey(Key);

        AbuseCheckResult result = await Service().CheckAsync(query, CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.NotPublic, null, null), result);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task A_null_query_gives_NotPublic_with_no_key_read_and_no_request()
    {
        StoreKey(Key);

        AbuseCheckResult result = await Service().CheckAsync(null!, CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.NotPublic, null, null), result);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task No_key_gives_NoKey_with_no_request()
    {
        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.NoKey, null, null), result);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_now_gives_KeyUnavailable_and_is_kept()
    {
        StoreKey(Key);
        _store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.KeyUnavailable, null, null), result);
        Assert.Equal(0, _handler.Calls);
        Assert.True(_store.Marker);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
    }

    [Fact]
    public async Task A_key_that_can_never_be_read_gives_NoKey_and_is_removed()
    {
        StoreKey(Key);
        _store.ReadFailure = new System.Security.Cryptography.CryptographicException("test");
        AbuseIpDbService service = Service();

        AbuseCheckResult result = await service.CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.NoKey, null, null), result);
        Assert.Equal(0, _handler.Calls);
        Assert.False(_store.Marker);
        Assert.False(service.HasKey);
    }

    [Fact]
    public async Task An_already_cancelled_check_reads_no_key_and_sends_nothing()
    {
        StoreKey(Key);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CheckAsync(Query8888(), cancel.Token));

        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _handler.Calls);
    }

    [Theory]
    [InlineData("short-key-01234")]
    [InlineData("abuseipdb sentinel key 0123456789")]
    public async Task A_stored_key_that_is_not_a_key_gives_NoKey_and_is_kept(string stored)
    {
        StoreKey(stored);

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.NoKey, result.Failure);
        Assert.Equal(0, _handler.Calls);
        Assert.True(_store.Marker);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == stored, "The stored key changed.");
    }

    [Fact]
    public async Task A_rejected_key_is_kept()
    {
        StoreKey(Key);
        _respond = request => Answer(request, HttpStatusCode.Unauthorized, "{\"errors\":[]}");

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.KeyRejected, null, null), result);
        Assert.True(_store.Marker);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
    }

    [Fact]
    public async Task A_valid_answer_gives_the_parsed_check()
    {
        StoreKey(Key);
        string? sentKey = null;
        _respond = request =>
        {
            sentKey = request.Headers.TryGetValues("Key", out var values) ? string.Join(",", values) : null;
            return Answer(request, HttpStatusCode.OK, CheckBody("8.8.8.8"));
        };

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, result.Failure);
        Assert.Null(result.RetryAt);
        Assert.Equal(new AbuseCheck(42, 7, 3, new DateTimeOffset(2026, 10, 18, 12, 30, 0, TimeSpan.Zero), false, true, "US"),
                     result.Check);
        Assert.True(sentKey == Key, "The Key header was not the saved key.");
        Assert.Equal("?ipAddress=8.8.8.8&maxAgeInDays=30", Assert.Single(_handler.RequestedUris).Query);
    }

    [Fact]
    public async Task The_address_sent_is_the_checked_bytes_not_the_query_text()
    {
        StoreKey(Key);

        await Service().CheckAsync(new SearchQuery(SearchKind.IpAddress, "not-what-is-sent", 0, [8, 8, 8, 8]),
                                   CancellationToken.None);

        Assert.Equal("?ipAddress=8.8.8.8&maxAgeInDays=30", Assert.Single(_handler.RequestedUris).Query);
    }

    [Fact]
    public async Task A_rate_limit_passes_RetryAt_through()
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        };

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.RateLimited, null, Now.AddMinutes(10)), result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"data\":{\"ipAddress\":\"1.1.1.1\",\"abuseConfidenceScore\":0}}")]
    [InlineData("{\"data\":{\"ipAddress\":\"8.8.8.8\",\"abuseConfidenceScore\":101}}")]
    public async Task A_body_the_parser_refuses_gives_BadResponse(string body)
    {
        StoreKey(Key);
        _respond = request => Answer(request, HttpStatusCode.OK, body);

        AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

        Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.BadResponse, null, null), result);
    }

    [Fact]
    public async Task A_failing_key_read_is_traced_without_the_key()
    {
        StoreKey(Key);
        _store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await Service().CheckAsync(Query8888(), CancellationToken.None);

            Assert.Contains("The AbuseIPDB key couldn't be", listener.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, listener.Text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task An_unexpected_failure_gives_ServiceError_and_traces_the_type_only()
    {
        const string message = "unexpected-sentinel-message-4711";
        StoreKey(Key);
        _respond = _ => throw new InvalidOperationException(message + " " + Key);
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            AbuseCheckResult result = await Service().CheckAsync(Query8888(), CancellationToken.None);

            Assert.Equal(new AbuseCheckResult(AbuseIpDbFailure.ServiceError, null, null), result);
            Assert.Contains("System.InvalidOperationException", listener.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(message, listener.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, listener.Text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task The_callers_own_cancel_escapes()
    {
        StoreKey(Key);
        using var cancel = new CancellationTokenSource();
        _respond = _ =>
        {
            cancel.Cancel();
            throw new TaskCanceledException("cancelled");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().CheckAsync(Query8888(), cancel.Token));
    }

    [Fact]
    public void The_services_own_handler_follows_no_redirect()
    {
        using SocketsHttpHandler handler = AbuseIpDbService.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void IsValidKey_takes_16_to_256_characters(int length, bool valid)
    {
        Assert.Equal(valid, AbuseIpDbService.IsValidKey(new string('k', length)));
    }

    [Theory]
    [InlineData('!', true)]
    [InlineData('~', true)]
    [InlineData(' ', false)]
    [InlineData('\u007F', false)]
    [InlineData('\t', false)]
    [InlineData('\u00E9', false)]
    public void IsValidKey_takes_printable_ASCII_only(char character, bool valid)
    {
        Assert.Equal(valid, AbuseIpDbService.IsValidKey("abuseipdb-key-01" + character));
    }

    // ---------- The blacklist download, the status and the clear ----------

    private const string IPv6Address = "2001:4860::8888";
    private static readonly DateTimeOffset Generated = Now.AddHours(-1);
    private static readonly DateTimeOffset NextUtcMidnight = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_or_unreadable_list_refuses_the_download_with_nothing_sent_and_no_key_read(bool newer)
    {
        StoreKey(Key);
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        string before = Sha(ListPath);

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal(Failed(newer ? AbuseIpDbFailure.ListNewer : AbuseIpDbFailure.ListUnreadable), result);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.Equal(before, Sha(ListPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_status_of_a_newer_or_unreadable_list_is_that_state_with_nothing_else(bool newer)
    {
        StoreKey(Key);
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        string before = Sha(ListPath);

        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);

        Assert.Equal(EmptyStatus(newer ? AbuseListState.Newer : AbuseListState.Unreadable), status);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.Equal(before, Sha(ListPath));
    }

    [Fact]
    public async Task The_status_of_no_list_is_Missing_with_nothing_sent_or_read()
    {
        StoreKey(Key);

        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);

        Assert.Equal(EmptyStatus(AbuseListState.Missing), status);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_stored_limit_with_nothing_left_refuses_until_its_reset_then_sends()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        int reads = _store.Reads;

        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(1, _handler.Calls);
        Assert.Equal(reads, _store.Reads);
        Assert.Equal(reset, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);

        _time.Advance(TimeSpan.FromHours(2));
        Assert.Null((await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_stored_limit_with_nothing_left_and_no_reset_refuses_nothing()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", null);
        AbuseIpDbService service = Service();

        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(2, _handler.Calls);
        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(new AbuseRateLimit(5, 0, null), status.RateLimit);
        Assert.Null(status.RefusedUntil);
    }

    [Fact]
    public async Task A_stored_limit_with_nothing_left_and_a_passed_reset_refuses_nothing()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(1);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        AbuseIpDbService service = Service();
        await service.DownloadBlacklistAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_stored_limit_turns_off_the_day_count()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "1000", "900", Now.AddHours(20));
        AbuseIpDbService service = Service();

        for (int i = 0; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + 1; i++)
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + 1, _handler.Calls);
        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + 1, status.DownloadsToday);
        Assert.Null(status.RefusedUntil);
    }

    [Fact]
    public async Task With_no_limit_ever_stored_five_downloads_today_refuse_the_sixth_until_the_next_UTC_midnight()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        for (int i = 0; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay; i++)
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        int reads = _store.Reads;

        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, NextUtcMidnight),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, _handler.Calls);
        Assert.Equal(reads, _store.Reads);
        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, status.DownloadsToday);
        Assert.Equal(NextUtcMidnight, status.RefusedUntil);
        Assert.Null(status.RateLimit);
    }

    [Fact]
    public async Task Five_downloads_yesterday_refuse_nothing_today_and_count_none_today()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        for (int i = 0; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay; i++)
            await service.DownloadBlacklistAsync(CancellationToken.None);
        _time.Advance(NextUtcMidnight - Now);

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(0, status.DownloadsToday);
        Assert.Null(status.RefusedUntil);
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + 1, _handler.Calls);
        Assert.Equal(1, (await service.GetStatusAsync(CancellationToken.None)).DownloadsToday);
    }

    [Fact]
    public async Task A_rate_limited_answer_stores_the_limit_and_the_next_download_is_refused_here()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(21);
        _respond = request => ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "0", reset);
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, reset), await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(1, _handler.Calls);
        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 0, 0, null, null, new AbuseRateLimit(5, 0, reset), 0, reset),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_limited_answer_with_only_Retry_After_is_stored_as_nothing_left_until_then()
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return response;
        };
        AbuseIpDbService service = Service();
        DateTimeOffset retry = Now.AddMinutes(30);

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, retry), await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, retry), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(1, _handler.Calls);
        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(new AbuseRateLimit(null, 0, retry), status.RateLimit);
        Assert.Equal(retry, status.RefusedUntil);
    }

    [Fact]
    public async Task A_rejected_key_gives_KeyRejected_and_the_key_is_kept()
    {
        StoreKey(Key);
        _respond = request => Answer(request, HttpStatusCode.Unauthorized, "{\"errors\":[]}");

        Assert.Equal(Failed(AbuseIpDbFailure.KeyRejected), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.True(_store.Marker);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
    }

    [Fact]
    public async Task Rejected_answers_count_no_download_and_store_no_limit()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.Unauthorized, "{\"errors\":[]}", "5", "0", Now.AddHours(2));
        AbuseIpDbService service = Service();
        for (int i = 0; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay; i++)
            Assert.Equal(Failed(AbuseIpDbFailure.KeyRejected), await service.DownloadBlacklistAsync(CancellationToken.None));

        // The attempt made the list before the request; the answers left nothing in it
        Assert.Equal(EmptyStatus(AbuseListState.Usable), await service.GetStatusAsync(CancellationToken.None));
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay + 1, _handler.Calls);
        Assert.Equal(1, (await service.GetStatusAsync(CancellationToken.None)).DownloadsToday);
    }

    [Fact]
    public async Task No_key_sends_no_download()
    {
        Assert.Equal(Failed(AbuseIpDbFailure.NoKey), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_key_that_cannot_be_read_now_sends_no_download_and_is_kept()
    {
        StoreKey(Key);
        _store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        Assert.Equal(Failed(AbuseIpDbFailure.KeyUnavailable), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(0, _handler.Calls);
        Assert.True(_store.Marker);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == Key, "The stored key changed.");
    }

    [Fact]
    public async Task A_stored_text_that_is_not_a_key_sends_no_download_and_is_kept()
    {
        StoreKey("short-key-01234");

        Assert.Equal(Failed(AbuseIpDbFailure.NoKey), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(0, _handler.Calls);
        Assert.True(Encoding.UTF8.GetString(_store.Secret!) == "short-key-01234", "The stored key changed.");
    }

    [Fact]
    public async Task The_download_sends_the_saved_key_in_its_header_and_nothing_in_the_url()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = true };
        StoreKey(Key);
        string? sentKey = null;
        _respond = request =>
        {
            sentKey = request.Headers.TryGetValues("Key", out var values) ? string.Join(",", values) : null;
            return ListAnswer(request, HttpStatusCode.OK, ListBody());
        };

        await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.True(sentKey == Key, "The Key header was not the saved key.");
        Assert.Equal("https://api.abuseipdb.com/api/v2/blacklist", Assert.Single(_handler.RequestedUris).OriginalString);
    }

    [Fact]
    public async Task With_ipv6_off_the_download_asks_for_ipv4_alone()
    {
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        int before = _ipinfo.SettingsReads;

        await service.DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal("https://api.abuseipdb.com/api/v2/blacklist?ipVersion=4", Assert.Single(_handler.RequestedUris).OriginalString);
        // One read gives both the keep days and the switch, so a save during the key read can't split them
        Assert.Equal(1, _ipinfo.SettingsReads - before);
    }

    [Fact]
    public async Task A_download_merges_and_the_same_list_again_adds_nothing()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(21);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "3", reset);
        AbuseIpDbService service = Service();

        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 3, 2, 3, 0, 0, Generated, null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, false, 3, 2, 0, 0, 0, Generated, null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 2, 1, Now.AddMinutes(10), Generated,
                                         new AbuseRateLimit(5, 3, reset), 2, null),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"ipAddress\":\"10.0.0.1\",\"abuseConfidenceScore\":100}]}")]
    public async Task A_body_the_parser_refuses_gives_BadResponse_and_still_counts_the_download(string body)
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, body);
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.BadResponse) with { Counted = true },
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 0, 0, null, null, null, 1, null),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, AbuseIpDbFailure.ServiceError)]
    [InlineData(HttpStatusCode.TooManyRequests, AbuseIpDbFailure.RateLimited)]
    public async Task A_server_error_or_a_rate_limit_stores_the_limit_and_counts_no_download(HttpStatusCode code,
                                                                                             AbuseIpDbFailure failure)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(21);
        _respond = request => ListAnswer(request, code, "{}", "5", "0", reset);
        AbuseIpDbService service = Service();

        Assert.Equal(failure, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 0, 0, null, null, new AbuseRateLimit(5, 0, reset), 0, reset),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_2xx_answer_counts_one_download_whether_or_not_its_body_reads(bool readable)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(21);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, readable ? ListBody() : "not json", "5", "4", reset);
        AbuseIpDbService service = Service();

        Assert.Equal(readable ? AbuseIpDbFailure.None : AbuseIpDbFailure.BadResponse,
                     (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, status.DownloadsToday);
        Assert.Equal(new AbuseRateLimit(5, 4, reset), status.RateLimit);
    }

    [Fact]
    public async Task A_rate_limited_answer_with_downloads_left_lets_the_next_download_go()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "3", Now.AddHours(21));
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.RateLimited, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());

        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_list_that_cannot_be_written_refuses_the_download_with_nothing_sent_and_no_key_read()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix file permissions.");
            return;
        }

        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        int reads = _store.Reads;
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            Assert.Equal(Failed(AbuseIpDbFailure.StorageError), await service.DownloadBlacklistAsync(CancellationToken.None));

            Assert.Equal(1, _handler.Calls);
            Assert.Equal(reads, _store.Reads);
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }
    }

    [Fact]
    public async Task A_list_that_cannot_be_opened_refuses_the_download_with_nothing_sent_and_no_key_read()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix file permissions.");
            return;
        }

        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        int reads = _store.Reads;
        UnixFileMode mode = File.GetUnixFileMode(ListPath);
        File.SetUnixFileMode(ListPath, UnixFileMode.None);
        try
        {
            Assert.Equal(Failed(AbuseIpDbFailure.StorageError), await service.DownloadBlacklistAsync(CancellationToken.None));

            Assert.Equal(1, _handler.Calls);
            Assert.Equal(reads, _store.Reads);
        }
        finally
        {
            File.SetUnixFileMode(ListPath, mode);
        }
    }

    [Fact]
    public async Task An_unexpected_failure_releases_the_download_for_the_next_one()
    {
        StoreKey(Key);
        _respond = _ => throw new InvalidOperationException("unexpected");
        AbuseIpDbService service = Service();
        Assert.Equal(Failed(AbuseIpDbFailure.ServiceError),
                     await service.DownloadBlacklistAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());

        // Bounded, so a gate left held fails here rather than hanging the run
        AbuseDownloadResult second = await service.DownloadBlacklistAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AbuseIpDbFailure.None, second.Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_cancel_once_the_answer_is_in_still_merges_it()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        using var cancel = new CancellationTokenSource();
        // The service reads the clock once before the request and once after the answer is in; the second read cancels
        var time = new CancellingTimeProvider(_time, cancelOnCall: 2, cancel);
        var service = new AbuseIpDbService(_http, _ipinfo, _temp.Paths, new CoreOptions(), time, _store, _accounting, _freeBytes);

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 3, 2, 3, 0, 0, Generated, null, Counted: true), result);
        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);
        Assert.Equal((2, 1, 1), (status.IPv4Count, status.IPv6Count, status.DownloadsToday));
    }

    [Fact]
    public async Task A_cancel_while_the_body_arrives_still_records_and_merges_it()
    {
        StoreKey(Key);
        using var cancel = new CancellationTokenSource();
        _respond = request => Bodied(ListAnswer(request, HttpStatusCode.OK, "{}", "5", "4", NextUtcMidnight), ListBody(),
                                     async token =>
                                     {
                                         cancel.Cancel();
                                         await Task.Delay(20, token);
                                     });

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 3, 2, 3, 0, 0, Generated, null, Counted: true), result);
        Assert.Equal((new AbuseRateLimit(5, 4, NextUtcMidnight), Now, true), Assert.Single(_accounting.Writes));
        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);
        Assert.Equal((2, 1, 1), (status.IPv4Count, status.IPv6Count, status.DownloadsToday));
    }

    [Fact]
    public async Task A_body_cut_short_gives_BadResponse_and_still_counts_the_download()
    {
        StoreKey(Key);
        _respond = request => Bodied(ListAnswer(request, HttpStatusCode.OK, "{}", "5", "4", NextUtcMidnight), ListBody(),
                                     _ => throw new IOException("reset"));

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal(Failed(AbuseIpDbFailure.BadResponse) with { Counted = true }, result);
        Assert.Equal((new AbuseRateLimit(5, 4, NextUtcMidnight), Now, true), Assert.Single(_accounting.Writes));
        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);
        Assert.Equal((0, 0, 1, new AbuseRateLimit(5, 4, NextUtcMidnight)),
                     (status.IPv4Count, status.IPv6Count, status.DownloadsToday, status.RateLimit));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_merge_that_fails_after_the_answer_says_the_download_counted(bool newer)
    {
        StoreKey(Key);
        // The pre-send write has made the file by the time the request is sent
        _respond = request =>
        {
            if (newer)
                ExecuteOnList("UPDATE metadata SET value = '2' WHERE key = 'schema_version'");
            else
                WriteUnreadableList();
            return ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        };

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal(Failed(newer ? AbuseIpDbFailure.ListNewer : AbuseIpDbFailure.ListUnreadable) with { Counted = true }, result);
        Assert.Equal((new AbuseRateLimit(5, 4, NextUtcMidnight), Now, true), Assert.Single(_accounting.Writes));
    }

    [Fact]
    public async Task A_merge_that_cannot_write_after_the_answer_says_the_download_counted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix file permissions.");
            return;
        }

        StoreKey(Key);
        UnixFileMode? mode = null;
        // The pre-send write has made the file by the time the request is sent; from then on it can't be written
        _respond = request =>
        {
            // The lambda escapes the guard above, so it repeats it
            if (!OperatingSystem.IsWindows())
            {
                mode = File.GetUnixFileMode(ListPath);
                File.SetUnixFileMode(ListPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            return ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        };
        try
        {
            AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

            Assert.Equal(Failed(AbuseIpDbFailure.StorageError) with { Counted = true }, result);
            Assert.Equal((new AbuseRateLimit(5, 4, NextUtcMidnight), Now, true), Assert.Single(_accounting.Writes));
        }
        finally
        {
            if (mode is { } before)
                File.SetUnixFileMode(ListPath, before);
        }
    }

    [Fact]
    public async Task A_clock_too_far_from_AbuseIPDBs_says_the_download_counted()
    {
        StoreKey(Key);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight), Now.AddHours(-30));

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal(Failed(AbuseIpDbFailure.ClockWrong) with { Counted = true }, result);
    }

    [Theory]
    [InlineData("no key")]
    [InlineData("refused")]
    [InlineData("401")]
    [InlineData("429")]
    [InlineData("500")]
    public async Task Counted_is_false_when_AbuseIPDB_gave_no_2xx(string what)
    {
        if (what != "no key")
            StoreKey(Key);
        if (what == "refused")
            _accounting.Current = AbuseAccounting.Empty.After(new AbuseRateLimit(5, 0, NextUtcMidnight), Now, countDownload: true);
        _respond = request => ListAnswer(request, what switch
        {
            "401" => HttpStatusCode.Unauthorized,
            "429" => HttpStatusCode.TooManyRequests,
            "500" => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.OK,
        }, ListBody(), "5", "0", NextUtcMidnight);

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        Assert.NotEqual(AbuseIpDbFailure.None, result.Failure);
        Assert.False(result.Counted);
    }

    /// <summary>The answer with <paramref name="body"/> read in two halves, <paramref name="midway"/> awaited between them.</summary>
    private static HttpResponseMessage Bodied(HttpResponseMessage response, string body, Func<CancellationToken, Task> midway)
    {
        response.Content = new AbuseIpDbClientTests.ScriptedContent(Encoding.UTF8.GetBytes(body), midway);
        return response;
    }

    [Theory]
    [InlineData(-2, -1, 1)]   // the Date 2 h behind: the reset an hour after the Date is an hour from now here
    [InlineData(-24, -23, 1)] // a day off is still moved
    [InlineData(2, 3, 1)]
    public async Task The_reset_is_moved_onto_this_computers_clock_by_the_Date_offset(int dateHours, int resetHours, int refusedHours)
    {
        StoreKey(Key);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", Now.AddHours(resetHours)),
                                    Now.AddHours(dateHours));
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        DateTimeOffset until = Now.AddHours(refusedHours);

        Assert.Equal(until, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, until), await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(1, _handler.Calls);
    }

    [Theory]
    [InlineData(-25, 3)]   // more than a day off: as sent
    [InlineData(25, 3)]
    [InlineData(-25, -1)]  // as sent, and already past
    [InlineData(null, 3)]  // no Date: as sent
    [InlineData(null, -1)]
    public async Task A_rate_limit_with_a_Date_more_than_a_day_off_or_none_keeps_the_reset_as_sent(int? dateHours, int resetHours)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(resetHours);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "0", reset);
            return dateHours is int h ? Dated(response, Now.AddHours(h)) : response;
        };
        AbuseIpDbService service = Service();
        // The result keeps the client's time, which is never earlier than now
        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, reset > Now ? reset : Now),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(reset, status.RateLimit?.ResetAt);
        Assert.Equal(reset > Now ? reset : (DateTimeOffset?)null, status.RefusedUntil);
    }

    [Fact]
    public async Task A_rate_limit_with_a_Date_behind_gives_one_time_for_the_result_the_limit_and_the_refusal()
    {
        StoreKey(Key);
        DateTimeOffset date = Now.AddHours(-2);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "0",
                                               date.AddHours(1)), date);
        AbuseIpDbService service = Service();
        DateTimeOffset until = Now.AddHours(1);

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, until), await service.DownloadBlacklistAsync(CancellationToken.None));

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(until, status.RateLimit?.ResetAt);
        Assert.Equal(until, status.RefusedUntil);
    }

    [Theory]
    [InlineData(false)]  // Retry-After: 1800, already on this clock
    [InlineData(true)]   // Retry-After: the Date plus 30 minutes, moved onto this clock
    public async Task A_rate_limit_with_only_Retry_After_and_a_Date_behind_is_refused_for_30_minutes(bool asDate)
    {
        StoreKey(Key);
        DateTimeOffset date = Now.AddHours(-2);
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}"), date);
            response.Headers.RetryAfter = asDate
                ? new RetryConditionHeaderValue(date.AddMinutes(30))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(1800));
            return response;
        };
        AbuseIpDbService service = Service();
        DateTimeOffset until = Now.AddMinutes(30);

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, until), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(until, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
    }

    [Theory]
    [InlineData(26, true)]
    [InlineData(27, false)]
    public async Task A_reset_more_than_26_hours_ahead_refuses_nothing(int resetHours, bool refused)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(resetHours);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(refused ? reset : null, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
        AbuseDownloadResult next = await service.DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal(refused ? AbuseIpDbFailure.LimitReached : AbuseIpDbFailure.None, next.Failure);
        Assert.Equal(refused ? 1 : 2, _handler.Calls);
    }

    [Fact]
    public async Task A_stored_reset_read_by_a_clock_set_a_day_back_refuses_nothing()
    {
        StoreKey(Key);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", Now.AddHours(12)), Now);
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        // The same data folder, read by a service whose clock is a day earlier (further back refuses the download for
        // the clock itself): the reset is 36 hours ahead of it
        var earlier = new FakeTimeProvider(Now.AddHours(-AbuseIpDbLimits.MaxClockDifferenceHours));
        AbuseIpDbService service = new(_http, _ipinfo, _temp.Paths, new CoreOptions(), earlier, _store, _accounting, _freeBytes);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());

        Assert.Null((await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task A_clock_more_than_a_day_behind_the_last_download_withholds_the_counts_and_keeps_the_rest()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        AbuseIpDbService ClockedAt(DateTimeOffset time) =>
            new(_http, _ipinfo, _temp.Paths, new CoreOptions(), new FakeTimeProvider(time), _store, _accounting, _freeBytes);

        // Accounting both earlier clocks read the same way: nothing left until a reset still ahead of them (a refusal in
        // force), and three downloads counted on their UTC day
        DateTimeOffset dayBehind = Now.AddHours(-AbuseIpDbLimits.MaxClockDifferenceHours);
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 0, Now), DateOnly.FromDateTime(dayBehind.UtcDateTime), 3);

        // Exactly a day behind still counts; a second more withholds the counts and keeps everything else
        AbuseListStatus normal = await ClockedAt(dayBehind).GetStatusAsync(CancellationToken.None);
        AbuseListStatus behind = await ClockedAt(dayBehind.AddSeconds(-1)).GetStatusAsync(CancellationToken.None);

        Assert.False(normal.ClockBehind);
        Assert.True(normal.IPv4Count > 0);
        Assert.Equal(Now, normal.LastDownloadAt);
        Assert.NotNull(normal.RateLimit);
        Assert.Equal(Now, normal.RefusedUntil);
        Assert.Equal(3, normal.DownloadsToday);
        Assert.Equal(normal with { IPv4Count = 0, IPv6Count = 0, ClockBehind = true }, behind);
        Assert.Equal(1, _handler.Calls);
    }

    [Theory]
    [InlineData(0, false)]  // exactly a day behind is sent
    [InlineData(1, true)]   // a second more is refused here
    public async Task A_clock_more_than_a_day_behind_the_last_download_refuses_the_download_with_nothing_written_read_or_sent(
        int extraSeconds, bool refused)
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        var earlier = new FakeTimeProvider(Now.AddHours(-AbuseIpDbLimits.MaxClockDifferenceHours).AddSeconds(-extraSeconds));
        AbuseIpDbService service = new(_http, _ipinfo, _temp.Paths, new CoreOptions(), earlier, _store, _accounting, _freeBytes);
        string before = Sha(ListPath);
        int reads = _store.Reads;
        int writes = _accounting.Writes.Count;

        AbuseDownloadResult result = await service.DownloadBlacklistAsync(CancellationToken.None);

        if (refused)
        {
            Assert.Equal(Failed(AbuseIpDbFailure.ClockBehind), result);
            Assert.Equal(1, _handler.Calls);
            Assert.Equal(reads, _store.Reads);
            Assert.Equal(writes, _accounting.Writes.Count);
            Assert.Equal(before, Sha(ListPath));
        }
        else
        {
            Assert.Equal(AbuseIpDbFailure.None, result.Failure);
            Assert.Equal(2, _handler.Calls);
        }
    }

    [Theory]
    [InlineData(0, true)]   // 26 h exactly still refuses
    [InlineData(1, false)]  // a second more is unknown
    public async Task A_stored_reset_more_than_26_hours_ahead_of_the_reading_clock_refuses_nothing(int extraSeconds, bool refused)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(6);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset), Now);
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        var earlier = new FakeTimeProvider(reset - TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours)
                                           - TimeSpan.FromSeconds(extraSeconds));
        AbuseIpDbService service = new(_http, _ipinfo, _temp.Paths, new CoreOptions(), earlier, _store, _accounting, _freeBytes);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());

        Assert.Equal(refused ? reset : null, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
        AbuseDownloadResult next = await service.DownloadBlacklistAsync(CancellationToken.None);

        if (refused)
            Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), next);
        else
            Assert.Equal(AbuseIpDbFailure.None, next.Failure);
        Assert.Equal(refused ? 1 : 2, _handler.Calls);
    }

    [Fact]
    public async Task The_generation_time_is_clamped_on_AbuseIPDBs_clock_not_this_one()
    {
        StoreKey(Key);
        // This computer's clock is 3 hours slow, inside the clock guard; the list was generated 30 minutes before the Date
        DateTimeOffset date = Now.AddHours(3);
        DateTimeOffset generated = date.AddMinutes(-30);
        string body = ListBody(generated, Entry("8.8.8.8"));
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, body), date);
        AbuseIpDbService service = Service();

        AbuseDownloadResult first = await service.DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal((AbuseIpDbFailure.None, true, generated), (first.Failure, first.NewGeneration, first.GeneratedAt));
        // The list's own times stay this computer's
        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal((generated, Now), (status.GeneratedAt, status.LastDownloadAt));

        // The same list again 30 minutes later is no new generation
        _time.Advance(TimeSpan.FromMinutes(30));
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, body), date.AddMinutes(30));
        AbuseDownloadResult again = await service.DownloadBlacklistAsync(CancellationToken.None);

        Assert.Equal((AbuseIpDbFailure.None, false, generated), (again.Failure, again.NewGeneration, again.GeneratedAt));
        status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal((generated, Now.AddMinutes(30)), (status.GeneratedAt, status.LastDownloadAt));
    }

    [Theory]
    [InlineData("0", 5 * 60)]  // nothing left: the next press is sent at the reset
    [InlineData("3", 30)]      // a throttle: Retry-After decides, and nothing is refused here
    public async Task A_rate_limit_names_the_time_the_next_press_will_be_sent(string remaining, int retryMinutes)
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", remaining,
                                                      Now.AddHours(5));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1800));
            return response;
        };
        AbuseIpDbService service = Service();
        DateTimeOffset retryAt = Now.AddMinutes(retryMinutes);

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, retryAt), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(remaining == "0" ? retryAt : null, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
    }

    [Fact]
    public async Task A_Retry_After_too_far_ahead_falls_back_to_the_reset_for_the_result_and_the_refusal()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(1);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "0", reset);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(100_000));
            return response;
        };
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.RateLimited, reset), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(reset, (await service.GetStatusAsync(CancellationToken.None)).RefusedUntil);
    }

    [Theory]
    [InlineData(180, 60, "0", 180, 180, 180)]     // Retry-After later than the reset: refused until Retry-After
    [InlineData(30, 120, "0", 120, 120, 120)]     // the reset later than Retry-After: refused until the reset
    [InlineData(45, null, "0", 45, 45, 45)]       // no reset: refused until Retry-After
    [InlineData(null, 90, "0", 90, 90, 90)]       // no Retry-After: refused until the reset
    [InlineData(180, 60, "3", null, 60, 180)]     // downloads left: a throttle, so nothing is refused and the reset stays as sent
    public async Task A_429_with_nothing_left_is_refused_here_until_the_later_of_Retry_After_and_the_reset(
        int? retryMinutes, int? resetMinutes, string remaining, int? refusedMinutes, int storedResetMinutes, int namedMinutes)
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", remaining,
                                                      resetMinutes is int reset ? Now.AddMinutes(reset) : null);
            if (retryMinutes is int retry)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(retry));
            return response;
        };
        AbuseIpDbService service = Service();
        DateTimeOffset? refusedUntil = refusedMinutes is int minutes ? Now.AddMinutes(minutes) : null;

        AbuseDownloadResult first = await service.DownloadBlacklistAsync(CancellationToken.None);
        Assert.Equal(AbuseIpDbFailure.RateLimited, first.Failure);
        Assert.Equal(Now.AddMinutes(namedMinutes), first.RetryAt);

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(refusedUntil, status.RefusedUntil);
        Assert.Equal(Now.AddMinutes(storedResetMinutes), status.RateLimit?.ResetAt);
        if (refusedUntil is DateTimeOffset until)
        {
            Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, until), await service.DownloadBlacklistAsync(CancellationToken.None));
            Assert.Equal(1, _handler.Calls);
        }
    }

    [Fact]
    public async Task A_reset_at_the_end_of_the_calendar_with_a_Date_just_behind_still_merges_and_counts()
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.OK, ListBody()), Now.AddSeconds(-2));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "253402300799");
            return response;
        };
        AbuseIpDbService service = Service();

        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 3, 2, 3, 0, 0, Generated, null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal((2, 1, 1), (status.IPv4Count, status.IPv6Count, status.DownloadsToday));
        Assert.Equal(new AbuseRateLimit(null, 0, null), status.RateLimit);
        Assert.Null(status.RefusedUntil);
    }

    [Fact]
    public async Task A_2xx_announced_over_the_size_cap_gives_BadResponse_and_counts_the_download()
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", Now.AddHours(21));
            response.Content.Headers.ContentLength = new CoreOptions().AbuseIpDbMaxBlacklistBytes + 1L;
            return response;
        };
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.BadResponse) with { Counted = true },
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal((0, 0, 1), (status.IPv4Count, status.IPv6Count, status.DownloadsToday));
        Assert.Equal(new AbuseRateLimit(5, 4, Now.AddHours(21)), status.RateLimit);
    }

    [Fact]
    public async Task No_connection_counts_no_download()
    {
        StoreKey(Key);
        _respond = _ => throw new HttpRequestException("failed");
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.Offline), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(EmptyStatus(AbuseListState.Usable), await service.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Rows_lapse_after_the_clamped_keep_days_at_the_next_new_generation()
    {
        StoreKey(Key);
        _ipinfo.Settings = new UserSettings { AbuseIpDbKeepDays = 0 };
        AbuseIpDbService service = Service();
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated, Entry("8.8.8.8"), Entry("9.9.9.9")));
        await service.DownloadBlacklistAsync(CancellationToken.None);

        // Half a day: inside the one day that 0 is clamped to
        _time.Advance(TimeSpan.FromHours(12));
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated.AddHours(12), Entry("8.8.8.8")));
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 1, 0, 0, 0, 0, Generated.AddHours(12), null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(2, (await service.GetStatusAsync(CancellationToken.None)).IPv4Count);

        // Past the day with no download: the keep days count from the last download, so the count doesn't change
        _time.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await service.GetStatusAsync(CancellationToken.None)).IPv4Count);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated.AddHours(24), Entry("8.8.8.8")));
        // Hidden by the keep days, not removed: nothing is removed before the year is up. 9.9.9.9 was within the keep
        // days of the last download and isn't within this one's, so it lapsed
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 1, 0, 0, 1, 0, Generated.AddHours(24), null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(1, (await service.GetStatusAsync(CancellationToken.None)).IPv4Count);
    }

    [Fact]
    public async Task The_status_counts_dont_change_when_the_clock_moves_past_the_keep_days_without_a_download()
    {
        StoreKey(Key);
        _ipinfo.Settings = new UserSettings { AbuseIpDbKeepDays = 1 };
        AbuseIpDbService service = Service();
        _respond = request => ListAnswer(request, HttpStatusCode.OK,
                                         ListBody(Generated, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry("2001:4860::8888")));
        await service.DownloadBlacklistAsync(CancellationToken.None);
        AbuseListStatus before = await service.GetStatusAsync(CancellationToken.None);

        foreach (int days in new[] { 2, 400 - 2 })
        {
            _time.Advance(TimeSpan.FromDays(days));
            AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);

            Assert.Equal(2, status.IPv4Count);
            Assert.Equal(1, status.IPv6Count);
            Assert.Equal(before.LastDownloadAt, status.LastDownloadAt);
        }
    }

    [Fact]
    public async Task Rows_lapse_after_the_keep_days_at_the_next_download_of_the_same_list()
    {
        StoreKey(Key);
        _ipinfo.Settings = new UserSettings { AbuseIpDbKeepDays = 1 };
        AbuseIpDbService service = Service();
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated, Entry("8.8.8.8"), Entry("9.9.9.9")));
        await service.DownloadBlacklistAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated.AddMinutes(30), Entry("8.8.8.8")));
        await service.DownloadBlacklistAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromDays(1));
        // 9.9.9.9 was within the keep day of the last download and isn't within this one's
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, false, 1, 0, 0, 1, 0, Generated.AddMinutes(30), null, Counted: true),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(1, (await service.GetStatusAsync(CancellationToken.None)).IPv4Count);
    }

    [Fact]
    public async Task Keep_days_above_the_most_are_clamped_to_the_most()
    {
        StoreKey(Key);
        _ipinfo.Settings = new UserSettings { AbuseIpDbKeepDays = 1000 };
        AbuseIpDbService service = Service();
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Generated, Entry("8.8.8.8"), Entry("9.9.9.9")));
        await service.DownloadBlacklistAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromDays(AbuseIpDbLimits.MaxKeepDays) + TimeSpan.FromSeconds(1));
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(Now.AddDays(AbuseIpDbLimits.MaxKeepDays), Entry("8.8.8.8")));

        // Without the clamp the cutoff would fall before the year the list keeps, and the merge would refuse it
        AbuseDownloadResult result = await service.DownloadBlacklistAsync(CancellationToken.None);
        Assert.Equal((AbuseIpDbFailure.None, 1), (result.Failure, result.Expired));
    }

    [Fact]
    public async Task An_already_cancelled_download_throws_with_no_key_read_and_nothing_sent()
    {
        StoreKey(Key);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().DownloadBlacklistAsync(cancel.Token));

        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _handler.Calls);
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task Two_downloads_at_once_run_one_after_the_other_and_the_second_sees_the_first_answer()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedHandler(async (request, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        });
        using var http = new HttpClient(gated);
        AbuseIpDbService service = Service(http);

        Task<AbuseDownloadResult> first = service.DownloadBlacklistAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<AbuseDownloadResult> second = service.DownloadBlacklistAsync(CancellationToken.None);
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.False(second.IsCompleted);
        Assert.Equal(1, gated.Calls);
        release.SetResult();

        Assert.Equal(AbuseIpDbFailure.None, (await first).Failure);
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await second);
        Assert.Equal(1, gated.Calls);
    }

    [Fact]
    public async Task A_cancel_during_the_download_throws_and_leaves_the_list_as_it_was()
    {
        StoreKey(Key);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedHandler(async (request, call, ct) =>
        {
            if (call == 1)
                return ListAnswer(request, HttpStatusCode.OK, ListBody());
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("not reached");
        });
        using var http = new HttpClient(gated);
        AbuseIpDbService service = Service(http);
        await service.DownloadBlacklistAsync(CancellationToken.None);
        // The attempt is written before the request, so the file changes; its rows and what it knows of downloads don't
        AbuseListStatus before = await service.GetStatusAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        Task<AbuseDownloadResult> download = service.DownloadBlacklistAsync(cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(before, await service.GetStatusAsync(CancellationToken.None));
        Assert.Equal(1, before.DownloadsToday);
    }

    [Fact]
    public async Task A_cancel_while_waiting_for_another_download_throws_and_sends_nothing()
    {
        StoreKey(Key);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedHandler(async (request, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ListAnswer(request, HttpStatusCode.OK, ListBody());
        });
        using var http = new HttpClient(gated);
        AbuseIpDbService service = Service(http);
        Task<AbuseDownloadResult> first = service.DownloadBlacklistAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int reads = _store.Reads;
        using var cancel = new CancellationTokenSource();

        Task<AbuseDownloadResult> second = service.DownloadBlacklistAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        await first;

        Assert.Equal(1, gated.Calls);
        Assert.Equal(reads, _store.Reads);
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(25)]
    public async Task A_server_time_more_than_a_day_off_gives_ClockWrong_and_merges_nothing(int hours)
    {
        StoreKey(Key);
        _respond = request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody()), Now.AddHours(hours));
        AbuseIpDbService service = Service();

        Assert.Equal(Failed(AbuseIpDbFailure.ClockWrong) with { Counted = true },
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 0, 0, null, null, null, 1, null),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(24)]
    [InlineData(-23)]
    [InlineData(23)]
    [InlineData(null)]
    public async Task A_server_time_within_a_day_or_none_merges(int? hours)
    {
        StoreKey(Key);
        _respond = request =>
        {
            HttpResponseMessage response = ListAnswer(request, HttpStatusCode.OK, ListBody());
            return hours is int h ? Dated(response, Now.AddHours(h)) : response;
        };

        AbuseDownloadResult result = await Service().DownloadBlacklistAsync(CancellationToken.None);

        // The generation time is clamped to an hour past the Date, AbuseIPDB's clock, so a Date far behind moves it back
        DateTimeOffset latest = (hours is int at ? Now.AddHours(at) : Now).AddHours(1);
        DateTimeOffset generated = Generated > latest ? latest : Generated;
        Assert.Equal(new AbuseDownloadResult(AbuseIpDbFailure.None, true, 3, 2, 3, 0, 0, generated, null, Counted: true), result);
    }

    [Fact]
    public async Task Clearing_removes_the_list_and_the_next_status_is_Missing_with_the_days_count_kept()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        await service.DownloadBlacklistAsync(CancellationToken.None);
        Assert.True(File.Exists(ListPath));

        Assert.Equal(AbuseIpDbFailure.None, await service.ClearListAsync(CancellationToken.None));

        Assert.False(File.Exists(ListPath));
        Assert.Equal(new AbuseListStatus(AbuseListState.Missing, 0, 0, null, null, null, 1, null),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_keeps_the_refusal_and_the_days_count()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(AbuseIpDbFailure.None, await service.ClearListAsync(CancellationToken.None));

        Assert.Equal(new AbuseListStatus(AbuseListState.Missing, 0, 0, null, null, new AbuseRateLimit(5, 0, reset), 1, reset),
                     await service.GetStatusAsync(CancellationToken.None));
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await Service().DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(1, _handler.Calls);
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_nearly_full_disk_refuses_with_nothing_written_sent_or_read()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        var asked = new List<string>();
        _freeBytes = folder =>
        {
            asked.Add(folder);
            return 0;
        };

        Assert.Equal(Failed(AbuseIpDbFailure.DiskNearlyFull), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal([_temp.Paths.DataFolder], asked);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.False(File.Exists(ListPath));
        Assert.Empty(_accounting.Writes);
    }

    [Theory]
    [InlineData(0, AbuseIpDbFailure.None)]
    [InlineData(-1, AbuseIpDbFailure.DiskNearlyFull)]
    public async Task Free_space_at_the_threshold_sends_and_a_byte_less_refuses(long offset, AbuseIpDbFailure failure)
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        _freeBytes = _ => AbuseIpDbLimits.MinFreeBytesForDownload + offset;

        Assert.Equal(failure, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(failure == AbuseIpDbFailure.None ? 1 : 0, _handler.Calls);
    }

    [Fact]
    public async Task The_list_files_length_raises_the_threshold()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.True(new FileInfo(ListPath).Length > 0);

        _freeBytes = _ => AbuseIpDbLimits.MinFreeBytesForDownload + new FileInfo(ListPath).Length - 1;
        Assert.Equal(Failed(AbuseIpDbFailure.DiskNearlyFull), await Service().DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(1, _handler.Calls);

        _freeBytes = _ => AbuseIpDbLimits.MinFreeBytesForDownload + new FileInfo(ListPath).Length;
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task Free_space_that_cant_be_measured_lets_the_download_go()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        _freeBytes = _ => null;

        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task LimitReached_wins_over_a_nearly_full_disk()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 0, reset), UtcDay(Now), 1);
        int measured = 0;
        _freeBytes = _ =>
        {
            measured++;
            return 0;
        };

        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(0, measured);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task Accounting_that_isnt_available_gives_StorageError_with_nothing_written_sent_or_read()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        _accounting.IsAvailable = false;

        Assert.Equal(Failed(AbuseIpDbFailure.StorageError), await Service().DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
        Assert.False(File.Exists(ListPath));
        Assert.Empty(_accounting.Writes);
    }

    [Fact]
    public async Task A_failed_accounting_write_still_merges_and_its_stored_limit_refuses_for_the_session()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        _accounting.FailWrites = true;
        AbuseIpDbService service = Service();

        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, reset), await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(1, _handler.Calls);
        Assert.Single(_accounting.Writes);
        Assert.Equal(AbuseAccounting.Empty, _accounting.Current);
        Assert.Equal(new AbuseListStatus(AbuseListState.Usable, 2, 1, Now, Generated, new AbuseRateLimit(5, 0, reset), 1, reset,
                                         NewCount: 3),
                     await service.GetStatusAsync(CancellationToken.None));

        // A new session has only what the store holds
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task Five_answers_whose_accounting_writes_failed_refuse_the_sixth_for_the_session()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        _accounting.FailWrites = true;
        AbuseIpDbService service = Service();
        for (int i = 0; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay; i++)
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, NextUtcMidnight),
                     await service.DownloadBlacklistAsync(CancellationToken.None));

        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, _handler.Calls);
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, _accounting.Writes.Count);
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, (await service.GetStatusAsync(CancellationToken.None)).DownloadsToday);
        Assert.Equal(AbuseIpDbFailure.None, (await Service().DownloadBlacklistAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task Once_a_write_has_failed_the_session_copy_keeps_every_later_answer_too()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        _accounting.FailWrites = true;
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        _accounting.FailWrites = false;
        for (int i = 1; i < AbuseIpDbLimits.FreeBlacklistDownloadsPerDay; i++)
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        // The store has four of the five, the session all five
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay - 1, _accounting.Current.DownloadsOnDay);
        Assert.Equal(Failed(AbuseIpDbFailure.LimitReached, NextUtcMidnight),
                     await service.DownloadBlacklistAsync(CancellationToken.None));
        Assert.Equal(AbuseIpDbLimits.FreeBlacklistDownloadsPerDay, _handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_status_beside_a_newer_or_unreadable_list_still_gives_the_accounting(bool newer)
    {
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        DateTimeOffset reset = Now.AddHours(2);
        _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 0, reset), UtcDay(Now), 3);

        AbuseListStatus status = await Service().GetStatusAsync(CancellationToken.None);

        Assert.Equal(new AbuseListStatus(newer ? AbuseListState.Newer : AbuseListState.Unreadable, 0, 0, null, null,
                                         new AbuseRateLimit(5, 0, reset), 3, reset), status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_status_while_the_accounting_isnt_available_is_a_storage_error(bool downloaded)
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(2);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "0", reset);
        AbuseIpDbService service = Service();
        if (downloaded)
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        else
            _accounting.Current = new AbuseAccounting(new AbuseRateLimit(5, 0, reset), UtcDay(Now), 3);
        _accounting.IsAvailable = false;

        Assert.Equal(new AbuseListStatus(AbuseListState.StorageError, 0, 0, null, null, null, 0, null),
                     await service.GetStatusAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_newer_or_unreadable_list_keeps_its_state_while_the_accounting_isnt_available(bool newer)
    {
        if (newer)
            WriteNewerList();
        else
            WriteUnreadableList();
        StoreKey(Key);
        _accounting.IsAvailable = false;
        AbuseIpDbService service = Service();

        Assert.Equal(newer ? AbuseListState.Newer : AbuseListState.Unreadable,
                     (await service.GetStatusAsync(CancellationToken.None)).State);
        Assert.Equal(newer ? AbuseIpDbFailure.ListNewer : AbuseIpDbFailure.ListUnreadable,
                     (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task Old_rate_limit_and_count_keys_in_a_list_file_are_ignored()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody());
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        string reset = Now.AddHours(2).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        ExecuteOnList("INSERT INTO metadata (key, value) VALUES ('rl_limit', '5'), ('rl_remaining', '0'), "
                      + "('rl_reset', '" + reset + "'), ('downloads_day', '2026-10-19'), ('downloads_count', '5');");

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(AbuseListState.Usable, status.State);
        Assert.Null(status.RateLimit);
        Assert.Null(status.RefusedUntil);
        Assert.Equal(1, status.DownloadsToday);

        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        Assert.Equal(2, _handler.Calls);
    }

    [Fact]
    public async Task The_accounting_in_config_db_is_there_at_the_next_launch()
    {
        StoreKey(Key);
        DateTimeOffset reset = Now.AddHours(16);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", reset);
        var tokens = new InMemorySecretStore();
        var configKeys = new InMemorySecretStore();

        using (IpInfoService first = RealIpInfo(tokens, configKeys))
        {
            Assert.Equal(StartupFailure.None, (await first.InitializeAsync(CancellationToken.None)).Failure);
            var service = new AbuseIpDbService(_http, first, _temp.Paths, new CoreOptions(), _time, _store, first.AbuseAccounting, _ => null);
            Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        }

        using IpInfoService second = RealIpInfo(tokens, configKeys);
        Assert.Equal(StartupFailure.None, (await second.InitializeAsync(CancellationToken.None)).Failure);
        var next = new AbuseIpDbService(_http, second, _temp.Paths, new CoreOptions(), _time, _store, second.AbuseAccounting, _ => null);

        AbuseListStatus status = await next.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, status.DownloadsToday);
        Assert.Equal(new AbuseRateLimit(5, 4, reset), status.RateLimit);
        Assert.Null(status.RefusedUntil);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task Clearing_removes_an_unreadable_list()
    {
        WriteUnreadableList();

        Assert.Equal(AbuseIpDbFailure.None, await Service().ClearListAsync(CancellationToken.None));

        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public async Task A_list_that_cannot_be_removed_gives_StorageError()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix folder permissions.");
            return;
        }

        WriteUnreadableList();
        UnixFileMode mode = File.GetUnixFileMode(_temp.Folder);
        File.SetUnixFileMode(_temp.Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(AbuseIpDbFailure.StorageError, await Service().ClearListAsync(CancellationToken.None));
            Assert.True(File.Exists(ListPath));
        }
        finally
        {
            File.SetUnixFileMode(_temp.Folder, mode);
        }
    }

    [Fact]
    public async Task An_unexpected_download_failure_gives_ServiceError_and_traces_the_type_only()
    {
        const string message = "unexpected-sentinel-message-4711";
        StoreKey(Key);
        _respond = _ => throw new InvalidOperationException(message + " " + Key);
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            Assert.Equal(Failed(AbuseIpDbFailure.ServiceError), await Service().DownloadBlacklistAsync(CancellationToken.None));

            Assert.Contains("System.InvalidOperationException", listener.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(message, listener.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, listener.Text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task Download_failures_and_merges_never_trace_the_key_or_the_url()
    {
        StoreKey(Key);
        AbuseIpDbService service = Service();
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            foreach (Func<HttpRequestMessage, HttpResponseMessage> respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                request => Answer(request, HttpStatusCode.Unauthorized, "{\"errors\":[]}"),
                request => Answer(request, HttpStatusCode.InternalServerError, "{}"),
                _ => throw new HttpRequestException("failed " + Key + " https://api.abuseipdb.com/api/v2/blacklist"),
                request => Answer(request, HttpStatusCode.OK, "not json " + Key),
                request => Dated(ListAnswer(request, HttpStatusCode.OK, ListBody()), Now.AddHours(-25)),
                request => ListAnswer(request, HttpStatusCode.OK, ListBody()),
                request => ListAnswer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}", "5", "0", Now.AddHours(2)),
            })
            {
                _respond = respond;
                await service.DownloadBlacklistAsync(CancellationToken.None);
            }
            await service.DownloadBlacklistAsync(CancellationToken.None);

            string text = listener.Text;
            Assert.Contains("The computer's clock is 25 hours from AbuseIPDB's", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist was merged: 3 listed, 2 skipped, 0 duplicates, 3 added, 0 lapsed, 0 expired, new generation True.",
                            text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.DoesNotContain("api.abuseipdb.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/api/v2", text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task The_status_and_the_clear_never_read_the_key_or_send_anything()
    {
        StoreKey(Key);
        AbuseIpDbService service = Service();

        await service.GetStatusAsync(CancellationToken.None);
        await service.ClearListAsync(CancellationToken.None);

        Assert.Equal(0, _store.Reads);
        Assert.Equal(0, _handler.Calls);
    }

    private string ListPath => _temp.Paths.AbuseIpDbDatabasePath;

    [Theory]
    [InlineData("newer", AbuseNewAddressesFailure.ListNewer)]
    [InlineData("unreadable", AbuseNewAddressesFailure.ListUnreadable)]
    [InlineData("missing", AbuseNewAddressesFailure.NoDownload)]
    [InlineData("attempted", AbuseNewAddressesFailure.NoDownload)]
    [InlineData("stale", AbuseNewAddressesFailure.NotKnown)]
    [InlineData("no table", AbuseNewAddressesFailure.NotKnown)]
    public async Task The_new_addresses_map_the_lists_state_to_a_failure(string list, AbuseNewAddressesFailure expected)
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        AbuseIpDbService service = Service();
        switch (list)
        {
            case "newer":
                WriteNewerList();
                break;
            case "unreadable":
                WriteUnreadableList();
                break;
            case "attempted":
                new AbuseIpDbList(ListPath).RecordAttempt(Now);
                break;
            case "stale":
                Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
                ExecuteOnList("UPDATE metadata SET value = '1' WHERE key = 'new_addresses_at';");
                break;
            case "no table":
                Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
                ExecuteOnList("DROP TABLE new_addresses;");
                break;
        }
        int keyReads = _store.Reads;
        int calls = _handler.Calls;

        AbuseNewAddresses result = await service.GetNewAddressesAsync(CancellationToken.None);

        Assert.Equal(expected, result.Failure);
        Assert.Empty(result.Addresses);
        Assert.Null(result.AsOf);
        Assert.Equal(keyReads, _store.Reads);
        Assert.Equal(calls, _handler.Calls);
    }

    [Fact]
    public async Task The_new_addresses_after_a_download_are_both_families_and_read_no_key_or_settings()
    {
        StoreKey(Key);
        _ipinfo.Settings = new UserSettings { IPv6Enabled = false };
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);
        int keyReads = _store.Reads;
        int settingsReads = _ipinfo.SettingsReads;

        AbuseNewAddresses result = await service.GetNewAddressesAsync(CancellationToken.None);

        Assert.Equal(AbuseNewAddressesFailure.None, result.Failure);
        Assert.Equal(Now, result.AsOf);
        DateTimeOffset reported = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);
        // PublicAddress has no value equality, so each record is compared field by field
        Assert.Equal(["8.8.8.8", "9.9.9.9", IPv6Address], result.Addresses.Select(a => a.Address.Text));
        Assert.All(result.Addresses, a => Assert.Equal(100, a.Score));
        Assert.All(result.Addresses, a => Assert.Equal(reported, a.LastReportedAt));
        Assert.Equal(keyReads, _store.Reads);
        Assert.Equal(Encoding.UTF8.GetBytes(Key), _store.Secret);
        Assert.Equal(settingsReads, _ipinfo.SettingsReads);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task The_new_addresses_carry_the_address_the_read_judged()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        AbuseIpDbService service = Service();
        Assert.Equal(AbuseIpDbFailure.None, (await service.DownloadBlacklistAsync(CancellationToken.None)).Failure);

        AbuseNewAddresses result = await service.GetNewAddressesAsync(CancellationToken.None);

        AbuseNewAddress v4 = result.Addresses[0];
        Assert.Equal("8.8.8.8", v4.Address.Text);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, v4.Address.Bytes);
        AbuseNewAddress v6 = result.Addresses[^1];
        Assert.Equal(IPv6Address, v6.Address.Text);
        Assert.Equal(new byte[] { 0x20, 0x01, 0x48, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x88, 0x88 }, v6.Address.Bytes);
    }

    [Fact]
    public async Task The_status_after_a_download_counts_its_new_addresses()
    {
        StoreKey(Key);
        _respond = request => ListAnswer(request, HttpStatusCode.OK, ListBody(), "5", "4", NextUtcMidnight);
        AbuseIpDbService service = Service();
        Assert.Equal(0, (await service.GetStatusAsync(CancellationToken.None)).NewCount);
        AbuseDownloadResult download = await service.DownloadBlacklistAsync(CancellationToken.None);

        AbuseListStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(3, download.Added);
        Assert.Equal(3, status.NewCount);
        Assert.Equal((2, 1), (status.IPv4Count, status.IPv6Count));
        ExecuteOnList("DROP TABLE new_addresses; CREATE TABLE new_addresses (ip TEXT PRIMARY KEY);");
        // A set of another shape counts 0 and the status is otherwise as it was
        Assert.Equal(status with { NewCount = 0 }, await service.GetStatusAsync(CancellationToken.None));
    }

    private static DateOnly UtcDay(DateTimeOffset time) => DateOnly.FromDateTime(time.UtcDateTime);

    /// <summary>A real service on the temp folder, with in-memory stores for the token and the config key.</summary>
    private IpInfoService RealIpInfo(InMemorySecretStore tokens, InMemorySecretStore configKeys) =>
        new(_temp.Paths, _http, new CoreOptions(), _time, tokens, configKeys, () => false, (_, _) => Task.CompletedTask);

    /// <summary>Runs SQL on the list file through a connection of its own.</summary>
    private void ExecuteOnList(string sql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static AbuseDownloadResult Failed(AbuseIpDbFailure failure, DateTimeOffset? retryAt = null) =>
        new(failure, false, 0, 0, 0, 0, 0, null, retryAt);

    private static AbuseListStatus EmptyStatus(AbuseListState state) => new(state, 0, 0, null, null, null, 0, null);

    private static string Entry(string address) =>
        "{\"ipAddress\":\"" + address + "\",\"abuseConfidenceScore\":100,\"countryCode\":\"US\","
        + "\"lastReportedAt\":\"2026-10-19T06:00:00+00:00\"}";

    /// <summary>Three public addresses (two IPv4, one IPv6), one private and one unreadable entry.</summary>
    private static string ListBody() =>
        ListBody(Generated, Entry("8.8.8.8"), Entry("9.9.9.9"), Entry(IPv6Address), Entry("10.0.0.1"),
                 "{\"ipAddress\":\"not-an-address\",\"abuseConfidenceScore\":100}");

    private static string ListBody(DateTimeOffset generatedAt, params string[] entries) =>
        "{\"meta\":{\"generatedAt\":\"" + generatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        + "\"},\"data\":[" + string.Join(",", entries) + "]}";

    /// <summary>A blacklist answer with the rate-limit headers given; a null one is left out.</summary>
    private static HttpResponseMessage ListAnswer(HttpRequestMessage request, HttpStatusCode status, string body,
                                                  string? limit = null, string? remaining = null, DateTimeOffset? reset = null)
    {
        HttpResponseMessage response = Answer(request, status, body);
        if (limit is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit);
        if (remaining is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
        if (reset is DateTimeOffset at)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return response;
    }

    private static HttpResponseMessage Dated(HttpResponseMessage response, DateTimeOffset date)
    {
        response.Headers.Date = date;
        return response;
    }

    /// <summary>A list file a newer version wrote: its metadata says schema version 2.</summary>
    private void WriteNewerList()
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = ListPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE addresses (ip BLOB PRIMARY KEY, seen INTEGER NOT NULL);
            INSERT INTO metadata (key, value) VALUES ('schema_version', '2');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A list file that isn't a database.</summary>
    private void WriteUnreadableList()
    {
        byte[] bytes = new byte[4096];
        new Random(4711).NextBytes(bytes);
        File.WriteAllBytes(ListPath, bytes);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>The fake clock, cancelling the source on the given 1-based read of the time, before it answers.</summary>
    private sealed class CancellingTimeProvider(FakeTimeProvider inner, int cancelOnCall, CancellationTokenSource cancel)
        : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) == cancelOnCall)
                cancel.Cancel();
            return inner.GetUtcNow();
        }
    }

    /// <summary>Answers with an async function of (request, 1-based call number, the request's token).</summary>
    private sealed class GatedHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request, Interlocked.Increment(ref _calls), cancellationToken);
        }
    }

    private AbuseIpDbService Service() => Service(_http);

    private AbuseIpDbService Service(HttpClient http) =>
        new(http, _ipinfo, _temp.Paths, new CoreOptions(), _time, _store, _accounting, _freeBytes);

    private void StoreKey(string key)
    {
        _store.Secret = Encoding.UTF8.GetBytes(key);
        _store.Marker = true;
    }

    private void AssertStoreUntouched()
    {
        Assert.Equal(0, _store.Reads);
        Assert.Null(_store.Secret);
        Assert.False(_store.Marker);
    }

    private static SearchQuery Query8888() => new(SearchKind.IpAddress, "8.8.8.8", 0, [8, 8, 8, 8]);

    private static string CheckBody(string address) =>
        "{\"data\":{\"ipAddress\":\"" + address + "\",\"isPublic\":true,\"ipVersion\":4,\"isWhitelisted\":false,"
        + "\"abuseConfidenceScore\":42,\"countryCode\":\"US\",\"usageType\":\"Data Center/Web Hosting/Transit\","
        + "\"isp\":\"Example\",\"domain\":\"example.com\",\"hostnames\":[],\"isTor\":true,\"totalReports\":7,"
        + "\"numDistinctUsers\":3,\"lastReportedAt\":\"2026-10-18T12:30:00+00:00\"}}";

    private static HttpResponseMessage Answer(HttpRequestMessage request, HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                    return _text.ToString();
            }
        }

        public override void Write(string? message)
        {
            lock (_text)
                _text.Append(message);
        }

        public override void WriteLine(string? message)
        {
            lock (_text)
                _text.AppendLine(message);
        }
    }
}
