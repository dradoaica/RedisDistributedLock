using Microsoft.Extensions.Options;
using RedisDistributedLock.Abstractions;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RedisDistributedLock.UnitTests;

public class RedisDistributedLockManagerTests
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PastPeriod = Period + TimeSpan.FromSeconds(1);

    private static CancellationToken TestCancelled => TestContext.Current.CancellationToken;

    public static TheoryData<Action<RedisDistributedLockOptions>, string> InvalidOptions => new()
    {
        {
            options => options.RedisConnectionString = null, "connection string"
        },
        {
            options => options.RedisConnectionStrings = null!,
            nameof(RedisDistributedLockOptions.RedisConnectionStrings)
        },
        {
            options => options.ConnectionMultiplexers = null!,
            nameof(RedisDistributedLockOptions.ConnectionMultiplexers)
        },
        {
            options => options.LockPrefix = null!, nameof(RedisDistributedLockOptions.LockPrefix)
        },
        {
            options => options.InstanceTimeout = TimeSpan.Zero, nameof(RedisDistributedLockOptions.InstanceTimeout)
        },
        {
            options => options.InstanceTimeout = TimeSpan.FromSeconds(-5),
            nameof(RedisDistributedLockOptions.InstanceTimeout)
        },
        {
            options => options.RetryCount = -1, nameof(RedisDistributedLockOptions.RetryCount)
        },
        {
            options => options.RetryDelay = TimeSpan.FromSeconds(-1), nameof(RedisDistributedLockOptions.RetryDelay)
        },
        {
            options => options.TimeProvider = null!, nameof(RedisDistributedLockOptions.TimeProvider)
        },
    };

    [Fact]
    public async Task TryLock_AcquiresOnEveryInstance_AndExcludesOthersUntilReleased()
    {
        var (manager, instances, _) = FakeTime.Create();

        var first = await manager.TryLockAsync("a", Period, TestCancelled);

        Assert.NotNull(first);
        Assert.Equal("a", first.LockId);
        Assert.Equal("lock_a", first.Key);
        Assert.All(instances, instance => Assert.True(instance.HasKey("lock_a")));
        Assert.Null(await manager.TryLockAsync("a", Period, TestCancelled));

        await manager.ReleaseLockAsync(first, TestCancelled);

        Assert.All(instances, instance => Assert.False(instance.HasKey("lock_a")));
        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task Lock_ReportsItsValidity_UntilItExpiresOrIsReleased()
    {
        var (manager, _, time) = FakeTime.Create();

        var expiring = await manager.TryLockAsync("a", Period, TestCancelled);
        var released = await manager.TryLockAsync("b", Period, TestCancelled);
        Assert.NotNull(expiring);
        Assert.NotNull(released);

        // Less than the lock period by the allowance for clock drift
        Assert.True(expiring.IsValid);
        Assert.InRange(expiring.RemainingValidity, Period - TimeSpan.FromSeconds(1), Period);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.InRange(
            expiring.RemainingValidity,
            Period - TimeSpan.FromSeconds(11),
            Period - TimeSpan.FromSeconds(10)
        );

        await manager.ReleaseLockAsync(released, TestCancelled);
        Assert.False(released.IsValid);
        Assert.Equal(TimeSpan.Zero, released.RemainingValidity);

        time.Advance(Period);
        Assert.False(expiring.IsValid);
        Assert.Equal(TimeSpan.Zero, expiring.RemainingValidity);
    }

    [Fact]
    public async Task TryLock_RacedByManyCallers_HasOneWinner()
    {
        var (manager, _, _) = FakeTime.Create();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => Task.Run(() => manager.TryLockAsync("a", Period, TestCancelled)))
        );

        Assert.Single(results, result => result != null);
    }

    [Fact]
    public async Task TryLock_FailedRound_IsRetried()
    {
        var (manager, _, time) = FakeTime.Create(
            configure: options =>
            {
                options.RetryCount = 1000;
                options.RetryDelay = TimeSpan.FromSeconds(1);
            }
        );
        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));

        // The first rounds fail; a later one succeeds once the first lock expired
        var retrying = manager.TryLockAsync("a", Period, TestCancelled);

        Assert.NotNull(await time.AdvanceUntilCompletedAsync(retrying, Period * 3));
    }

    [Fact]
    public async Task TryLock_WithMinorityDown_Acquires()
    {
        var (manager, instances, _) = FakeTime.Create();
        instances[2].Mode = FakeRedisMode.Down;

        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task TryLock_WithMajorityDown_FailsAndLeavesNothingLocked()
    {
        var (manager, instances, _) = FakeTime.Create();
        instances[1].Mode = FakeRedisMode.Down;
        instances[2].Mode = FakeRedisMode.Down;

        Assert.Null(await manager.TryLockAsync("a", Period, TestCancelled));
        Assert.False(instances[0].HasKey("lock_a"));
    }

    [Fact]
    public async Task TryLock_WithHungInstance_AcquiresWithMostOfTheLockPeriodLeft()
    {
        var (manager, instances, time) = FakeTime.Create();
        instances[2].Mode = FakeRedisMode.Hung;

        var acquired = await time.AdvanceUntilCompletedAsync(manager.TryLockAsync("a", Period, TestCancelled), Period);

        // The hung instance only cost the instance timeout, a quarter of the lock period by default
        Assert.NotNull(acquired);
        Assert.InRange(acquired.RemainingValidity, Period * 0.7, Period * 0.75);
    }

    [Fact]
    public async Task TryLock_InstanceTimeoutLongerThanTheLockPeriod_IsCappedToHalfOfIt()
    {
        var (manager, instances, time) = FakeTime.Create(configure: options => options.InstanceTimeout = Period * 10);
        instances[2].Mode = FakeRedisMode.Hung;

        var acquired = await time.AdvanceUntilCompletedAsync(manager.TryLockAsync("a", Period, TestCancelled), Period);

        Assert.NotNull(acquired);
        Assert.InRange(acquired.RemainingValidity, Period * 0.45, Period * 0.5);
    }

    [Fact]
    public async Task TryLock_AfterUnreleasedLockExpired_Acquires()
    {
        var (manager, _, time) = FakeTime.Create();
        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));

        time.Advance(PastPeriod);

        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task TryLock_IdThatStartsWithThePrefix_IsADifferentLock()
    {
        var (manager, _, _) = FakeTime.Create();

        Assert.NotNull(await manager.TryLockAsync("orders", Period, TestCancelled));
        Assert.NotNull(await manager.TryLockAsync("lock_orders", Period, TestCancelled));
    }

    [Fact]
    public async Task TryLock_Cancelled_Throws()
    {
        var (manager, _, _) = FakeTime.Create();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.TryLockAsync(
                "a",
                Period,
                new CancellationToken(true)
            )
        );
    }

    [Fact]
    public async Task TryLock_InvalidArguments_Throw()
    {
        var (manager, _, _) = FakeTime.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryLockAsync("", Period, TestCancelled));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.TryLockAsync(
                "a",
                TimeSpan.Zero,
                TestCancelled
            )
        );
    }

    [Fact]
    public async Task TryLock_WhenTwoInstancesAreTheSameServer_ThrowsAndLeavesNothingLocked()
    {
        var (manager, instances, _) = FakeTime.Create();
        instances[1].ServerId = instances[0].ServerId;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryLockAsync(
                "a",
                Period,
                TestCancelled
            )
        );

        Assert.Contains("redis1", exception.Message);
        Assert.Contains("redis2", exception.Message);
        Assert.All(instances, instance => Assert.False(instance.HasKey("lock_a")));
    }

    [Fact]
    public async Task TryLock_WhenAnInstanceThatWasDownTurnsOutToBeTheSameServer_Throws()
    {
        var (manager, instances, _) = FakeTime.Create();
        instances[2].ServerId = instances[0].ServerId;
        instances[2].Mode = FakeRedisMode.Down;
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        await manager.ReleaseLockAsync(acquired, TestCancelled);

        instances[2].Mode = FakeRedisMode.Healthy;

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task Renew_ExtendsTheLock()
    {
        var (manager, _, time) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);

        time.Advance(Period * 0.6);
        Assert.True(await manager.RenewAsync(acquired, TestCancelled));
        time.Advance(Period * 0.6);

        // Past the original expiry, but still held
        Assert.True(acquired.IsValid);
        Assert.Null(await manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task Renew_AfterExpiry_ThrowsLockLost()
    {
        var (manager, _, time) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);

        time.Advance(PastPeriod);

        var exception = await Assert.ThrowsAsync<DistributedLockLostException>(() => manager.RenewAsync(
                acquired,
                TestCancelled
            )
        );
        Assert.Equal("a", exception.LockId);
    }

    [Fact]
    public async Task Renew_WhenMajorityLostTheKey_ThrowsLockLostAndReleasesTheRest()
    {
        var (manager, instances, _) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        instances[0].Clear();
        instances[1].Clear();

        await Assert.ThrowsAsync<DistributedLockLostException>(() => manager.RenewAsync(acquired, TestCancelled));
        Assert.False(instances[2].HasKey("lock_a"));
    }

    [Fact]
    public async Task Renew_WhenMinorityLostTheKey_StillExtends()
    {
        var (manager, instances, _) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        instances[0].Clear();

        Assert.True(await manager.RenewAsync(acquired, TestCancelled));
    }

    [Fact]
    public async Task Renew_WhenMajorityUnreachable_FailsUntilTheLockExpiresThenThrowsLockLost()
    {
        var (manager, instances, time) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        instances[1].Mode = FakeRedisMode.Down;
        instances[2].Mode = FakeRedisMode.Down;

        // Not lost yet: the instances may come back before the lock expires
        Assert.False(await manager.RenewAsync(acquired, TestCancelled));

        time.Advance(PastPeriod);
        await Assert.ThrowsAsync<DistributedLockLostException>(() => manager.RenewAsync(acquired, TestCancelled));
    }

    [Fact]
    public async Task Renew_WhenInstancesDoNotAnswer_GivesUpByTheTimeTheLockExpires()
    {
        var (manager, instances, time) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        Assert.All(instances, instance => instance.Mode = FakeRedisMode.Hung);
        // Less time left than the instance timeout, a quarter of the lock period by default
        time.Advance(Period * 0.9);
        var started = time.GetTimestamp();

        var renewing = manager.RenewAsync(acquired, TestCancelled);
        await time.AdvanceUntilAsync(() => renewing.IsCompleted, Period);

        await Assert.ThrowsAsync<DistributedLockLostException>(() => renewing);
        // Up to the expiry for the renewal itself, then one round to release what may be left of the lock
        Assert.InRange(time.GetElapsedTime(started), TimeSpan.Zero, Period * 0.4);
    }

    [Fact]
    public async Task Release_OfALostLock_DoesNotReleaseTheNewHolder()
    {
        var (manager, _, time) = FakeTime.Create();
        var stale = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(stale);
        time.Advance(PastPeriod);
        Assert.NotNull(await manager.TryLockAsync("a", Period, TestCancelled));

        await manager.ReleaseLockAsync(stale, TestCancelled);

        Assert.Null(await manager.TryLockAsync("a", Period, TestCancelled));
    }

    [Fact]
    public async Task Release_WhenInstancesAreUnreachable_DoesNotThrow()
    {
        var (manager, instances, _) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);
        Assert.All(instances, instance => instance.Mode = FakeRedisMode.Down);

        await manager.ReleaseLockAsync(acquired, TestCancelled);
    }

    [Fact]
    public async Task Release_OfAForeignLock_Throws()
    {
        var (manager, _, _) = FakeTime.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ReleaseLockAsync(new ForeignLock(), TestCancelled));
    }

    [Fact]
    public async Task Manager_Disposed_ThrowsButStillAcceptsReleases()
    {
        var (manager, _, _) = FakeTime.Create();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);
        Assert.NotNull(acquired);

        await manager.DisposeAsync();
        manager.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.TryLockAsync("b", Period, TestCancelled));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.RenewAsync(acquired, TestCancelled));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.TryCreateRenewableLockHandleAsync(
                "b",
                Period,
                Period,
                RenewalRetryStrategy.ExponentialBackoff,
                null,
                TestCancelled
            )
        );
        // A lock is typically released in a finally block, where throwing would hide the original exception
        await manager.ReleaseLockAsync(acquired, TestCancelled);
        Assert.False(acquired.IsValid);
    }

    [Fact]
    public async Task Manager_WithAnUnreachableMaster_ConstructsWithoutWaitingForIt_AndFailsFast()
    {
        // Nothing listens on port 1
        var options = new RedisDistributedLockOptions
        {
            RedisConnectionString = "localhost:1,connectTimeout=500",
            RetryCount = 0,
            InstanceTimeout = TimeSpan.FromSeconds(20),
        };
        await using var manager = new RedisDistributedLockManager(Options.Create(options));

        var stopwatch = Stopwatch.StartNew();
        var acquired = await manager.TryLockAsync("a", Period, TestCancelled);

        Assert.Null(acquired);
        // Neither the instance timeout nor the client's own timeout had to run out
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Manager_WithInvalidOptions_Throws(
        Action<RedisDistributedLockOptions> invalidate,
        string expectedInMessage
    )
    {
        var options = new RedisDistributedLockOptions
        {
            RedisConnectionString = "localhost:1",
        };
        invalidate(options);

        var exception = Assert.Throws<OptionsValidationException>(() =>
            new RedisDistributedLockManager(Options.Create(options))
        );

        Assert.Contains(expectedInMessage, exception.Message);
    }

    private sealed class ForeignLock : IDistributedLock
    {
        public string LockId => "foreign";

        public string Key => "foreign";

        public TimeSpan RemainingValidity => TimeSpan.Zero;

        public bool IsValid => false;
    }
}
