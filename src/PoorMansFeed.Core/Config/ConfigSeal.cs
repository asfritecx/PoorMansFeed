using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace PoorMansFeed.Core.Config;

/// <summary>A section of config.db, sealed on its own. Each has its own row, named by <see cref="ConfigSeal.SectionName"/>.</summary>
internal enum ConfigSection
{
    Settings,
    Exceptions,
    Accounting,
}

/// <summary>
/// Seals and opens config.db's sections with AES-GCM. This is the one file that may name AesGcm or
/// RandomNumberGenerator: the sections are sealed under a random key kept in the operating system's store, so there is
/// no key in the source and none derived from a passphrase. A blob is the format version, a fresh random nonce, the
/// tag and the ciphertext. The associated data binds it to its file, section and schema version, and is rebuilt for
/// every call, never stored. Nothing here traces: a blob that doesn't open is a false, and the caller traces a fixed
/// text. One instance isn't documented as safe across threads, so its caller serialises every call.
/// </summary>
internal sealed class ConfigSeal : IDisposable
{
    internal const int KeyBytes = 32, NonceBytes = 12, TagBytes = 16, FormatVersion = 1;
    internal const int HeaderBytes = 1 + NonceBytes + TagBytes;

    private const int FileIdLength = 32;

    private readonly AesGcm _gcm;
    private readonly string _fileId;

    /// <summary>
    /// Takes the key into the AES-GCM instance and zeroes the caller's array, whether or not the key is accepted, so
    /// no copy of it stays here. Throws ArgumentException when the key isn't 32 bytes or the file id isn't 32
    /// lower-case hex digits.
    /// </summary>
    internal ConfigSeal(byte[] key, string fileId)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            ArgumentNullException.ThrowIfNull(fileId);
            // Checked before AesGcm sees the key, so a bad length is an ArgumentException, not a CryptographicException
            if (key.Length != KeyBytes)
                throw new ArgumentException("The config key must be 32 bytes.", nameof(key));
            if (!IsFileId(fileId))
                throw new ArgumentException("The file id must be 32 lower-case hex digits.", nameof(fileId));

            _gcm = new AesGcm(key, TagBytes);
            _fileId = fileId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>A new random 256-bit key.</summary>
    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>A new random file id: 16 bytes as 32 lower-case hex digits.</summary>
    internal static string NewFileId()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        string id = Convert.ToHexStringLower(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return id;
    }

    /// <summary>The section's row name, which is also part of its associated data.</summary>
    internal static string SectionName(ConfigSection section) => section switch
    {
        ConfigSection.Settings => "settings",
        ConfigSection.Exceptions => "exceptions",
        ConfigSection.Accounting => "accounting",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    /// <summary>Seals the plaintext under a fresh random nonce. Every call gives a different blob.</summary>
    internal byte[] Seal(ConfigSection section, int schemaVersion, ReadOnlySpan<byte> plaintext)
    {
        byte[] aad = AssociatedData(section, schemaVersion);
        var blob = new byte[HeaderBytes + plaintext.Length];
        blob[0] = FormatVersion;
        Span<byte> nonce = blob.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        _gcm.Encrypt(nonce, plaintext, blob.AsSpan(HeaderBytes), blob.AsSpan(1 + NonceBytes, TagBytes), aad);
        return blob;
    }

    /// <summary>
    /// Opens a blob sealed for this file, section and schema version. False, with no plaintext, when the blob is too
    /// short, has another format version, or doesn't authenticate (a wrong key, a changed byte or another context).
    /// </summary>
    internal bool TryOpen(ConfigSection section, int schemaVersion, ReadOnlySpan<byte> blob, [NotNullWhen(true)] out byte[]? plaintext)
    {
        plaintext = null;
        // AesGcm throws ArgumentException for a wrong nonce or tag length, so a short blob never reaches it
        if (blob.Length < HeaderBytes || blob[0] != FormatVersion)
            return false;

        byte[] aad = AssociatedData(section, schemaVersion);
        var buffer = new byte[blob.Length - HeaderBytes];
        try
        {
            _gcm.Decrypt(blob.Slice(1, NonceBytes), blob[HeaderBytes..], blob.Slice(1 + NonceBytes, TagBytes), buffer, aad);
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException included; AesGcm has zeroed the buffer
            return false;
        }

        plaintext = buffer;
        return true;
    }

    public void Dispose() => _gcm.Dispose();

    // "PoorMansFeed config.db" | 0x00 | format version | file id (32 ASCII bytes) | name length | name | schema version
    // (4 bytes, big-endian)
    private byte[] AssociatedData(ConfigSection section, int schemaVersion)
    {
        ReadOnlySpan<byte> label = "PoorMansFeed config.db"u8;
        byte[] name = Encoding.UTF8.GetBytes(SectionName(section));
        var aad = new byte[label.Length + 2 + FileIdLength + 1 + name.Length + sizeof(int)];
        int at = 0;
        label.CopyTo(aad);
        at += label.Length;
        aad[at++] = 0x00;
        aad[at++] = FormatVersion;
        at += Encoding.ASCII.GetBytes(_fileId, aad.AsSpan(at, FileIdLength));
        aad[at++] = checked((byte)name.Length);
        name.CopyTo(aad, at);
        at += name.Length;
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(at), schemaVersion);
        return aad;
    }

    private static bool IsFileId(string fileId) =>
        fileId.Length == FileIdLength && fileId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
