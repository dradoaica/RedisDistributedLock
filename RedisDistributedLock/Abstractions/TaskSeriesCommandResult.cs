namespace RedisDistributedLock.Abstractions;

using System.Threading.Tasks;

public struct TaskSeriesCommandResult
{
    public TaskSeriesCommandResult(Task wait) => this.Wait = wait;

    /// <summary>
    ///     Wait for this task to complete before calling <see cref="ITaskSeriesCommand.ExecuteAsync" /> again.
    /// </summary>
    public Task Wait { get; }
}
