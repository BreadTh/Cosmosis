using System;
using System.Security.Cryptography;

namespace BreadTh.Cosmosis.Internal;

internal static class Sha256Hasher
{
    private const int MaxSha256ByteCount = 32; // = sha256 / 8 bits in a byte
    
    public static byte[] Hash(byte[] data, int hashSize)
    {   
        if (hashSize is < 1 or > MaxSha256ByteCount)
            throw new ArgumentOutOfRangeException(
                nameof(hashSize), 
                hashSize, 
                $"Must be between 1 and {MaxSha256ByteCount}."
            );

        byte[] fullHash;
        using (var sha = SHA256.Create())
            fullHash = sha.ComputeHash(data);
        var hash = new byte[hashSize];
        Array.Copy(fullHash, hash, hashSize);
        return hash;
    }

    public static void Verify(byte[] expected, byte[] data, int hashSize)
    {
        if (expected.Length != hashSize)
            throw new ArgumentException(
                $"Expected hash length ({expected.Length}) does not match hashSize ({hashSize}).", 
                nameof(expected)
            );

        var actual = Hash(data, hashSize);

        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException(
                "Hash verification failed. The payload was bound to a different context than the one provided."
            );
    }
}