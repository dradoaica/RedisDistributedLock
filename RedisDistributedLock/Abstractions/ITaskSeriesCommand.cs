namespace RedisDistributedLock.Abstractions;

using System.Threading;
using System.Threading.Tasks;

public interface ITaskSeriesCommand
{
    Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}
