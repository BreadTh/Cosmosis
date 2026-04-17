using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BreadTh.Cosmosis.Data.Dto;
using BreadTh.Cosmosis.Data.ValueObjects;
using BreadTh.Cosmosis.Internal;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace BreadTh.Cosmosis.Query;

internal class CosmosisQuery<T>(
    IQueryable<T> Queryable,
    CosmosisQueryOptions Options,
    ContainerPath ContainerPath,
    RetryExecutor RetryExecutor,
    Container Container
) :
    // Implementing all these interfaces allows the interfaces to return each other rather than
    // CosmosisQuery - this is the basis for the "protected" LINQ -> Cosmos. It creates a (one directional)
    // state machine with only valid paths forwards exposed. E.g. .Skip must come before .Take but both are
    // optional and both only once each. .Where cannot be after .Skip or .Take, but .Where can be repeated.
    ICosmosisQueryEntry<T>,
    ICosmosisQueryAfterDistinct<T>,
    ICosmosisQueryOrdered<T>,
    ICosmosisQueryOrderedAfterDistinct<T>,
    ICosmosisQueryPostOrdered<T>,
    ICosmosisQueryPostOrderedAfterDistinct<T>,
    ICosmosisQuerySkipped<T>,
    ICosmosisQueryTaken<T>,
    ICosmosisQueryProjected<T>,
    ICosmosisQueryProjectedSkipped<T>,
    ICosmosisQueryProjectedTaken<T>,
    IUnprotectedCosmosisQuery<T>
{
     // === Helpers ===
    private CosmosisQuery<T> NextStep(IQueryable<T> newQueryable) =>
        new(newQueryable, Options, ContainerPath, RetryExecutor, Container);

    // When projection methods are called (like .Select), methods that require the original T,
    // the document shape we're querying is no longer available in LINQ.
    // Thus, we do not need to track both T_document and T_projected.
    private CosmosisQuery<T_PROJECTED> NextStep<T_PROJECTED>(IQueryable<T_PROJECTED> queryable) =>
        new(queryable, Options, ContainerPath, RetryExecutor, Container);

    // === Interface implementations ===
    // The return type on the same methods across the interfaces (the state machine) differ,
    // so the implementation must be explicit to target the return type defined on each interface.
    // for consistency all the interfaces' methods are implemented like this.

    private CosmosisQuery<T> Where(Expression<Func<T, bool>> predicate) => 
        NextStep(Queryable.Where(predicate));
    private CosmosisQuery<T> Distinct() => 
        NextStep(Queryable.Distinct());
    private CosmosisQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => 
        NextStep(Queryable.OrderBy(keySelector));
    private CosmosisQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => 
        NextStep(Queryable.OrderByDescending(keySelector));
    private CosmosisQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => 
        NextStep(((IOrderedQueryable<T>)Queryable).ThenBy(keySelector));
    private CosmosisQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => 
        NextStep(((IOrderedQueryable<T>)Queryable).ThenByDescending(keySelector));
    private CosmosisQuery<T> Skip(int count) => 
        NextStep(Queryable.Skip(count));
    
    private CosmosisQuery<T> Take(int count) => 
        NextStep(Queryable.Take(count));
    
        private async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        using var iterator = Queryable.ToFeedIterator();
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            var response = await RetryExecutor.RetryAsync(
                retryContext =>
         iterator.ReadNextAsync(retryContext.CancellationToken),
                Options,
                ContainerPath,
                cancellationToken
            );
            results.AddRange(response);
        }
        return results;
    }

    private async Task<(List<T> items, string? continuationToken)> ToPageAsync(
        int pageSize,
        byte[] encryptionKey,
        string? continuationToken = null,
        CancellationToken cancellationToken = default
    ) {
        var queryDefinition = Queryable.ToQueryDefinition();
        var queryText = queryDefinition.QueryText;

        string? rawToken;
        if (continuationToken is not null)
        {
            var decrypted = AesGcmEncryption.Decrypt(
                Convert.FromBase64String(continuationToken),
                encryptionKey,
                Options.EncryptionNonceSize,
                Options.EncryptionTagSize
            );
            var embeddedHash = decrypted.AsSpan(0, Options.QueryHashSize).ToArray();
            Sha256Hasher.Verify(embeddedHash, Encoding.UTF8.GetBytes(queryText), Options.QueryHashSize);
            rawToken = Encoding.UTF8.GetString(decrypted, Options.QueryHashSize, decrypted.Length - Options.QueryHashSize);
        }
        else
        {
            rawToken = null;
        }

        var requestOptions = new QueryRequestOptions { MaxItemCount = pageSize };
        using var iterator = Container.GetItemQueryIterator<T>(queryDefinition, rawToken, requestOptions);

        var response = await RetryExecutor.RetryAsync(
            ctx =>
         iterator.ReadNextAsync(ctx.CancellationToken),
            Options,
            ContainerPath,
            cancellationToken
        );

        var nextToken = response.ContinuationToken;
        if (nextToken is not null)
        {
            var queryHash = Sha256Hasher.Hash(Encoding.UTF8.GetBytes(queryText), Options.QueryHashSize).AsSpan();
            var tokenPayload = Encoding.UTF8.GetBytes(nextToken);
            var bound = new byte[Options.QueryHashSize + tokenPayload.Length];
            queryHash.CopyTo(bound);
            tokenPayload.CopyTo(bound.AsSpan(Options.QueryHashSize));
            nextToken = Convert.ToBase64String(
                AesGcmEncryption.Encrypt(bound, encryptionKey, Options.EncryptionNonceSize, Options.EncryptionTagSize)
            );
        }

        return (response.ToList(), nextToken);
    }

    private async IAsyncEnumerable<T> ToAsyncEnumerable([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var iterator = Queryable.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            var response = await RetryExecutor.RetryAsync(
                retryContext =>
         iterator.ReadNextAsync(retryContext.CancellationToken),
                Options,
                ContainerPath,
                cancellationToken
            );
            foreach (var item in response)
                yield return item;
        }
    }

    private async Task<int> CountAsync(CancellationToken cancellationToken = default) =>

        await RetryExecutor.RetryAsync(
            async retryContext =>
         (await Queryable.CountAsync(retryContext.CancellationToken)).Resource,
            Options,
            ContainerPath,
            cancellationToken
        );

    private async Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>

        await NextStep(Queryable.Take(1)).CountAsync(cancellationToken) > 0;

    private async Task<T> FirstAsync(CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(1)).ToListAsync(cancellationToken);

        if (results.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements.");

        return results[0];
    }

    private async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(1)).ToListAsync(cancellationToken);
        return results.Count > 0 ? results[0] : default;
    }

    private async Task<T> FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(1)).ToListAsync(cancellationToken);
        return results.Count > 0 ? results[0] : defaultValue;
    }

    private async Task<T> SingleAsync(CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(2)).ToListAsync(cancellationToken);

        return results.Count switch
        {
            0 =>
         throw new InvalidOperationException("Sequence contains no elements."),
            1 =>
         results[0],
            _ =>
         throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }

    private async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(2)).ToListAsync(cancellationToken);

        return results.Count switch
        {
            0 =>
         default,
            1 =>
         results[0],
            _ =>
         throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }

    private async Task<T> SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Take(2)).ToListAsync(cancellationToken);

        return results.Count switch
        {
            0 =>
         defaultValue,
            1 =>
         results[0],
            _ =>
         throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }
    
    private async Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Select(selector).OrderBy(x =>
            x).Take(1)).ToListAsync(cancellationToken);

        if (results.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements.");

        return results[0];
    }

    private async Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default)
    {
        var results = await NextStep(Queryable.Select(selector).OrderByDescending(x =>
            x).Take(1)).ToListAsync(cancellationToken);

        if (results.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements.");

        return results[0];
    }

    private async Task<int> SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
             await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<int?> SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<long> SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<long?> SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default) => 
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<float> SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<float?> SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double> SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double?> SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default) =>

        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<decimal?> SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).SumAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;

    private async Task<double> AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double?> AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default) =>

        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double> AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double?> AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<float> AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<float?> AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double> AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<double?> AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default) =>

        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    private async Task<decimal?> AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default) =>
        (await RetryExecutor.RetryAsync(async ctx =>
         await Queryable.Select(selector).AverageAsync(ctx.CancellationToken), Options, ContainerPath, cancellationToken)).Resource;
    
    // Note that as we progress through the interfaces more and more calls will not be available as they should have called earlier.
    // The reason that they are still implemented is to give the library-user a better user experience.
    // They write .Take(m).Skip(n) and are then told at compile-time by the Obsolete("skip before take", true) message compile error how to fix it.
    // rather than them wondering why there's no skip.
    // Unfortunately a Obsolete("", true) decoration does not remove the need for the implementor to implement the interface.
    // The method can still be invoked through the interface via reflections and the like, even if a direct evocation will not compile.
    // So for those we'll just throw
    
    ICosmosisQueryEntry<T>                                       ICosmosisQueryEntry<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQueryAfterDistinct<T>                       ICosmosisQueryAfterDistinct<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQueryPostOrdered<T>                               ICosmosisQueryOrdered<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQueryPostOrderedAfterDistinct<T>     ICosmosisQueryOrderedAfterDistinct<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQueryPostOrdered<T>                           ICosmosisQueryPostOrdered<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.Where(Expression<Func<T, bool>> predicate) => throw new InvalidOperationException("Where must come first. Rearrange to .Where(...) before other operations.");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.Where(Expression<Func<T, bool>> predicate) => throw new InvalidOperationException("Where must come first. Rearrange to .Where(...) before other operations.");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.Where(Expression<Func<T, bool>> predicate) => throw new InvalidOperationException("Select must come last. Rearrange to .Where(...) before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.Where(Expression<Func<T, bool>> predicate) => throw new InvalidOperationException("Where must come before .Select(). Rearrange to .Where(...) before .Select(...).");
    ICosmosisQueryProjectedTaken<T >                    ICosmosisQueryProjectedTaken<T>.Where(Expression<Func<T, bool>> predicate) => throw new InvalidOperationException("Where must come before .Select(). Rearrange to .Where(...) before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.Where(Expression<Func<T, bool>> predicate) => Where(predicate);

    ICosmosisQueryAfterDistinct<T>                               ICosmosisQueryEntry<T>.Distinct() => Distinct();
    ICosmosisQueryAfterDistinct<T>                       ICosmosisQueryAfterDistinct<T>.Distinct() => throw new InvalidOperationException("Distinct has already been applied.");
    ICosmosisQueryPostOrderedAfterDistinct<T>                  ICosmosisQueryOrdered<T>.Distinct() => Distinct();
    ICosmosisQueryOrderedAfterDistinct<T>         ICosmosisQueryOrderedAfterDistinct<T>.Distinct() => throw new InvalidOperationException("Distinct has already been applied.");
    ICosmosisQueryPostOrderedAfterDistinct<T>              ICosmosisQueryPostOrdered<T>.Distinct() => Distinct();
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.Distinct() => throw new InvalidOperationException("Distinct has already been applied.");
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.Distinct() => throw new InvalidOperationException("Distinct must come before OrderBy/Skip. Rearrange to .Distinct().OrderBy(...).Skip(...).");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.Distinct() => throw new InvalidOperationException("Distinct must come before Take. Rearrange to .Distinct() before .Take(...).");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.Distinct() => throw new InvalidOperationException("Select must come last. Rearrange to .Distinct() before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.Distinct() => throw new InvalidOperationException("Distinct must come before .Select(). Rearrange to .Distinct() before .Select(...).");
    ICosmosisQueryProjectedTaken<T>                     ICosmosisQueryProjectedTaken<T>.Distinct() => throw new InvalidOperationException("Distinct must come before .Select(). Rearrange to .Distinct() before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.Distinct() => Distinct();

    ICosmosisQueryOrdered<T>                                     ICosmosisQueryEntry<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => OrderBy(keySelector);
    ICosmosisQueryOrderedAfterDistinct<T>                ICosmosisQueryAfterDistinct<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => OrderBy(keySelector);
    ICosmosisQueryOrdered<T>                                   ICosmosisQueryOrdered<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied. Use ThenBy for additional sort columns.");
    ICosmosisQueryOrderedAfterDistinct<T>         ICosmosisQueryOrderedAfterDistinct<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied. Use ThenBy for additional sort columns.");
    ICosmosisQueryPostOrdered<T>                           ICosmosisQueryPostOrdered<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before Take. Rearrange to .OrderBy(...) before .Take(...).");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("Select must come last. Rearrange to .OrderBy(...) before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before .Select(). Rearrange to .OrderBy(...) before .Select(...).");
    ICosmosisQueryProjectedTaken<T>                     ICosmosisQueryProjectedTaken<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before .Select(). Rearrange to .OrderBy(...) before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => OrderBy(keySelector);

    ICosmosisQueryOrdered<T>                                     ICosmosisQueryEntry<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => OrderByDescending(keySelector);
    ICosmosisQueryOrderedAfterDistinct<T>                ICosmosisQueryAfterDistinct<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => OrderByDescending(keySelector);
    ICosmosisQueryOrdered<T>                                   ICosmosisQueryOrdered<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied. Use ThenByDescending for additional sort columns.");
    ICosmosisQueryOrderedAfterDistinct<T>         ICosmosisQueryOrderedAfterDistinct<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied. Use ThenByDescending for additional sort columns.");
    ICosmosisQueryPostOrdered<T>                           ICosmosisQueryPostOrdered<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy has already been applied.");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before Take. Rearrange to .OrderByDescending(...) before .Take(...).");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("Select must come last. Rearrange to .OrderByDescending(...) before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before .Select(). Rearrange to .OrderByDescending(...) before .Select(...).");
    ICosmosisQueryProjectedTaken<T>                     ICosmosisQueryProjectedTaken<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("OrderBy must come before .Select(). Rearrange to .OrderByDescending(...) before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => OrderByDescending(keySelector);

    ICosmosisQueryEntry<T>                                       ICosmosisQueryEntry<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy requires OrderBy first. Rearrange to .OrderBy(...).ThenBy(...).");
    ICosmosisQueryAfterDistinct<T>                       ICosmosisQueryAfterDistinct<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy requires OrderBy first. Rearrange to .OrderBy(...).ThenBy(...).");
    ICosmosisQueryOrdered<T>                                   ICosmosisQueryOrdered<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => ThenBy(keySelector);
    ICosmosisQueryOrderedAfterDistinct<T>         ICosmosisQueryOrderedAfterDistinct<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => ThenBy(keySelector);
    ICosmosisQueryPostOrdered<T>                           ICosmosisQueryPostOrdered<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come immediately after OrderBy. Rearrange to .OrderBy(...).ThenBy(...) before .Where(...).");
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come immediately after OrderBy. Rearrange to .OrderBy(...).ThenBy(...) before .Where(...).");
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before Skip. Rearrange to .OrderBy(...).ThenBy(...).Skip(...).");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before Take. Rearrange to .OrderBy(...).ThenBy(...) before .Take(...).");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("Select must come last. Rearrange to .OrderBy(...).ThenBy(...) before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenBy(...) before .Select(...).");
    ICosmosisQueryProjectedTaken<T>                     ICosmosisQueryProjectedTaken<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenBy(...) before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.ThenBy<TKey>(Expression<Func<T, TKey>> keySelector) => ThenBy(keySelector);

    ICosmosisQueryEntry<T>                                       ICosmosisQueryEntry<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending requires OrderBy first. Rearrange to .OrderBy(...).ThenByDescending(...).");
    ICosmosisQueryAfterDistinct<T>                       ICosmosisQueryAfterDistinct<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending requires OrderBy first. Rearrange to .OrderBy(...).ThenByDescending(...).");
    ICosmosisQueryOrdered<T>                                   ICosmosisQueryOrdered<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => ThenByDescending(keySelector);
    ICosmosisQueryOrderedAfterDistinct<T>         ICosmosisQueryOrderedAfterDistinct<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => ThenByDescending(keySelector);
    ICosmosisQueryPostOrdered<T>                           ICosmosisQueryPostOrdered<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending must come immediately after OrderBy. Rearrange to .OrderBy(...).ThenByDescending(...) before .Where(...).");
    ICosmosisQueryPostOrderedAfterDistinct<T> ICosmosisQueryPostOrderedAfterDistinct<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending must come immediately after OrderBy. Rearrange to .OrderBy(...).ThenByDescending(...) before .Where(...).");
    ICosmosisQuerySkipped<T>                                   ICosmosisQuerySkipped<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending must come before Skip. Rearrange to .OrderBy(...).ThenByDescending(...).Skip(...).");
    ICosmosisQueryTaken<T>                                       ICosmosisQueryTaken<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenByDescending must come before Take. Rearrange to .OrderBy(...).ThenByDescending(...) before .Take(...).");
    ICosmosisQueryProjected<T>                               ICosmosisQueryProjected<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("Select must come last. Rearrange to .OrderBy(...).ThenByDescending(...) before .Select(...).");
    ICosmosisQueryProjectedSkipped<T>                 ICosmosisQueryProjectedSkipped<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenByDescending(...) before .Select(...).");
    ICosmosisQueryProjectedTaken<T>                     ICosmosisQueryProjectedTaken<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => throw new InvalidOperationException("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenByDescending(...) before .Select(...).");
    IUnprotectedCosmosisQuery<T>                           IUnprotectedCosmosisQuery<T>.ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => ThenByDescending(keySelector);

    ICosmosisQuerySkipped<T>                     ICosmosisQueryEntry<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>             ICosmosisQueryAfterDistinct<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>                   ICosmosisQueryOrdered<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>      ICosmosisQueryOrderedAfterDistinct<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>               ICosmosisQueryPostOrdered<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>  ICosmosisQueryPostOrderedAfterDistinct<T>.Skip(int count) => Skip(count);
    ICosmosisQuerySkipped<T>                   ICosmosisQuerySkipped<T>.Skip(int count) => throw new InvalidOperationException("Skip has already been applied.");
    ICosmosisQueryTaken<T>                       ICosmosisQueryTaken<T>.Skip(int count) => throw new InvalidOperationException("Skip must come before Take. Rearrange to .OrderBy(...).Skip(...) before .Take(...).");
    ICosmosisQueryProjectedSkipped<T>        ICosmosisQueryProjected<T>.Skip(int count) => Skip(count);
    ICosmosisQueryProjectedSkipped<T> ICosmosisQueryProjectedSkipped<T>.Skip(int count) => throw new InvalidOperationException("Skip has already been applied.");
    ICosmosisQueryProjectedTaken<T>     ICosmosisQueryProjectedTaken<T>.Skip(int count) => throw new InvalidOperationException("Skip must come before Take. Rearrange to .Select(...).Skip(...).Take(...).");
    IUnprotectedCosmosisQuery<T>           IUnprotectedCosmosisQuery<T>.Skip(int count) => Skip(count);

    ICosmosisQueryTaken<T>                     ICosmosisQueryEntry<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>             ICosmosisQueryAfterDistinct<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>                   ICosmosisQueryOrdered<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>      ICosmosisQueryOrderedAfterDistinct<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>               ICosmosisQueryPostOrdered<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>  ICosmosisQueryPostOrderedAfterDistinct<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>                   ICosmosisQuerySkipped<T>.Take(int count) => Take(count);
    ICosmosisQueryTaken<T>                     ICosmosisQueryTaken<T>.Take(int count) => throw new InvalidOperationException("Take has already been applied.");
    ICosmosisQueryProjectedTaken<T>        ICosmosisQueryProjected<T>.Take(int count) => Take(count);
    ICosmosisQueryProjectedTaken<T> ICosmosisQueryProjectedSkipped<T>.Take(int count) => Take(count);
    ICosmosisQueryProjectedTaken<T>   ICosmosisQueryProjectedTaken<T>.Take(int count) => throw new InvalidOperationException("Take has already been applied.");
    IUnprotectedCosmosisQuery<T>         IUnprotectedCosmosisQuery<T>.Take(int count) => Take(count);

    ICosmosisQueryProjected<TResult>                    ICosmosisQueryEntry<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>            ICosmosisQueryAfterDistinct<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>                  ICosmosisQueryOrdered<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>     ICosmosisQueryOrderedAfterDistinct<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>              ICosmosisQueryPostOrdered<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult> ICosmosisQueryPostOrderedAfterDistinct<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>                  ICosmosisQuerySkipped<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>                    ICosmosisQueryTaken<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));
    ICosmosisQueryProjected<TResult>                ICosmosisQueryProjected<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => throw new InvalidOperationException("Select has already been applied.");
    ICosmosisQueryProjectedSkipped<TResult>  ICosmosisQueryProjectedSkipped<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => throw new InvalidOperationException("Select has already been applied.");
    ICosmosisQueryProjectedTaken<TResult>      ICosmosisQueryProjectedTaken<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => throw new InvalidOperationException("Select has already been applied.");
    IUnprotectedCosmosisQuery<TResult>            IUnprotectedCosmosisQuery<T>.Select<TResult>(Expression<Func<T, TResult>> selector) => NextStep(Queryable.Select(selector));

    ICosmosisQueryProjected<TResult>                    ICosmosisQueryEntry<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>            ICosmosisQueryAfterDistinct<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>                  ICosmosisQueryOrdered<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>     ICosmosisQueryOrderedAfterDistinct<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>              ICosmosisQueryPostOrdered<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult> ICosmosisQueryPostOrderedAfterDistinct<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>                  ICosmosisQuerySkipped<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>                    ICosmosisQueryTaken<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));
    ICosmosisQueryProjected<TResult>                ICosmosisQueryProjected<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => throw new InvalidOperationException("Select has already been applied.");
    ICosmosisQueryProjectedSkipped<TResult>  ICosmosisQueryProjectedSkipped<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => throw new InvalidOperationException("Select has already been applied.");
    ICosmosisQueryProjectedTaken<TResult>      ICosmosisQueryProjectedTaken<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => throw new InvalidOperationException("Select has already been applied.");
    IUnprotectedCosmosisQuery<TResult>            IUnprotectedCosmosisQuery<T>.SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => NextStep(Queryable.SelectMany(selector));

    Task<List<T>>                    ICosmosisQueryEntry<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>            ICosmosisQueryAfterDistinct<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>                  ICosmosisQueryOrdered<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>     ICosmosisQueryOrderedAfterDistinct<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>              ICosmosisQueryPostOrdered<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>> ICosmosisQueryPostOrderedAfterDistinct<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>                  ICosmosisQuerySkipped<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>                    ICosmosisQueryTaken<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>                ICosmosisQueryProjected<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>         ICosmosisQueryProjectedSkipped<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>           ICosmosisQueryProjectedTaken<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);
    Task<List<T>>              IUnprotectedCosmosisQuery<T>.ToListAsync(CancellationToken cancellationToken) => ToListAsync(cancellationToken);

    IAsyncEnumerable<T>                    ICosmosisQueryEntry<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>            ICosmosisQueryAfterDistinct<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>                  ICosmosisQueryOrdered<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>     ICosmosisQueryOrderedAfterDistinct<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>              ICosmosisQueryPostOrdered<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T> ICosmosisQueryPostOrderedAfterDistinct<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>                  ICosmosisQuerySkipped<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>                    ICosmosisQueryTaken<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>                ICosmosisQueryProjected<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>         ICosmosisQueryProjectedSkipped<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>           ICosmosisQueryProjectedTaken<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);
    IAsyncEnumerable<T>              IUnprotectedCosmosisQuery<T>.ToAsyncEnumerable(CancellationToken cancellationToken) => ToAsyncEnumerable(cancellationToken);

    Task<(List<T> items, string? continuationToken)>                    ICosmosisQueryEntry<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>            ICosmosisQueryAfterDistinct<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                  ICosmosisQueryOrdered<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>     ICosmosisQueryOrderedAfterDistinct<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>              ICosmosisQueryPostOrdered<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)> ICosmosisQueryPostOrderedAfterDistinct<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                  ICosmosisQuerySkipped<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                    ICosmosisQueryTaken<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                ICosmosisQueryProjected<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>         ICosmosisQueryProjectedSkipped<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>           ICosmosisQueryProjectedTaken<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>              IUnprotectedCosmosisQuery<T>.ToPageAsync(int pageSize, byte[] encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, encryptionKey, continuationToken, cancellationToken);

    Task<(List<T> items, string? continuationToken)>                    ICosmosisQueryEntry<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>            ICosmosisQueryAfterDistinct<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                  ICosmosisQueryOrdered<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>     ICosmosisQueryOrderedAfterDistinct<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>              ICosmosisQueryPostOrdered<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)> ICosmosisQueryPostOrderedAfterDistinct<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                  ICosmosisQuerySkipped<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                    ICosmosisQueryTaken<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>                ICosmosisQueryProjected<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>         ICosmosisQueryProjectedSkipped<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>           ICosmosisQueryProjectedTaken<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);
    Task<(List<T> items, string? continuationToken)>              IUnprotectedCosmosisQuery<T>.ToPageAsync(int pageSize, string encryptionKey, string? continuationToken, CancellationToken cancellationToken) => ToPageAsync(pageSize, Encoding.UTF8.GetBytes(encryptionKey), continuationToken, cancellationToken);

    Task<bool>                    ICosmosisQueryEntry<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>            ICosmosisQueryAfterDistinct<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>                  ICosmosisQueryOrdered<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>     ICosmosisQueryOrderedAfterDistinct<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>              ICosmosisQueryPostOrdered<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool> ICosmosisQueryPostOrderedAfterDistinct<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>                  ICosmosisQuerySkipped<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>                    ICosmosisQueryTaken<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>                ICosmosisQueryProjected<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>         ICosmosisQueryProjectedSkipped<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>           ICosmosisQueryProjectedTaken<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);
    Task<bool>              IUnprotectedCosmosisQuery<T>.AnyAsync(CancellationToken cancellationToken) => AnyAsync(cancellationToken);

    Task<T>                    ICosmosisQueryEntry<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>            ICosmosisQueryAfterDistinct<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>                  ICosmosisQueryOrdered<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>     ICosmosisQueryOrderedAfterDistinct<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>              ICosmosisQueryPostOrdered<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T> ICosmosisQueryPostOrderedAfterDistinct<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>                  ICosmosisQuerySkipped<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>                    ICosmosisQueryTaken<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>                ICosmosisQueryProjected<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>         ICosmosisQueryProjectedSkipped<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>           ICosmosisQueryProjectedTaken<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);
    Task<T>              IUnprotectedCosmosisQuery<T>.FirstAsync(CancellationToken cancellationToken) => FirstAsync(cancellationToken);

    Task<T?>                   ICosmosisQueryEntry<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>                    ICosmosisQueryEntry<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>           ICosmosisQueryAfterDistinct<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>            ICosmosisQueryAfterDistinct<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                 ICosmosisQueryOrdered<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>                  ICosmosisQueryOrdered<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>    ICosmosisQueryOrderedAfterDistinct<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>     ICosmosisQueryOrderedAfterDistinct<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>             ICosmosisQueryPostOrdered<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>              ICosmosisQueryPostOrdered<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?> ICosmosisQueryPostOrderedAfterDistinct<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>  ICosmosisQueryPostOrderedAfterDistinct<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                 ICosmosisQuerySkipped<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>                  ICosmosisQuerySkipped<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                   ICosmosisQueryTaken<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>                    ICosmosisQueryTaken<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>               ICosmosisQueryProjected<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>                ICosmosisQueryProjected<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>        ICosmosisQueryProjectedSkipped<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>         ICosmosisQueryProjectedSkipped<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>          ICosmosisQueryProjectedTaken<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>           ICosmosisQueryProjectedTaken<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>             IUnprotectedCosmosisQuery<T>.FirstOrDefaultAsync(CancellationToken cancellationToken) => FirstOrDefaultAsync(cancellationToken);
    Task<T>              IUnprotectedCosmosisQuery<T>.FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => FirstOrDefaultAsync(defaultValue, cancellationToken);

    Task<T>                    ICosmosisQueryEntry<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>            ICosmosisQueryAfterDistinct<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>                  ICosmosisQueryOrdered<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>     ICosmosisQueryOrderedAfterDistinct<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>              ICosmosisQueryPostOrdered<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T> ICosmosisQueryPostOrderedAfterDistinct<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>                  ICosmosisQuerySkipped<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>                    ICosmosisQueryTaken<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>                ICosmosisQueryProjected<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>         ICosmosisQueryProjectedSkipped<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>           ICosmosisQueryProjectedTaken<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);
    Task<T>              IUnprotectedCosmosisQuery<T>.SingleAsync(CancellationToken cancellationToken) => SingleAsync(cancellationToken);

    Task<T?>                   ICosmosisQueryEntry<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>                    ICosmosisQueryEntry<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>           ICosmosisQueryAfterDistinct<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>            ICosmosisQueryAfterDistinct<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                 ICosmosisQueryOrdered<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>                  ICosmosisQueryOrdered<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>    ICosmosisQueryOrderedAfterDistinct<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>     ICosmosisQueryOrderedAfterDistinct<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>             ICosmosisQueryPostOrdered<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>              ICosmosisQueryPostOrdered<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?> ICosmosisQueryPostOrderedAfterDistinct<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>  ICosmosisQueryPostOrderedAfterDistinct<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                 ICosmosisQuerySkipped<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>                  ICosmosisQuerySkipped<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>                   ICosmosisQueryTaken<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>                    ICosmosisQueryTaken<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>               ICosmosisQueryProjected<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>                ICosmosisQueryProjected<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>        ICosmosisQueryProjectedSkipped<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>         ICosmosisQueryProjectedSkipped<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>          ICosmosisQueryProjectedTaken<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>           ICosmosisQueryProjectedTaken<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);
    Task<T?>             IUnprotectedCosmosisQuery<T>.SingleOrDefaultAsync(CancellationToken cancellationToken) => SingleOrDefaultAsync(cancellationToken);
    Task<T>              IUnprotectedCosmosisQuery<T>.SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken) => SingleOrDefaultAsync(defaultValue, cancellationToken);

    Task<int>                    ICosmosisQueryEntry<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>            ICosmosisQueryAfterDistinct<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>                  ICosmosisQueryOrdered<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>     ICosmosisQueryOrderedAfterDistinct<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>              ICosmosisQueryPostOrdered<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int> ICosmosisQueryPostOrderedAfterDistinct<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>                  ICosmosisQuerySkipped<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>                    ICosmosisQueryTaken<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);
    Task<int>                ICosmosisQueryProjected<T>.CountAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Use .CountAsync() instead of .Select(...).CountAsync().");
    Task<int>         ICosmosisQueryProjectedSkipped<T>.CountAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Use .CountAsync() instead of .Select(...).CountAsync().");
    Task<int>           ICosmosisQueryProjectedTaken<T>.CountAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Use .CountAsync() instead of .Select(...).CountAsync().");
    Task<int>              IUnprotectedCosmosisQuery<T>.CountAsync(CancellationToken cancellationToken) => CountAsync(cancellationToken);

    Task<int>                         ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                 ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                       ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>          ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                   ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>      ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                       ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                         ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int>                     ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int>              ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int>                ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int>                   IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                        ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                      ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>         ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                  ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>     ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                      ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                        ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<int?>                    ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int?>             ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int?>               ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<int?>                  IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                        ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                      ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>         ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                  ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>     ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                      ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                        ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long>                    ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long>             ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long>               ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long>                  IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                       ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>               ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                     ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>        ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                 ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>    ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                     ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                       ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<long?>                   ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long?>            ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long?>              ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<long?>                 IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                       ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>               ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                     ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>        ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                 ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>    ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                     ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                       ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float>                   ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float>            ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float>              ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float>                 IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                      ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>              ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                    ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>       ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>   ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                    ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                      ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<float?>                  ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float?>           ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float?>             ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<float?>                IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>              ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>       ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>   ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double>                  ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double>           ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double>             ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double>                IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>             ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>      ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>               ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>  ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<double?>                 ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double?>          ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double?>            ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<double?>               IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>                     ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>             ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>                   ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>      ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>               ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>  ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>                   ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>                     ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal>                 ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal>          ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal>            ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal>               IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>                    ICosmosisQueryEntry<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>            ICosmosisQueryAfterDistinct<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>                  ICosmosisQueryOrdered<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>     ICosmosisQueryOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>              ICosmosisQueryPostOrdered<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?> ICosmosisQueryPostOrderedAfterDistinct<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>                  ICosmosisQuerySkipped<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>                    ICosmosisQueryTaken<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);
    Task<decimal?>                ICosmosisQueryProjected<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal?>         ICosmosisQueryProjectedSkipped<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal?>           ICosmosisQueryProjectedTaken<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).");
    Task<decimal?>              IUnprotectedCosmosisQuery<T>.SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => SumAsync(selector, cancellationToken);

    Task<double>                      ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>              ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>       ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>   ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                  ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>           ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>             ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>                IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>             ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>      ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>               ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>  ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                 ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>          ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>            ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>               IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>              ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>       ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>   ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                  ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>           ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>             ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>                IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>             ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>      ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>               ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>  ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                 ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>          ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>            ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>               IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                       ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>               ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                     ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>        ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                 ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>    ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                     ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                       ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float>                   ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float>            ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float>              ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float>                 IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                      ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>              ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                    ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>       ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>   ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                    ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                      ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<float?>                  ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float?>           ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float?>             ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<float?>                IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>              ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>       ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>   ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                    ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                      ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double>                  ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>           ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>             ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double>                IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>             ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>      ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>               ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>  ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                   ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                     ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<double?>                 ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>          ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>            ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<double?>               IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>                     ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>             ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>                   ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>      ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>               ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>  ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>                   ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>                     ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal>                 ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal>          ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal>            ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal>               IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>                    ICosmosisQueryEntry<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>            ICosmosisQueryAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>                  ICosmosisQueryOrdered<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>     ICosmosisQueryOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>              ICosmosisQueryPostOrdered<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?> ICosmosisQueryPostOrderedAfterDistinct<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>                  ICosmosisQuerySkipped<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>                    ICosmosisQueryTaken<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);
    Task<decimal?>                ICosmosisQueryProjected<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal?>         ICosmosisQueryProjectedSkipped<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal?>           ICosmosisQueryProjectedTaken<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).");
    Task<decimal?>              IUnprotectedCosmosisQuery<T>.AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken) => AverageAsync(selector, cancellationToken);

    Task<TResult>                    ICosmosisQueryEntry<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>            ICosmosisQueryAfterDistinct<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>                  ICosmosisQueryOrdered<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>     ICosmosisQueryOrderedAfterDistinct<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>              ICosmosisQueryPostOrdered<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult> ICosmosisQueryPostOrderedAfterDistinct<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>                  ICosmosisQuerySkipped<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>                    ICosmosisQueryTaken<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);
    Task<TResult>                ICosmosisQueryProjected<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MinAsync(selector) instead of .Select(...).MinAsync(...).");
    Task<TResult>         ICosmosisQueryProjectedSkipped<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MinAsync(selector) instead of .Select(...).MinAsync(...).");
    Task<TResult>           ICosmosisQueryProjectedTaken<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MinAsync(selector) instead of .Select(...).MinAsync(...).");
    Task<TResult>              IUnprotectedCosmosisQuery<T>.MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MinAsync(selector, cancellationToken);

    Task<TResult>                    ICosmosisQueryEntry<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>            ICosmosisQueryAfterDistinct<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>                  ICosmosisQueryOrdered<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>     ICosmosisQueryOrderedAfterDistinct<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>              ICosmosisQueryPostOrdered<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult> ICosmosisQueryPostOrderedAfterDistinct<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>                  ICosmosisQuerySkipped<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>                    ICosmosisQueryTaken<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);
    Task<TResult>                ICosmosisQueryProjected<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MaxAsync(selector) instead of .Select(...).MaxAsync(...).");
    Task<TResult>         ICosmosisQueryProjectedSkipped<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MaxAsync(selector) instead of .Select(...).MaxAsync(...).");
    Task<TResult>           ICosmosisQueryProjectedTaken<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => throw new InvalidOperationException("Use .MaxAsync(selector) instead of .Select(...).MaxAsync(...).");
    Task<TResult>              IUnprotectedCosmosisQuery<T>.MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken) => MaxAsync(selector, cancellationToken);

#pragma warning restore CS0618
}
