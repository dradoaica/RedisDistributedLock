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
    Task<IDistributedLock?> TryLockAsync(string lockId, TimeSpan lockPeriod, CancellationToken cancellationToken);

    /// <summary>Try create a renewable lock handle specified by lockId.</summary>
    /// <param name="lockId">The name of the lock. </param>
    /// <param name="lockPeriod">
    ///     The length of the lease to acquire. The exact value here is restricted based on the underlying
    ///     implementation.
    /// </param>
    /// <param name="leasePeriod"></param>
    /// <param name="linear"></param>
    /// <param name="preExecuteCheck"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>Null if can't acquire the lock. This is common if somebody else holds it.</returns>
    Task<IRenewableDistributedLockHandle?> TryCreateRenewableLockHandleAsync(
        string lockId,
        TimeSpan lockPeriod,
        TimeSpan leasePeriod,
        bool linear,
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
    /// <remarks>If this throws an exception, the lease is cancelled.</remarks>
    Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken);

    /// <summary>Release a lock that was previously acquired via TryLockAsync.</summary>
    /// <param name="lockHandle">The previously acquired handle to be released.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken);
}
