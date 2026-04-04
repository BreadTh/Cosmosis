using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// Cosmos DB returned 503 Service Unavailable even after retry.
/// </summary>
public class CosmosServiceUnavailableException(CosmosException inner, int numberOfFailures)
    : CosmosException(
        $"Service unavailable after {numberOfFailures} attempt(s). {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public int NumberOfFailures { get; } = numberOfFailures;
}
