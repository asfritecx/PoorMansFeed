using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Core;

/// <summary>
/// The IPInfo API token, kept by the operating system for the current user (<see cref="ISecretStore"/>): DPAPI on
/// Windows, the login Keychain on macOS. Failures come back as null or false, and nothing traced holds the token.
/// The AbuseIPDB key uses the same rules over its own store; <paramref name="secretName"/> says in traces which secret
/// a failure is about.
/// </summary>
internal sealed class TokenStore(ISecretStore store, string secretName = "API token")
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ISecretStore _store = store;
    private readonly string _secretName = secretName;

    /// <summary>True when a token is saved. It doesn't read the token, so it never makes the OS ask the user.</summary>
    public bool HasToken => _store.Exists;

    /// <summary>
    /// Returns the token, or null when none is saved or it can't be read now. A saved token that can never be read
    /// (protected for another account, damaged, or not text) is removed.
    /// </summary>
    public string? TryLoad()
    {
        try
        {
            byte[]? secret = _store.Read();
            if (secret is null)
            {
                // A Keychain marker left without its item (removed by hand) would keep HasToken true
                if (_store.Exists)
                    DeleteQuietly();
                return null;
            }

            return StrictUtf8.GetString(secret);
        }
        catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
        {
            TraceFailure("read, so it is removed", ex);
            DeleteQuietly();
            return null;
        }
        catch (Exception ex) when (ex is SecretStoreException or IOException or UnauthorizedAccessException)
        {
            TraceFailure("read", ex);
            return null;
        }
    }

    /// <summary>
    /// Saves the token in place of any saved before, then reads it back. Returns true only when the store gives the
    /// same token back, so a write that failed without saying so counts as a failure.
    /// </summary>
    public bool Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            byte[] secret = StrictUtf8.GetBytes(token);
            _store.Write(secret);
            byte[]? readBack = _store.Read();
            return readBack is not null && readBack.AsSpan().SequenceEqual(secret);
        }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException or EncoderFallbackException
                                       or IOException or UnauthorizedAccessException)
        {
            TraceFailure("saved", ex);
            return false;
        }
    }

    /// <summary>Removes the saved token. Returns true when none is saved afterwards.</summary>
    public bool Clear()
    {
        try
        {
            _store.Delete();
            return !_store.Exists;
        }
        catch (Exception ex) when (ex is SecretStoreException or IOException or UnauthorizedAccessException)
        {
            TraceFailure("removed", ex);
            return false;
        }
    }

    private void DeleteQuietly()
    {
        try
        {
            _store.Delete();
        }
        catch (Exception ex) when (ex is SecretStoreException or IOException or UnauthorizedAccessException)
        {
            TraceFailure("removed", ex);
        }
    }

    // Types and messages only. The stores' messages name the store, a path or an OS error code, never the secret.
    private void TraceFailure(string what, Exception ex)
    {
        string cause = ex.InnerException is { } inner ? $"; {Describe(inner)}" : "";
        Trace.TraceWarning($"The {_secretName} couldn't be {what} ({Describe(ex)}{cause}).");
    }

    // A fallback exception's message quotes the bytes or the character it couldn't convert, which are the token's own,
    // so only its type is traced
    private static string Describe(Exception ex) =>
        ex is DecoderFallbackException or EncoderFallbackException ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
}
