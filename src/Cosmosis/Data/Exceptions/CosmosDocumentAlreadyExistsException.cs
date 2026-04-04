using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;
/// <summary>
/// The Create operation failed because the document already exists.
/// To ignore this scenario, consider using Upsert instead of Create.
/// </summary>
public class CosmosDocumentAlreadyExistsException(ContainerPath path, CosmosDocumentKey docKey, CosmosException inner)
    : CosmosException(
        $"Document '{docKey.CosmosDocumentId.Value}' already exists in " +
            $"{path.CosmosDatabaseName.Value}/{path.CosmosContainerName.Value}. {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public ContainerPath ContainerPath { get; } = path;
    public CosmosDocumentKey DocumentKey { get; } = docKey;
}