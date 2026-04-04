namespace BreadTh.Cosmosis.Data.ValueObjects;

/// <summary>
/// Pairs a database and container name, since they are always used together.
/// </summary>
public readonly struct ContainerPath(CosmosDatabaseName cosmosDatabaseName, CosmosContainerName cosmosContainerName)
{
    public CosmosDatabaseName CosmosDatabaseName { get; } = cosmosDatabaseName;
    public CosmosContainerName CosmosContainerName { get; } = cosmosContainerName;
}
