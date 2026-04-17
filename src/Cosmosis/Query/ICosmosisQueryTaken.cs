using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BreadTh.Cosmosis.Query;

public interface ICosmosisQueryTaken<T>
{
    ICosmosisQueryProjected<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    ICosmosisQueryProjected<TResult> SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector);

    [Obsolete("Where must come first. Rearrange to .Where(...) before other operations.", true)]
    ICosmosisQueryTaken<T> Where(Expression<Func<T, bool>> predicate);
    [Obsolete("Distinct must come before Take. Rearrange to .Distinct() before .Take(...).", true)]
    ICosmosisQueryTaken<T> Distinct();
    [Obsolete("OrderBy must come before Take. Rearrange to .OrderBy(...) before .Take(...).", true)]
    ICosmosisQueryTaken<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("OrderBy must come before Take. Rearrange to .OrderByDescending(...) before .Take(...).", true)]
    ICosmosisQueryTaken<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("ThenBy must come before Take. Rearrange to .OrderBy(...).ThenBy(...) before .Take(...).", true)]
    ICosmosisQueryTaken<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("ThenByDescending must come before Take. Rearrange to .OrderBy(...).ThenByDescending(...) before .Take(...).", true)]
    ICosmosisQueryTaken<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("Skip must come before Take. Rearrange to .OrderBy(...).Skip(...) before .Take(...).", true)]
    ICosmosisQueryTaken<T> Skip(int count);
    [Obsolete("Take has already been applied.", true)]
    ICosmosisQueryTaken<T> Take(int count);

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
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
    Task<T> FirstAsync(CancellationToken cancellationToken = default);
    Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> FirstOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);
    Task<T> SingleAsync(CancellationToken cancellationToken = default);
    Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default);
    Task<T> SingleOrDefaultAsync(T defaultValue, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
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
