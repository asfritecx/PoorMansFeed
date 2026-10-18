namespace PoorMansFeed.Core.Secrets;

/// <summary>Picks the operating system's store for the current user.</summary>
internal static class SecretStores
{
    /// <summary>
    /// DPAPI on Windows and the login Keychain on macOS, with <paramref name="path"/> as the DPAPI file or the Keychain
    /// marker. Any other system gets a store that saves nothing, so a secret is never written in plain text.
    /// </summary>
    public static ISecretStore ForCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(path);
        if (OperatingSystem.IsMacOS())
            return new KeychainSecretStore(path);
        return new UnavailableSecretStore();
    }

    private sealed class UnavailableSecretStore : ISecretStore
    {
        public bool Exists => false;

        public byte[]? Read() => null;

        public void Write(byte[] secret) =>
            throw new SecretStoreException("This system has no secret store the app can use.");

        public void Delete()
        {
        }
    }
}
