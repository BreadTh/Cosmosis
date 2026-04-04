using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// The specified database does not exist in this Cosmos DB account.
/// </summary>
public class CosmosDatabaseNotFoundException(CosmosDatabaseName databaseName, CosmosException inner)
    : CosmosException(
        $"Database '{databaseName.Value}' not found. {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public CosmosDatabaseName DatabaseName { get; } = databaseName;
}
