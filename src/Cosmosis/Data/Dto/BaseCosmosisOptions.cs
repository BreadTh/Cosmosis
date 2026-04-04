using System;
using BreadTh.Cosmosis.Data.Exceptions;

namespace BreadTh.Cosmosis.Data.Dto;

public abstract class BaseCosmosisOptions
{
    static readonly TimeSpan[] DefaultRetryBackoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];

    /// <summary>
    /// Backoff intervals for transient error retries. Each element is the delay before that retry attempt.
    /// Array length determines the maximum number of retries. Set to null to disable retry.
    /// Default: 100ms, 500ms, 2s, 5s, 10s (5 retries).
    /// </summary>
    public TimeSpan[]? RetryBackoff { get; set; } = DefaultRetryBackoff;

    /// <summary>
    /// Maximum number of total retry attempts across all error types.
    /// Default: 5.
    /// </summary>
    public int MaxTotalRetries { get; set; } = 5;

    /// <summary>
    /// Maximum number of consecutive network timeout (408) failures before throwing
    /// <see cref="CosmosConnectionTimedOutException"/>.
    /// Default: 3.
    /// </summary>
    public int MaxNetworkFailureRetries { get; set; } = 3;

    /// <summary>
    /// Maximum number of throttle (429) failures before throwing
    /// <see cref="CosmosTooManyRequestsException"/>.
    /// Default: 3.
    /// </summary>
    public int MaxThrottleRetries { get; set; } = 3;

    /// <summary>
    /// Maximum number of service unavailable (503) failures before throwing
    /// <see cref="CosmosServiceUnavailableException"/>.
    /// Default: 3.
    /// </summary>
    public int MaxServiceUnavailableRetries { get; set; } = 3;
}