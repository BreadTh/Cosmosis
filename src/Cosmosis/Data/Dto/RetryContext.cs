using System.Threading;

namespace BreadTh.Cosmosis.Data.Dto;

internal sealed class RetryContext
{
    public int Attempt { get; internal set; }
    public int NetworkFailureCount { get; internal set; }
    public int ThrottleCount { get; internal set; }
    public int ServiceUnavailableCount { get; internal set; }
    public int BackoffCount { get; internal set; }
    public CancellationToken CancellationToken { get; internal set; }
}
