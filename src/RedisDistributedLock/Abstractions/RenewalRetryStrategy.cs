namespace RedisDistributedLock.Abstractions;

/// <summary>
///     How a renewable lock retries a failed renewal. Either way the delay stays between 250ms and the normal renewal
///     interval.
/// </summary>
public enum RenewalRetryStrategy
{
    /// <summary>Retry as soon as possible, then back off exponentially (randomized) up to the normal interval.</summary>
    ExponentialBackoff,

    /// <summary>Halve the delay after each failure, starting from the normal interval.</summary>
    Speedup,
}
