using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BreadTh.Cosmosis.Query;

public interface ICosmosisQueryProjectedTaken<T>
{
    [Obsolete("Where must come before .Select(). Rearrange to .Where(...) before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> Where(Expression<Func<T, bool>> predicate);
    [Obsolete("Distinct must come before .Select(). Rearrange to .Distinct() before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> Distinct();
    [Obsolete("OrderBy must come before .Select(). Rearrange to .OrderBy(...) before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("OrderBy must come before .Select(). Rearrange to .OrderByDescending(...) before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenBy(...) before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("ThenBy must come before .Select(). Rearrange to .OrderBy(...).ThenByDescending(...) before .Select(...).", true)]
    ICosmosisQueryProjectedTaken<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    [Obsolete("Skip must come before Take. Rearrange to .Select(...).Skip(...).Take(...).", true)]
    ICosmosisQueryProjectedTaken<T> Skip(int count);
    [Obsolete("Take has already been applied.", true)]
    ICosmosisQueryProjectedTaken<T> Take(int count);
    [Obsolete("Select has already been applied.", true)]
    ICosmosisQueryProjectedTaken<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    [Obsolete("Select has already been applied.", true)]
    ICosmosisQueryProjectedTaken<TResult> SelectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector);

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

    [Obsolete("Use .CountAsync() instead of .Select(...).CountAsync().", true)]
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<int> SumAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<int?> SumAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<long> SumAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<long?> SumAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<float> SumAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<float?> SumAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<double> SumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<double?> SumAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<decimal> SumAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .SumAsync(selector) instead of .Select(...).SumAsync(...).", true)]
    Task<decimal?> SumAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double> AverageAsync(Expression<Func<T, int>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double?> AverageAsync(Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double> AverageAsync(Expression<Func<T, long>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double?> AverageAsync(Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<float> AverageAsync(Expression<Func<T, float>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<float?> AverageAsync(Expression<Func<T, float?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double> AverageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<double?> AverageAsync(Expression<Func<T, double?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<decimal> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .AverageAsync(selector) instead of .Select(...).AverageAsync(...).", true)]
    Task<decimal?> AverageAsync(Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .MinAsync(selector) instead of .Select(...).MinAsync(...).", true)]
    Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default);
    [Obsolete("Use .MaxAsync(selector) instead of .Select(...).MaxAsync(...).", true)]
    Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken cancellationToken = default);
}
