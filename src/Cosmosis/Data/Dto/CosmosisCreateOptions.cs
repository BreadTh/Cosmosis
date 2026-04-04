using BreadTh.Cosmosis.Data.Exceptions;

namespace BreadTh.Cosmosis.Data.Dto;

public sealed class CosmosisCreateOptions : CosmosisGetOptions
{
    /// <summary>
    /// If the first write fails due to networking issues, a retry may produce a false 409 Conflict (document already exists)
    /// because the original create arrived at the server but the response was lost. For this reason, CreateAsync
    /// checks if the document exists before writing. When this flag is true and the document already exists,
    /// a <see cref="CosmosDocumentAlreadyExistsException"/> is thrown immediately
    /// with a synthetic CosmosException, avoiding the extra round-trip to produce a real 409 response from Cosmos.
    /// This can safely be turned off, but it'll make things a little slower and a little more costly.
    /// Default: true.
    /// </summary>
    public bool FakeCosmosExceptionWhenDocumentAlreadyExists { get; set; } = true;

    /// <summary>
    /// Controls what happens on retry after a create-attempt where the outcome is uncertain.
    /// Default: <see cref="CosmosisCreateRetryBehavior.Upsert"/>.
    /// </summary>
    public CosmosisCreateRetryBehavior RetryBehavior { get; set; } = CosmosisCreateRetryBehavior.Upsert;

    /// <summary>
    /// Skips the existence check before create. Saves a round-trip when the caller knows
    /// the document does not exist. If the document does exist, the create-attempt will fail with a real 409.
    /// Instead of setting this to true, consider using upsert as it would more reliably lead to a more
    /// consistent behavior - Always overwriting rather than sometimes (on network issues) overwriting
    /// Default: false.
    /// </summary>
    public bool AssumeDocumentDoesNotExist { get; set; } = false;
}