using System;
using System.Threading.Tasks;
using RedisDistributedLock.Abstractions;

namespace RedisDistributedLock;

public static class RenewableDistributedLockHandleFactory
{
    private static ITaskSeriesTimer CreateLeaseRenewalTimer(TimeSpan leasePeriod,
        IDistributedLockManager distributedLockManager, IDistributedLock lockHandle, Func<bool>? preExecuteCheck)
    {
        // renew the lease when it is halfway to expiring   
        var normalUpdateInterval = new TimeSpan(leasePeriod.Ticks / 2);
        IDelayStrategy speedupStrategy =
            new LinearSpeedupStrategy(normalUpdateInterval, TimeSpan.FromMilliseconds(250));
        ITaskSeriesCommand command = new RenewLeaseCommand(distributedLockManager, lockHandle, speedupStrategy);
        return new TaskSeriesTimer(command, Task.Delay(normalUpdateInterval), preExecuteCheck);
    }

    public static RenewableDistributedLockHandle CreateRenewableLockHandle(TimeSpan leasePeriod,
        IDistributedLockManager distributedLockManager, IDistributedLock lockHandle, Func<bool>? preExecuteCheck)
    {
        var renewal =
            CreateLeaseRenewalTimer(leasePeriod, distributedLockManager, lockHandle, preExecuteCheck);
        renewal.Start();
        return new RenewableDistributedLockHandle(lockHandle, renewal);
    }
}
