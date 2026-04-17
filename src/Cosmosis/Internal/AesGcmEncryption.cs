using System;
using System.Security.Cryptography;

namespace BreadTh.Cosmosis.Internal;

/// <summary>
/// AES-GCM authenticated encryption.
/// <para>
/// AES-GCM encrypts data using a short fixed key (16, 24, or 32 bytes) and a random per-encryption nonce
/// (like a salt in password hashing). The nonce ensures that encrypting the same plaintext twice with the
/// same key produces different ciphertexts.
/// The tag is an authentication checksum that detects tampering - if anyone modifies the ciphertext,
/// decryption will fail rather than silently returning corrupted data.
/// </para>
/// <para>
/// The output format is: [nonce][tag][ciphertext], concatenated as raw bytes.
/// There are no delimiters between the parts.
/// </para>
/// </summary>
internal static class AesGcmEncryption
{
    public static byte[] Encrypt(byte[] plaintext, byte[] key, int nonceSize, int tagSize)
    {
        ValidateKeyLength(key);
        var nonce = new byte[nonceSize];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[tagSize];

        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        
        var result = new byte[nonceSize + tagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, nonceSize);
        ciphertext.CopyTo(result, nonceSize + tagSize);

        return result;
    }

    /// <summary>
    /// Decrypts data previously encrypted by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> if the data has been tampered with, the key is wrong,
    /// the nonceSize or tagSize is wrong, or the ciphertext is corrupted.
    /// </summary>
    public static byte[] Decrypt(byte[] encrypted, byte[] key, int nonceSize, int tagSize)
    {
        ValidateKeyLength(key);

        var nonce = encrypted.AsSpan(0, nonceSize);
        var tag = encrypted.AsSpan(nonceSize, tagSize);
        var ciphertext = encrypted.AsSpan(nonceSize + tagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private static void ValidateKeyLength(byte[] key)
    {
        if (key.Length is not (16 or 24 or 32))
            throw new ArgumentException(
                "Encryption key must be exactly 16, 24, or 32 bytes (AES-128, AES-192, or AES-256). " +
                    $"Got {key.Length} bytes.",
                nameof(key)
            );
    }
}
