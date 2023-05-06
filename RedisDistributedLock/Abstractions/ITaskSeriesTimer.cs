namespace RedisDistributedLock.Abstractions;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface ITaskSeriesTimer : IDisposable
{
    void Start();

    Task StopAsync(CancellationToken cancellationToken);

    void Cancel();
}
