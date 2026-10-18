namespace PoorMansFeed.Tests;

/// <summary>
/// Answers HTTP requests with a function of (request, 1-based call number). Whatever the function throws
/// is thrown from SendAsync, so HttpClient surfaces it unchanged.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;
    private readonly List<Uri> _uris = new();
    private readonly List<string> _userAgents = new();

    public StubHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
    }

    public int Calls { get; private set; }

    public IReadOnlyList<Uri> RequestedUris => _uris;

    public IReadOnlyList<string> UserAgents => _userAgents;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        _uris.Add(request.RequestUri!);
        _userAgents.Add(request.Headers.UserAgent.ToString());
        return Task.FromResult(_respond(request, Calls));
    }
}
