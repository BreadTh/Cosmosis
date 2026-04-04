using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// Cosmos DB returned 429 Too Many Requests even after retry. The account's provisioned throughput has been exceeded.
/// </summary>
public class CosmosTooManyRequestsException(int numberOfThrottles, CosmosException inner)
    : CosmosException(
        $"Too many requests after {numberOfThrottles} throttle(s). {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public int NumberOfThrottles { get; } = numberOfThrottles;
}
