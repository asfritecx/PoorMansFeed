using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PoorMansFeed.Core;

/// <summary>
/// Talks to the IPInfo Lite checksum and download endpoints. The API token travels in the query string,
/// so URLs are never traced and no exception message leaves this class; failures are returned as enums.
/// </summary>
public sealed class IpInfoClient
{
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(30);
    private static readonly long ProgressIntervalTicks = Stopwatch.Frequency / 10;   // 100 ms
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly CoreOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public IpInfoClient(HttpClient http, CoreOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public async Task<ChecksumFetch> FetchChecksumAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(ChecksumTimeout);

        try
        {
            using var request = CreateRequest(_options.ChecksumUrl, token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotAcceptable:
                    return new ChecksumFetch(UpdateCheckOutcome.TokenRejected, null);
                case HttpStatusCode.TooManyRequests:
                    return new ChecksumFetch(UpdateCheckOutcome.RateLimited, null, ReadRetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"Checksum request failed with HTTP {(int)response.StatusCode}.");
                return new ChecksumFetch(UpdateCheckOutcome.ServiceError, null);
            }

            string json = await response.Content.ReadAsStringAsync(linked.Token);
            string? sha256 = ParseSha256(json);
            if (sha256 is null)
            {
                Trace.TraceWarning("Checksum response did not contain a SHA-256 value.");
                return new ChecksumFetch(UpdateCheckOutcome.ServiceError, null);
            }

            return new ChecksumFetch(UpdateCheckOutcome.UpdateAvailable, sha256);
        }
        catch (JsonException)
        {
            Trace.TraceWarning("Checksum response was not valid JSON.");
            return new ChecksumFetch(UpdateCheckOutcome.ServiceError, null);
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning("Checksum endpoint could not be reached.");
            return new ChecksumFetch(UpdateCheckOutcome.Offline, null);
        }
        catch (IOException)
        {
            Trace.TraceWarning("Checksum response could not be read.");
            return new ChecksumFetch(UpdateCheckOutcome.Offline, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the per-request timeout fired.
            Trace.TraceWarning("Checksum request timed out.");
            return new ChecksumFetch(UpdateCheckOutcome.Offline, null);
        }
    }

    public async Task<DownloadOutcome> DownloadAsync(string token, string tmpPath, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int maxAttempts = Math.Max(1, _options.MaxRetries);
        bool succeeded = false;

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                DownloadOutcome outcome = await AttemptDownloadAsync(token, tmpPath, progress, ct);
                if (outcome.Failure == UpdateFailure.None)
                {
                    succeeded = true;
                    return outcome;
                }

                if (!IsRetryable(outcome.Failure) || attempt >= maxAttempts)
                {
                    return outcome;
                }

                Trace.TraceWarning($"Download attempt {attempt} of {maxAttempts} failed ({outcome.Failure}). Retrying.");
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
        finally
        {
            if (!succeeded)
            {
                DeleteQuietly(tmpPath);
            }
        }
    }

    private async Task<DownloadOutcome> AttemptDownloadAsync(string token, string tmpPath, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(_options.HttpTimeoutMinutes));
        CancellationToken requestToken = linked.Token;

        try
        {
            using var request = CreateRequest(_options.DownloadUrl, token);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotAcceptable:
                    return new DownloadOutcome(UpdateFailure.TokenRejected, null);
                case HttpStatusCode.TooManyRequests:
                    return new DownloadOutcome(UpdateFailure.RateLimited, null, ReadRetryAfter(response));
            }

            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"Download request failed with HTTP {(int)response.StatusCode}.");
                return new DownloadOutcome(UpdateFailure.ServiceError, null);
            }

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long length && !HasEnoughDiskSpace(tmpPath, length))
            {
                return new DownloadOutcome(UpdateFailure.NotEnoughDiskSpace, null);
            }

            return await CopyToFileAsync(response, contentLength, tmpPath, progress, requestToken);
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning("Download endpoint could not be reached.");
            return new DownloadOutcome(UpdateFailure.Offline, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the per-request timeout fired.
            Trace.TraceWarning("Download request timed out.");
            return new DownloadOutcome(UpdateFailure.Offline, null);
        }
        catch (IOException)
        {
            // Raised while closing the partial file; network read errors are handled inside CopyToFileAsync.
            return new DownloadOutcome(UpdateFailure.StorageError, null);
        }
    }

    private static async Task<DownloadOutcome> CopyToFileAsync(HttpResponseMessage response, long? contentLength, string tmpPath,
                                                               IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        long expected = contentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct);

            FileStream file;
            try
            {
                file = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not create the download file.");
                return new DownloadOutcome(UpdateFailure.StorageError, null);
            }

            await using (file)
            {
                long total = 0;
                long nextReport = Stopwatch.GetTimestamp();

                while (true)
                {
                    int read;
                    try
                    {
                        read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), ct);
                    }
                    catch (IOException)
                    {
                        Trace.TraceWarning("Download stream was interrupted.");
                        return new DownloadOutcome(UpdateFailure.Offline, null);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    catch (IOException)
                    {
                        Trace.TraceWarning("Could not write the download file.");
                        return new DownloadOutcome(UpdateFailure.StorageError, null);
                    }

                    hash.AppendData(buffer, 0, read);
                    total += read;

                    long now = Stopwatch.GetTimestamp();
                    if (now >= nextReport)
                    {
                        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, total, expected));
                        nextReport = now + ProgressIntervalTicks;
                    }
                }

                try
                {
                    await file.FlushAsync(ct);
                }
                catch (IOException)
                {
                    Trace.TraceWarning("Could not write the download file.");
                    return new DownloadOutcome(UpdateFailure.StorageError, null);
                }

                progress?.Report(new UpdateProgress(UpdatePhase.Downloading, total, expected));
                return new DownloadOutcome(UpdateFailure.None, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private HttpRequestMessage CreateRequest(string baseUrl, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "?token=" + Uri.EscapeDataString(token));
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        return request;
    }

    // Needs three times the content length free, as the original code did. Integer division keeps the
    // comparison exact: contentLength > free / 3 is the same as 3 * contentLength > free.
    private static bool HasEnoughDiskSpace(string tmpPath, long contentLength)
    {
        long free;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(tmpPath));
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The free space cannot be measured, so the check is skipped rather than blocking the download.
            return true;
        }

        return contentLength <= free / 3;
    }

    private static string? ParseSha256(string json)
    {
        ChecksumResponse? parsed = JsonSerializer.Deserialize<ChecksumResponse>(json);
        string? sha256 = parsed?.Checksums?.Sha256;
        return string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? value = response.Headers.RetryAfter;
        if (value is null)
        {
            return null;
        }

        if (value.Delta is TimeSpan delta)
        {
            return delta;
        }

        if (value.Date is DateTimeOffset date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsRetryable(UpdateFailure failure) =>
        failure is UpdateFailure.Offline or UpdateFailure.ServiceError;

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not delete the partial download.");
        }
    }
}
