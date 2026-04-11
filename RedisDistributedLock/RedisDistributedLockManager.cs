using Dasync.Collections;
using Microsoft.Extensions.Options;
using Polly;
using RedisDistributedLock.Abstractions;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock;

public class RedisDistributedLockManager : IDistributedLockManager
{
    private readonly ConcurrentDictionary<string, DistributedLock> distributedLocks = new();
    private readonly string lockPrefix;
    private readonly string redisConnectionString;
    private readonly RedLock redLock;

    public RedisDistributedLockManager(IOptions<RedisDistributedLockOptions> redisDistributedLockOptions)
    {
        redisConnectionString = redisDistributedLockOptions.Value.RedisConnectionString;
        lockPrefix = redisDistributedLockOptions.Value.LockPrefix;
        ConnectionMultiplexer connectionMultiplexer =
            GetMultiplexer() ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
        redLock = new RedLock(connectionMultiplexer);
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
        return result;
    }

    private ConnectionMultiplexer? GetMultiplexer()
    {
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.ClientName = $"{nameof(RedisDistributedLockManager)}";
        return ConnectionMultiplexer.ConnectAsync(options).GetAwaiter().GetResult();
    }

    private class DistributedLock(RedisKey resource, RedisValue value, TimeSpan validity, string lockId)
        : IDistributedLock
    {
        public RedisKey Resource { get; } = resource;

        public RedisValue Value { get; } = value;

        public TimeSpan Validity { get; } = validity;

        public string LockId { get; } = lockId;
    }

    private class RedLock
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

        private const string ExtendScript = """
                                            local currentVal = redis.call('get', KEYS[1])
                                            if (currentVal == false) then
                                                return redis.call('set', KEYS[1], ARGV[1], 'PX', ARGV[2]) and 1 or 0
                                            elseif (currentVal == ARGV[1]) then
                                                return redis.call('pexpire', KEYS[1], ARGV[2])
                                            else
                                                return -1
                                            end
                                            """;

        private static readonly TimeSpan DefaultRetryDelay = new(0, 0, 0, 0, 200);
        private readonly Dictionary<string, ConnectionMultiplexer> redisMasterDictionary = new();

        public RedLock(params ConnectionMultiplexer[] list)
        {
            foreach (var item in list)
            {
                redisMasterDictionary.Add(item.GetEndPoints().First().ToString(), item);
            }
        }

        private int Quorum => (redisMasterDictionary.Count / 2) + 1;

        private static byte[] CreateUniqueLockId() => Guid.NewGuid().ToByteArray();

        private async Task<bool> LockInstanceAsync(string redisServer, string resource, byte[] val, TimeSpan ttl)
        {
            bool succeeded;
            try
            {
                var redis = redisMasterDictionary[redisServer];
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

        private async Task<bool> UnlockInstanceAsync(string redisServer, string resource, byte[] val)
        {
            bool succeeded;
            try
            {
                RedisKey[] key = [resource,];
                RedisValue[] values = [val,];
                var redis = redisMasterDictionary[redisServer];
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

        private async Task<bool> ExtendInstanceAsync(string redisServer, string resource, byte[] val, TimeSpan ttl)
        {
            bool succeeded;
            try
            {
                var redis = redisMasterDictionary[redisServer];
                RedisKey[] key = [resource,];
                RedisValue[] values = [val, (long)ttl.TotalMilliseconds,];
                // Returns 1 on success, 0 on failure setting expiry or key not existing, -1 if the key value didn't match
                var extendResult = (long)await redis.GetDatabase()
                    .ScriptEvaluateAsync(ExtendScript, key, values)
                    .ConfigureAwait(false);
                succeeded = extendResult == 1;
            }
            catch (Exception)
            {
                succeeded = false;
            }

            return succeeded;
        }

        public async Task<(bool, DistributedLock?)> LockAsync(RedisKey resource, TimeSpan ttl)
        {
            var val = CreateUniqueLockId();
            DistributedLock? innerLock = null;
            var successful = await Policy.HandleResult(false)
                .WaitAndRetryAsync(
                    DefaultRetryCount,
                    _ =>
                    {
                        var maxRetryDelay = (int)DefaultRetryDelay.TotalMilliseconds;
                        var rnd = new Random();
                        return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                    }
                )
                .ExecuteAsync(async () =>
                    {
                        try
                        {
                            var n = 0;
                            var startTime = DateTime.Now;
                            // Use keys
                            await redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                    {
                                        if (await LockInstanceAsync(kvp.Key, resource!, val, ttl).ConfigureAwait(false))
                                        {
                                            n++;
                                        }
                                    }
                                )
                                .ConfigureAwait(false);

                            /*
                             * Add 2 milliseconds to the drift to account for Redis expires
                             * precision, which is 1 millisecond, plus 1 millisecond min drift
                             * for small TTLs.
                             */
                            var drift = Convert.ToInt32((ttl.TotalMilliseconds * ClockDriveFactor) + 2);
                            var validityTime = ttl - (DateTime.Now - startTime) - new TimeSpan(0, 0, 0, 0, drift);
                            if (n >= Quorum && validityTime.TotalMilliseconds > 0)
                            {
                                innerLock = new DistributedLock(resource, val, validityTime, resource!);
                                return true;
                            }

                            await redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                    {
                                        await UnlockInstanceAsync(kvp.Key, resource!, val).ConfigureAwait(false);
                                    }
                                )
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
            .WaitAndRetryAsync(
                DefaultRetryCount,
                _ =>
                {
                    var maxRetryDelay = (int)DefaultRetryDelay.TotalMilliseconds;
                    var rnd = new Random();
                    return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                }
            )
            .ExecuteAsync(async () =>
                {
                    try
                    {
                        var n = 0;
                        await redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                {
                                    if (await UnlockInstanceAsync(kvp.Key, lockObject.Resource!, lockObject.Value!)
                                            .ConfigureAwait(false))
                                    {
                                        n++;
                                    }
                                }
                            )
                            .ConfigureAwait(false);
                        if (n >= Quorum)
                        {
                            return true;
                        }

                        return false;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            )
            .ConfigureAwait(false);

        public async Task<bool> ExtendAsync(DistributedLock lockObject) => await Policy.HandleResult(false)
            .WaitAndRetryAsync(
                DefaultRetryCount,
                _ =>
                {
                    var maxRetryDelay = (int)DefaultRetryDelay.TotalMilliseconds;
                    var rnd = new Random();
                    return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                }
            )
            .ExecuteAsync(async () =>
                {
                    try
                    {
                        var n = 0;
                        var startTime = DateTime.Now;
                        // Use keys
                        await redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                {
                                    if (await ExtendInstanceAsync(
                                                kvp.Key,
                                                lockObject.Resource!,
                                                lockObject.Value!,
                                                lockObject.Validity
                                            )
                                            .ConfigureAwait(false))
                                    {
                                        n++;
                                    }
                                }
                            )
                            .ConfigureAwait(false);

                        /*
                         * Add 2 milliseconds to the drift to account for Redis expires
                         * precision, which is 1 millisecond, plus 1 millisecond min drift
                         * for small TTLs.
                         */
                        var drift = Convert.ToInt32((lockObject.Validity.TotalMilliseconds * ClockDriveFactor) + 2);
                        var validityTime = lockObject.Validity -
                                           (DateTime.Now - startTime) -
                                           new TimeSpan(0, 0, 0, 0, drift);
                        if (n >= Quorum && validityTime.TotalMilliseconds > 0)
                        {
                            return true;
                        }

                        await redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                {
                                    await UnlockInstanceAsync(kvp.Key, lockObject.Resource!, lockObject.Value!)
                                        .ConfigureAwait(false);
                                }
                            )
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
    }
}
