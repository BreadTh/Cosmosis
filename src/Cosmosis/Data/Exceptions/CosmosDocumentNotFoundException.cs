using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// The document was not found during get, update or delete.
/// </summary>
public class CosmosDocumentNotFoundException(ContainerPath containerPath, CosmosDocumentKey documentKey, string operation, CosmosException inner)
    : CosmosException(
        $"Document '{documentKey.CosmosDocumentId.Value}' not found in {containerPath.CosmosDatabaseName.Value}" +
            $"/{containerPath.CosmosContainerName.Value} during {operation}. {inner.Message}",
        inner.StatusCode,
        inner.SubStatusCode,
        inner.ActivityId,
        inner.RequestCharge
    )
{
    public ContainerPath ContainerPath { get; } = containerPath;
    public CosmosDocumentKey DocumentKey { get; } = documentKey;
    public string Operation { get; } = operation;
}