using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Identity.Client.Extensions.Msal;

namespace PoorMansFeed.Core.Secrets;

/// <summary>
/// macOS: the secret as a generic password in the login Keychain, through MSAL's Keychain storage. The file at the
/// path is only an empty marker, so Exists answers without asking the Keychain, which can prompt.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class KeychainSecretStore : ISecretStore
{
    internal const string ServiceName = "PoorMansFeed";
    internal const string AccountName = "ipinfo-api-token";

    /// <summary>The AbuseIPDB key's account, under the same <see cref="ServiceName"/>.</summary>
    internal const string AbuseIpDbAccountName = "abuseipdb-api-key";

    /// <summary>The config.db key's account, under the same <see cref="ServiceName"/>.</summary>
    internal const string ConfigKeyAccountName = "config-key";

    private readonly string _markerPath;
    private readonly Storage _storage;

    public KeychainSecretStore(string markerPath, string serviceName = ServiceName, string accountName = AccountName)
    {
        ArgumentException.ThrowIfNullOrEmpty(markerPath);
        _markerPath = Path.GetFullPath(markerPath);

        StorageCreationProperties properties =
            new StorageCreationPropertiesBuilder(Path.GetFileName(_markerPath), Path.GetDirectoryName(_markerPath))
                .WithMacKeyChain(serviceName, accountName)
                .Build();
        // Warnings and errors only, whatever IDENTITYEXTENSIONTRACELEVEL says. The library traces the service and
        // account names, byte counts and its own exceptions, never the secret.
        _storage = Storage.Create(properties, new TraceSource("PoorMansFeed.Keychain", SourceLevels.Warning));
    }

    public bool Exists => File.Exists(_markerPath);

    public byte[]? Read()
    {
        byte[] secret = Call(_storage.ReadData, "read");
        return secret.Length == 0 ? null : secret;
    }

    public void Write(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Call(() => _storage.WriteData(secret), "write");

        // The library touches the marker after the Keychain write but doesn't report a failure to, so it is made here
        CreateMarker();
    }

    public void Delete()
    {
        try
        {
            Call(() => _storage.Clear(), "delete");
        }
        catch (SecretStoreException)
        {
            // The library deletes the marker before it removes the item, and throws only when the item is still there
            // (a missing item is not an error), so the marker is made again for Exists to stay true
            CreateMarker();
            throw;
        }

        // The library deletes the marker but doesn't report a failure to, so it is deleted again here
        File.Delete(_markerPath);
    }

    // CreateNew does not follow a link left in the marker's place
    private void CreateMarker()
    {
        if (!File.Exists(_markerPath))
            new FileStream(_markerPath, FileMode.CreateNew, FileAccess.Write).Dispose();
    }

    private static void Call(Action action, string verb) => Call(() => { action(); return true; }, verb);

    // The boundary to the library: a Keychain error (a refused prompt, a locked keychain) arrives as the library's own
    // exception types, which are internal to it, so every exception becomes one SecretStoreException here.
    private static T Call<T>(Func<T> action, string verb)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            throw new SecretStoreException($"The Keychain couldn't {verb} the secret.", ex);
        }
    }
}
