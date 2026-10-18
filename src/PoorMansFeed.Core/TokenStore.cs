using System.Security.Cryptography;
using System.Text;

namespace PoorMansFeed.Core;

/// <summary>
/// Stores the IPInfo API token encrypted (AES-256-CBC, PBKDF2 key, a random salt per save).
/// File layout: [16-byte salt][16-byte IV][ciphertext].
/// </summary>
public sealed class TokenStore(string path)
{
    // Fixed when tokens were first saved: a new value would make every saved token unreadable. It is not the app name.
    private const string EncryptionPassphrase = "IPInfoLiteParser";
    private const int SaltSizeBytes = 16;
    private const int IvSizeBytes = 16;

    private readonly string _path = path;

    public bool HasToken => File.Exists(_path);

    /// <summary>
    /// Returns the decrypted token, or null if there is no token. A file that cannot be decrypted
    /// is deleted and null is returned.
    /// </summary>
    public string? TryLoad()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            byte[] file = File.ReadAllBytes(_path);
            if (file.Length < SaltSizeBytes + IvSizeBytes + 1)
            {
                DeleteQuietly(_path);
                return null;
            }

            byte[] key = CryptoHelper.DeriveKey(EncryptionPassphrase, file[..SaltSizeBytes]);
            return CryptoHelper.Decrypt(file[SaltSizeBytes..], key);
        }
        catch (CryptographicException)
        {
            DeleteQuietly(_path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Encrypts and writes the token. Returns false on IO failure, leaving any saved token as it was.
    /// </summary>
    public bool Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string tmpPath = _path + ".tmp";
        try
        {
            // The salt is stored in the file. It must not come from the host name, which
            // changes with the network on macOS and would make the token unreadable.
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] key = CryptoHelper.DeriveKey(EncryptionPassphrase, salt);
            byte[] encrypted = CryptoHelper.Encrypt(token, key);
            WriteOwnerOnly(tmpPath, [.. salt, .. encrypted]);

            // The move replaces the token file whole, so it is never half written and never has a wider mode.
            File.Move(tmpPath, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tmpPath);
            return false;
        }
    }

    /// <summary>
    /// Deletes the token file. Returns true if it is gone afterwards.
    /// </summary>
    public bool Clear()
    {
        DeleteQuietly(_path + ".tmp");
        DeleteQuietly(_path);
        return !File.Exists(_path);
    }

    // On Unix the file is created as 0600, so it is never readable by another account, even for a moment. The create
    // mode applies only to a new file, so a leftover is deleted first, and CreateNew fails rather than reuse one that
    // appears in between.
    private static void WriteOwnerOnly(string path, byte[] content)
    {
        File.Delete(path);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        stream.Write(content);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort
        }
    }
}
