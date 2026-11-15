using System;
using System.Threading;

namespace RedisDistributedLock.Abstractions;

/// <summary>A lock whose lease is renewed in the background. Dispose of it to stop the renewal and release the lock.</summary>
public interface IRenewableDistributedLockHandle : IAsyncDisposable
{
    IDistributedLock InnerLock { get; }

    /// <summary>
    ///     Canceled when the lock is lost, i.e. it could not be renewed before it expired, it is now held by someone
    ///     else, or the pre-execute check stopped the renewal. Renewal stops at that point; the holder should stop working on
    ///     the protected resource. Not canceled when the lock is released.
    /// </summary>
    CancellationToken LockLost { get; }
}
