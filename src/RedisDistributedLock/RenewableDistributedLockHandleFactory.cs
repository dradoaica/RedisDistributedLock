using Microsoft.Extensions.Logging;
using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

internal static class RenewableDistributedLockHandleFactory
{
    private static ITaskSeriesTimer CreateLeaseRenewalTimer(
        TimeSpan leasePeriod,
        RenewalRetryStrategy retryStrategy,
        IDistributedLockManager distributedLockManager,
        IDistributedLock lockHandle,
        Func<bool>? preExecuteCheck,
        CancellationTokenSource lockLost,
        TimeProvider timeProvider
    )
    {
        // renew the lease when it is halfway to expiring
        var normalUpdateInterval = new TimeSpan(leasePeriod.Ticks / 2);
        // retry failed renewals sooner, but not more often than every 250ms
        var minimumUpdateInterval = new TimeSpan(
            Math.Min(TimeSpan.FromMilliseconds(250).Ticks, normalUpdateInterval.Ticks)
        );
        IDelayStrategy speedupStrategy = retryStrategy == RenewalRetryStrategy.Speedup
            ? new LinearSpeedupStrategy(normalUpdateInterval, minimumUpdateInterval)
            : new RandomizedExponentialBackoffStrategy(minimumUpdateInterval, normalUpdateInterval);
        ITaskSeriesCommand command = new RenewLeaseCommand(
            distributedLockManager,
            lockHandle,
            speedupStrategy,
            timeProvider,
            lockLost
        );
        return new TaskSeriesTimer(
            command,
            timeProvider.Delay(normalUpdateInterval),
            preExecuteCheck == null ? null : () => CheckOrSignalLockLost(preExecuteCheck, lockLost)
        );
    }

    // Once the check stops the renewal the lock expires, so the holder must be told just as when a renewal fails
    private static bool CheckOrSignalLockLost(Func<bool> preExecuteCheck, CancellationTokenSource lockLost)
    {
        if (preExecuteCheck())
        {
            return true;
        }

        lockLost.Cancel();
        return false;
    }

    /// <summary>
    ///     Signals that the lock is lost at the moment it expires. A renewal only finds out when it next runs, and can
    ///     itself be waiting for instances that don't answer.
    /// </summary>
    private static async Task WatchExpiryAsync(
        RedisDistributedLockManager.DistributedLock lockHandle,
        ITaskSeriesTimer renewal,
        CancellationTokenSource lockLost,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Each renewal moves the expiry, so look again after waiting for it
            while (!lockHandle.IsReleased && !lockLost.IsCancellationRequested)
            {
                var timeUntilExpiry = lockHandle.TimeUntilExpiry;
                if (timeUntilExpiry <= TimeSpan.Zero)
                {
                    logger.LogWarning(
                        "The distributed lock '{LockId}' expired before it could be renewed.",
                        lockHandle.LockId
                    );
                    lockLost.Cancel();
                    // A renewal that succeeded from here on would bring back a lock its holder already gave up
                    renewal.Cancel();
                    return;
                }

                await timeProvider.Delay(timeUntilExpiry, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The handle is being disposed of
        }
    }

    public static RenewableDistributedLockHandle CreateRenewableLockHandle(
        TimeSpan leasePeriod,
        RenewalRetryStrategy retryStrategy,
        IDistributedLockManager distributedLockManager,
        RedisDistributedLockManager.DistributedLock lockHandle,
        Func<bool>? preExecuteCheck,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        var lockLost = new CancellationTokenSource();
        var renewal = CreateLeaseRenewalTimer(
            leasePeriod,
            retryStrategy,
            distributedLockManager,
            lockHandle,
            preExecuteCheck,
            lockLost,
            timeProvider
        );
        renewal.Start();
        var stopExpiryWatch = new CancellationTokenSource();
        var expiryWatch = WatchExpiryAsync(lockHandle, renewal, lockLost, timeProvider, logger, stopExpiryWatch.Token);
        return new RenewableDistributedLockHandle(
            lockHandle,
            renewal,
            distributedLockManager,
            lockLost,
            expiryWatch,
            stopExpiryWatch
        );
    }
}
