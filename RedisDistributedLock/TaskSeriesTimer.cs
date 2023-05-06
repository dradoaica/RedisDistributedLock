namespace RedisDistributedLock;

using System;
using System.Threading;
using System.Threading.Tasks;
using Abstractions;

/// <summary>Represents a timer that executes one task after another in a series.</summary>
public sealed class TaskSeriesTimer : ITaskSeriesTimer
{
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly ITaskSeriesCommand command;
    private readonly Task initialWait;
    private readonly Func<bool>? preExecuteCheck;
    private bool disposed;
    private Task run;
    private bool started;
    private bool stopped;

    public TaskSeriesTimer(ITaskSeriesCommand command, Task initialWait, Func<bool>? preExecuteCheck = null)
    {
        if (command == null)
        {
            throw new ArgumentNullException("command");
        }

        if (initialWait == null)
        {
            throw new ArgumentNullException("initialWait");
        }

        this.command = command;
        this.initialWait = initialWait;
        this.preExecuteCheck = preExecuteCheck;
        this.cancellationTokenSource = new CancellationTokenSource();
    }

    public void Start()
    {
        this.ThrowIfDisposed();

        if (this.started)
        {
            throw new InvalidOperationException("The timer has already been started; it cannot be restarted.");
        }

        this.run = this.RunAsync(this.cancellationTokenSource.Token);
        this.started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.ThrowIfDisposed();

        if (!this.started)
        {
            throw new InvalidOperationException("The timer has not yet been started.");
        }

        if (this.stopped)
        {
            throw new InvalidOperationException("The timer has already been stopped.");
        }

        this.cancellationTokenSource.Cancel();
        return this.StopAsyncCore(cancellationToken);
    }

    public void Cancel()
    {
        this.ThrowIfDisposed();
        this.cancellationTokenSource.Cancel();
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            // Running callers might still be using the cancellation token.
            // Mark it canceled but don't dispose of the source while the callers are running.
            // Otherwise, callers would receive ObjectDisposedException when calling token.Register.
            // For now, rely on finalization to clean up _cancellationTokenSource's wait handle (if allocated).
            this.cancellationTokenSource.Cancel();
            this.cancellationTokenSource.Dispose();

            this.disposed = true;
        }
    }

    private async Task StopAsyncCore(CancellationToken cancellationToken)
    {
        await Task.Delay(0);
        var cancellationTaskSource = new TaskCompletionSource<object>();

        using (cancellationToken.Register(() => cancellationTaskSource.SetCanceled()))
        {
            // Wait for all pending command tasks to complete (or cancellation of the token) before returning.
            await Task.WhenAny(this.run, cancellationTaskSource.Task);
        }

        this.stopped = true;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Allow Start to return immediately without waiting for any initial iteration work to start.
            await Task.Yield();

            var wait = this.initialWait;

            // Execute tasks one at a time (in a series) until stopped.
            while (!cancellationToken.IsCancellationRequested)
            {
                var cancellationTaskSource = new TaskCompletionSource<object>();

                using (cancellationToken.Register(() => cancellationTaskSource.SetCanceled()))
                {
                    try
                    {
                        await Task.WhenAny(wait, cancellationTaskSource.Task);
                    }
                    catch (OperationCanceledException)
                    {
                        // When Stop fires, don't make it wait for wait before it can return.
                    }
                }

                try
                {
                    if (this.preExecuteCheck != null && !this.preExecuteCheck())
                    {
                        await this.StopAsync(CancellationToken.None);
                    }
                }
                catch
                {
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var result = await this.command.ExecuteAsync(cancellationToken);
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
        if (this.disposed)
        {
            throw new ObjectDisposedException(null);
        }
    }
}
