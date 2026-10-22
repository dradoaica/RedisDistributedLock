using System.Threading;

namespace RedisDistributedLock.Abstractions;

public interface IRenewableDistributedLockHandle
{
    IDistributedLock InnerLock { get; }
    ITaskSeriesTimer LeaseRenewalTimer { get; }

    /// <summary>
    ///     Canceled when the lock is lost, i.e. it could not be renewed before it expired or it is now held by someone
    ///     else. Renewal stops at that point; the holder should stop working on the protected resource.
    /// </summary>
    CancellationToken LockLost { get; }
}
