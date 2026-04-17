using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BreadTh.Cosmosis.Query;

/// <summary>
/// Exposes all query operations without compile-time ordering constraints.
/// Use this when <see cref="ICosmosisClient.Query{T}"/> restricts a valid query.
/// No guardrails - invalid or surprising combinations are possible.
/// </summary>
public interface IUnprotectedCosmosisQuery<T>
{
    IUnprotectedCosmosisQuery<T> Where(Expression<Func<T, bool>> predicate);
    IUnprotectedCosmosisQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    IUnprotectedCosmosisQuery<TResult> SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector);
    IUnprotectedCosmosisQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    IUnprotectedCosmosisQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    IUnprotectedCosmosisQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);
    IUnprotectedCosmosisQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    IUnprotectedCosmosisQuery<T> Take(int count);
    IUnprotectedCosmosisQuery<T> Skip(int count);
    IUnprotectedCosmosisQuery<T> Distinct();
    
    Task<List<T>> ToListAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<T> ToAsyncEnumerable(CancellationToken cancellationToken = default);
    Task<(List<T> items, string? continuationToken)> ToPageAsync(
        int pageSize,
        byte[] encryptionKey,
        string? continuationToken = null,
        CancellationToken cancellationToken = default
    );
    Task<(List<T> items, string? continuationToken)> ToPageAsync(
        int pageSize,
        string encryptionKey,
        string? continuationToken = null,
        CancellationToken cancellationToken = default
    );
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
    Task<T> FirstAsync(CancellationToken cancellationToken = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);
    Task<T> SingleAsync(CancellationToken cancellationToken = default);
    Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);

    Task<int> SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default);
    Task<int?> SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default);
    Task<long> SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default);
    Task<long?> SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default);
    Task<float> SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default);
    Task<float?> SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default);
    Task<double> SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default);
    Task<double?> SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default);
    Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default);
    Task<decimal?> SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default);

    Task<double> AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default);
    Task<double?> AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default);
    Task<double> AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default);
    Task<double?> AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default);
    Task<float> AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default);
    Task<float?> AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default);
    Task<double> AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default);
    Task<double?> AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default);
    Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default);
    Task<decimal?> AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default);

    Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default);
    Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default);
}
