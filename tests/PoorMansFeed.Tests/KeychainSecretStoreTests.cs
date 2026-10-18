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
