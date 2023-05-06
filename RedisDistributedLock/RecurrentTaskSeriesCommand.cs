namespace RedisDistributedLock;

using System.Threading;
using System.Threading.Tasks;
using Abstractions;

public class RecurrentTaskSeriesCommand : ITaskSeriesCommand
{
    private readonly IDelayStrategy delayStrategy;
    private readonly IRecurrentCommand innerCommand;

    public RecurrentTaskSeriesCommand(IRecurrentCommand innerCommand, IDelayStrategy delayStrategy)
    {
        this.innerCommand = innerCommand;
        this.delayStrategy = delayStrategy;
    }

    public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var succeeded = await this.innerCommand.TryExecuteAsync(cancellationToken);
        var wait = Task.Delay(this.delayStrategy.GetNextDelay(succeeded));
        return new TaskSeriesCommandResult(wait);
    }
}
