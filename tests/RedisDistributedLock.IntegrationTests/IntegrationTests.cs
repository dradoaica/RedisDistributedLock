using RedisDistributedLock.Abstractions;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace RedisDistributedLock.IntegrationTests;

/// <summary>The same tests for every kind of server: see the classes at the end of the file.</summary>
public abstract class IntegrationTests(ServerFixture server)
{
    private static readonly TimeSpan LongPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShortPeriod = TimeSpan.FromMilliseconds(500);

    private static CancellationToken TestCancelled => TestContext.Current.CancellationToken;

    private static string NewLockId() => $"test-{Guid.NewGuid():N}";

    [Fact]
    public async Task Lock_ExcludesOtherManagersUntilReleased()
    {
        server.SkipIfUnavailable();
        await using var first = server.CreateManager();
        await using var second = server.CreateManager();
        var lockId = NewLockId();

        var acquired = await first.TryLockAsync(lockId, LongPeriod, TestCancelled);

        Assert.NotNull(acquired);
        Assert.Null(await second.TryLockAsync(lockId, LongPeriod, TestCancelled));

        await first.ReleaseLockAsync(acquired, TestCancelled);

        var reacquired = await second.TryLockAsync(lockId, LongPeriod, TestCancelled);
        Assert.NotNull(reacquired);
        await second.ReleaseLockAsync(reacquired, TestCancelled);
    }

    [Fact]
    public async Task Lock_Expires_AndCanThenNeitherBeRenewedNorReleaseTheNewHolder()
    {
        server.SkipIfUnavailable();
        await using var first = server.CreateManager();
        await using var second = server.CreateManager();
        var lockId = NewLockId();
        var stale = await first.TryLockAsync(lockId, ShortPeriod, TestCancelled);
        Assert.NotNull(stale);

        await Task.Delay(ShortPeriod + TimeSpan.FromMilliseconds(200), TestCancelled);
        var current = await second.TryLockAsync(lockId, LongPeriod, TestCancelled);

        Assert.NotNull(current);
        await Assert.ThrowsAsync<DistributedLockLostException>(() => first.RenewAsync(stale, TestCancelled));
        await first.ReleaseLockAsync(stale, TestCancelled);
        Assert.True(await second.RenewAsync(current, TestCancelled));
        Assert.Null(await first.TryLockAsync(lockId, LongPeriod, TestCancelled));

        await second.ReleaseLockAsync(current, TestCancelled);
    }

    [Fact]
    public async Task Renew_ExtendsTheLock()
    {
        server.SkipIfUnavailable();
        await using var first = server.CreateManager();
        await using var second = server.CreateManager();
        var lockId = NewLockId();
        var period = TimeSpan.FromSeconds(2);
        var acquired = await first.TryLockAsync(lockId, period, TestCancelled);
        Assert.NotNull(acquired);

        await Task.Delay(1200, TestCancelled);
        Assert.True(await first.RenewAsync(acquired, TestCancelled));
        await Task.Delay(1200, TestCancelled);

        // Past the original expiry, but still held
        Assert.Null(await second.TryLockAsync(lockId, period, TestCancelled));
        await first.ReleaseLockAsync(acquired, TestCancelled);
    }

    [Fact]
    public async Task RenewableHandle_KeepsTheLockBeyondTheLockPeriod_AndReleasesItWhenDisposed()
    {
        server.SkipIfUnavailable();
        await using var first = server.CreateManager();
        await using var second = server.CreateManager();
        var lockId = NewLockId();
        var period = TimeSpan.FromSeconds(1);
        var handle = await first.TryCreateRenewableLockHandleAsync(
            lockId,
            period,
            period,
            RenewalRetryStrategy.ExponentialBackoff,
            null,
            TestCancelled
        );
        Assert.NotNull(handle);
        var lockLost = handle.LockLost;

        await Task.Delay(period * 3, TestCancelled);

        Assert.False(lockLost.IsCancellationRequested);
        Assert.Null(await second.TryLockAsync(lockId, period, TestCancelled));

        await handle.DisposeAsync();

        var reacquired = await second.TryLockAsync(lockId, period, TestCancelled);
        Assert.NotNull(reacquired);
        Assert.False(lockLost.IsCancellationRequested);
        await second.ReleaseLockAsync(reacquired, TestCancelled);
    }

    [Fact]
    public async Task Lock_RacedByManyCallers_HasOneWinner()
    {
        server.SkipIfUnavailable();
        var managers = Enumerable.Range(0, 4).Select(_ => server.CreateManager()).ToArray();
        try
        {
            var lockId = NewLockId();

            var results = await Task.WhenAll(
                Enumerable.Range(0, 40)
                    .Select(i => Task.Run(() => managers[i % managers.Length]
                            .TryLockAsync(lockId, LongPeriod, TestCancelled)
                        )
                    )
            );

            var winner = Assert.Single(results, result => result != null);
            await managers[0].ReleaseLockAsync(winner!, TestCancelled);
        }
        finally
        {
            foreach (var manager in managers)
            {
                await manager.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Lock_HeldInTurnByManyWorkers_IsNeverHeldByTwoAtOnce()
    {
        server.SkipIfUnavailable();
        var lockId = NewLockId();
        var holders = 0;
        var overlaps = 0;
        var turns = 0;

        async Task WorkAsync()
        {
            await using var manager = server.CreateManager();
            for (var turn = 0; turn < 5; turn++)
            {
                IDistributedLock? acquired;
                while ((acquired = await manager.TryLockAsync(lockId, LongPeriod, TestCancelled)) == null)
                {
                }

                if (Interlocked.Increment(ref holders) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                await Task.Delay(5, TestCancelled);
                Interlocked.Increment(ref turns);
                Interlocked.Decrement(ref holders);
                await manager.ReleaseLockAsync(acquired, TestCancelled);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(WorkAsync)));

        Assert.Equal(0, overlaps);
        Assert.Equal(30, turns);
    }

    [Fact]
    public async Task Lock_WhenTheSameMasterIsConfiguredTwice_Throws()
    {
        server.SkipIfUnavailable();
        // The same server under another name; a plain string comparison doesn't see it
        var first = server.ConnectionStrings[0];
        Assert.SkipUnless(
            first.Contains("localhost"),
            "Needs a master addressed as localhost to address it by IP too."
        );
        await using var manager = server.CreateManager(first.Replace("localhost", "127.0.0.1"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryLockAsync(
                NewLockId(),
                LongPeriod,
                TestCancelled
            )
        );

        Assert.Contains("same server", exception.Message);
    }

    [Fact]
    public async Task Lock_WithAnUnreachableMaster_StillAcquiresWithAShortLockPeriod()
    {
        server.SkipIfUnavailable();
        // Nothing listens on port 1. With the reachable masters it must still leave a majority.
        var unreachable = Enumerable.Range(0, (server.ConnectionStrings.Count - 1) / 2)
            .Select(i => $"localhost:1,connectTimeout=500,name=unreachable{i}")
            .ToArray();
        Assert.SkipWhen(unreachable.Length == 0, "Needs at least three masters to tolerate an unreachable one.");
        await using var manager = server.CreateManager(unreachable);
        var lockId = NewLockId();

        var acquired = await manager.TryLockAsync(lockId, TimeSpan.FromSeconds(1), TestCancelled);

        Assert.NotNull(acquired);
        Assert.True(await manager.RenewAsync(acquired, TestCancelled));
        await manager.ReleaseLockAsync(acquired, TestCancelled);
    }
}

public class RedisIntegrationTests(RedisFixture redis) : IntegrationTests(redis), IClassFixture<RedisFixture>;

public class ValkeyIntegrationTests(ValkeyFixture valkey) : IntegrationTests(valkey), IClassFixture<ValkeyFixture>;
