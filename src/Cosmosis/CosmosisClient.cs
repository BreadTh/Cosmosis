using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BreadTh.Cosmosis.Data.Dto;
using BreadTh.Cosmosis.Data.Exceptions;
using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis;

public sealed class CosmosisClient(CosmosClient cosmosClient) : ICosmosisClient
{
    public async Task<ItemResponse<T>> CreateAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey documentKey,
        T document,
        CosmosisCreateOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : notnull
    {
        options ??= new CosmosisCreateOptions();
        var container = GetContainer(containerPath);
        
        // If the first write fails due to networking issues, we may end up in a situation where a retry will fail
        // because the earlier create command arrived at the server, but the response did not come back. But now
        // on retry the response will come back that the document already exists because we just created it.
        // For this reason we must check if the document exists beforehand.
        // This check can be disabled with options.AssumeDocumentDoesNotExist, to save the roundtrip to get the document
        // before writing the document, but network issues => retries may then either cause false
        // "document already created" errors or overwrite an existing document, depending on the
        // options.RetryBehavior selected.
        bool documentAlreadyExists;
        if (options.AssumeDocumentDoesNotExist)
            documentAlreadyExists = false;
        else
        {
            try
            {
                await GetOneAsync<T>(containerPath, documentKey, options, cancellationToken);
                documentAlreadyExists = false;
            }
            catch (CosmosDocumentNotFoundException)
            {
                // Inversely, we can save the extra write attempt when the document exists, but then we don't get
                // a genuine CosmosException as thrown by the Cosmos client, but rather an imitation that may not be
                // fully accurate.
                if (options.FakeCosmosExceptionWhenDocumentAlreadyExists)
                    throw new CosmosDocumentAlreadyExistsException(
                        containerPath,
                        documentKey,
                        new CosmosException(
                            $"Document '{documentKey.CosmosDocumentId.Value}' already exists in " +
                            $"{containerPath.CosmosDatabaseName.Value}/{containerPath.CosmosContainerName.Value}.",
                            HttpStatusCode.Conflict, subStatusCode: 0, string.Empty, requestCharge: 0
                        )
                    );
                documentAlreadyExists = true;
            }
        }

        Func<RetryContext, Task<ItemResponse<T>>> funcAsync;
        if (documentAlreadyExists) // Deliberately call create each time to trigger a real cosmos exception.
            funcAsync = AlwaysCreateAsync;
        else if (options.RetryBehavior == CosmosisCreateRetryBehavior.Create)
            funcAsync = AlwaysCreateAsync;
        else
            funcAsync = FirstCreateThenUpsertAsync;

        return await RetryAsync(funcAsync, options, containerPath, cancellationToken);

        async Task<ItemResponse<T>> FirstCreateThenUpsertAsync(RetryContext context)
        {
            if(context.NetworkFailureCount == 0)
                return await container.CreateItemAsync(
                    document, 
                    documentKey.PartitionKey, 
                    cancellationToken: cancellationToken
                );

            // Upsert avoids false 409s from our own timed-out create that may have committed,
            // but may overwrite a document created by another process in the window since our existence check.
            // Cosmos does not provide guarantees on both network and safety as there's no data locking.
            return await container.UpsertItemAsync(
                document, 
                documentKey.PartitionKey, 
                cancellationToken: cancellationToken
            );
        }

        async Task<ItemResponse<T>> AlwaysCreateAsync(RetryContext context) =>
            await container.CreateItemAsync(document, documentKey.PartitionKey, cancellationToken: cancellationToken);
    }

    public async Task<ItemResponse<T>> UpsertAsync<T>(
        ContainerPath containerPath,
        PartitionKey partitionKey,
        T document,
        CosmosisUpsertOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : notnull
    {
        options ??= new CosmosisUpsertOptions();
        var container = GetContainer(containerPath);

        async Task<ItemResponse<T>> FuncAsync(RetryContext retryContext)
        {
            return await container.UpsertItemAsync(
                document, 
                partitionKey, 
                cancellationToken: retryContext.CancellationToken
            );
        }

        return await RetryAsync(FuncAsync, options, containerPath, cancellationToken);
    }

    public async Task<ItemResponse<T>> GetOneAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey cosmosDocumentKey,
        CosmosisGetOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : notnull
    {
        options ??= new CosmosisGetOptions();
        var container = GetContainer(containerPath);

        async Task<ItemResponse<T>> FuncAsync(RetryContext retryContext)
        {
            try
            {
                return await container.ReadItemAsync<T>(
                    cosmosDocumentKey.CosmosDocumentId.Value, 
                    cosmosDocumentKey.PartitionKey, 
                    cancellationToken: retryContext.CancellationToken
                );
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw new CosmosDocumentNotFoundException(containerPath, cosmosDocumentKey, "get", ex);
            }
        }

        return await RetryAsync(FuncAsync, options, containerPath, cancellationToken);
    }

    public async Task<ItemResponse<T>> UpdateAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey cosmosDocumentKey,
        Func<T, Task<T>> updateDocumentAsync,
        (T document, string etag)? cached = null,
        CosmosisUpdateOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : notnull
    {
        options ??= new CosmosisUpdateOptions();
        var maxETagMismatchRetries = options.MaxETagMismatchRetries;
        var container = GetContainer(containerPath);

        return await RetryAsync(FuncAsync, options, containerPath, cancellationToken);

        async Task<ItemResponse<T>> FuncAsync(RetryContext retryContext)
        {
            CosmosException? lastMismatch = null;
            for (var attempt = 0; attempt < maxETagMismatchRetries; attempt++)
            {
                T current;
                string etag;
                
                if (attempt == 0 && cached.HasValue)
                {
                    current = cached.Value.document;
                    etag = cached.Value.etag;
                }
                else
                {
                    ItemResponse<T> response;
                    try
                    {
                        response = await container.ReadItemAsync<T>(
                            cosmosDocumentKey.CosmosDocumentId.Value, 
                            cosmosDocumentKey.PartitionKey, 
                            cancellationToken: retryContext.CancellationToken
                        );
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new CosmosDocumentNotFoundException(containerPath, cosmosDocumentKey, "update", ex);
                    }

                    current = response.Resource;
                    etag = response.ETag;
                }

                var updated = await updateDocumentAsync(current);

                try
                {
                    return await container.ReplaceItemAsync(
                        updated,
                        cosmosDocumentKey.CosmosDocumentId.Value,
                        cosmosDocumentKey.PartitionKey,
                        new ItemRequestOptions { IfMatchEtag = etag },
                        retryContext.CancellationToken
                    );
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    lastMismatch = ex;
                }
            }
            
            // Out of retries. Stop to avoid an infinite loop.
            // Can't currently see why this would happen, but better safe than sorry.
            throw new CosmosETagMismatchExhaustionException(
                containerPath,
                cosmosDocumentKey,
                maxETagMismatchRetries,
                lastMismatch!
            );
        }
    }

    public async Task<ItemResponse<T>?> DeleteAsync<T>(
        ContainerPath containerPath,
        CosmosDocumentKey documentKey,
        CosmosisDeleteOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : notnull
    {
        options ??= new CosmosisDeleteOptions();
        var container = GetContainer(containerPath);

        if (!options.ThrowIfNotFound)
            return await RetryAsync(DontThrowOnNotFound, options, containerPath, cancellationToken);

        // We need to know if the document existed to correctly report whether it was deleted successfully
        // in case of a network issue leading to retry.
        bool documentExistedBefore;
        if (options.AssumeDocumentExists)
            documentExistedBefore = true;
        else
        {
            try
            {
                await GetOneAsync<T>(containerPath, documentKey, options, cancellationToken);
                documentExistedBefore = true;
            }
            catch (CosmosDocumentNotFoundException)
            {
                if (options.FakeCosmosExceptionWhenDocumentNotFound)
                    throw new CosmosDocumentNotFoundException(
                        containerPath,
                        documentKey,
                        "delete",
                        new CosmosException(
                            $"Document '{documentKey.CosmosDocumentId.Value}' not found in " +
                                $"{containerPath.CosmosDatabaseName.Value}/{containerPath.CosmosContainerName.Value}.",
                            HttpStatusCode.NotFound, 
                            subStatusCode: 0, 
                            string.Empty, 
                            requestCharge: 0
                        )
                    );
                documentExistedBefore = false;
            }
        }
        
        return await RetryAsync(ThrowOnNotFound, options, containerPath, cancellationToken);
        
        async Task<ItemResponse<T>?> DontThrowOnNotFound(RetryContext retryContext)
        {
            try
            {
                return await container.DeleteItemAsync<T>(
                    documentKey.CosmosDocumentId.Value, 
                    documentKey.PartitionKey, 
                    cancellationToken: retryContext.CancellationToken
                );
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The document is gone. Nothing more to do.
                return null;
            }
        }
        
        async Task<ItemResponse<T>?> ThrowOnNotFound(RetryContext retryContext)
        {
            try
            {
                return await container.DeleteItemAsync<T>(
                    documentKey.CosmosDocumentId.Value, 
                    documentKey.PartitionKey, 
                    cancellationToken: retryContext.CancellationToken
                );
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // if the document existed before and doesn't exist now, then it was deleted successfully either by
                // a very close running parallel process or a retry caused by networking issues.
                // We'll make the assumption that we successfully deleted it in a previous try. 
                if (documentExistedBefore)
                    return null;
                
                throw new CosmosDocumentNotFoundException(containerPath, documentKey, "delete", ex);
                
            }

        }
    }

    public async Task<ResponseMessage> DeletePartitionAsync(
        ContainerPath containerPath,
        PartitionKey partitionKey,
        CosmosisDeletePartitionOptions? options = null,
        CancellationToken cancellationToken = default
    ) {
        options ??= new CosmosisDeletePartitionOptions();
        var container = GetContainer(containerPath);

        return await RetryAsync(FuncAsync, options, containerPath, cancellationToken);

        async Task<ResponseMessage> FuncAsync(RetryContext retryContext)
        {
            try
            {
                return await container.DeleteAllItemsByPartitionKeyStreamAsync(
                    partitionKey, 
                    cancellationToken: retryContext.CancellationToken
                );
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new CosmosPartitionDeleteNotEnabledException(containerPath, ex);
            }
        }
    }
    
    private Container GetContainer(ContainerPath containerPath) =>
        cosmosClient.GetContainer(containerPath.CosmosDatabaseName.Value, containerPath.CosmosContainerName.Value);

    private static bool IsOwnerResourceMissing(CosmosException ex) =>
        ex.Message.Contains("Owner resource does not exist");

    private async Task<TResult> RetryAsync<TResult>(
        Func<RetryContext, Task<TResult>> funcAsync,
        BaseCosmosisOptions options,
        ContainerPath containerPath,
        CancellationToken cancellationToken = default
    ) {
        var context = new RetryContext { CancellationToken = cancellationToken };
        CosmosException? lastException = null;
        for (; context.Attempt < options.MaxTotalRetries; context.Attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await funcAsync(context);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.RequestTimeout)
            {
                lastException = ex;
                context.NetworkFailureCount++;
                if (context.NetworkFailureCount >= options.MaxNetworkFailureRetries)
                    throw new CosmosConnectionTimedOutException(ex, context.NetworkFailureCount);
                await BackoffAsync(context.BackoffCount++, options, cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                lastException = ex;
                context.ThrottleCount++;
                if (context.ThrottleCount >= options.MaxThrottleRetries)
                    throw new CosmosTooManyRequestsException(context.ThrottleCount, ex);
                await BackoffAsync(context.BackoffCount++, options, cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                lastException = ex;
                context.ServiceUnavailableCount++;
                if (context.ServiceUnavailableCount >= options.MaxServiceUnavailableRetries)
                    throw new CosmosServiceUnavailableException(ex, context.ServiceUnavailableCount);
                await BackoffAsync(context.BackoffCount++, options, cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CosmosAuthenticationFailedException(ex);
            }
            catch (CosmosException ex) when (ex is { StatusCode: HttpStatusCode.NotFound, SubStatusCode: 1003 })
            {
                throw new CosmosContainerNotFoundException(containerPath, ex);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && IsOwnerResourceMissing(ex))
            {
                throw new CosmosDatabaseNotFoundException(containerPath.CosmosDatabaseName, ex);
            }
        }

        throw lastException!;
    }

    private static async Task BackoffAsync(
        int attempt, 
        BaseCosmosisOptions options, 
        CancellationToken cancellationToken = default
    ) {
        var backoff = options.RetryBackoff;
        if (backoff is null or { Length: 0 })
            return;
        var delay = backoff[Math.Min(attempt, backoff.Length - 1)];
        await Task.Delay(delay, cancellationToken);
    }

}
