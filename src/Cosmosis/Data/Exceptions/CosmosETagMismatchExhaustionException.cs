using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// Thrown when an update exhausts all ETag mismatch retries. This typically indicates a hot document
/// with too many concurrent writers - consider redesigning the data model to reduce contention.
/// </summary>
public class CosmosETagMismatchExhaustionException(ContainerPath containerPath, CosmosDocumentKey documentKey, int attempts, CosmosException inner)
    : CosmosException(
        $"Failed to update document '{documentKey.CosmosDocumentId.Value}'"
            + $" in {containerPath.CosmosDatabaseName.Value}/{containerPath.CosmosContainerName.Value}"
            + $" after {attempts} attempts due to repeated ETag mismatches."
            + " This typically indicates a hot document with too many concurrent writers"
            + $" - consider redesigning the data model to reduce contention. {inner.Message}",
        inner.StatusCode,
        inner.SubStatusCode,
        inner.ActivityId,
        inner.RequestCharge
    )
{
    public ContainerPath ContainerPath { get; } = containerPath;
    public CosmosDocumentKey DocumentKey { get; } = documentKey;
    public int Attempts { get; } = attempts;
}