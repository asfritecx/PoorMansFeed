using System.Diagnostics;
using System.Security.Cryptography;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Core.Config;

/// <summary>What reading or saving the config key gave.</summary>
internal enum KeyState
{
    /// <summary>The 32-byte key was read, or saved and read back.</summary>
    Read,

    /// <summary>No key is saved.</summary>
    Missing,

    /// <summary>A key is saved but can never be read: protected for another account, damaged, the wrong length, or a
    /// marker without its item.</summary>
    Unusable,

    /// <summary>The store can't be reached now (a locked keychain, a denied prompt), or a save didn't read back.</summary>
    Unavailable,

    /// <summary>This system has no store the app can use, so no key can be kept.</summary>
    NoStore,
}

/// <summary>
/// The raw 32-byte config key over an ISecretStore. Read-back on save; never deletes on read. A key that can never be
/// read is reported, not removed, because config.db's sections can't be opened without it and the caller decides what
/// starts fresh. Nothing traced holds the key's bytes.
/// </summary>
internal sealed class ConfigKeyStore(ISecretStore store)
{
    private const string SecretName = "config key";

    private readonly ISecretStore _store = store;

    /// <summary>True when a key is saved. It doesn't read the key, so it never makes the OS ask the user.</summary>
    internal bool Exists => _store.Exists;

    /// <summary>Reads the saved key. <paramref name="key"/> is the 32 bytes for <see cref="KeyState.Read"/> and null otherwise.</summary>
    internal KeyState TryLoad(out byte[]? key)
    {
        key = null;
        if (_store is SecretStores.UnavailableSecretStore)
            return KeyState.NoStore;
        // A read can prompt, so a missing key is told from the marker alone
        if (!_store.Exists)
            return KeyState.Missing;

        byte[]? secret;
        try
        {
            secret = _store.Read();
        }
        catch (CryptographicException ex)
        {
            TraceFailure("read", ex);
            return KeyState.Unusable;
        }
        catch (Exception ex) when (ex is SecretStoreException or IOException or UnauthorizedAccessException)
        {
            TraceFailure("read now", ex);
            return KeyState.Unavailable;
        }

        if (secret is null)
        {
            Trace.TraceWarning($"The {SecretName} couldn't be read (its marker has no saved key).");
            return KeyState.Unusable;
        }

        if (secret.Length != ConfigSeal.KeyBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            Trace.TraceWarning($"The {SecretName} couldn't be read (it isn't the length of a key).");
            return KeyState.Unusable;
        }

        key = secret;
        return KeyState.Read;
    }

    /// <summary>
    /// Saves the key in place of any saved before, then reads it back. <see cref="KeyState.Read"/> only when the store
    /// gives the same 32 bytes back, so a write that failed without saying so counts as a failure. The caller's array is
    /// neither kept nor zeroed. Throws ArgumentException when the key isn't 32 bytes.
    /// </summary>
    internal KeyState Replace(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != ConfigSeal.KeyBytes)
            throw new ArgumentException("The config key must be 32 bytes.", nameof(key));
        if (_store is SecretStores.UnavailableSecretStore)
            return KeyState.NoStore;

        try
        {
            _store.Write(key);
            byte[]? readBack = _store.Read();
            bool same = readBack is not null && readBack.AsSpan().SequenceEqual(key);
            if (readBack is not null)
                CryptographicOperations.ZeroMemory(readBack);
            if (same)
                return KeyState.Read;

            Trace.TraceWarning($"The {SecretName} couldn't be saved (the store didn't give it back).");
            return KeyState.Unavailable;
        }
        catch (Exception ex) when (ex is SecretStoreException or CryptographicException or IOException
                                       or UnauthorizedAccessException)
        {
            TraceFailure("saved", ex);
            return KeyState.Unavailable;
        }
    }

    // Types and messages only. The stores' messages name the store, a path or an OS error code, never the secret.
    private static void TraceFailure(string what, Exception ex)
    {
        string cause = ex.InnerException is { } inner ? $"; {Describe(inner)}" : "";
        Trace.TraceWarning($"The {SecretName} couldn't be {what} ({Describe(ex)}{cause}).");
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
