using System.Diagnostics;

namespace PoorMansFeed.Core.Azure;

internal sealed record AzurePageResult(AzureSourceFailure Failure, AzurePublishedFile? File);

internal sealed record AzureFileResult(AzureSourceFailure Failure, byte[]? Body);

/// <summary>
/// The two requests the Azure tool makes: the download page, then the file it names. Failures come back as values.
/// Only a cancel by the caller throws. Nothing here writes a URL to the trace.
/// </summary>
internal sealed class AzureServiceTagsClient
{
    private readonly HttpClient _http;
    private readonly CoreOptions _options;

    public AzureServiceTagsClient(HttpClient http, CoreOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<AzurePageResult> GetPublishedFileAsync(CancellationToken ct)
    {
        (AzureSourceFailure failure, byte[]? body) = await FetchAsync(
            _options.AzureServiceTagsPageUrl,
            _options.AzureMaxPageBytes,
            TimeSpan.FromSeconds(_options.AzurePageTimeoutSeconds),
            AzureSourceFailure.PageUnreadable,
            ct);
        if (body is null)
            return new AzurePageResult(failure, null);

        AzurePublishedFile? file = AzureDownloadPage.TryFind(body);
        if (file is null)
        {
            Trace.TraceWarning("The Azure download page did not name a service tags file.");
            return new AzurePageResult(AzureSourceFailure.PageUnreadable, null);
        }

        return new AzurePageResult(AzureSourceFailure.None, file);
    }

    public async Task<AzureFileResult> DownloadAsync(AzurePublishedFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        (AzureSourceFailure failure, byte[]? body) = await FetchAsync(
            file.Url.AbsoluteUri,
            _options.AzureMaxFileBytes,
            TimeSpan.FromMinutes(_options.HttpTimeoutMinutes),
            AzureSourceFailure.FileUnreadable,
            ct);
        return new AzureFileResult(failure, body);
    }

    private async Task<(AzureSourceFailure Failure, byte[]? Body)> FetchAsync(
        string url, long maxBytes, TimeSpan timeout, AzureSourceFailure tooLarge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                Trace.TraceWarning($"An Azure request failed with HTTP {(int)response.StatusCode}.");
                return (AzureSourceFailure.ServiceError, null);
            }

            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            {
                Trace.TraceWarning("An Azure response was larger than the size cap.");
                return (tooLarge, null);
            }

            byte[]? body = await ReadCappedAsync(response, maxBytes, linked.Token);
            if (body is null)
            {
                Trace.TraceWarning("An Azure response was larger than the size cap.");
                return (tooLarge, null);
            }

            return (AzureSourceFailure.None, body);
        }
        catch (HttpRequestException)
        {
            Trace.TraceWarning("An Azure endpoint could not be reached.");
            return (AzureSourceFailure.Offline, null);
        }
        catch (IOException)
        {
            Trace.TraceWarning("An Azure response could not be read.");
            return (AzureSourceFailure.Offline, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace.TraceWarning("An Azure request timed out.");
            return (AzureSourceFailure.Offline, null);
        }
    }

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
