using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PoorMansFeed.Core.Secrets;

namespace PoorMansFeed.Tests;

/// <summary>
/// DPAPI exists only on Windows, so these run there and are skipped elsewhere. Each test uses its own file: DPAPI keys
/// belong to the Windows account, so nothing here reaches the app's saved token. The platform attribute is for the
/// analyzer, which doesn't follow the skip guard into a lambda; the guard is what skips the tests elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("abc123-SECRET");

    [Fact]
    public void Write_then_Read_returns_the_secret_and_the_file_does_not_hold_it_in_clear()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        using var temp = new TempFolder();
        string path = temp.File("ipinfo_token");
        var store = new DpapiSecretStore(path);

        store.Write(Secret);

        Assert.True(store.Exists);
        Assert.Equal(Secret, store.Read());
        Assert.DoesNotContain("abc123-SECRET", Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        Assert.Equal([path], Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Read_returns_null_when_nothing_is_saved()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        using var temp = new TempFolder();
        var store = new DpapiSecretStore(temp.File("ipinfo_token"));

        Assert.False(store.Exists);
        Assert.Null(store.Read());
    }

    [Fact]
    public void Read_of_a_damaged_file_throws_CryptographicException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        using var temp = new TempFolder();
        string path = temp.File("ipinfo_token");
        File.WriteAllBytes(path, new byte[64]);
        var store = new DpapiSecretStore(path);

        Assert.Throws<CryptographicException>(() => store.Read());
    }

    [Fact]
    public void Delete_removes_the_file_and_a_leftover_temporary_file()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        using var temp = new TempFolder();
        string path = temp.File("ipinfo_token");
        var store = new DpapiSecretStore(path);
        store.Write(Secret);
        File.WriteAllBytes(path + ".tmp", [1, 2, 3]);

        store.Delete();
        store.Delete();

        Assert.False(store.Exists);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void Failed_Write_keeps_the_secret_saved_before()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        using var temp = new TempFolder();
        string path = temp.File("ipinfo_token");
        var store = new DpapiSecretStore(path);
        store.Write(Secret);
        // A folder in the temporary file's place makes the write fail
        Directory.CreateDirectory(path + ".tmp");

        Assert.Throws<UnauthorizedAccessException>(() => store.Write(Encoding.UTF8.GetBytes("def456")));

        Assert.Equal(Secret, store.Read());
    }
}
