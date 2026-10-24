namespace PoorMansFeed.Core.Secrets;

/// <summary>
/// One secret kept by the operating system for the current user. The store does the protecting: nothing in this app
/// encrypts, derives a key or holds a passphrase.
/// </summary>
internal interface ISecretStore
{
    /// <summary>True when a secret is saved. It never reads the secret, so it never makes the OS ask the user.</summary>
    bool Exists { get; }

    /// <summary>
    /// The saved secret, or null when none. Throws CryptographicException when it can never be read, and
    /// SecretStoreException, IOException or UnauthorizedAccessException when the store can't be reached now.
    /// </summary>
    byte[]? Read();

    /// <summary>Saves the secret in place of any saved before. Throws as Read does when it can't.</summary>
    void Write(byte[] secret);

    /// <summary>Removes the saved secret. Removing a secret that isn't there succeeds.</summary>
    void Delete();
}

/// <summary>
/// The operating system's store refused or failed. It wraps the library's own exception, whose types are internal to
/// the library, so callers catch one type. Neither message holds the secret.
/// </summary>
internal sealed class SecretStoreException(string message, Exception? innerException = null) : Exception(message, innerException);
