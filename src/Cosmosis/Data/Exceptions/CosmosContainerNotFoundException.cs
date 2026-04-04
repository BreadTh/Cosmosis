using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// The specified container does not exist in this database.
/// </summary>
/// <param name="inner">The underlying CosmosException</param>
public class CosmosContainerNotFoundException(ContainerPath containerPath, CosmosException inner)
    : CosmosException(
        $"Container '{containerPath.CosmosContainerName.Value}' not found in database " +
            $"'{containerPath.CosmosDatabaseName.Value}'. {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public ContainerPath ContainerPath { get; } = containerPath;
}
