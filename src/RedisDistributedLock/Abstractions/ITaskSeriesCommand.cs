using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

internal interface ITaskSeriesCommand
{
    Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}
