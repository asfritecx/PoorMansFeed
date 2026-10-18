using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

public sealed class TokenStoreTests
{
    public static TheoryData<Exception> StoreFailures =>
    [
        new SecretStoreException("The Keychain couldn't write the secret."),
        new CryptographicException(),
        new IOException(),
        new UnauthorizedAccessException(),
    ];

    [Fact]
    public void Save_then_TryLoad_returns_same_text()
    {
        var tokens = new TokenStore(new InMemorySecretStore());
        const string token = "abc123-t\u00F6ken-SECRET";

        Assert.True(tokens.Save(token));

        Assert.Equal(token, tokens.TryLoad());
    }

    [Fact]
    public void The_store_gets_the_token_as_UTF8_and_does_the_protecting()
    {
        // Nothing in the app encrypts: the bytes handed over are the token, and DPAPI or the Keychain protect them
        var store = new InMemorySecretStore();

        Assert.True(new TokenStore(store).Save("t\u00F6ken"));

        Assert.Equal(Encoding.UTF8.GetBytes("t\u00F6ken"), store.Secret);
    }

    [Fact]
    public void TryLoad_with_nothing_saved_returns_null()
    {
        Assert.Null(new TokenStore(new InMemorySecretStore()).TryLoad());
    }

    [Fact]
    public void HasToken_follows_Save_and_Clear_without_reading_the_store()
    {
        var store = new InMemorySecretStore();
        var tokens = new TokenStore(store);
        Assert.False(tokens.HasToken);

        Assert.True(tokens.Save("abc123"));
        int reads = store.Reads;
        Assert.True(tokens.HasToken);
        Assert.True(tokens.Clear());
        Assert.False(tokens.HasToken);

        // A Keychain read can prompt, and HasToken is asked on the UI thread
        Assert.Equal(reads, store.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_refuses_a_blank_token(string token)
    {
        var store = new InMemorySecretStore();

        Assert.False(new TokenStore(store).Save(token));

        Assert.False(store.Exists);
    }

    [Fact]
    public void Save_fails_when_the_store_drops_the_write_without_saying_so()
    {
        var store = new InMemorySecretStore { DropWrites = true };

        Assert.False(new TokenStore(store).Save("abc123"));
    }

    [Fact]
    public void Save_fails_when_the_store_gives_back_other_bytes()
    {
        var store = new InMemorySecretStore { ReadsOtherBytes = true };

        Assert.False(new TokenStore(store).Save("abc123"));
    }

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public void Failed_Save_reports_false_and_keeps_the_saved_token(Exception failure)
    {
        var store = new InMemorySecretStore();
        var tokens = new TokenStore(store);
        Assert.True(tokens.Save("abc123"));
        store.WriteFailure = failure;

        Assert.False(tokens.Save("def456"));

        store.WriteFailure = null;
        Assert.Equal("abc123", tokens.TryLoad());
    }

    [Fact]
    public void TryLoad_keeps_the_token_when_the_store_cant_be_reached_now()
    {
        // A refused Keychain prompt or a locked keychain: the token is still there for the next try
        var store = new InMemorySecretStore();
        var tokens = new TokenStore(store);
        Assert.True(tokens.Save("abc123"));
        store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");

        Assert.Null(tokens.TryLoad());

        Assert.True(tokens.HasToken);
        Assert.NotNull(store.Secret);
    }

    [Fact]
    public void TryLoad_removes_a_token_that_can_never_be_read()
    {
        // DPAPI data protected for another account, or damaged
        var store = new InMemorySecretStore();
        var tokens = new TokenStore(store);
        Assert.True(tokens.Save("abc123"));
        store.ReadFailure = new CryptographicException();

        Assert.Null(tokens.TryLoad());

        Assert.False(tokens.HasToken);
        Assert.Null(store.Secret);
    }

    [Fact]
    public void TryLoad_removes_a_saved_secret_that_is_not_text()
    {
        var store = new InMemorySecretStore { Secret = [0xC3, 0x28], Marker = true };
        var tokens = new TokenStore(store);

        Assert.Null(tokens.TryLoad());

        Assert.False(tokens.HasToken);
        Assert.Null(store.Secret);
    }

    [Fact]
    public void TryLoad_removes_a_marker_left_without_its_secret()
    {
        // The Keychain item was removed by hand, so HasToken would otherwise stay true
        var store = new InMemorySecretStore { Marker = true };
        var tokens = new TokenStore(store);

        Assert.Null(tokens.TryLoad());

        Assert.False(tokens.HasToken);
    }

    [Fact]
    public void Failures_never_trace_the_token()
    {
        const string token = "trace-sentinel-7Q4KX";
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var store = new InMemorySecretStore();
            var tokens = new TokenStore(store);
            Assert.True(tokens.Save(token));
            store.ReadFailure = new SecretStoreException("The Keychain couldn't read the secret.");
            Assert.Null(tokens.TryLoad());
            Assert.False(tokens.Save(token));
            store.ReadFailure = new CryptographicException();
            Assert.Null(tokens.TryLoad());
            store.WriteFailure = new IOException();
            Assert.False(tokens.Save(token));
            store.DeleteFailure = new UnauthorizedAccessException();
            Assert.False(tokens.Clear());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains("The API token couldn't be", listener.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(token, listener.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_conversion_failures_never_trace_the_bytes_or_characters()
    {
        // The fallback exceptions' own messages quote what they couldn't convert: "bytes [FE][FD]", "character \uD800"
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var store = new InMemorySecretStore { Secret = [0x61, 0xFE, 0xFD], Marker = true };
            Assert.Null(new TokenStore(store).TryLoad());
            Assert.False(new TokenStore(new InMemorySecretStore()).Save("abc\uD800def"));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains(nameof(DecoderFallbackException), listener.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(EncoderFallbackException), listener.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[FE]", listener.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("D800", listener.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clear_reports_false_when_the_store_cant_remove_the_token()
    {
        var store = new InMemorySecretStore();
        var tokens = new TokenStore(store);
        Assert.True(tokens.Save("abc123"));
        store.DeleteFailure = new SecretStoreException("The Keychain couldn't delete the secret.");

        Assert.False(tokens.Clear());

        Assert.True(tokens.HasToken);
    }

    // Trace listeners are process-wide, so this also hears other tests; it only looks for a text they don't write
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
