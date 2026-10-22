using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PoorMansFeed.Core;
using PoorMansFeed.Core.AbuseIpDb;

namespace PoorMansFeed.Tests;

public sealed class AbuseIpDbClientTests : IDisposable
{
    private const string Key = "abuseipdb-sentinel-key-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly StubHttpHandler _handler;
    private readonly HttpClient _http;
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = request => Answer(request, HttpStatusCode.OK, "{}");
    private readonly List<string?> _keys = new();
    private readonly List<string> _accepts = new();

    public AbuseIpDbClientTests()
    {
        _handler = new StubHttpHandler((request, _) =>
        {
            _keys.Add(request.Headers.TryGetValues("Key", out var values) ? string.Join(",", values) : null);
            _accepts.Add(request.Headers.Accept.ToString());
            return _respond(request);
        });
        _http = new HttpClient(_handler);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task An_IPv4_check_sends_the_address_and_age_in_the_query_and_the_key_in_a_header()
    {
        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Equal("{}", Encoding.UTF8.GetString(fetch.Body!));
        Uri uri = Assert.Single(_handler.RequestedUris);
        Assert.Equal("https://api.abuseipdb.com/api/v2/check?ipAddress=8.8.8.8&maxAgeInDays=30", uri.AbsoluteUri);
        Assert.True(Assert.Single(_keys) == Key, "The Key header was not the saved key.");
        Assert.Equal("application/json", Assert.Single(_accepts));
        Assert.Equal("curl/8.7.1", Assert.Single(_handler.UserAgents));
    }

    [Fact]
    public async Task An_IPv6_address_is_escaped_and_the_key_is_never_in_the_address()
    {
        byte[] bytes = new byte[16];
        bytes[0] = 0x20; bytes[1] = 0x01; bytes[2] = 0x48; bytes[3] = 0x60; bytes[14] = 0x88; bytes[15] = 0x88;
        Assert.True(PublicAddress.TryCreate(bytes, out PublicAddress? address));

        await Client().CheckAsync(address, Key, CancellationToken.None);

        Uri uri = Assert.Single(_handler.RequestedUris);
        Assert.Contains("ipAddress=2001%3A4860%3A%3A8888", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("maxAgeInDays=30", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, uri.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("key", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verbose", uri.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_rejected_key_gives_KeyRejected()
    {
        _respond = request => Answer(request, HttpStatusCode.Unauthorized, "{\"errors\":[]}");

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.KeyRejected, null, null), fetch);
    }

    [Fact]
    public async Task A_rate_limit_with_a_Retry_After_delta_gives_now_plus_the_delta()
    {
        _respond = request => RateLimited(request, response =>
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1792137600");
        });

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, Now.AddHours(1)), fetch);
    }

    [Fact]
    public async Task A_rate_limit_with_a_Retry_After_date_gives_that_date()
    {
        DateTimeOffset when = Now.AddMinutes(90);
        _respond = request => RateLimited(request, response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(when));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, when), fetch);
    }

    [Fact]
    public async Task A_Retry_After_date_in_the_past_is_floored_at_now()
    {
        _respond = request => RateLimited(request, response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddDays(-1)));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(Now, fetch.RetryAt);
    }

    [Fact]
    public async Task A_rate_limit_with_only_the_reset_header_gives_the_reset_time()
    {
        DateTimeOffset reset = Now.AddHours(5);
        _respond = request => RateLimited(request, response =>
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString()));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, reset), fetch);
    }

    [Fact]
    public async Task A_reset_in_the_past_is_floored_at_now()
    {
        _respond = request => RateLimited(request, response =>
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1545973200"));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(Now, fetch.RetryAt);
    }

    [Theory]
    [InlineData("+1792137600")]
    [InlineData("1792137600.5")]
    [InlineData(" 1792137600")]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("253402300800")]
    [InlineData("")]
    public async Task A_reset_that_is_not_plain_epoch_seconds_gives_no_time(string reset)
    {
        _respond = request => RateLimited(request, response =>
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, null), fetch);
    }

    [Fact]
    public async Task A_rate_limit_with_no_header_gives_no_time()
    {
        _respond = request => RateLimited(request, _ => { });

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, null), fetch);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Any_other_status_gives_ServiceError(HttpStatusCode status)
    {
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, status, "{}");
            response.Headers.Location = new Uri("https://elsewhere.example/check");
            return response;
        };

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    [Theory]
    [InlineData("https://elsewhere.example/api/v2/check?ipAddress=8.8.8.8")]
    [InlineData("https://api.abuseipdb.com.elsewhere.example/api/v2/check")]
    [InlineData("https://api.abuseipdb.com:8443/api/v2/check")]
    [InlineData("http://api.abuseipdb.com/api/v2/check")]
    public async Task A_success_from_another_endpoint_gives_ServiceError(string answeredBy)
    {
        _respond = request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), answeredBy);

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    /// <summary>The endpoint is checked before the status, so another host's 401 or 429 says nothing about the key.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Any_status_from_another_host_gives_ServiceError(HttpStatusCode status)
    {
        _respond = request =>
        {
            HttpResponseMessage response = AnsweredBy(Answer(request, status, "{}"), "https://elsewhere.example/api/v2/check");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        };

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    [Theory]
    [InlineData("https://API.AbuseIPDB.com/api/v2/check")]
    [InlineData("https://api.abuseipdb.com:443/api/v2/check")]
    public async Task A_success_from_the_same_endpoint_in_another_form_is_accepted(string answeredBy)
    {
        _respond = request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), answeredBy);

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_answer_with_no_request_message_gives_ServiceError(HttpStatusCode status)
    {
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, status, "{}");
            response.RequestMessage = null;
            return response;
        };

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    public static TheoryData<Exception> NetworkFailures =>
    [
        new HttpRequestException("no route"),
        new IOException("reset"),
        new TaskCanceledException("timed out"),
    ];

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task A_network_failure_or_a_timeout_gives_Offline(Exception failure)
    {
        _respond = _ => throw failure;

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.Offline, null, null), fetch);
    }

    [Fact]
    public async Task The_callers_own_cancel_before_the_request_escapes()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().CheckAsync(Address(8, 8, 8, 8), Key, cancel.Token));
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task The_callers_own_cancel_during_the_request_escapes()
    {
        using var cancel = new CancellationTokenSource();
        _respond = _ =>
        {
            cancel.Cancel();
            throw new TaskCanceledException("cancelled");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().CheckAsync(Address(8, 8, 8, 8), Key, cancel.Token));
    }

    [Fact]
    public async Task A_body_announced_over_the_cap_gives_BadResponse()
    {
        _respond = request => Answer(request, HttpStatusCode.OK, new string('x', 101));

        AbuseFetch fetch = await Client(maxBytes: 100).CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.BadResponse, null, null), fetch);
    }

    [Fact]
    public async Task A_body_over_the_cap_with_no_length_gives_BadResponse()
    {
        _respond = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[101]),
            RequestMessage = request,
        };

        AbuseFetch fetch = await Client(maxBytes: 100).CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.BadResponse, null, null), fetch);
    }

    [Fact]
    public async Task A_body_at_the_cap_with_no_length_is_read()
    {
        _respond = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[100]),
            RequestMessage = request,
        };

        AbuseFetch fetch = await Client(maxBytes: 100).CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Equal(100, fetch.Body!.Length);
    }

    [Fact]
    public async Task Failures_never_trace_the_key_the_address_or_the_url()
    {
        const string address = "149.112.112.112";
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            PublicAddress asked = Address(185, 199, 108, 153);
            foreach (Func<HttpRequestMessage, HttpResponseMessage> respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                request => Answer(request, HttpStatusCode.Unauthorized, "{}"),
                request => Answer(request, HttpStatusCode.Forbidden, "{}"),
                request =>
                {
                    HttpResponseMessage response = Answer(request, HttpStatusCode.Found, "{}");
                    response.Headers.Location = new Uri("https://elsewhere.example/next");
                    return response;
                },
                request => RateLimited(request, response =>
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "soon " + Key)),
                request => Answer(request, HttpStatusCode.InternalServerError, "{}"),
                _ => throw new HttpRequestException("failed " + Key + " " + address),
                request => Answer(request, HttpStatusCode.OK, new string('x', 101)),
            })
            {
                _respond = respond;
                await Client(maxBytes: 100, url: "https://api.abuseipdb.com/api/v2/check").CheckAsync(asked, Key, CancellationToken.None);
            }

            string text = listener.Text;
            Assert.Contains("The AbuseIPDB check failed with HTTP 401.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB check failed with HTTP 403.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB check failed with HTTP 302.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB check could not be reached.", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.DoesNotContain("185.199.108.153", text, StringComparison.Ordinal);
            Assert.DoesNotContain(address, text, StringComparison.Ordinal);
            Assert.DoesNotContain("api.abuseipdb.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ipAddress=", text, StringComparison.Ordinal);
            Assert.DoesNotContain("elsewhere.example", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    // ---------- The blacklist download ----------

    private const string BlacklistUrl = "https://api.abuseipdb.com/api/v2/blacklist";
    private static readonly DateTimeOffset Reset = new(2026, 10, 20, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_blacklist_request_goes_to_the_blacklist_url_with_no_query_and_the_key_in_a_header()
    {
        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Equal("{}", Encoding.UTF8.GetString(fetch.Body!));
        Assert.Null(fetch.RetryAt);
        Uri uri = Assert.Single(_handler.RequestedUris);
        Assert.Equal(BlacklistUrl, uri.OriginalString);
        Assert.Equal("", uri.Query);
        Assert.DoesNotContain(Key, uri.OriginalString, StringComparison.Ordinal);
        Assert.True(Assert.Single(_keys) == Key, "The Key header was not the saved key.");
        Assert.Equal("application/json", Assert.Single(_accepts));
        Assert.Equal("curl/8.7.1", Assert.Single(_handler.UserAgents));
    }

    [Fact]
    public async Task An_ipv4_only_blacklist_request_adds_only_the_ip_version_query_and_keeps_the_key_in_a_header()
    {
        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: true, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Uri uri = Assert.Single(_handler.RequestedUris);
        Assert.Equal(BlacklistUrl + "?ipVersion=4", uri.OriginalString);
        Assert.Equal("/api/v2/blacklist", uri.AbsolutePath);
        Assert.DoesNotContain(Key, uri.OriginalString, StringComparison.Ordinal);
        Assert.True(Assert.Single(_keys) == Key, "The Key header was not the saved key.");
        Assert.Equal("application/json", Assert.Single(_accepts));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, AbuseIpDbFailure.None)]
    [InlineData(HttpStatusCode.Unauthorized, AbuseIpDbFailure.KeyRejected)]
    [InlineData(HttpStatusCode.TooManyRequests, AbuseIpDbFailure.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, AbuseIpDbFailure.ServiceError)]
    public async Task The_rate_limit_headers_are_read_from_every_answer(HttpStatusCode status, AbuseIpDbFailure failure)
    {
        _respond = request => WithRateLimit(Answer(request, status, "{}"), "5", "3", Reset.ToUnixTimeSeconds().ToString());

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(failure, fetch.Failure);
        Assert.Equal(new AbuseRateLimit(5, 3, Reset), fetch.RateLimit);
    }

    [Fact]
    public async Task A_rate_limit_answer_gives_both_the_retry_time_and_the_limit()
    {
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "5", "0",
                                            Reset.ToUnixTimeSeconds().ToString());

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, Reset, new AbuseRateLimit(5, 0, Reset)), fetch);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_answer_without_the_headers_gives_a_rate_limit_of_nulls(HttpStatusCode status)
    {
        _respond = request => Answer(request, status, "{}");

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(null, null, null), fetch.RateLimit);
    }

    [Fact]
    public async Task A_reset_in_the_past_is_kept_as_sent_while_the_retry_time_is_floored_at_now()
    {
        DateTimeOffset past = new(2018, 12, 28, 5, 0, 0, TimeSpan.Zero);
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "0", "0",
                                            past.ToUnixTimeSeconds().ToString());

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(Now, fetch.RetryAt);
        Assert.Equal(new AbuseRateLimit(0, 0, past), fetch.RateLimit);
    }

    [Theory]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("-1")]
    [InlineData("5.0")]
    [InlineData("five")]
    [InlineData("")]
    [InlineData("2147483648")]
    [InlineData("5,5")]
    public async Task A_limit_or_remaining_that_is_not_plain_digits_that_fit_gives_null_for_that_part(string value)
    {
        string reset = Reset.ToUnixTimeSeconds().ToString();
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), value, "3", reset);
        AbuseFetch limit = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), "5", value, reset);
        AbuseFetch remaining = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(null, 3, Reset), limit.RateLimit);
        Assert.Equal(new AbuseRateLimit(5, null, Reset), remaining.RateLimit);
    }

    [Fact]
    public async Task The_largest_int_is_kept()
    {
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), "2147483647", "0", null);

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(int.MaxValue, 0, null), fetch.RateLimit);
    }

    [Theory]
    [InlineData("+1792137600")]
    [InlineData(" 1792137600")]
    [InlineData("-1")]
    [InlineData("1792137600.5")]
    [InlineData("soon")]
    [InlineData("253402300800")]
    [InlineData("")]
    public async Task A_reset_that_is_not_plain_epoch_seconds_gives_null_for_that_part(string reset)
    {
        _respond = request => WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "5", "0", reset);

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, null, new AbuseRateLimit(5, 0, null)), fetch);
    }

    [Fact]
    public async Task Two_values_of_a_header_give_null_for_that_part()
    {
        _respond = request =>
        {
            HttpResponseMessage response = WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), "5", "3", "1792137600");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "5");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "3");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1792137600");
            return response;
        };

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(null, null, null), fetch.RateLimit);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task An_answer_from_another_host_gives_ServiceError_and_no_rate_limit(HttpStatusCode status)
    {
        _respond = request => AnsweredBy(WithRateLimit(Answer(request, status, "{}"), "5", "0", "1792137600"),
                                         "https://elsewhere.example/api/v2/blacklist");

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public async Task A_blacklist_network_failure_or_timeout_gives_Offline_and_no_rate_limit(Exception failure)
    {
        _respond = _ => throw failure;

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.Offline, null, null), fetch);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AbuseIpDbFailure.KeyRejected)]
    [InlineData(HttpStatusCode.PaymentRequired, AbuseIpDbFailure.ServiceError)]
    [InlineData(HttpStatusCode.Forbidden, AbuseIpDbFailure.ServiceError)]
    [InlineData(HttpStatusCode.Found, AbuseIpDbFailure.ServiceError)]
    [InlineData(HttpStatusCode.InternalServerError, AbuseIpDbFailure.ServiceError)]
    [InlineData(HttpStatusCode.TooManyRequests, AbuseIpDbFailure.RateLimited)]
    public async Task The_blacklist_statuses_map_as_the_checks_do(HttpStatusCode status, AbuseIpDbFailure failure)
    {
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, status, "{}");
            response.Headers.Location = new Uri("https://elsewhere.example/next");
            return response;
        };

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(failure, null, null, new AbuseRateLimit(null, null, null)), fetch);
        Assert.Equal(1, _handler.Calls);
    }

    [Fact]
    public async Task A_blacklist_announced_over_its_cap_gives_BadResponse()
    {
        _respond = request => Answer(request, HttpStatusCode.OK, new string('x', 101));

        AbuseFetch fetch = await BlacklistClient(maxBytes: 100).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.BadResponse, fetch.Failure);
        Assert.Null(fetch.Body);
    }

    [Fact]
    public async Task A_blacklist_over_its_cap_with_no_length_gives_BadResponse()
    {
        _respond = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[101]),
            RequestMessage = request,
        };

        AbuseFetch fetch = await BlacklistClient(maxBytes: 100).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.BadResponse, fetch.Failure);
        Assert.Null(fetch.Body);
    }

    [Fact]
    public async Task A_blacklist_is_capped_by_its_own_limit_not_the_checks()
    {
        _respond = request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[1000]),
            RequestMessage = request,
        };

        AbuseFetch fetch = await BlacklistClient(maxBytes: 1000, maxCheckBytes: 100).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Equal(1000, fetch.Body!.Length);
    }

    [Fact]
    public async Task The_blacklist_answer_is_checked_against_the_blacklist_url_not_the_check_url()
    {
        _respond = request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), BlacklistUrl);

        AbuseFetch fetch = await BlacklistClient(checkUrl: "https://check.example/api/v2/check")
            .GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
    }

    [Fact]
    public async Task A_blacklist_answered_by_the_check_host_but_sent_elsewhere_gives_ServiceError()
    {
        _respond = request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), BlacklistUrl);

        AbuseFetch fetch = await BlacklistClient(url: "https://list.example/api/v2/blacklist")
            .GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    [Fact]
    public async Task The_check_answer_is_checked_against_the_check_url_not_the_blacklist_url()
    {
        _respond = request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), "https://api.abuseipdb.com/api/v2/check");

        AbuseFetch fetch = await BlacklistClient(url: "https://list.example/api/v2/blacklist")
            .CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
    }

    [Fact]
    public async Task The_callers_own_cancel_of_a_download_escapes()
    {
        using var cancel = new CancellationTokenSource();
        _respond = _ =>
        {
            cancel.Cancel();
            throw new TaskCanceledException("cancelled");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, cancel.Token));
    }

    [Fact]
    public async Task Blacklist_failures_never_trace_the_key_or_the_url()
    {
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            foreach (Func<HttpRequestMessage, HttpResponseMessage> respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
            {
                request => Answer(request, HttpStatusCode.Unauthorized, "{}"),
                request => Answer(request, HttpStatusCode.Forbidden, "{}"),
                request =>
                {
                    HttpResponseMessage response = Answer(request, HttpStatusCode.Found, "{}");
                    response.Headers.Location = new Uri("https://elsewhere.example/next");
                    return response;
                },
                request => RateLimited(request, response =>
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "soon " + Key)),
                request => Answer(request, HttpStatusCode.InternalServerError, "{}"),
                _ => throw new HttpRequestException("failed " + Key + " " + BlacklistUrl),
                request => Answer(request, HttpStatusCode.OK, new string('x', 101)),
                request => AnsweredBy(Answer(request, HttpStatusCode.OK, "{}"), "https://elsewhere.example/list"),
            })
            {
                _respond = respond;
                await BlacklistClient(maxBytes: 100).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);
            }

            string text = listener.Text;
            Assert.Contains("The AbuseIPDB blacklist download failed with HTTP 401.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download failed with HTTP 403.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download failed with HTTP 302.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download failed with HTTP 429.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download failed with HTTP 500.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download could not be reached.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download answer is larger than the size limit.", text, StringComparison.Ordinal);
            Assert.Contains("The AbuseIPDB blacklist download was answered by another host", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AbuseIPDB check", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.DoesNotContain("api.abuseipdb.com", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/api/v2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("elsewhere.example", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("soon", text, StringComparison.Ordinal);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    // ---------- The server's clock ----------

    private static readonly DateTimeOffset ServerDate = new(2026, 10, 19, 7, 59, 30, TimeSpan.Zero);

    [Theory]
    [InlineData(HttpStatusCode.OK, AbuseIpDbFailure.None)]
    [InlineData(HttpStatusCode.TooManyRequests, AbuseIpDbFailure.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, AbuseIpDbFailure.KeyRejected)]
    [InlineData(HttpStatusCode.InternalServerError, AbuseIpDbFailure.ServiceError)]
    public async Task A_blacklist_answer_gives_its_Date_as_the_server_time(HttpStatusCode status, AbuseIpDbFailure failure)
    {
        _respond = request => Dated(Answer(request, status, "{}"), ServerDate);

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(failure, fetch.Failure);
        Assert.Equal(ServerDate, fetch.ServerTime);
    }

    [Fact]
    public async Task An_oversize_blacklist_answer_still_gives_the_server_time()
    {
        _respond = request => Dated(Answer(request, HttpStatusCode.OK, new string('x', 101)), ServerDate);

        AbuseFetch fetch = await BlacklistClient(maxBytes: 100).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.BadResponse, fetch.Failure);
        Assert.Equal(ServerDate, fetch.ServerTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    [InlineData("Mon, 19 Oct 2026 07:59:30 GMT, Mon, 19 Oct 2026 07:59:31 GMT")]
    public async Task A_blacklist_answer_without_a_readable_Date_gives_no_server_time(string? date)
    {
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, HttpStatusCode.OK, "{}");
            if (date is not null)
                response.Headers.TryAddWithoutValidation("Date", date);
            return response;
        };

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Null(fetch.ServerTime);
    }

    [Fact]
    public async Task A_check_answer_with_a_Date_keeps_the_server_time_null()
    {
        _respond = request => Dated(Answer(request, HttpStatusCode.OK, "{}"), ServerDate);

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.None, fetch.Body, null, null, null), fetch);
    }

    [Fact]
    public async Task A_check_rate_limit_with_a_Date_keeps_the_server_time_null()
    {
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{}"), ServerDate);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        };

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, Now.AddMinutes(10)), fetch);
    }

    [Fact]
    public async Task A_blacklist_answer_from_another_host_gives_no_server_time()
    {
        _respond = request => AnsweredBy(Dated(Answer(request, HttpStatusCode.OK, "{}"), ServerDate),
                                         "https://elsewhere.example/api/v2/blacklist");

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.ServiceError, null, null), fetch);
    }

    // ---------- Every time on this computer's clock, bounded ----------

    private static readonly TimeSpan Ahead = TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours);

    [Theory]
    [InlineData(0, true)]   // 26 h exactly is kept
    [InlineData(1, false)]  // a second more is unknown
    public async Task A_Retry_After_delta_over_26_hours_gives_no_time(int extraSeconds, bool kept)
    {
        TimeSpan delta = Ahead + TimeSpan.FromSeconds(extraSeconds);
        _respond = request => RateLimited(request, response => response.Headers.RetryAfter = new RetryConditionHeaderValue(delta));

        AbuseFetch fetch = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(kept ? Now + delta : null, fetch.RetryAt);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_check_Retry_After_date_or_reset_more_than_26_hours_ahead_gives_no_time(int extraSeconds, bool kept)
    {
        DateTimeOffset when = Now + Ahead + TimeSpan.FromSeconds(extraSeconds);
        _respond = request => RateLimited(request, response => response.Headers.RetryAfter = new RetryConditionHeaderValue(when));
        AbuseFetch date = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);
        _respond = request => RateLimited(request, response =>
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", when.ToUnixTimeSeconds().ToString()));
        AbuseFetch reset = await Client().CheckAsync(Address(8, 8, 8, 8), Key, CancellationToken.None);

        Assert.Equal(kept ? when : null, date.RetryAt);
        Assert.Equal(kept ? when : null, reset.RetryAt);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_blacklist_reset_or_Retry_After_date_more_than_26_hours_after_its_Date_gives_no_time(int extraSeconds, bool kept)
    {
        DateTimeOffset date = Now.AddHours(-2);
        DateTimeOffset when = date + Ahead + TimeSpan.FromSeconds(extraSeconds);
        DateTimeOffset? moved = kept ? when.AddHours(2) : null;
        _respond = request => Dated(WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "5", "0",
                                                  when.ToUnixTimeSeconds().ToString()), date);
        AbuseFetch reset = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{}"), date);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(when);
            return response;
        };
        AbuseFetch retryDate = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(5, 0, moved), reset.RateLimit);
        Assert.Equal(moved, reset.RetryAt);
        Assert.Equal(moved, retryDate.RetryAt);
    }

    [Fact]
    public async Task A_Retry_After_too_far_ahead_falls_back_to_the_reset()
    {
        DateTimeOffset reset = Now.AddHours(1);
        _respond = request =>
        {
            HttpResponseMessage response = WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "5", "0",
                                                         reset.ToUnixTimeSeconds().ToString());
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(100_000));
            return response;
        };

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(reset, fetch.RetryAt);
        Assert.Equal(new AbuseRateLimit(5, 0, reset), fetch.RateLimit);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_reset_at_the_end_of_the_calendar_with_a_Date_just_behind_gives_no_time(HttpStatusCode status)
    {
        _respond = request => Dated(WithRateLimit(Answer(request, status, "{}"), "5", "0", "253402300799"), Now.AddSeconds(-2));

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(5, 0, null), fetch.RateLimit);
        Assert.Null(fetch.RetryAt);
    }

    [Fact]
    public async Task A_blacklist_rate_limit_with_only_the_reset_moves_both_times_by_the_Date_offset()
    {
        DateTimeOffset date = Now.AddHours(-2);
        _respond = request => Dated(WithRateLimit(Answer(request, HttpStatusCode.TooManyRequests, "{}"), "5", "0",
                                                  date.AddHours(1).ToUnixTimeSeconds().ToString()), date);

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.RateLimited, null, Now.AddHours(1), new AbuseRateLimit(5, 0, Now.AddHours(1)), date),
                     fetch);
    }

    [Fact]
    public async Task A_blacklist_Retry_After_date_is_moved_by_the_Date_offset_and_a_delta_is_not()
    {
        DateTimeOffset date = Now.AddHours(-2);
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{}"), date);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date.AddHours(1));
            return response;
        };
        AbuseFetch retryDate = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{}"), date);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        };
        AbuseFetch delta = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(Now.AddHours(1), retryDate.RetryAt);
        Assert.Equal(Now.AddHours(1), delta.RetryAt);
    }

    [Fact]
    public async Task A_time_more_than_a_day_before_its_Date_is_kept_as_sent()
    {
        DateTimeOffset date = Now.AddHours(-2);
        DateTimeOffset reset = date.AddHours(-25);
        _respond = request => Dated(WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), "5", "0",
                                                  reset.ToUnixTimeSeconds().ToString()), date);
        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);
        // A Date ahead of this clock would move the time back past the calendar's start
        _respond = request =>
        {
            HttpResponseMessage response = Dated(Answer(request, HttpStatusCode.TooManyRequests, "{}"), Now.AddHours(2));
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.MinValue);
            return response;
        };
        AbuseFetch earliest = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseRateLimit(5, 0, reset), fetch.RateLimit);
        Assert.Equal(Now, earliest.RetryAt);
    }

    // ---------- A 2xx blacklist answer whose body is still arriving ----------

    private static readonly DateTimeOffset BodyDate = new(2026, 10, 19, 7, 59, 50, TimeSpan.Zero);

    /// <summary>A blacklist 200 with the rate-limit headers and a Date, whose body runs <paramref name="midway"/> half way.</summary>
    private static HttpResponseMessage CountedAnswer(HttpRequestMessage request, string body, Func<CancellationToken, Task> midway)
    {
        HttpResponseMessage response = Dated(WithRateLimit(Answer(request, HttpStatusCode.OK, "{}"), "5", "3", null), BodyDate);
        response.Content = new ScriptedContent(Encoding.UTF8.GetBytes(body), midway);
        return response;
    }

    [Fact]
    public async Task A_callers_cancel_while_a_blacklist_body_arrives_still_returns_the_body()
    {
        using var cancel = new CancellationTokenSource();
        const string body = "{\"data\":[1,2,3]}";
        _respond = request => CountedAnswer(request, body, async token =>
        {
            cancel.Cancel();
            await Task.Delay(20, token);
        });

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(AbuseIpDbFailure.None, fetch.Failure);
        Assert.Equal(body, Encoding.UTF8.GetString(fetch.Body!));
        Assert.Equal(new AbuseRateLimit(5, 3, null), fetch.RateLimit);
        Assert.Equal(BodyDate, fetch.ServerTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blacklist_body_cut_short_gives_BadResponse_with_the_rate_limit_and_the_Date(bool httpError)
    {
        _respond = request => CountedAnswer(request, "{\"data\":[1,2,3]}",
            _ => throw (httpError ? new HttpRequestException("reset") : new IOException("reset")));

        AbuseFetch fetch = await BlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None);

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.BadResponse, null, null, new AbuseRateLimit(5, 3, null), BodyDate), fetch);
    }

    [Fact]
    public async Task A_blacklist_body_that_outlasts_the_timeout_gives_BadResponse_with_the_rate_limit()
    {
        _respond = request => CountedAnswer(request, "{\"data\":[]}", token => Task.Delay(Timeout.Infinite, token));

        AbuseFetch fetch = await TimedBlacklistClient().GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None)
                                                       .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new AbuseFetch(AbuseIpDbFailure.BadResponse, null, null, new AbuseRateLimit(5, 3, null), BodyDate), fetch);
    }

    [Fact]
    public async Task The_timeout_counts_from_the_send_not_from_the_headers()
    {
        // The headers take 1.3 s and the body 1.3 s more: inside a fresh two seconds each, but not inside one two seconds.
        // The headers have 0.7 s to spare, so a busy run doesn't time them out
        _respond = request =>
        {
            Thread.Sleep(1300);
            return CountedAnswer(request, "{\"data\":[]}", token => Task.Delay(1300, token));
        };

        AbuseFetch fetch = await TimedBlacklistClient(seconds: 2).GetBlacklistAsync(Key, ipv4Only: false, CancellationToken.None)
                                                       .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AbuseIpDbFailure.BadResponse, fetch.Failure);
    }

    [Fact]
    public async Task The_callers_cancel_while_a_check_body_arrives_still_escapes()
    {
        using var cancel = new CancellationTokenSource();
        _respond = request =>
        {
            HttpResponseMessage response = Answer(request, HttpStatusCode.OK, "{}");
            response.Content = new ScriptedContent(Encoding.UTF8.GetBytes(CheckBodyText), async token =>
            {
                cancel.Cancel();
                await Task.Delay(20, token);
            });
            return response;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().CheckAsync(Address(8, 8, 8, 8), Key, cancel.Token));
    }

    private const string CheckBodyText = "{\"data\":{\"ipAddress\":\"8.8.8.8\",\"abuseConfidenceScore\":0}}";

    private AbuseIpDbClient TimedBlacklistClient(int seconds = 1) =>
        new(_http, new CoreOptions { AbuseIpDbBlacklistUrl = BlacklistUrl, AbuseIpDbRequestTimeoutSeconds = seconds }, _time);

    private static HttpResponseMessage Dated(HttpResponseMessage response, DateTimeOffset date)
    {
        response.Headers.Date = date;
        return response;
    }

    private AbuseIpDbClient BlacklistClient(int maxBytes = 16 * 1024 * 1024, string url = BlacklistUrl,
                                            string checkUrl = "https://api.abuseipdb.com/api/v2/check",
                                            int maxCheckBytes = 256 * 1024) =>
        new(_http, new CoreOptions
        {
            AbuseIpDbMaxBlacklistBytes = maxBytes,
            AbuseIpDbBlacklistUrl = url,
            AbuseIpDbCheckUrl = checkUrl,
            AbuseIpDbMaxCheckBytes = maxCheckBytes,
        }, _time);

    /// <summary>The answer with the three rate-limit headers; a null value leaves that header out.</summary>
    private static HttpResponseMessage WithRateLimit(HttpResponseMessage response, string? limit, string? remaining, string? reset)
    {
        if (limit is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit);
        if (remaining is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
        if (reset is not null)
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);
        return response;
    }

    private AbuseIpDbClient Client(int maxBytes = 256 * 1024, string url = "https://api.abuseipdb.com/api/v2/check") =>
        new(_http, new CoreOptions { AbuseIpDbMaxCheckBytes = maxBytes, AbuseIpDbCheckUrl = url }, _time);

    private static PublicAddress Address(byte a, byte b, byte c, byte d)
    {
        Assert.True(PublicAddress.TryCreate([a, b, c, d], out PublicAddress? address));
        return address;
    }

    /// <summary>
    /// Every stub answer carries its request, as SocketsHttpHandler sets it (HttpClient never does), because the client
    /// checks the endpoint before the status.
    /// </summary>
    private static HttpResponseMessage Answer(HttpRequestMessage request, HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };

    /// <summary>The answer as if a redirect had taken the request to <paramref name="url"/>.</summary>
    private static HttpResponseMessage AnsweredBy(HttpResponseMessage response, string url)
    {
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        return response;
    }

    private static HttpResponseMessage RateLimited(HttpRequestMessage request, Action<HttpResponseMessage> headers)
    {
        HttpResponseMessage response = Answer(request, HttpStatusCode.TooManyRequests, "{\"errors\":[]}");
        headers(response);
        return response;
    }

    /// <summary>A body whose length is not announced, so the client has to count the bytes as it reads them.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>
    /// A body read in two halves, with <paramref name="midway"/> awaited between them under the reader's token, as a
    /// network stream honours it; whatever <paramref name="midway"/> throws comes out of the read.
    /// </summary>
    internal sealed class ScriptedContent(byte[] body, Func<CancellationToken, Task> midway) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ScriptedStream(body, midway));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ScriptedStream(byte[] body, Func<CancellationToken, Task> midway) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int half = body.Length / 2;
            switch (++_reads)
            {
                case 1:
                    body.AsMemory(0, half).CopyTo(buffer);
                    return half;
                case 2:
                    await midway(cancellationToken);
                    body.AsMemory(half).CopyTo(buffer);
                    return body.Length - half;
                default:
                    return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
