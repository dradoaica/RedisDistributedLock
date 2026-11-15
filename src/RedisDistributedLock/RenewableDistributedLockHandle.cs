using RedisDistributedLock.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

/// <param name="handle">The lock that is renewed.</param>
/// <param name="renewal">The started timer that renews the lease.</param>
/// <param name="distributedLockManager">Releases the lock when the handle is disposed of.</param>
/// <param name="lockLost">Canceled when the lock is lost.</param>
/// <param name="expiryWatch">Completes once it has seen the lock expire, or once it was told to stop.</param>
/// <param name="stopExpiryWatch">Stops the expiry watch.</param>
internal sealed class RenewableDistributedLockHandle(
    IDistributedLock handle,
    ITaskSeriesTimer renewal,
    IDistributedLockManager distributedLockManager,
    CancellationTokenSource lockLost,
    Task expiryWatch,
    CancellationTokenSource stopExpiryWatch
) : IRenewableDistributedLockHandle
{
    private int disposed;

    /// <summary>The inner lock for the underlying implementation. This is a pluggable implementation.</summary>
    public IDistributedLock InnerLock { get; } = handle;

    /// <inheritdoc />
    public CancellationToken LockLost { get; } = lockLost.Token;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            // First, so that the release below isn't taken for a loss
            stopExpiryWatch.Cancel();
            await expiryWatch.ConfigureAwait(false);
            await renewal.StopAsync(CancellationToken.None).ConfigureAwait(false);
            renewal.Dispose();
            await distributedLockManager.ReleaseLockAsync(InnerLock, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lockLost.Dispose();
            stopExpiryWatch.Dispose();
        }
    }
}
