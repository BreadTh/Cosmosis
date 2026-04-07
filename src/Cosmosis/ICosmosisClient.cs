using System;
using System.Threading;
using System.Threading.Tasks;
using BreadTh.Cosmosis.Data.Dto;
using BreadTh.Cosmosis.Data.Exceptions;
using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis;

/// <summary>
/// A thin but opinionated Microsoft.Azure.Cosmos wrapper designed for data and runtime safety.
/// </summary>
public interface ICosmosisClient
{
    /// <summary>
    /// Creates a new document. Throws if the id is taken.
    /// Cosmos's Create cannot guarantee that it won't overwrite another document created at the same moment
    /// while also guaranteeing that it won't throw DocumentAlreadyExists on a write-retry if a network failure happened
    /// during the first write, and the document was received by Cosmos but the OK-respond didn't make it back to the
    /// client. Cosmos does not have an idempotency parameter for creation.
    /// If overwrite isn't an issue, consider using the much simpler .UpsertAsync
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// <see cref="CosmosDocumentAlreadyExistsException"/>
    /// </summary>
    Task<ItemResponse<T>> CreateAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey documentKey,
        T document,
        CosmosisCreateOptions? cosmosisOptions = null,
        ItemRequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    ) where T : notnull;

    /// <summary>
    /// Creates a document if it doesn't exist (by id) or replaces it if it does.
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// </summary>
    Task<ItemResponse<T>> UpsertAsync<T>(
        ContainerPath containerPath,
        PartitionKey partitionKey,
        T document,
        CosmosisUpsertOptions? cosmosisOptions = null,
        ItemRequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    ) where T : notnull;

    /// <summary>
    /// Reads a document by id and partition key.
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// <see cref="CosmosDocumentNotFoundException"/>,
    /// </summary>
    Task<ItemResponse<T>> GetOneAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey cosmosDocumentKey,
        CosmosisGetOptions? cosmosisOptions = null,
        ItemRequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    ) where T : notnull;

    /// <summary>
    /// Reads a document, applies <paramref name="updateDocumentAsync"/> to produce a new version, and replaces it
    /// using ETag If-Match for optimistic concurrency. On ETag mismatch, the cycle is retried with fresh data.
    /// If <paramref name="cached"/> is provided, the first attempt uses it instead of reading from Cosmos,
    /// saving a round-trip when you already have a recent copy. On ETag mismatch it falls back to reading.
    /// If cached is provided, the library will assume cached.document is the original state. If you have modified
    /// .document the update will have unintended side effects.
    /// updateDocumentAsync may run multiple times in situation where the update succeeds but the success response
    /// didn't make it back to the client, which then retries. For this reason, the update done by updateDocumentAsync
    /// must be idempotent. Be especially careful when adding/removing elements of a list. 
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// <see cref="CosmosDocumentNotFoundException"/>,
    /// <see cref="CosmosETagMismatchExhaustionException"/>,
    /// </summary>
    Task<ItemResponse<T>> UpdateAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey cosmosDocumentKey,
        Func<T, Task<T>> updateDocumentAsync,
        (T document, string etag)? cached = null,
        CosmosisUpdateOptions? cosmosisOptions = null,
        ItemRequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    ) where T : notnull;

    /// <summary>
    /// Deletes a document by id and partition key.
    /// returns null if options.ThrowIfNotFound is false and the item was not found or when a network issue caused
    /// the delete-OK response to timeout, and retry confirmed that the deletion was a success.
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// <see cref="CosmosDocumentNotFoundException"/>,
    /// </summary>
    Task<ItemResponse<T>?> DeleteAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey documentKey,
        CosmosisDeleteOptions? cosmosisOptions = null,
        ItemRequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    ) where T : notnull;

    /// <summary>
    /// Deletes all documents in a partition. Cosmos DB runs this as an asynchronous background operation
    /// using a percentage of the account's provisioned RUs.
    /// Requires the 'Delete All Items By Partition Key' feature to be enabled on the Cosmos DB account.
    /// Enable it in the Azure Portal under Settings > Features > 'Delete All Items By Partition Key',
    /// or via Azure CLI: az cosmosdb update --resource-group &lt;rg&gt; --name &lt;account&gt; --capabilities EnableDeleteAllItemsByPartitionKey
    /// May throw:
    /// <see cref="CosmosAuthenticationFailedException"/>,
    /// <see cref="CosmosDatabaseNotFoundException"/>,
    /// <see cref="CosmosContainerNotFoundException"/>,
    /// <see cref="CosmosConnectionTimedOutException"/>,
    /// <see cref="CosmosTooManyRequestsException"/>,
    /// <see cref="CosmosServiceUnavailableException"/>,
    /// <see cref="CosmosPartitionDeleteNotEnabledException"/>,
    /// </summary>
    Task<ResponseMessage> DeletePartitionAsync(
        ContainerPath containerPath,
        PartitionKey partitionKey,
        CosmosisDeletePartitionOptions? cosmosisOptions = null,
        RequestOptions? cosmosOptions = null,
        CancellationToken cancellationToken = default
    );
}
