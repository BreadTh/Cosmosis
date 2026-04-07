using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BreadTh.Cosmosis.Data.Dto;
using BreadTh.Cosmosis.Data.Exceptions;
using BreadTh.Cosmosis.Data.ValueObjects;
using Microsoft.Azure.Cosmos;

namespace BreadTh.Cosmosis.Internal;

internal class RetryExecutor
{
    public async Task<TResult> RetryAsync<TResult>(
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
            catch (CosmosException ex) 
                when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CosmosAuthenticationFailedException(ex);
            }
            catch (CosmosException ex) 
                when (ex is { StatusCode: HttpStatusCode.NotFound, SubStatusCode: 1003 })
            {
                throw new CosmosContainerNotFoundException(containerPath, ex);
            }
            catch (CosmosException ex) 
                when (ex.StatusCode == HttpStatusCode.NotFound && IsOwnerResourceMissing(ex))
            {
                throw new CosmosDatabaseNotFoundException(containerPath.CosmosDatabaseName, ex);
            }
        }

        throw lastException!;
    }

    private async Task BackoffAsync(
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

    private bool IsOwnerResourceMissing(CosmosException ex) =>
        ex.Message.Contains("Owner resource does not exist");
}
