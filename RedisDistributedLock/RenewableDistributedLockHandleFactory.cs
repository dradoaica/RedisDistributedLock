using RedisDistributedLock.Abstractions;
using System;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public static class RenewableDistributedLockHandleFactory
{
    private static ITaskSeriesTimer CreateLeaseRenewalTimer(
        TimeSpan leasePeriod,
        bool linear,
        IDistributedLockManager distributedLockManager,
        IDistributedLock lockHandle,
        Func<bool>? preExecuteCheck
    )
    {
        // renew the lease when it is halfway to expiring   
        var normalUpdateInterval = new TimeSpan(leasePeriod.Ticks / 2);
        IDelayStrategy speedupStrategy = linear
            ? new LinearSpeedupStrategy(normalUpdateInterval, TimeSpan.FromMilliseconds(250))
            : new RandomizedExponentialBackoffStrategy(normalUpdateInterval, TimeSpan.FromMilliseconds(250));
        ITaskSeriesCommand command = new RenewLeaseCommand(distributedLockManager, lockHandle, speedupStrategy);
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
        var renewal = CreateLeaseRenewalTimer(leasePeriod, linear, distributedLockManager, lockHandle, preExecuteCheck);
        renewal.Start();
        return new RenewableDistributedLockHandle(lockHandle, renewal);
    }
}
