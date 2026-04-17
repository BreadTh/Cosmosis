namespace BreadTh.Cosmosis.Data.Dto;

public sealed class CosmosisQueryOptions : BaseCosmosisOptions
{
    /// <summary>
    /// Size of the AES-GCM nonce in bytes used when encrypting continuation tokens.
    /// Default: 12.
    /// </summary>
    public int EncryptionNonceSize { get; set; } = 12;

    /// <summary>
    /// Size of the AES-GCM authentication tag in bytes used when encrypting continuation tokens.
    /// Default: 16.
    /// </summary>
    public int EncryptionTagSize { get; set; } = 16;

    /// <summary>
    /// Number of bytes from the SHA-256 query hash to embed in encrypted continuation tokens.
    /// The hash prevents using a continuation token on the wrong Cosmosis query.
    /// Must be between 1 and 32 (full SHA-256 length).
    /// Default: 32 (full hash, no collision risk).
    /// </summary>
    public int QueryHashSize { get; set; } = 32;
}
