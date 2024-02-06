using System.Threading;
using System.Threading.Tasks;
using RedisDistributedLock.Abstractions;

namespace RedisDistributedLock;

public class RecurrentTaskSeriesCommand(IRecurrentCommand innerCommand, IDelayStrategy delayStrategy)
    : ITaskSeriesCommand
{
    public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var succeeded = await innerCommand.TryExecuteAsync(cancellationToken).ConfigureAwait(false);
        var wait = Task.Delay(delayStrategy.GetNextDelay(succeeded), cancellationToken);
        return new TaskSeriesCommandResult(wait);
    }
}
