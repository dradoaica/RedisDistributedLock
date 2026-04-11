using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.Abstractions;

public interface ITaskSeriesTimer : IDisposable
{
    void Start();

    Task StopAsync(CancellationToken cancellationToken);

    void Cancel();
}
