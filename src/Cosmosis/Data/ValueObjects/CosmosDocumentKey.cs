using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.ValueObjects;

/// <summary>
/// Pairs a document id and partition key, since they are often used together to identify a document.
/// </summary>
public readonly struct CosmosDocumentKey(CosmosDocumentId cosmosDocumentId, PartitionKey partitionKey)
{
    public CosmosDocumentId CosmosDocumentId { get; } = cosmosDocumentId;
    public PartitionKey PartitionKey { get; } = partitionKey;
}
