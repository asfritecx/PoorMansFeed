using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PoorMansFeed.Core.AbuseIpDb;

/// <summary>
/// <see cref="RateLimit"/> and <see cref="ServerTime"/> (the Date as sent) come only with a blacklist answer from
/// AbuseIPDB's endpoint. <see cref="RetryAt"/> and the reset are on this clock, null past MaxResetAheadHours ahead.
/// </summary>
internal sealed record AbuseFetch(AbuseIpDbFailure Failure, byte[]? Body, DateTimeOffset? RetryAt, AbuseRateLimit? RateLimit = null,
                                  DateTimeOffset? ServerTime = null);

/// <summary>
/// The key travels only in the <c>Key</c> header, never in the URL, and no trace names the address, the key, a header
/// or the request URL.
/// </summary>
internal sealed class AbuseIpDbClient
{
    /// <summary>The largest epoch second <see cref="DateTimeOffset.FromUnixTimeSeconds"/> takes (9999-12-31).</summary>
    private const long MaxUnixSeconds = 253_402_300_799;

    private readonly HttpClient _http;
    private readonly CoreOptions _options;
    private readonly TimeProvider _time;

    public AbuseIpDbClient(HttpClient http, CoreOptions options, TimeProvider time)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Asks for the score of <paramref name="address"/>. Only the caller's own cancellation escapes.</summary>
    public async Task<AbuseFetch> CheckAsync(PublicAddress address, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{_options.AbuseIpDbCheckUrl}?ipAddress={Uri.EscapeDataString(address.Text)}&maxAgeInDays={AbuseIpDbLimits.CheckMaxAgeInDays}");
        return await SendAsync(url, _options.AbuseIpDbCheckUrl, key, _options.AbuseIpDbMaxCheckBytes, "The AbuseIPDB check",
                         keepRateLimit: false, ct);
    }

    /// <summary>
    /// The caller's cancel applies only until a 2xx answer's headers are in: AbuseIPDB has counted the download then,
    /// so its body is read under the request's timeout alone.
    /// </summary>
    public async Task<AbuseFetch> GetBlacklistAsync(string key, bool ipv4Only, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        string endpoint = _options.AbuseIpDbBlacklistUrl;
        string url = ipv4Only ? endpoint + "?ipVersion=4" : endpoint;
        return await SendAsync(url, endpoint, key, _options.AbuseIpDbMaxBlacklistBytes, "The AbuseIPDB blacklist download",
                         keepRateLimit: true, ct);
    }

    /// <summary>
    /// Sends one request and maps its answer. <paramref name="what"/> starts every trace ("The AbuseIPDB check"), so
    /// the texts stay fixed; <paramref name="endpoint"/> is the URL without its query, which the answer must come from.
    /// </summary>
    private async Task<AbuseFetch> SendAsync(string url, string endpoint, string key, long maxBytes, string what,
                                             bool keepRateLimit, CancellationToken ct)
    {
        // One budget for the whole request, started before the send, so a blacklist body read under the timeout alone
        // still ends when the request's time is up
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.AbuseIpDbRequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            using HttpRequestMessage request = CreateRequest(url, key);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            // A backstop: the service's own handler has redirects off. An answer from any other scheme, host or port,
            // whatever its status, says nothing about the key and is not read, its headers included
            if (!IsSameEndpoint(response.RequestMessage?.RequestUri, endpoint))
            {
                Trace.TraceWarning($"{what} was answered by another host, so the answer was not read.");
                return Failure(AbuseIpDbFailure.ServiceError, null);
            }

            AbuseRateLimit? rateLimit = keepRateLimit ? RateLimitOf(response) : null;
            // Null when the header is missing or can't be read
            DateTimeOffset? serverTime = keepRateLimit ? response.Headers.Date : null;
            // The clock is read once, and only for an answer that has a time to place
            DateTimeOffset? now = null;
            DateTimeOffset Now() => now ??= _time.GetUtcNow();
            if (rateLimit is { ResetAt: DateTimeOffset reset })
                rateLimit = rateLimit with { ResetAt = OnThisClock(reset, serverTime, Now()) };
            int status = (int)response.StatusCode;
            // Only a 401 means the key; AbuseIPDB documents no 403, and an edge block can send one
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Trace.TraceWarning($"{what} failed with HTTP {status}.");
                return Failure(AbuseIpDbFailure.KeyRejected, rateLimit, serverTime);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Trace.TraceWarning($"{what} failed with HTTP {status}.");
                return new AbuseFetch(AbuseIpDbFailure.RateLimited, null, RetryAt(response, serverTime, Now), rateLimit, serverTime);
            }

            // A 3xx comes back here as the answer, because the service's handler has redirects off: its body is not
            // read and its Location is not followed
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"{what} failed with HTTP {status}.");
                return Failure(AbuseIpDbFailure.ServiceError, rateLimit, serverTime);
            }

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                Trace.TraceWarning($"{what} answer is larger than the size limit.");
                return Failure(AbuseIpDbFailure.BadResponse, rateLimit, serverTime);
            }

            byte[]? body;
            if (keepRateLimit)
            {
                // AbuseIPDB counts a blacklist download once it sends a 2xx, so the caller's cancel no longer applies,
                // and a body cut short or too slow is a bad answer that still carries the rate limit
                try
                {
                    body = await ReadCappedAsync(response, maxBytes, timeout.Token);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                {
                    Trace.TraceWarning($"{what} answer could not be read in full.");
                    return Failure(AbuseIpDbFailure.BadResponse, rateLimit, serverTime);
                }
            }
            else
            {
                body = await ReadCappedAsync(response, maxBytes, linked.Token);
            }

            if (body is null)
            {
                Trace.TraceWarning($"{what} answer is larger than the size limit.");
                return Failure(AbuseIpDbFailure.BadResponse, rateLimit, serverTime);
            }

            return new AbuseFetch(AbuseIpDbFailure.None, body, null, rateLimit, serverTime);
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning($"{what} could not be reached.");
            return Failure(AbuseIpDbFailure.Offline, null);
        }
        catch (IOException)
        {
            Trace.TraceWarning($"{what} answer could not be read.");
            return Failure(AbuseIpDbFailure.Offline, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.TraceWarning($"{what} timed out.");
            return Failure(AbuseIpDbFailure.Offline, null);
        }
    }

    /// <summary>Every request: a GET with the key in the <c>Key</c> header, never in <paramref name="url"/>.</summary>
    private HttpRequestMessage CreateRequest(string url, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Key", key);
        // Without it, errors such as the 429 come back as an HTML page
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        return request;
    }

    private static AbuseFetch Failure(AbuseIpDbFailure failure, AbuseRateLimit? rateLimit, DateTimeOffset? serverTime = null) =>
        new(failure, null, null, rateLimit, serverTime);

    /// <summary>
    /// Same scheme, host and port as <paramref name="sentUrl"/>, the URL the request went to (a default port written
    /// out counts as the same).
    /// </summary>
    private static bool IsSameEndpoint(Uri? answeredBy, string sentUrl) =>
        answeredBy is { IsAbsoluteUri: true }
        && Uri.TryCreate(sentUrl, UriKind.Absolute, out Uri? sent)
        && Uri.Compare(answeredBy, sent, UriComponents.SchemeAndServer, UriFormat.Unescaped,
                       StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The three rate-limit headers, each null when it is missing or not in the one form that is read.</summary>
    private static AbuseRateLimit RateLimitOf(HttpResponseMessage response) =>
        new(Count(response, "X-RateLimit-Limit"), Count(response, "X-RateLimit-Remaining"), ResetOf(response));

    /// <summary>Exactly one value of digits only that fits an int; anything else is null, never clamped.</summary>
    private static int? Count(HttpResponseMessage response, string name) =>
        OneValue(response, name) is string text
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    /// <summary><c>X-RateLimit-Reset</c> as sent: exactly one value of epoch seconds, digits only, up to 9999-12-31.</summary>
    private static DateTimeOffset? ResetOf(HttpResponseMessage response) =>
        OneValue(response, "X-RateLimit-Reset") is string text
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
        && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static string? OneValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) && values.ToList() is [string value]
            ? value
            : null;

    /// <summary>
    /// <c>Retry-After</c> (seconds or a date), else <c>X-RateLimit-Reset</c>, placed on this clock and never earlier
    /// than now; the reset also stands in for a <c>Retry-After</c> more than MaxResetAheadHours ahead.
    /// </summary>
    private static DateTimeOffset? RetryAt(HttpResponseMessage response, DateTimeOffset? serverTime, Func<DateTimeOffset> now)
    {
        DateTimeOffset? at = null;
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            // A delta is counted from now, so it is already on this clock and never moved
            if (retryAfter.Delta is TimeSpan delta)
                at = delta > TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours) ? null : Later(now(), delta);
            else if (retryAfter.Date is DateTimeOffset date)
                at = OnThisClock(date, serverTime, now());
        }

        if (at is null && ResetOf(response) is DateTimeOffset reset)
            at = OnThisClock(reset, serverTime, now());

        return at is DateTimeOffset value && value < now() ? now() : at;
    }

    /// <summary>
    /// Moved by the answer's Date offset only when that Date is within MaxClockDifferenceHours of now, else taken as
    /// sent. Everything is compared as a difference, so no time near either end of the calendar can throw.
    /// </summary>
    private static DateTimeOffset? OnThisClock(DateTimeOffset time, DateTimeOffset? serverTime, DateTimeOffset now)
    {
        TimeSpan clockLimit = TimeSpan.FromHours(AbuseIpDbLimits.MaxClockDifferenceHours);
        DateTimeOffset clock = serverTime is DateTimeOffset sent && (now - sent).Duration() <= clockLimit ? sent : now;
        TimeSpan ahead = time - clock;
        if (ahead > TimeSpan.FromHours(AbuseIpDbLimits.MaxResetAheadHours))
            return null;
        if (ahead < -clockLimit)
            return time;

        // The same as time + (now - clock), written from now so that only a now at the calendar's edge could leave it
        return Later(now, ahead);
    }

    /// <summary><paramref name="at"/> plus <paramref name="span"/>, or null when that is outside the calendar.</summary>
    private static DateTimeOffset? Later(DateTimeOffset at, TimeSpan span) =>
        span <= DateTimeOffset.MaxValue - at && -span <= at - DateTimeOffset.MinValue ? at + span : null;

    /// <summary>Null when the body is larger than the cap.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (body.Length + read > maxBytes)
                return null;
            body.Write(buffer, 0, read);
        }

        return body.ToArray();
    }
}
