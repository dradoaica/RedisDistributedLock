using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

internal class RenewLeaseCommand(
    IDistributedLockManager distributedLockManager,
    IDistributedLock distributedLock,
    IDelayStrategy delayStrategy,
    TimeProvider timeProvider,
    CancellationTokenSource? lockLost = null
) : ITaskSeriesCommand
{
    public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        bool renewalSucceeded;
        try
        {
            renewalSucceeded = await distributedLockManager.RenewAsync(distributedLock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The lease is cancelled: notify the holder and let the exception propagate to stop the renewal timer
            lockLost?.Cancel();
            throw;
        }

        var delay = delayStrategy.GetNextDelay(renewalSucceeded);
        return new TaskSeriesCommandResult(timeProvider.Delay(delay, cancellationToken));
    }
}
