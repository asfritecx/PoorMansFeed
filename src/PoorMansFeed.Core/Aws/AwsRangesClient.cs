using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace PoorMansFeed.Core.Aws;

/// <summary>
/// The outcome of one request. NotModified is true for a 304, when Body and ETag are null. ETag is the header as
/// the server sent it, or null when there was none or it could not be read.
/// </summary>
internal sealed record AwsFetchResult(AwsSourceFailure Failure, bool NotModified, byte[]? Body, string? ETag);

/// <summary>
/// The one request the AWS tool makes: ip-ranges.json, sent with If-None-Match when a saved ETag is known, so an
/// unchanged file costs no body. Failures come back as values. Only a cancel by the caller throws. Nothing here
/// writes a URL to the trace.
/// </summary>
internal sealed class AwsRangesClient
{
    private readonly HttpClient _http;
    private readonly CoreOptions _options;

    public AwsRangesClient(HttpClient http, CoreOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AwsFetchResult> FetchAsync(string? knownETag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.AwsRequestTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.AwsIpRangesUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            if (knownETag is not null && EntityTagHeaderValue.TryParse(knownETag, out EntityTagHeaderValue? tag))
                request.Headers.IfNoneMatch.Add(tag);

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new AwsFetchResult(AwsSourceFailure.None, true, null, null);

            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"The AWS ranges request failed with HTTP {(int)response.StatusCode}.");
                return Failure(AwsSourceFailure.ServiceError);
            }

            long maxBytes = _options.AwsMaxFileBytes;
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                Trace.TraceWarning("The AWS ranges response was larger than the size cap.");
                return Failure(AwsSourceFailure.FileUnreadable);
            }

            byte[]? body = await ReadCappedAsync(response, maxBytes, linked.Token);
            if (body is null)
            {
                Trace.TraceWarning("The AWS ranges response was larger than the size cap.");
                return Failure(AwsSourceFailure.FileUnreadable);
            }

            return new AwsFetchResult(AwsSourceFailure.None, false, body, response.Headers.ETag?.ToString());
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning("The AWS ranges could not be reached.");
            return Failure(AwsSourceFailure.Offline);
        }
        catch (IOException)
        {
            Trace.TraceWarning("The AWS ranges response could not be read.");
            return Failure(AwsSourceFailure.Offline);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.TraceWarning("The AWS ranges request timed out.");
            return Failure(AwsSourceFailure.Offline);
        }
    }

    private static AwsFetchResult Failure(AwsSourceFailure failure) => new(failure, false, null, null);

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
