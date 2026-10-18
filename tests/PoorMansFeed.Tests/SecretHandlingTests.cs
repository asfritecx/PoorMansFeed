using System.Runtime.CompilerServices;

namespace PoorMansFeed.Tests;

/// <summary>
/// Sweeps the sources for the rules that keep the API token in the operating system's store: no encryption of the
/// app's own, each platform library used in one file, and no test that reaches the real store.
/// </summary>
public sealed class SecretHandlingTests
{
    // The token is protected by DPAPI or the Keychain. A cipher or key derivation here would mean a key or passphrase
    // in the app, which anyone with the source or the binary can read.
    private static readonly string[] OwnCryptography =
    [
        "Aes.Create", "AesGcm", "AesCcm", "AesCng", "AesManaged", "AesCryptoServiceProvider", "Rijndael",
        "ChaCha20Poly1305", "TripleDES", "DESCryptoServiceProvider", "DES.Create", "RC2", "SymmetricAlgorithm",
        "EncryptCbc", "DecryptCbc", "EncryptEcb", "DecryptEcb", "RSA.Create", "RSACryptoServiceProvider", "HKDF",
        "Rfc2898DeriveBytes", "PasswordDeriveBytes", "Pbkdf2", "CreateEncryptor", "CreateDecryptor", "ICryptoTransform",
        "CryptoStream", "DataProtectionProvider", "IDataProtector",
    ];

    // What makes a store that holds the developer's own token
    private static readonly string[] RealStores =
    [
        "IpInfoService.ForCurrentUser(", "SecretStores.ForCurrentUser(", "new KeychainSecretStore(", "new DpapiSecretStore(",
    ];

    [Fact]
    public void No_source_file_encrypts_or_derives_a_key_itself()
    {
        var offenders = SourceFiles("src")
            .SelectMany(file => OwnCryptography
                .Where(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetFileName(file)}: {name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("ProtectedData.", "DpapiSecretStore.cs")]
    [InlineData("Microsoft.Identity.Client", "KeychainSecretStore.cs")]
    [InlineData("SecretStores.ForCurrentUser(", "IpInfoService.cs")]
    public void Each_store_library_is_used_in_one_file(string usage, string onlyFile)
    {
        var users = SourceFiles("src")
            .Where(file => File.ReadAllText(file).Contains(usage, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([onlyFile], users);
    }

    [Fact]
    public void Tests_never_make_a_real_secret_store()
    {
        // The two platform store tests use throwaway files and Keychain names of their own
        string[] allowed = [Path.GetFileName(ThisFile()), "DpapiSecretStoreTests.cs", "KeychainSecretStoreTests.cs"];
        var offenders = SourceFiles("tests")
            .Where(file => !allowed.Contains(Path.GetFileName(file)))
            .SelectMany(file => RealStores
                .Where(call => File.ReadAllText(file).Contains(call, StringComparison.Ordinal))
                .Select(call => $"{Path.GetFileName(file)}: {call}"))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(separator + "obj" + separator, StringComparison.Ordinal)
                && !file.Contains(separator + "bin" + separator, StringComparison.Ordinal));
    }

    private static string ThisFile([CallerFilePath] string testFile = "") => testFile;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new FileInfo(ThisFile()).Directory; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PoorMansFeed.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
