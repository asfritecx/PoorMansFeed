using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace PoorMansFeed.Core.Secrets;

/// <summary>
/// Windows: the secret protected with DPAPI for the current user, in one file. No extra entropy: a constant in the
/// source adds nothing, because anyone who can read the source can pass it too.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(string path) : ISecretStore
{
    private readonly string _path = path;

    public bool Exists => File.Exists(_path);

    public byte[]? Read()
    {
        byte[] blob;
        try
        {
            blob = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public void Write(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        byte[] blob = ProtectedData.Protect(secret, optionalEntropy: null, DataProtectionScope.CurrentUser);

        // Written whole, then moved over the file, so a failed or interrupted save leaves the secret saved before
        string tmpPath = _path + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, blob);
            File.Move(tmpPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(tmpPath);
            throw;
        }
    }

    public void Delete()
    {
        File.Delete(_path + ".tmp");
        File.Delete(_path);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next save writes the temporary file again
        }
    }
}
