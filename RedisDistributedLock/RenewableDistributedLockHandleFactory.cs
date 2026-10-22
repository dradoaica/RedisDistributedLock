using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public static class RenewableDistributedLockHandleFactory
{
    private static ITaskSeriesTimer CreateLeaseRenewalTimer(
        TimeSpan leasePeriod,
        bool linear,
        IDistributedLockManager distributedLockManager,
        IDistributedLock lockHandle,
        Func<bool>? preExecuteCheck,
        CancellationTokenSource lockLost
    )
    {
        // renew the lease when it is halfway to expiring
        var normalUpdateInterval = new TimeSpan(leasePeriod.Ticks / 2);
        // retry failed renewals sooner, but not more often than every 250ms
        var minimumUpdateInterval = new TimeSpan(
            Math.Min(TimeSpan.FromMilliseconds(250).Ticks, normalUpdateInterval.Ticks)
        );
        IDelayStrategy speedupStrategy = linear ? new LinearSpeedupStrategy(normalUpdateInterval, minimumUpdateInterval)
            : new RandomizedExponentialBackoffStrategy(minimumUpdateInterval, normalUpdateInterval);
        ITaskSeriesCommand command = new RenewLeaseCommand(
            distributedLockManager,
            lockHandle,
            speedupStrategy,
            lockLost
        );
        return new TaskSeriesTimer(command, Task.Delay(normalUpdateInterval), preExecuteCheck);
    }

    public static RenewableDistributedLockHandle CreateRenewableLockHandle(
        TimeSpan leasePeriod,
        bool linear,
        IDistributedLockManager distributedLockManager,
        IDistributedLock lockHandle,
        Func<bool>? preExecuteCheck
    )
    {
        var lockLost = new CancellationTokenSource();
        var renewal = CreateLeaseRenewalTimer(
            leasePeriod,
            linear,
            distributedLockManager,
            lockHandle,
            preExecuteCheck,
            lockLost
        );
        renewal.Start();
        return new RenewableDistributedLockHandle(lockHandle, renewal, lockLost.Token);
    }
}
