using BreadTh.Cosmosis.Data.Exceptions;

namespace BreadTh.Cosmosis.Data.Dto;

public sealed class CosmosisDeleteOptions : CosmosisGetOptions
{
    /// <summary>
    /// When false, deleting a non-existent document silently succeeds instead of throwing
    /// <see cref="CosmosDocumentNotFoundException"/>.
    /// Default: true.
    /// </summary>
    public bool ThrowIfNotFound { get; set; } = true;

    /// <summary>
    /// Skips the existence check on retry after a timed-out delete. Saves a round-trip when the caller
    /// knows the document exists. If the prior attempt actually deleted the document, the retry will
    /// throw <see cref="CosmosDocumentNotFoundException"/> (unless ThrowIfNotFound is false).
    /// Default: false.
    /// </summary>
    public bool AssumeDocumentExists { get; set; } = false;
    
    /// <summary>
    /// When true and the existence check shows the document is already gone, throws a
    /// <see cref="CosmosDocumentNotFoundException"/> immediately with a synthetic CosmosException,
    /// avoiding the extra round-trip to produce a real 404 from Cosmos.
    /// Only applies when ThrowIfNotFound is true.
    /// Default: true.
    /// </summary>
    public bool FakeCosmosExceptionWhenDocumentNotFound { get; set; } = true;
}