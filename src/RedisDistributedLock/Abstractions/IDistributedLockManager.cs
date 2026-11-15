using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

/// <summary>Manage distributed lock. A lock is specified by lockId.</summary>
/// <remarks></remarks>
public interface IDistributedLockManager
{
    /// <summary>Try to acquire a lock specified by lockId.</summary>
    /// <param name="lockId">The name of the lock. </param>
    /// <param name="lockPeriod">
    ///     The length of the lease to acquire. The caller is responsible for Renewing the lease well
    ///     before this time is up. The exact value here is restricted based on the underlying implementation.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <returns>Null if can't acquire the lock. This is common if somebody else holds it.</returns>
    /// <remarks>The lock isn't renewed: it expires after <paramref name="lockPeriod" /> unless the caller renews it.</remarks>
    Task<IDistributedLock?> TryLockAsync(string lockId, TimeSpan lockPeriod, CancellationToken cancellationToken);

    /// <summary>Try create a renewable lock handle specified by lockId.</summary>
    /// <param name="lockId">The name of the lock. </param>
    /// <param name="lockPeriod">
    ///     The length of the lease to acquire. The exact value here is restricted based on the underlying
    ///     implementation.
    /// </param>
    /// <param name="leasePeriod">
    ///     The lease is renewed every half of this period. It must not be longer than
    ///     <paramref name="lockPeriod" />, so that a renewal that fails can be retried before the lock expires.
    /// </param>
    /// <param name="retryStrategy">How failed renewals are retried.</param>
    /// <param name="preExecuteCheck">
    ///     Called before each renewal. Returning false stops the renewal and cancels
    ///     <see cref="IRenewableDistributedLockHandle.LockLost" />, because the lock then expires. The lock is left to expire
    ///     rather than released, which gives the holder the rest of the lock period to stop.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <returns>Null if can't acquire the lock. This is common if somebody else holds it.</returns>
    /// <remarks>Dispose of the handle to stop the renewal and release the lock.</remarks>
    Task<IRenewableDistributedLockHandle?> TryCreateRenewableLockHandleAsync(
        string lockId,
        TimeSpan lockPeriod,
        TimeSpan leasePeriod,
        RenewalRetryStrategy retryStrategy,
        Func<bool>? preExecuteCheck,
        CancellationToken cancellationToken
    );

    /// <summary>Called by the client to renew the lease.</summary>
    /// <param name="lockHandle"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    ///     A hint used for determining the next time delay in calling Renew. True means the next execution should occur
    ///     at a normal delay. False means the next execution should occur quickly; use this in network error cases.
    /// </returns>
    /// <remarks>
    ///     If this throws an exception, the lease is cancelled. Throws <c>DistributedLockLostException</c> when the lock
    ///     expired or is held by someone else. A renewable lock handle renews its lock itself.
    /// </remarks>
    Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken);

    /// <summary>Release a lock that was previously acquired via TryLockAsync.</summary>
    /// <param name="lockHandle">The previously acquired handle to be released.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <remarks>
    ///     For a renewable lock handle, prefer disposing of the handle, which also stops the renewal. Releasing a lock
    ///     that was lost in the meantime is harmless: it never releases someone else's lock.
    /// </remarks>
    Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken);
}
