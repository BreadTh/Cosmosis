using BreadTh.Cosmosis.Data.Exceptions;

namespace BreadTh.Cosmosis.Data.Dto;

/// <summary>
/// Controls what happens on retry after a create-attempt where the outcome is uncertain
/// (e.g. the request timed out, and we don't know if the document was committed).
/// </summary>
public enum CosmosisCreateRetryBehavior
{
    /// <summary>
    /// Retry with upsert. Safe against our own timed-out writes (no false 409), but may silently
    /// overwrite a document created by another process in the window between our existence check and retry.
    /// </summary>
    Upsert,

    /// <summary>
    /// Retry with create. Safe against overwriting other processes' data, but may throw a false
    /// <see cref="CosmosDocumentAlreadyExistsException"/> if our
    /// timed-out write actually committed.
    /// </summary>
    Create,
}
