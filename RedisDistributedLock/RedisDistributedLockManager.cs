using Microsoft.Extensions.Options;
using Polly;
using RedisDistributedLock.Abstractions;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public class RedisDistributedLockManager : IDistributedLockManager
{
    private readonly ConcurrentDictionary<string, DistributedLock> distributedLocks = new();
    private readonly string lockPrefix;
    private readonly RedLock redLock;

    public RedisDistributedLockManager(IOptions<RedisDistributedLockOptions> redisDistributedLockOptions)
    {
        lockPrefix = redisDistributedLockOptions.Value.LockPrefix;
        var redisConnectionStrings = GetConnectionStrings(redisDistributedLockOptions.Value);
        if (redisConnectionStrings.Count == 0)
        {
            throw new InvalidOperationException("At least one Redis connection string must be configured.");
        }

        redLock = new RedLock(redisConnectionStrings.Select(GetMultiplexer).ToArray());
    }

    public async Task<IDistributedLock?> TryLockAsync(
        string lockId,
        TimeSpan lockPeriod,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(lockPrefix) && !lockId.StartsWith(lockPrefix))
        {
            lockId = $"{lockPrefix}{lockId}";
        }

        DistributedLock? entry = null;
        if (!distributedLocks.ContainsKey(lockId))
        {
            var (successful, distributedLock) = await redLock.LockAsync(lockId, lockPeriod).ConfigureAwait(false);
            if (successful)
            {
                if (distributedLocks.TryAdd(lockId, distributedLock!))
                {
                    entry = distributedLock;
                }
            }
        }

        return entry;
    }

    public async Task<IRenewableDistributedLockHandle?> TryCreateRenewableLockHandleAsync(
        string lockId,
        TimeSpan lockPeriod,
        TimeSpan leasePeriod,
        bool linear,
        Func<bool>? preExecuteCheck,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(lockPrefix) && !lockId.StartsWith(lockPrefix))
        {
            lockId = $"{lockPrefix}{lockId}";
        }

        IRenewableDistributedLockHandle? entry = null;
        var distributedLock = await TryLockAsync(lockId, lockPeriod, cancellationToken).ConfigureAwait(false);
        if (distributedLock != null)
        {
            entry = RenewableDistributedLockHandleFactory.CreateRenewableLockHandle(
                leasePeriod,
                linear,
                this,
                distributedLock,
                preExecuteCheck
            );
        }

        return entry;
    }

    public async Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        var entry = (DistributedLock)lockHandle;
        try
        {
            await redLock.UnlockAsync(entry).ConfigureAwait(false);
        }
        catch
        {
            // Ignored
        }
        finally
        {
            distributedLocks.TryRemove(entry.LockId, out _);
        }
    }

    public async Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        var entry = (DistributedLock)lockHandle;
        var result = await redLock.ExtendAsync(entry).ConfigureAwait(false);
        if (result == ExtendResult.Extended)
        {
            return true;
        }

        // Either a quorum can no longer be reached, or we failed to extend the lock before it expired
        if (result == ExtendResult.Lost || entry.IsExpired)
        {
            distributedLocks.TryRemove(entry.LockId, out _);
            throw new DistributedLockLostException(entry.LockId);
        }

        return false;
    }

    private static List<string> GetConnectionStrings(RedisDistributedLockOptions options)
    {
        var redisConnectionStrings = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
        {
            redisConnectionStrings.Add(options.RedisConnectionString);
        }

        redisConnectionStrings.AddRange(options.RedisConnectionStrings.Where(cs => !string.IsNullOrWhiteSpace(cs)));
        return redisConnectionStrings.Distinct().ToList();
    }

    private static ConnectionMultiplexer GetMultiplexer(string redisConnectionString)
    {
        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.ClientName = $"{nameof(RedisDistributedLockManager)}";
        return ConnectionMultiplexer.ConnectAsync(options).GetAwaiter().GetResult();
    }

    private static long GetTimestampAfter(TimeSpan timeSpan) =>
        Stopwatch.GetTimestamp() + (long)(timeSpan.TotalSeconds * Stopwatch.Frequency);

    private enum ExtendResult
    {
        Extended,
        Failed,
        Lost,
    }

    private class DistributedLock(RedisKey resource, RedisValue value, TimeSpan ttl, string lockId) : IDistributedLock
    {
        public RedisKey Resource { get; } = resource;

        public RedisValue Value { get; } = value;

        /// <summary>The requested time to live, also used when extending the lock.</summary>
        public TimeSpan Ttl { get; } = ttl;

        /// <summary>The <see cref="Stopwatch" /> timestamp after which the lock can no longer be assumed to be held.</summary>
        public long ValidUntil { get; set; }

        public bool IsExpired => Stopwatch.GetTimestamp() >= ValidUntil;

        public string LockId { get; } = lockId;
    }

    private class RedLock(IReadOnlyList<ConnectionMultiplexer> redisMasters)
    {
        private const int DefaultRetryCount = 3;
        private const double ClockDriveFactor = 0.01;

        private const string UnlockScript = """
                                            local currentVal = redis.call('get', KEYS[1])
                                            if (currentVal == false) then
                                                return 1
                                            elseif currentVal == ARGV[1] then
                                                return redis.call('del', KEYS[1])
                                            else
                                                return 0
                                            end
                                            """;

        // Never recreates a missing key: it expired or was released, so another client may have acquired it since
        private const string ExtendScript = """
                                            local currentVal = redis.call('get', KEYS[1])
                                            if (currentVal == false) then
                                                return 0
                                            elseif (currentVal == ARGV[1]) then
                                                return redis.call('pexpire', KEYS[1], ARGV[2])
                                            else
                                                return -1
                                            end
                                            """;

        private static readonly TimeSpan DefaultRetryDelay = new(0, 0, 0, 0, 200);

        private int Quorum => (redisMasters.Count / 2) + 1;

        private static byte[] CreateUniqueLockId() => Guid.NewGuid().ToByteArray();

        private static TimeSpan GetRetryDelay(int retryAttempt)
        {
            var maxRetryDelay = (int)DefaultRetryDelay.TotalMilliseconds;
            var rnd = new Random();
            return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
        }

        /*
         * Add 2 milliseconds to the drift to account for Redis expires
         * precision, which is 1 millisecond, plus 1 millisecond min drift
         * for small TTLs.
         */
        private static TimeSpan GetValidityTime(TimeSpan ttl, TimeSpan elapsed)
        {
            var drift = Convert.ToInt32((ttl.TotalMilliseconds * ClockDriveFactor) + 2);
            return ttl - elapsed - new TimeSpan(0, 0, 0, 0, drift);
        }

        private static async Task<bool> LockInstanceAsync(
            ConnectionMultiplexer redis,
            string resource,
            byte[] val,
            TimeSpan ttl
        )
        {
            bool succeeded;
            try
            {
                succeeded = await redis.GetDatabase()
                    .StringSetAsync(resource, val, ttl, When.NotExists)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                succeeded = false;
            }

            return succeeded;
        }

        private static async Task<bool> UnlockInstanceAsync(ConnectionMultiplexer redis, string resource, byte[] val)
        {
            bool succeeded;
            try
            {
                RedisKey[] key = [resource,];
                RedisValue[] values = [val,];
                var extendResult = (long)await redis.GetDatabase()
                    .ScriptEvaluateAsync(UnlockScript, key, values)
                    .ConfigureAwait(false);
                succeeded = extendResult == 1;
            }
            catch (Exception)
            {
                succeeded = false;
            }

            return succeeded;
        }

        /// <returns>
        ///     1 on success, 0 if the key doesn't exist, -1 if the key value didn't match, null if the instance couldn't be
        ///     reached.
        /// </returns>
        private static async Task<long?> ExtendInstanceAsync(
            ConnectionMultiplexer redis,
            string resource,
            byte[] val,
            TimeSpan ttl
        )
        {
            try
            {
                RedisKey[] key = [resource,];
                RedisValue[] values = [val, (long)ttl.TotalMilliseconds,];
                return (long)await redis.GetDatabase()
                    .ScriptEvaluateAsync(ExtendScript, key, values)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<(bool, DistributedLock?)> LockAsync(RedisKey resource, TimeSpan ttl)
        {
            var val = CreateUniqueLockId();
            DistributedLock? innerLock = null;
            var successful = await Policy.HandleResult(false)
                .WaitAndRetryAsync(DefaultRetryCount, GetRetryDelay)
                .ExecuteAsync(async () =>
                    {
                        try
                        {
                            var stopwatch = Stopwatch.StartNew();
                            var results = await Task.WhenAll(
                                    redisMasters.Select(redis => LockInstanceAsync(redis, resource!, val, ttl))
                                )
                                .ConfigureAwait(false);

                            var validityTime = GetValidityTime(ttl, stopwatch.Elapsed);
                            if (results.Count(r => r) >= Quorum && validityTime > TimeSpan.Zero)
                            {
                                innerLock = new DistributedLock(resource, val, ttl, resource!)
                                {
                                    ValidUntil = GetTimestampAfter(validityTime),
                                };
                                return true;
                            }

                            await Task.WhenAll(redisMasters.Select(redis => UnlockInstanceAsync(redis, resource!, val)))
                                .ConfigureAwait(false);
                            return false;
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }
                )
                .ConfigureAwait(false);

            return (successful, innerLock);
        }

        public async Task UnlockAsync(DistributedLock lockObject) => await Policy.HandleResult(false)
            .WaitAndRetryAsync(DefaultRetryCount, GetRetryDelay)
            .ExecuteAsync(async () =>
                {
                    try
                    {
                        var results = await Task.WhenAll(
                                redisMasters.Select(redis => UnlockInstanceAsync(
                                    redis,
                                    lockObject.Resource!,
                                    lockObject.Value!
                                )
                                )
                            )
                            .ConfigureAwait(false);
                        return results.Count(r => r) >= Quorum;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            )
            .ConfigureAwait(false);

        public async Task<ExtendResult> ExtendAsync(DistributedLock lockObject) => await Policy
            .HandleResult(ExtendResult.Failed)
            .WaitAndRetryAsync(DefaultRetryCount, GetRetryDelay)
            .ExecuteAsync(async () =>
                {
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        var results = await Task.WhenAll(
                                redisMasters.Select(redis => ExtendInstanceAsync(
                                        redis,
                                        lockObject.Resource!,
                                        lockObject.Value!,
                                        lockObject.Ttl
                                    )
                                )
                            )
                            .ConfigureAwait(false);

                        var validityTime = GetValidityTime(lockObject.Ttl, stopwatch.Elapsed);
                        if (results.Count(r => r == 1) >= Quorum && validityTime > TimeSpan.Zero)
                        {
                            lockObject.ValidUntil = GetTimestampAfter(validityTime);
                            return ExtendResult.Extended;
                        }

                        // Don't unlock on failure: the lock may still be valid and the next renewal may succeed.
                        // It's lost only if too many instances no longer hold our value for a quorum to be possible.
                        var lostCount = results.Count(r => r is 0 or -1);
                        return lostCount > redisMasters.Count - Quorum ? ExtendResult.Lost : ExtendResult.Failed;
                    }
                    catch (Exception)
                    {
                        return ExtendResult.Failed;
                    }
                }
            )
            .ConfigureAwait(false);
    }
}
