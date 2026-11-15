using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisDistributedLock.Abstractions;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public class RedisDistributedLockManager : IDistributedLockManager, IDisposable, IAsyncDisposable
{
    private readonly string lockPrefix;
    private readonly ILogger logger;
    private readonly List<Task<IConnectionMultiplexer>> ownedMultiplexers = [];
    private readonly RedLock redLock;
    private readonly TimeProvider timeProvider;
    private int disposed;

    public RedisDistributedLockManager(IOptions<RedisDistributedLockOptions> redisDistributedLockOptions)
        : this(redisDistributedLockOptions, null)
    {
    }

    public RedisDistributedLockManager(
        IOptions<RedisDistributedLockOptions> redisDistributedLockOptions,
        ILogger<RedisDistributedLockManager>? logger
    )
    {
        var options = redisDistributedLockOptions.Value;
        RedisDistributedLockOptionsValidator.ThrowIfInvalid(options);
        this.logger = logger ?? (ILogger)NullLogger.Instance;
        lockPrefix = options.LockPrefix;
        timeProvider = options.TimeProvider;

        var redisInstances = new List<IRedisInstance>();
        try
        {
            foreach (var redisConnectionString in GetConnectionStrings(options))
            {
                var configuration = GetConfiguration(redisConnectionString);
                // Not awaited: the masters connect in the background, all at once, and one that can't be reached
                // doesn't hold up the others or whoever resolves the manager
                var multiplexer = ConnectAsync(configuration);
                ownedMultiplexers.Add(multiplexer);
                redisInstances.Add(new RedisInstance(multiplexer, configuration.EndPoints));
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        redisInstances.AddRange(options.ConnectionMultiplexers.Select(multiplexer => new RedisInstance(multiplexer)));
        redLock = new RedLock(redisInstances, options, this.logger);
    }

    internal RedisDistributedLockManager(
        IReadOnlyList<IRedisInstance> redisInstances,
        RedisDistributedLockOptions options,
        ILogger? logger = null
    )
    {
        this.logger = logger ?? NullLogger.Instance;
        lockPrefix = options.LockPrefix;
        timeProvider = options.TimeProvider;
        redLock = new RedLock(redisInstances, options, this.logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var multiplexer in ownedMultiplexers)
        {
            try
            {
                await (await multiplexer.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Never connected, so there is nothing to dispose of
            }
        }

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var multiplexer in ownedMultiplexers)
        {
            // Without blocking on a connection that is still being established
            multiplexer.ContinueWith(
                static connecting =>
                {
                    if (connecting.Status == TaskStatus.RanToCompletion)
                    {
                        connecting.Result.Dispose();
                    }
                    else
                    {
                        _ = connecting.Exception;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        GC.SuppressFinalize(this);
    }

    public async Task<IDistributedLock?> TryLockAsync(
        string lockId,
        TimeSpan lockPeriod,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateLock(lockId, lockPeriod);
        return await redLock.LockAsync(lockId, GetKey(lockId), lockPeriod, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IRenewableDistributedLockHandle?> TryCreateRenewableLockHandleAsync(
        string lockId,
        TimeSpan lockPeriod,
        TimeSpan leasePeriod,
        RenewalRetryStrategy retryStrategy,
        Func<bool>? preExecuteCheck,
        CancellationToken cancellationToken
    )
    {
        ThrowIfDisposed();
        ValidateLock(lockId, lockPeriod);
        if (leasePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leasePeriod), "The lease period must be positive.");
        }

        // The lease is renewed every leasePeriod / 2, which leaves at least half of the lock period for retries
        if (leasePeriod > lockPeriod)
        {
            throw new ArgumentException(
                "The lease period must not be longer than the lock period, otherwise a renewal that fails can't be retried before the lock expires.",
                nameof(leasePeriod)
            );
        }

        var distributedLock = await redLock.LockAsync(lockId, GetKey(lockId), lockPeriod, cancellationToken)
            .ConfigureAwait(false);
        if (distributedLock == null)
        {
            return null;
        }

        return RenewableDistributedLockHandleFactory.CreateRenewableLockHandle(
            leasePeriod,
            retryStrategy,
            this,
            distributedLock,
            preExecuteCheck,
            timeProvider,
            logger
        );
    }

    public async Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        var entry = GetEntry(lockHandle);
        // Before unlocking, so a renewal that finds the key gone doesn't report the lock as lost
        entry.MarkReleased();
        if (Volatile.Read(ref disposed) != 0)
        {
            // The connections are gone; the lock expires by itself
            return;
        }

        try
        {
            await redLock.UnlockAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The lock expires by itself
            logger.LogWarning(ex, "Failed to release the distributed lock '{LockId}'.", entry.LockId);
        }
    }

    public async Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var entry = GetEntry(lockHandle);
        if (entry.IsReleased)
        {
            // Nothing left to renew or to lose; a renewal timer that is still running just idles
            return true;
        }

        var result = await redLock.ExtendAsync(entry, cancellationToken).ConfigureAwait(false);
        if (result == ExtendResult.Extended || entry.IsReleased)
        {
            return true;
        }

        // Either a quorum can no longer be reached, or we failed to extend the lock before it expired
        if (result == ExtendResult.Lost || entry.IsExpired)
        {
            logger.LogWarning("The distributed lock '{LockId}' was lost.", entry.LockId);
            // Don't keep the instances that still hold our value locked until they expire
            await redLock.UnlockOnceAsync(entry).ConfigureAwait(false);
            throw new DistributedLockLostException(entry.LockId);
        }

        return false;
    }

    private static void ValidateLock(string lockId, TimeSpan lockPeriod)
    {
        if (string.IsNullOrEmpty(lockId))
        {
            throw new ArgumentException("The lock id must not be null or empty.", nameof(lockId));
        }

        // Redis expires keys with a precision of 1 millisecond
        if (lockPeriod < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lockPeriod), "The lock period must be at least 1ms.");
        }
    }

    private static DistributedLock GetEntry(IDistributedLock lockHandle)
    {
        if (lockHandle is DistributedLock entry)
        {
            return entry;
        }

        throw new ArgumentException(
            $"The lock was not acquired from a {nameof(RedisDistributedLockManager)}.",
            nameof(lockHandle)
        );
    }

    private static List<string> GetConnectionStrings(RedisDistributedLockOptions options)
    {
        var redisConnectionStrings = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            redisConnectionStrings.Add(options.RedisConnectionString!);
        }

        redisConnectionStrings.AddRange(options.RedisConnectionStrings.Where(cs => !string.IsNullOrWhiteSpace(cs)));
        return redisConnectionStrings.Distinct().ToList();
    }

    private static ConfigurationOptions GetConfiguration(string redisConnectionString)
    {
        var configuration = ConfigurationOptions.Parse(redisConnectionString);
        configuration.ClientName = $"{nameof(RedisDistributedLockManager)}";
        // A master that is down must not prevent the others from forming a quorum: keep connecting in the
        // background instead of throwing, and fail commands right away instead of queueing them until it's back
        configuration.AbortOnConnectFail = false;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        return configuration;
    }

    private async Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions configuration)
    {
        try
        {
            return await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to connect to Redis instance {Instance}; it counts as unreachable.",
                string.Join(",", configuration.EndPoints.Select(endPoint => endPoint.ToString()))
            );
            throw;
        }
    }

    private string GetKey(string lockId) => $"{lockPrefix}{lockId}";

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RedisDistributedLockManager));
        }
    }

    private enum ExtendResult
    {
        Extended,
        Failed,
        Lost,
    }

    internal sealed class DistributedLock(
        string lockId,
        string key,
        byte[] value,
        TimeSpan ttl,
        TimeProvider timeProvider,
        TimeSpan validity
    ) : IDistributedLock
    {
        private int released;
        private long validUntil = GetTimestampAfter(timeProvider, validity);

        public byte[] Value { get; } = value;

        /// <summary>The requested time to live, also used when extending the lock.</summary>
        public TimeSpan Ttl { get; } = ttl;

        public bool IsExpired => timeProvider.GetTimestamp() >= Volatile.Read(ref validUntil);

        public bool IsReleased => Volatile.Read(ref released) != 0;

        /// <summary>How long the lock can still be assumed to be held, whether or not it was released since.</summary>
        public TimeSpan TimeUntilExpiry
        {
            get
            {
                var remaining = Volatile.Read(ref validUntil) - timeProvider.GetTimestamp();
                return remaining <= 0 ? TimeSpan.Zero
                    : TimeSpan.FromSeconds((double)remaining / timeProvider.TimestampFrequency);
            }
        }

        public string LockId { get; } = lockId;

        public string Key { get; } = key;

        public TimeSpan RemainingValidity => IsReleased ? TimeSpan.Zero : TimeUntilExpiry;

        public bool IsValid => !IsReleased && !IsExpired;

        public void MarkReleased() => Volatile.Write(ref released, 1);

        /// <summary>The lock can no longer be assumed to be held once this validity time has passed.</summary>
        public void Extend(TimeSpan validity) =>
            Volatile.Write(ref validUntil, GetTimestampAfter(timeProvider, validity));

        private static long GetTimestampAfter(TimeProvider timeProvider, TimeSpan timeSpan) =>
            timeProvider.GetTimestamp() + (long)(timeSpan.TotalSeconds * timeProvider.TimestampFrequency);
    }

    private sealed class RedLock(
        IReadOnlyList<IRedisInstance> redisMasters,
        RedisDistributedLockOptions options,
        ILogger logger
    )
    {
        private const double ClockDriftFactor = 0.01;
        private static readonly Random RetryDelayRandom = new();
        private readonly TimeSpan? instanceTimeout = options.InstanceTimeout;
        private readonly int retryCount = options.RetryCount;
        private readonly int retryDelayMsec = (int)options.RetryDelay.TotalMilliseconds;

        private readonly IReadOnlyCollection<string>?[]
            serverIds = new IReadOnlyCollection<string>?[redisMasters.Count];

        private readonly TimeProvider timeProvider = options.TimeProvider;
        private volatile bool mastersVerified;

        private int Quorum => (redisMasters.Count / 2) + 1;

        private static byte[] CreateUniqueLockId() => Guid.NewGuid().ToByteArray();

        /*
         * Add 2 milliseconds to the drift to account for Redis expires
         * precision, which is 1 millisecond, plus 1 millisecond min drift
         * for small TTLs.
         */
        private static TimeSpan GetValidityTime(TimeSpan ttl, TimeSpan elapsed)
        {
            var drift = Convert.ToInt32((ttl.TotalMilliseconds * ClockDriftFactor) + 2);
            return ttl - elapsed - new TimeSpan(0, 0, 0, 0, drift);
        }

        private TimeSpan GetRetryDelay()
        {
            lock (RetryDelayRandom)
            {
                return TimeSpan.FromMilliseconds(RetryDelayRandom.Next(retryDelayMsec + 1));
            }
        }

        // Never more than half of the lock period, so that a master that doesn't answer leaves the lock some validity
        private TimeSpan GetInstanceTimeout(TimeSpan ttl)
        {
            var timeout = instanceTimeout ?? new TimeSpan(ttl.Ticks / 4);
            var maximum = new TimeSpan(ttl.Ticks / 2);
            return timeout < maximum ? timeout : maximum;
        }

        /// <returns>Whether the instance answered in time, and if so its answer.</returns>
        private async Task<(bool Answered, T Answer)> ExecuteAsync<T>(
            IRedisInstance redis,
            Func<IRedisInstance, Task<T>> operation,
            TimeSpan timeout
        )
        {
            try
            {
                var task = operation(redis);
                if (!task.IsCompleted)
                {
                    using var timeoutCancellation = new CancellationTokenSource();
                    var completed = await Task.WhenAny(task, timeProvider.Delay(timeout, timeoutCancellation.Token))
                        .ConfigureAwait(false);
                    if (completed != task)
                    {
                        // Observe a failure that arrives after we stopped waiting
                        _ = task.ContinueWith(
                            static t => _ = t.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );
                        logger.LogDebug(
                            "Redis instance {Instance} didn't answer within {Timeout} ms.",
                            redis.Name,
                            timeout.TotalMilliseconds
                        );
                        return (false, default!);
                    }

                    timeoutCancellation.Cancel();
                }

                return (true, await task.ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Redis instance {Instance} failed.", redis.Name);
                return (false, default!);
            }
        }

        /// <returns>For each instance its answer, or null if it failed or didn't answer in time.</returns>
        private Task<bool?[]> ExecuteOnAllAsync(Func<IRedisInstance, Task<bool>> operation, TimeSpan timeout) =>
            Task.WhenAll(
                redisMasters.Select(async redis =>
                    {
                        var (answered, answer) = await ExecuteAsync(redis, operation, timeout).ConfigureAwait(false);
                        return answered ? answer : (bool?)null;
                    }
                )
            );

        private Task<bool?[]> UnlockInstancesAsync(string key, byte[] value, TimeSpan timeout) =>
            ExecuteOnAllAsync(redis => redis.TryReleaseAsync(key, value), timeout);

        /// <summary>
        ///     Throws if two of the configured masters turn out to be the same server, e.g. addressed once by name and once
        ///     by IP address: it would get two votes, and a majority would no longer mean a majority of servers. Masters that
        ///     can't be reached yet are checked the next time.
        /// </summary>
        private async Task VerifyMastersAreDistinctAsync(TimeSpan timeout)
        {
            if (mastersVerified)
            {
                return;
            }

            await Task.WhenAll(
                    redisMasters.Select(async (redis, index) =>
                        {
                            if (Volatile.Read(ref serverIds[index]) != null)
                            {
                                return;
                            }

                            var (answered, ids) = await ExecuteAsync(redis, r => r.GetServerIdsAsync(), timeout)
                                .ConfigureAwait(false);
                            if (!answered)
                            {
                                return;
                            }

                            if (ids.Count == 0)
                            {
                                logger.LogWarning(
                                    "Redis instance {Instance} doesn't report its run id, so it can't be checked that it is configured only once.",
                                    redis.Name
                                );
                            }

                            Volatile.Write(ref serverIds[index], ids);
                        }
                    )
                )
                .ConfigureAwait(false);

            var known = serverIds.ToArray();
            for (var i = 0; i < known.Length; i++)
            {
                for (var j = i + 1; j < known.Length; j++)
                {
                    if (known[i] != null && known[j] != null && known[i]!.Intersect(known[j]!).Any())
                    {
                        throw new InvalidOperationException(
                            $"The Redis instances {redisMasters[i].Name} and {redisMasters[j].Name} are the same server. Each master must be configured only once, otherwise it gets more than one vote."
                        );
                    }
                }
            }

            mastersVerified = known.All(ids => ids != null);
        }

        public async Task<DistributedLock?> LockAsync(
            string lockId,
            string key,
            TimeSpan ttl,
            CancellationToken cancellationToken
        )
        {
            var value = CreateUniqueLockId();
            var timeout = GetInstanceTimeout(ttl);
            for (var attempt = 0;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var started = timeProvider.GetTimestamp();
                // Alongside the round rather than before it, so that it doesn't use up validity time
                var verification = VerifyMastersAreDistinctAsync(timeout);
                var results = await ExecuteOnAllAsync(redis => redis.TryAcquireAsync(key, value, ttl), timeout)
                    .ConfigureAwait(false);
                try
                {
                    await verification.ConfigureAwait(false);
                }
                catch
                {
                    await UnlockInstancesAsync(key, value, timeout).ConfigureAwait(false);
                    throw;
                }

                var validityTime = GetValidityTime(ttl, timeProvider.GetElapsedTime(started));
                if (results.Count(r => r == true) >= Quorum && validityTime > TimeSpan.Zero)
                {
                    return new DistributedLock(lockId, key, value, ttl, timeProvider, validityTime);
                }

                // Also on the instances that didn't answer: they may still have set the key
                await UnlockInstancesAsync(key, value, timeout).ConfigureAwait(false);
                if (attempt >= retryCount)
                {
                    logger.LogDebug("Failed to acquire the distributed lock '{LockId}'.", lockId);
                    return null;
                }

                await timeProvider.Delay(GetRetryDelay(), cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task UnlockAsync(DistributedLock lockObject, CancellationToken cancellationToken)
        {
            var timeout = GetInstanceTimeout(lockObject.Ttl);
            for (var attempt = 0;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var results = await UnlockInstancesAsync(lockObject.Key, lockObject.Value, timeout)
                    .ConfigureAwait(false);

                // An answer is final, even if the key holds another value because the lock was already lost.
                // Only instances that couldn't be reached are worth retrying.
                if (results.Count(r => r.HasValue) >= Quorum)
                {
                    return;
                }

                if (attempt >= retryCount)
                {
                    logger.LogWarning(
                        "Failed to release the distributed lock '{LockId}' on a majority of the Redis instances; it's left to expire.",
                        lockObject.LockId
                    );
                    return;
                }

                await timeProvider.Delay(GetRetryDelay(), cancellationToken).ConfigureAwait(false);
            }
        }

        public Task UnlockOnceAsync(DistributedLock lockObject) => UnlockInstancesAsync(
            lockObject.Key,
            lockObject.Value,
            GetInstanceTimeout(lockObject.Ttl)
        );

        public async Task<ExtendResult> ExtendAsync(DistributedLock lockObject, CancellationToken cancellationToken)
        {
            for (var attempt = 0;; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Don't wait for an instance beyond the moment the lock expires: the holder must know by then
                var timeout = GetInstanceTimeout(lockObject.Ttl);
                var timeUntilExpiry = lockObject.TimeUntilExpiry;
                if (timeUntilExpiry > TimeSpan.Zero && timeUntilExpiry < timeout)
                {
                    timeout = timeUntilExpiry;
                }

                var started = timeProvider.GetTimestamp();
                var results = await ExecuteOnAllAsync(
                        redis => redis.TryExtendAsync(lockObject.Key, lockObject.Value, lockObject.Ttl),
                        timeout
                    )
                    .ConfigureAwait(false);

                var validityTime = GetValidityTime(lockObject.Ttl, timeProvider.GetElapsedTime(started));
                if (results.Count(r => r == true) >= Quorum && validityTime > TimeSpan.Zero)
                {
                    lockObject.Extend(validityTime);
                    return ExtendResult.Extended;
                }

                // Don't unlock on failure: the lock may still be valid and the next renewal may succeed.
                // It's lost only if too many instances no longer hold our value for a quorum to be possible.
                var lostCount = results.Count(r => r == false);
                if (lostCount > redisMasters.Count - Quorum)
                {
                    return ExtendResult.Lost;
                }

                if (attempt >= retryCount || lockObject.IsExpired)
                {
                    return ExtendResult.Failed;
                }

                await timeProvider.Delay(GetRetryDelay(), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
