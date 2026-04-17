using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BreadTh.Cosmosis.Query;

public interface ICosmosisQueryEntry<T>
{
    ICosmosisQueryEntry<T> Where(Expression<Func<T, bool>> predicate);
    ICosmosisQueryAfterDistinct<T> Distinct();
    ICosmosisQueryOrdered<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    ICosmosisQueryOrdered<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    ICosmosisQuerySkipped<T> Skip(int count);
    ICosmosisQueryTaken<T> Take(int count);
    ICosmosisQueryProjected<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    ICosmosisQueryProjected<TResult> SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector);

    [Obsolete("ThenBy requires OrderBy first. Rearrange to .OrderBy(...).ThenBy(...).", true)]
    ICosmosisQueryEntry<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("ThenByDescending requires OrderBy first. Rearrange to .OrderBy(...).ThenByDescending(...).", true)]
    ICosmosisQueryEntry<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);

    Task<List<T>> ToListAsync(CancellationToken cancellationToken = default);
    // IAsyncEnumerable is the primary reason we target netstandard2.1 rather than 2.0 like Cosmos SDK does.
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
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
    Task<T> FirstAsync(CancellationToken cancellationToken = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);
    Task<T> SingleAsync(CancellationToken cancellationToken = default);
    Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    // Sum and Average overloads are listed explicitly per numeric type because netstandard2.1
    // does not support INumber<T> (requires .NET 7+).

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
