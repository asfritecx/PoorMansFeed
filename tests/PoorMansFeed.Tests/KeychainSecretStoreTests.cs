using System.Text;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

/// <summary>
/// These use the real login Keychain, so they run only on macOS and only when asked for:
/// <c>POORMANSFEED_KEYCHAIN_TESTS=1 dotnet test</c>. Each test saves under its own service name, never the app's, and
/// removes its item afterwards. The test process creates the items itself, so the Keychain doesn't prompt.
/// </summary>
public sealed class KeychainSecretStoreTests
{
    private const string EnableVariable = "POORMANSFEED_KEYCHAIN_TESTS";
    private const string SkipReason = "Uses the real login Keychain: set " + EnableVariable + "=1 on macOS to run it.";

    private static readonly byte[] First = Encoding.UTF8.GetBytes("abc123-SECRET");
    private static readonly byte[] Second = Encoding.UTF8.GetBytes("def456-SECRET");

    private static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    private static string ThrowawayService() => "PoorMansFeed.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void The_service_and_account_names_stay_fixed_and_apart()
    {
        // Makes no store, so it runs on every macOS test run without the variable; the guard is only for CA1416.
        // Changing a name loses every saved secret, because the item is found by these names alone; equal account
        // names would make one secret replace the other.
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("The Keychain names are used only on macOS.");
            return;
        }

        Assert.Equal("PoorMansFeed", KeychainSecretStore.ServiceName);
        Assert.Equal("ipinfo-api-token", KeychainSecretStore.AccountName);
        Assert.Equal("abuseipdb-api-key", KeychainSecretStore.AbuseIpDbAccountName);
        Assert.NotEqual(KeychainSecretStore.AccountName, KeychainSecretStore.AbuseIpDbAccountName);
        Assert.Equal("config-key", KeychainSecretStore.ConfigKeyAccountName);
        Assert.NotEqual(KeychainSecretStore.AccountName, KeychainSecretStore.ConfigKeyAccountName);
        Assert.NotEqual(KeychainSecretStore.AbuseIpDbAccountName, KeychainSecretStore.ConfigKeyAccountName);
    }

    [Fact]
    public void Raw_key_bytes_round_trip()
    {
        // The config key is 32 raw bytes, not text, so zero and 0xFF bytes must come back as they were saved
        if (!OperatingSystem.IsMacOS() || !Enabled)
        {
            Assert.Skip(SkipReason);
            return;
        }

        byte[] key = [0x00, 0xFF, .. Enumerable.Range(1, 28).Select(i => (byte)i), 0xFF, 0x00];
        using var temp = new TempFolder();
        var store = new KeychainSecretStore(temp.File("config_key"), ThrowawayService(), "test");
        try
        {
            store.Write(key);
            Assert.Equal(key, store.Read());

            store.Delete();
            Assert.False(store.Exists);
        }
        finally
        {
            store.Delete();
        }
    }

    [Fact]
    public void Write_Read_overwrite_and_Delete_round_trip()
    {
        if (!OperatingSystem.IsMacOS() || !Enabled)
        {
            Assert.Skip(SkipReason);
            return;
        }

        using var temp = new TempFolder();
        string marker = temp.File("ipinfo_token");
        var store = new KeychainSecretStore(marker, ThrowawayService(), "test");
        try
        {
            Assert.False(store.Exists);
            Assert.Null(store.Read());

            store.Write(First);
            Assert.True(store.Exists);
            Assert.Equal(First, store.Read());
            // The marker says only that a secret is saved
            Assert.Equal(0, new FileInfo(marker).Length);

            store.Write(Second);
            Assert.Equal(Second, store.Read());

            store.Delete();
            Assert.False(store.Exists);
            Assert.Null(store.Read());
        }
        finally
        {
            store.Delete();
        }
    }

    [Fact]
    public void A_new_store_with_the_same_names_reads_the_saved_secret()
    {
        if (!OperatingSystem.IsMacOS() || !Enabled)
        {
            Assert.Skip(SkipReason);
            return;
        }

        using var temp = new TempFolder();
        string marker = temp.File("ipinfo_token");
        string service = ThrowawayService();
        var writer = new KeychainSecretStore(marker, service, "test");
        try
        {
            writer.Write(First);

            var reader = new KeychainSecretStore(marker, service, "test");
            Assert.True(reader.Exists);
            Assert.Equal(First, reader.Read());
        }
        finally
        {
            writer.Delete();
        }
    }

    [Fact]
    public void Two_accounts_under_one_service_keep_their_own_secrets()
    {
        // The IPinfo token and the AbuseIPDB key share the service name and differ by account and marker
        if (!OperatingSystem.IsMacOS() || !Enabled)
        {
            Assert.Skip(SkipReason);
            return;
        }

        using var temp = new TempFolder();
        string service = ThrowawayService();
        var a = new KeychainSecretStore(temp.File("ipinfo_token"), service, "test-a");
        var b = new KeychainSecretStore(temp.File("abuseipdb_key"), service, "test-b");
        try
        {
            a.Write(First);
            b.Write(Second);
            Assert.Equal(First, a.Read());
            Assert.Equal(Second, b.Read());

            a.Delete();
            Assert.False(a.Exists);
            Assert.Null(a.Read());
            Assert.True(b.Exists);
            Assert.Equal(Second, b.Read());
        }
        finally
        {
            // b is removed even when removing a throws, so no throwaway item is left in the login Keychain
            try
            {
                a.Delete();
            }
            finally
            {
                b.Delete();
            }
        }
    }

    [Fact]
    public void Delete_with_nothing_saved_succeeds()
    {
        if (!OperatingSystem.IsMacOS() || !Enabled)
        {
            Assert.Skip(SkipReason);
            return;
        }

        using var temp = new TempFolder();
        var store = new KeychainSecretStore(temp.File("ipinfo_token"), ThrowawayService(), "test");

        store.Delete();

        Assert.False(store.Exists);
    }
}
