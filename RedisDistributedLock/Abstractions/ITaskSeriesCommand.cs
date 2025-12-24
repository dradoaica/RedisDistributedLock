using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

public interface ITaskSeriesCommand
{
    Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}
