using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RedisDistributedLock.UnitTests;

public class TaskSeriesTimerTests
{
    private static CancellationToken TestCancelled => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Timer_ExecutesTheCommandRepeatedlyUntilStopped()
    {
        var command = new CountingCommand(TimeSpan.FromMilliseconds(10));
        using var timer = new TaskSeriesTimer(command, Task.CompletedTask);

        timer.Start();
        await command.WaitForExecutionsAsync(3);
        await timer.StopAsync(TestCancelled);

        var executions = command.Executions;
        await Task.Delay(100, TestCancelled);
        Assert.Equal(executions, command.Executions);
    }

    [Fact]
    public async Task Timer_WaitsForTheInitialWait()
    {
        var command = new CountingCommand(TimeSpan.FromMilliseconds(10));
        var initialWait = new TaskCompletionSource<object?>();
        using var timer = new TaskSeriesTimer(command, initialWait.Task);

        timer.Start();
        await Task.Delay(100, TestCancelled);
        Assert.Equal(0, command.Executions);

        initialWait.SetResult(null);
        await command.WaitForExecutionsAsync(1);
        await timer.StopAsync(TestCancelled);
    }

    [Fact]
    public async Task Timer_WhenThePreExecuteCheckFails_StopsWithoutExecuting_AndCanStillBeStopped()
    {
        var command = new CountingCommand(TimeSpan.FromMilliseconds(10));
        using var timer = new TaskSeriesTimer(command, Task.CompletedTask, () => false);

        timer.Start();
        await Task.Delay(100, TestCancelled);
        Assert.Equal(0, command.Executions);

        await timer.StopAsync(TestCancelled);
    }

    [Fact]
    public async Task Timer_WhenTheCommandThrows_StopsExecuting()
    {
        var command = new CountingCommand(TimeSpan.FromMilliseconds(10))
        {
            ThrowOnExecution = 2,
        };
        using var timer = new TaskSeriesTimer(command, Task.CompletedTask);

        timer.Start();
        await command.WaitForExecutionsAsync(2);
        await Task.Delay(100, TestCancelled);

        Assert.Equal(2, command.Executions);
        await timer.StopAsync(TestCancelled);
    }

    [Fact]
    public async Task Timer_MisusedLifecycle_Throws()
    {
        var timer = new TaskSeriesTimer(new CountingCommand(TimeSpan.FromMilliseconds(10)), Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => timer.StopAsync(TestCancelled));
        timer.Start();
        Assert.Throws<InvalidOperationException>(timer.Start);
        await timer.StopAsync(TestCancelled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => timer.StopAsync(TestCancelled));
        timer.Dispose();
        timer.Dispose();
        Assert.Throws<ObjectDisposedException>(timer.Cancel);
    }

    private sealed class CountingCommand(TimeSpan delay) : ITaskSeriesCommand
    {
        private readonly SemaphoreSlim executed = new(0);
        private int executions;

        public int Executions => Volatile.Read(ref executions);

        public int? ThrowOnExecution { get; init; }

        public Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            var execution = Interlocked.Increment(ref executions);
            executed.Release();
            if (execution == ThrowOnExecution)
            {
                throw new InvalidOperationException("The command failed.");
            }

            return Task.FromResult(new TaskSeriesCommandResult(Task.Delay(delay, cancellationToken)));
        }

        public async Task WaitForExecutionsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await executed.WaitAsync(TimeSpan.FromSeconds(5)), "The command wasn't executed in time.");
            }
        }
    }
}
