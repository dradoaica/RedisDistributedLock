using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

public readonly struct TaskSeriesCommandResult(Task wait)
{
    /// <summary>Wait for this task to complete before calling <see cref="ITaskSeriesCommand.ExecuteAsync" /> again.</summary>
    public Task Wait { get; } = wait;
}
