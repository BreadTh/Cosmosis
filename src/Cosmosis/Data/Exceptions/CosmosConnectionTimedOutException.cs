using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// The connection to Cosmos DB timed out even after retry.
/// </summary>
public class CosmosConnectionTimedOutException(CosmosException inner, int numberOfTimeouts)
    : CosmosException(
        $"Connection timed out after {numberOfTimeouts} attempt(s). {inner.Message}", 
        inner.StatusCode, 
        inner.SubStatusCode, 
        inner.ActivityId, 
        inner.RequestCharge
    )
{
    public int NumberOfTimeouts { get; } = numberOfTimeouts;
}
