using RedisDistributedLock.Abstractions;

namespace RedisDistributedLock;

public class RenewableDistributedLockHandle(IDistributedLock handle, ITaskSeriesTimer renewal)
    : IRenewableDistributedLockHandle
{
    /// <summary>The inner lock for the underlying implementation. This is a pluggable implementation.</summary>
    public IDistributedLock InnerLock { get; } = handle;

    /// <summary>Handle to a timer for renewing the lease. We handle the timer.</summary>
    public ITaskSeriesTimer LeaseRenewalTimer { get; } = renewal;
}
