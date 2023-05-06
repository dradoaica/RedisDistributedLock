namespace RedisDistributedLock;

using System.Threading;
using System.Threading.Tasks;
using Abstractions;

public class RenewLeaseCommand : ITaskSeriesCommand
{
    private readonly IDelayStrategy delayStrategy;
    private readonly IDistributedLock @lock;
    private readonly IDistributedLockManager lockManager;

    public RenewLeaseCommand(IDistributedLockManager lockManager, IDistributedLock @lock, IDelayStrategy delayStrategy)
    {
        this.@lock = @lock;
        this.lockManager = lockManager;
        this.delayStrategy = delayStrategy;
    }

    public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Exceptions will propagate
        var renewalSucceeded = await this.lockManager.RenewAsync(this.@lock, cancellationToken);

        var delay = this.delayStrategy.GetNextDelay(renewalSucceeded);
        return new TaskSeriesCommandResult(Task.Delay(delay));
    }
}
