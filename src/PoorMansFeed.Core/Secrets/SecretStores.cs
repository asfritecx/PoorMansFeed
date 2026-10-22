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

    /// <summary>
    /// The AbuseIPDB key's store: DPAPI on Windows and its own account in the login Keychain on macOS, with
    /// <see cref="AppPaths.AbuseIpDbKeyPath"/> as the DPAPI file or the Keychain marker. The path is taken here, not
    /// from the caller, because on Windows the file is the only thing that tells the two secrets apart: any other path
    /// could replace the IPinfo token. Any other system gets a store that saves nothing.
    /// </summary>
    public static ISecretStore AbuseIpDbKeyForCurrentUser(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string path = paths.AbuseIpDbKeyPath;
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(path);
        if (OperatingSystem.IsMacOS())
            return new KeychainSecretStore(path, KeychainSecretStore.ServiceName, KeychainSecretStore.AbuseIpDbAccountName);
        return new UnavailableSecretStore();
    }

    /// <summary>
    /// The config.db key's store: DPAPI on Windows and its own account in the login Keychain on macOS, with
    /// <see cref="AppPaths.ConfigKeyPath"/> as the DPAPI file or the Keychain marker. The path is taken here, not from
    /// the caller, because on Windows the file is the only thing that tells the three secrets apart: any other path
    /// could replace the IPinfo token or the AbuseIPDB key. Any other system gets a store that saves nothing.
    /// </summary>
    internal static ISecretStore ConfigKeyForCurrentUser(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string path = paths.ConfigKeyPath;
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(path);
        if (OperatingSystem.IsMacOS())
            return new KeychainSecretStore(path, KeychainSecretStore.ServiceName, KeychainSecretStore.ConfigKeyAccountName);
        return new UnavailableSecretStore();
    }

    internal sealed class UnavailableSecretStore : ISecretStore
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
