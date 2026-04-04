using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Data.Exceptions;

/// <summary>
/// The Cosmos DB credentials are invalid or the account does not have sufficient permissions.
/// </summary>
public class CosmosAuthenticationFailedException(CosmosException inner)
    : CosmosException(inner.Message, inner.StatusCode, inner.SubStatusCode, inner.ActivityId, inner.RequestCharge);
