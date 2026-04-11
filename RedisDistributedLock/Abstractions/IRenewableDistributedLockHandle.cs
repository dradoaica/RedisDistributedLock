namespace RedisDistributedLock.Abstractions;

public interface IRenewableDistributedLockHandle
{
    IDistributedLock InnerLock { get; }
    ITaskSeriesTimer LeaseRenewalTimer { get; }
}
