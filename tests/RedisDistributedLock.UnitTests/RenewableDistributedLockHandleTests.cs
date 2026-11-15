using RedisDistributedLock.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RedisDistributedLock.UnitTests;

public class RenewableDistributedLockHandleTests
{
    private static readonly TimeSpan LockPeriod = TimeSpan.FromSeconds(60);

    // Renewed every 5 seconds
    private static readonly TimeSpan LeasePeriod = TimeSpan.FromSeconds(10);

    private static CancellationToken TestCancelled => TestContext.Current.CancellationToken;

    private static Task<IRenewableDistributedLockHandle?> CreateHandleAsync(
        RedisDistributedLockManager manager,
        RenewalRetryStrategy retryStrategy = RenewalRetryStrategy.ExponentialBackoff,
        Func<bool>? preExecuteCheck = null
    ) => manager.TryCreateRenewableLockHandleAsync(
        "a",
        LockPeriod,
        LeasePeriod,
        retryStrategy,
        preExecuteCheck,
        TestCancelled
    );

    [Theory]
    [InlineData(RenewalRetryStrategy.ExponentialBackoff)]
    [InlineData(RenewalRetryStrategy.Speedup)]
    public async Task Handle_KeepsTheLockBeyondTheLockPeriod_AndReleasesItWhenDisposed(
        RenewalRetryStrategy retryStrategy
    )
    {
        var (manager, instances, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager, retryStrategy);
        Assert.NotNull(handle);
        var lockLost = handle.LockLost;

        await time.AdvanceAsync(LockPeriod * 3);

        Assert.False(lockLost.IsCancellationRequested);
        Assert.True(handle.InnerLock.IsValid);
        Assert.InRange(instances[0].Extensions, 30, 40);
        Assert.Null(await manager.TryLockAsync("a", LockPeriod, TestCancelled));

        await time.DisposeAsync(handle);
        await handle.DisposeAsync();

        Assert.False(handle.InnerLock.IsValid);
        Assert.NotNull(await manager.TryLockAsync("a", LockPeriod, TestCancelled));
        Assert.False(lockLost.IsCancellationRequested);
    }

    [Fact]
    public async Task Handle_SignalsLockLost_WhenTheLockIsLost()
    {
        var (manager, instances, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager);
        Assert.NotNull(handle);

        Assert.All(instances, instance => instance.Clear());

        // By the next renewal
        Assert.True(await time.AdvanceUntilAsync(() => handle.LockLost.IsCancellationRequested, LeasePeriod));

        await time.DisposeAsync(handle);
    }

    [Theory]
    [InlineData(FakeRedisMode.Down)]
    [InlineData(FakeRedisMode.Hung)]
    public async Task Handle_SignalsLockLostByTheTimeTheLockExpires_WhenAQuorumStaysUnreachable(FakeRedisMode outage)
    {
        var (manager, instances, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager);
        Assert.NotNull(handle);
        await time.AdvanceAsync(LockPeriod);
        Assert.True(handle.InnerLock.IsValid);

        instances[0].Mode = outage;
        instances[1].Mode = outage;
        var started = time.GetTimestamp();

        Assert.True(await time.AdvanceUntilAsync(() => handle.LockLost.IsCancellationRequested, LockPeriod * 2));
        // Not earlier than necessary, and not after the lock expired: the last renewal was at most half a lease
        // period before the outage
        Assert.False(handle.InnerLock.IsValid);
        Assert.InRange(time.GetElapsedTime(started), LockPeriod - LeasePeriod, LockPeriod);

        await time.DisposeAsync(handle);
    }

    [Fact]
    public async Task Handle_StopsRenewing_OnceTheLockIsLost()
    {
        var (manager, instances, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager);
        Assert.NotNull(handle);
        Assert.All(instances, instance => instance.Mode = FakeRedisMode.Hung);
        Assert.True(await time.AdvanceUntilAsync(() => handle.LockLost.IsCancellationRequested, LockPeriod * 2));

        // The instances still hold the key for the rest of its time to live: a renewal would bring the lock back
        Assert.All(instances, instance => instance.Mode = FakeRedisMode.Healthy);
        var extensions = instances[0].Extensions;
        await time.AdvanceAsync(LockPeriod * 2);

        Assert.Equal(extensions, instances[0].Extensions);
        Assert.False(handle.InnerLock.IsValid);
        Assert.NotNull(await manager.TryLockAsync("a", LockPeriod, TestCancelled));

        await time.DisposeAsync(handle);
    }

    [Theory]
    [InlineData(FakeRedisMode.Down)]
    [InlineData(FakeRedisMode.Hung)]
    public async Task Handle_SurvivesAMinorityOutage(FakeRedisMode outage)
    {
        var (manager, instances, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager);
        Assert.NotNull(handle);

        instances[0].Mode = outage;
        await time.AdvanceAsync(LockPeriod * 3);

        Assert.False(handle.LockLost.IsCancellationRequested);
        Assert.True(handle.InnerLock.IsValid);
        // Waits on the clock for the instance that doesn't answer
        var competing = manager.TryLockAsync("a", LockPeriod, TestCancelled);
        Assert.Null(await time.AdvanceUntilCompletedAsync(competing, LockPeriod));

        await time.DisposeAsync(handle);
    }

    [Fact]
    public async Task Handle_ReleasedThroughTheManager_DoesNotSignalLockLost()
    {
        var (manager, _, time) = FakeTime.Create();
        var handle = await CreateHandleAsync(manager);
        Assert.NotNull(handle);

        await manager.ReleaseLockAsync(handle.InnerLock, TestCancelled);
        await time.AdvanceAsync(LockPeriod * 2);

        Assert.False(handle.LockLost.IsCancellationRequested);

        await time.DisposeAsync(handle);
    }

    [Fact]
    public async Task Handle_WhenThePreExecuteCheckFails_StopsRenewingSignalsLockLostAndLeavesTheLockToExpire()
    {
        var (manager, instances, time) = FakeTime.Create();
        var keepRenewing = true;
        var handle = await CreateHandleAsync(manager, preExecuteCheck: () => Volatile.Read(ref keepRenewing));
        Assert.NotNull(handle);
        await time.AdvanceAsync(LockPeriod * 2);
        Assert.False(handle.LockLost.IsCancellationRequested);

        Volatile.Write(ref keepRenewing, false);

        // By the next renewal
        Assert.True(await time.AdvanceUntilAsync(() => handle.LockLost.IsCancellationRequested, LeasePeriod));
        var extensions = instances[0].Extensions;
        Assert.Null(await manager.TryLockAsync("a", LockPeriod, TestCancelled));

        await time.AdvanceAsync(LockPeriod);

        Assert.Equal(extensions, instances[0].Extensions);
        Assert.NotNull(await manager.TryLockAsync("a", LockPeriod, TestCancelled));

        await time.DisposeAsync(handle);
    }

    [Fact]
    public async Task Create_WhenTheLockIsHeld_ReturnsNull()
    {
        var (manager, _, _) = FakeTime.Create();
        Assert.NotNull(await manager.TryLockAsync("a", LockPeriod, TestCancelled));

        Assert.Null(await CreateHandleAsync(manager));
    }

    [Fact]
    public async Task Create_WithALeaseThatLeavesNoTimeToRetryARenewal_Throws()
    {
        var (manager, _, _) = FakeTime.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryCreateRenewableLockHandleAsync(
                "a",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1.5),
                RenewalRetryStrategy.ExponentialBackoff,
                null,
                TestCancelled
            )
        );
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.TryCreateRenewableLockHandleAsync(
                "a",
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                RenewalRetryStrategy.ExponentialBackoff,
                null,
                TestCancelled
            )
        );
    }
}
