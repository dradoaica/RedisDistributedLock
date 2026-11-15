using Microsoft.Extensions.Time.Testing;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RedisDistributedLock.UnitTests;

/// <summary>
///     Locks, timeouts and renewals run on a clock that only the test moves, so no test depends on how fast the
///     machine is: a lock period can't run out because a test was slow.
/// </summary>
internal static class FakeTime
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    public static (RedisDistributedLockManager Manager, FakeRedisInstance[] Instances, FakeTimeProvider Time) Create(
        int instanceCount = 3,
        Action<RedisDistributedLockOptions>? configure = null
    )
    {
        var time = new FakeTimeProvider();
        var instances = Enumerable.Range(1, instanceCount)
            .Select(i => new FakeRedisInstance($"redis{i}", time))
            .ToArray();
        // No retries by default, so a failure is the outcome of exactly one round
        var options = new RedisDistributedLockOptions
        {
            RetryCount = 0,
            TimeProvider = time,
        };
        configure?.Invoke(options);
        return (new RedisDistributedLockManager(instances, options), instances, time);
    }

    /// <summary>
    ///     Moves the clock forward in small steps until the condition holds or the duration has passed. After each step
    ///     the work that the step triggered in the background (a renewal, a timeout) gets real time to run, which is why the
    ///     steps are much smaller than the periods the tests use.
    /// </summary>
    /// <returns>Whether the condition held in the end.</returns>
    public static async Task<bool> AdvanceUntilAsync(
        this FakeTimeProvider time,
        Func<bool> condition,
        TimeSpan duration
    )
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Step)
        {
            if (condition())
            {
                return true;
            }

            time.Advance(Step);
            await Task.Delay(1);
        }

        return condition();
    }

    public static Task AdvanceAsync(this FakeTimeProvider time, TimeSpan duration) =>
        time.AdvanceUntilAsync(() => false, duration);

    /// <summary>Completes an operation that waits on the clock, e.g. for an instance that doesn't answer.</summary>
    public static async Task<T> AdvanceUntilCompletedAsync<T>(
        this FakeTimeProvider time,
        Task<T> task,
        TimeSpan duration
    )
    {
        await time.AdvanceUntilAsync(() => task.IsCompleted, duration);
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    ///     Disposes of something that may have to wait on the clock first, e.g. a handle whose renewal is waiting for an
    ///     instance that doesn't answer.
    /// </summary>
    public static async Task DisposeAsync(this FakeTimeProvider time, IAsyncDisposable disposable)
    {
        var disposing = disposable.DisposeAsync().AsTask();
        await time.AdvanceUntilAsync(() => disposing.IsCompleted, TimeSpan.FromMinutes(10));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
