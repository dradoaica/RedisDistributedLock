using RedisDistributedLock.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public class RenewLeaseCommand(
    IDistributedLockManager distributedLockManager,
    IDistributedLock distributedLock,
    IDelayStrategy delayStrategy
) : ITaskSeriesCommand
{
    public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Exceptions will propagate
        var renewalSucceeded = await distributedLockManager.RenewAsync(distributedLock, cancellationToken);

        var delay = delayStrategy.GetNextDelay(renewalSucceeded);
        return new TaskSeriesCommandResult(Task.Delay(delay, cancellationToken));
    }
}
