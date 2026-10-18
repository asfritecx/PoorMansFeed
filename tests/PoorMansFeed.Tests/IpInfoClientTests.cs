using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;

namespace PoorMansFeed.Tests;

public class IpInfoClientTests
{
    private const string ChecksumJson = "{\"checksums\":{\"md5\":\"m\",\"sha1\":\"s\",\"sha256\":\"abc\"}}";

    private static (IpInfoClient Client, StubHttpHandler Handler) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond, CoreOptions? options = null)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new IpInfoClient(http, options ?? new CoreOptions(), (_, _) => Task.CompletedTask);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ipinfo-client-test-{Guid.NewGuid():N}.tmp");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // ---- Checksum ----

    [Fact]
    public async Task Checksum_200_ReturnsSha256()
    {
        var (client, _) = Create((_, _) => Json(ChecksumJson));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("abc", result.Sha256);
        Assert.Null(result.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    public async Task Checksum_AuthStatus_IsTokenRejected(HttpStatusCode status)
    {
        var (client, _) = Create((_, _) => new HttpResponseMessage(status));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.TokenRejected, result.Outcome);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task Checksum_429_WithRetryAfter_IsRateLimitedWithDelay()
    {
        var (client, _) = Create((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(2), result.RetryAfter);
    }

    [Fact]
    public async Task Checksum_429_WithoutRetryAfter_IsRateLimitedWithNull()
    {
        var (client, _) = Create((_, _) => new HttpResponseMessage((HttpStatusCode)429));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.RateLimited, result.Outcome);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task Checksum_HttpRequestException_IsOffline()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("network down"));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Offline, result.Outcome);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task Checksum_200_NotJson_IsServiceError()
    {
        var (client, _) = Create((_, _) => Json("not json"));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.ServiceError, result.Outcome);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task Checksum_200_MissingSha256_IsServiceError()
    {
        var (client, _) = Create((_, _) => Json("{\"checksums\":{\"md5\":\"m\"}}"));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.ServiceError, result.Outcome);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task Checksum_500_IsServiceError()
    {
        var (client, _) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        ChecksumFetch result = await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.ServiceError, result.Outcome);
    }

    [Fact]
    public async Task Checksum_SendsUserAgentFromOptions()
    {
        var options = new CoreOptions { UserAgent = "PoorMansFeed-Test/1.0" };
        var (client, handler) = Create((_, _) => Json(ChecksumJson), options);

        await client.FetchChecksumAsync("token", CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Contains("PoorMansFeed-Test/1.0", handler.UserAgents);
    }

    [Fact]
    public async Task Checksum_CancelledToken_ThrowsOperationCanceled()
    {
        var (client, _) = Create((_, _) => Json(ChecksumJson));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchChecksumAsync("token", cts.Token));
    }

    // ---- Download ----

    private sealed class RecordingProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Reports { get; } = new();

        public void Report(UpdateProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Download_200_WritesFileAndReturnsSha256()
    {
        byte[] body = new byte[200 * 1024];
        Random.Shared.NextBytes(body);
        string path = TempPath();
        try
        {
            var (client, _) = Create((_, _) => Bytes(body));
            var progress = new RecordingProgress();

            DownloadOutcome result = await client.DownloadAsync("token", path, progress, CancellationToken.None);

            Assert.Equal(UpdateFailure.None, result.Failure);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), result.Sha256);
            Assert.Equal(body, File.ReadAllBytes(path));

            Assert.NotEmpty(progress.Reports);
            long previous = 0;
            foreach (UpdateProgress report in progress.Reports)
            {
                Assert.True(report.Done >= previous, "progress must not decrease");
                previous = report.Done;
            }
            Assert.Equal((long)body.Length, progress.Reports[^1].Done);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task Download_FirstAttemptFails_SecondSucceeds()
    {
        byte[] body = new byte[4096];
        Random.Shared.NextBytes(body);
        string path = TempPath();
        try
        {
            var (client, handler) = Create((_, call) =>
                call == 1 ? throw new HttpRequestException("transient") : Bytes(body));

            DownloadOutcome result = await client.DownloadAsync("token", path, null, CancellationToken.None);

            Assert.Equal(UpdateFailure.None, result.Failure);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), result.Sha256);
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task Download_AlwaysFails_IsOfflineAfterMaxRetries_AndNoTmpFileRemains()
    {
        var options = new CoreOptions();
        string path = TempPath();
        try
        {
            var (client, handler) = Create((_, _) => throw new HttpRequestException("down"), options);

            DownloadOutcome result = await client.DownloadAsync("token", path, null, CancellationToken.None);

            Assert.Equal(UpdateFailure.Offline, result.Failure);
            Assert.Null(result.Sha256);
            Assert.Equal(options.MaxRetries, handler.Calls);
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task Download_401_IsTokenRejectedWithoutRetry()
    {
        string path = TempPath();
        try
        {
            var (client, handler) = Create((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));

            DownloadOutcome result = await client.DownloadAsync("token", path, null, CancellationToken.None);

            Assert.Equal(UpdateFailure.TokenRejected, result.Failure);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task Download_429_IsRateLimitedWithoutRetry()
    {
        string path = TempPath();
        try
        {
            var (client, handler) = Create((_, _) => new HttpResponseMessage((HttpStatusCode)429));

            DownloadOutcome result = await client.DownloadAsync("token", path, null, CancellationToken.None);

            Assert.Equal(UpdateFailure.RateLimited, result.Failure);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task Download_ContentLengthFarAboveFreeSpace_IsNotEnoughDiskSpace()
    {
        string path = TempPath();
        try
        {
            var (client, handler) = Create((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) };
                response.Content.Headers.ContentLength = long.MaxValue / 4;
                return response;
            });

            DownloadOutcome result = await client.DownloadAsync("token", path, null, CancellationToken.None);

            Assert.Equal(UpdateFailure.NotEnoughDiskSpace, result.Failure);
            Assert.Null(result.Sha256);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }
}
