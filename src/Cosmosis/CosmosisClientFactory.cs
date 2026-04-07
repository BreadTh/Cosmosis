using BreadTh.Cosmosis.Internal;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis;

public class CosmosisClientFactory
{
    public ICosmosisClient Build(string connectionString) =>
        new CosmosisClient(new CosmosClient(connectionString), new RetryExecutor());

    public ICosmosisClient Build(CosmosClient cosmosClient) =>
        new CosmosisClient(cosmosClient, new RetryExecutor());
}
