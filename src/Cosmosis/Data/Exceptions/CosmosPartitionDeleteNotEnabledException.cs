using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;
/// <summary>
/// Thrown when the 'Delete All Items By Partition Key' feature is not enabled on the Cosmos DB account.
/// Enable it in the Azure Portal under your Cosmos DB account > Settings > Features
/// > 'Delete All Items By Partition Key',
/// or via Azure CLI: <c>az cosmosdb update --resource-group &lt;rg&gt; --name &lt;account&gt; --capabilities EnableDeleteAllItemsByPartitionKey</c>
/// </summary>
public class CosmosPartitionDeleteNotEnabledException(ContainerPath containerPath, CosmosException inner)
    : CosmosException(
        "The 'Delete All Items By Partition Key' feature is not enabled on this Cosmos DB account."
            + $" Attempted on {containerPath.CosmosDatabaseName.Value}/{containerPath.CosmosContainerName.Value}."
            + " Enable it in the Azure Portal under your Cosmos DB account > Settings > Features"
            + " > 'Delete All Items By Partition Key',"
            + " or via Azure CLI: az cosmosdb update"
            + " --resource-group <rg> --name <account>"
            + $" --capabilities EnableDeleteAllItemsByPartitionKey. {inner.Message}",
        inner.StatusCode,
        inner.SubStatusCode,
        inner.ActivityId,
        inner.RequestCharge
    )
{
    public ContainerPath ContainerPath { get; } = containerPath;
}
