using System.Security.Cryptography;
using System.Text;

namespace PoorMansFeed.Core;

/// <summary>
/// AES-256-CBC helpers with PBKDF2 key derivation. Ported from the console app's CryptoHelper.
/// Ciphertext layout: [16-byte IV][ciphertext].
/// </summary>
public static class CryptoHelper
{
    private const int KeySizeBytes = 32; // AES-256
    private const int IvSizeBytes = 16;  // AES block size
    private const int Pbkdf2Iterations = 100_000;

    public static byte[] DeriveKey(string passphrase, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySizeBytes);
    }

    public static byte[] Encrypt(string plainText, byte[] key)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Key = key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[IvSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, IvSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, result, IvSizeBytes, cipherBytes.Length);
        return result;
    }

    public static string Decrypt(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < IvSizeBytes + 1)
            throw new CryptographicException("Encrypted data is too short.");

        byte[] iv = new byte[IvSizeBytes];
        Buffer.BlockCopy(encryptedData, 0, iv, 0, IvSizeBytes);

        byte[] cipherBytes = new byte[encryptedData.Length - IvSizeBytes];
        Buffer.BlockCopy(encryptedData, IvSizeBytes, cipherBytes, 0, cipherBytes.Length);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
