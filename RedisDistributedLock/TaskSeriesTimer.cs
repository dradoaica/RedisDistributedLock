using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

/// <summary>Represents a timer that executes one task after another in a series.</summary>
public sealed class TaskSeriesTimer(ITaskSeriesCommand? command, Task? initialWait, Func<bool>? preExecuteCheck = null)
    : ITaskSeriesTimer
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly ITaskSeriesCommand command = command ?? throw new ArgumentNullException(nameof(command));
    private readonly Task initialWait = initialWait ?? throw new ArgumentNullException(nameof(initialWait));
    private bool disposed;
    private Task? run;
    private bool started;
    private bool stopped;

    public void Start()
    {
        ThrowIfDisposed();

        if (started)
        {
            throw new InvalidOperationException("The timer has already been started; it cannot be restarted.");
        }

        run = RunAsync(cancellationTokenSource.Token);
        started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!started)
        {
            throw new InvalidOperationException("The timer has not yet been started.");
        }

        if (stopped)
        {
            throw new InvalidOperationException("The timer has already been stopped.");
        }

        cancellationTokenSource.Cancel();
        return StopAsyncCore(cancellationToken);
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        cancellationTokenSource.Cancel();
    }

    public void Dispose()
    {
        if (!disposed)
        {
            // Running callers might still be using the cancellation token.
            // Mark it canceled but don't dispose of the source while the callers are running.
            // Otherwise, callers would receive ObjectDisposedException when calling token.Register.
            // For now, rely on finalization to clean up _cancellationTokenSource's wait handle (if allocated).
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            disposed = true;
        }
    }

    private async Task StopAsyncCore(CancellationToken cancellationToken)
    {
        await Task.Delay(0, cancellationToken).ConfigureAwait(false);
        var cancellationTaskSource = new TaskCompletionSource<object>();

        await using (cancellationToken.Register(() => cancellationTaskSource.SetCanceled()))
        {
            // Wait for all pending command tasks to complete (or cancellation of the token) before returning.
            await Task.WhenAny(run, cancellationTaskSource.Task).ConfigureAwait(false);
        }

        stopped = true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Allow Start to return immediately without waiting for any initial iteration work to start.
            await Task.Yield();

            var wait = initialWait;

            // Execute tasks one at a time (in a series) until stopped.
            while (!cancellationToken.IsCancellationRequested)
            {
                var cancellationTaskSource = new TaskCompletionSource<object>();

                await using (cancellationToken.Register(() => cancellationTaskSource.SetCanceled()))
                {
                    try
                    {
                        await Task.WhenAny(wait, cancellationTaskSource.Task).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // When Stop fires, don't make it wait for wait before it can return.
                    }
                }

                try
                {
                    if (preExecuteCheck != null && !preExecuteCheck())
                    {
                        await StopAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignored
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var result = await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    wait = result.Wait;
                }
                catch (Exception ex) when (ex.InnerException is OperationCanceledException)
                {
                    // OperationCanceledExceptions coming from storage are wrapped in a StorageException.
                    // We'll handle them all here so they don't have to be managed for every call.
                }
                catch (OperationCanceledException)
                {
                    // Don't fail the task, throw a background exception, or stop looping when a task cancels.
                }
            }
        }
        catch (Exception)
        {
            // Don't capture the exception as a fault of this Task; that would delay any exception reporting until
            // Stop is called, which might never happen.
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(null);
        }
    }
}
