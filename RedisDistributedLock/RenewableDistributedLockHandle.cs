namespace RedisDistributedLock;

using Abstractions;

public class RenewableDistributedLockHandle : IRenewableDistributedLockHandle
{
    public RenewableDistributedLockHandle(IDistributedLock handle, ITaskSeriesTimer renewal)
    {
        this.InnerLock = handle;
        this.LeaseRenewalTimer = renewal;
    }

    // The inner lock for the underlying implementation. 
    // This is a pluggable implementation. 
    public IDistributedLock InnerLock { get; }

    // Handle to a timer for renewing the lease. 
    // We handle the timer.
    public ITaskSeriesTimer LeaseRenewalTimer { get; }
}
