using RedisDistributedLock.Abstractions;

namespace RedisDistributedLock;

public class RenewableDistributedLockHandle(IDistributedLock handle, ITaskSeriesTimer renewal)
    : IRenewableDistributedLockHandle
{
    // The inner lock for the underlying implementation. 
    // This is a pluggable implementation. 
    public IDistributedLock InnerLock { get; } = handle;

    // Handle to a timer for renewing the lease. 
    // We handle the timer.
    public ITaskSeriesTimer LeaseRenewalTimer { get; } = renewal;
}
