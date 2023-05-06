namespace RedisDistributedLock;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Abstractions;
using Dasync.Collections;
using Polly;
using StackExchange.Redis;

public class RedisDistributedLockManager : IDistributedLockManager
{
    private readonly ConcurrentDictionary<string, DistributedLock> distributedLocks = new();
    private readonly string redisConnectionString;
    private readonly Redlock redlock;

    public RedisDistributedLockManager(string redisConnectionString)
    {
        this.redisConnectionString = redisConnectionString;
        ConnectionMultiplexer connectionMultiplexer =
            this.GetMultiplexer() ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
        this.redlock = new Redlock(connectionMultiplexer);
    }

    public async Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        var entry = (DistributedLock)lockHandle;
        try
        {
            await this.redlock.UnlockAsync(entry).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }
        finally
        {
            this.distributedLocks.TryRemove(entry.LockId, out _);
        }
    }

    public async Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
    {
        var entry = (DistributedLock)lockHandle;
        var result = await this.redlock.ExtendAsync(entry).ConfigureAwait(false);
        return result;
    }

    public async Task<IDistributedLock?> TryLockAsync(string lockId, TimeSpan lockPeriod,
        CancellationToken cancellationToken)
    {
        DistributedLock? entry = null;
        if (!this.distributedLocks.ContainsKey(lockId))
        {
            var (successful, distributedLock) =
                await this.redlock.LockAsync(lockId, lockPeriod).ConfigureAwait(false);
            if (successful)
            {
                distributedLock.LockId = lockId;
                if (this.distributedLocks.TryAdd(lockId, distributedLock))
                {
                    entry = distributedLock;
                }
            }
        }

        return entry;
    }

    public async Task<IRenewableDistributedLockHandle?> TryCreateRenewableLockHandleAsync(string lockId,
        TimeSpan lockPeriod,
        TimeSpan leasePeriod, Func<bool>? preExecuteCheck, CancellationToken cancellationToken)
    {
        IRenewableDistributedLockHandle? entry = null;
        var distributedLock = await this.TryLockAsync(lockId, lockPeriod, cancellationToken);
        if (distributedLock != null)
        {
            entry = RenewableDistributedLockHandleFactory.CreateRenewableLockHandle(leasePeriod, this,
                distributedLock,
                preExecuteCheck);
        }

        return entry;
    }

    private ConnectionMultiplexer? GetMultiplexer()
    {
        if (string.IsNullOrWhiteSpace(this.redisConnectionString))
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(this.redisConnectionString);
        options.ClientName = $"{nameof(RedisDistributedLockManager)}";
        return ConnectionMultiplexer.ConnectAsync(options).GetAwaiter().GetResult();
    }

    private class DistributedLock : IDistributedLock
    {
        public DistributedLock(RedisKey resource, RedisValue value, TimeSpan validity)
        {
            this.Resource = resource;
            this.Value = value;
            this.Validity = validity;
        }

        public RedisKey Resource { get; }

        public RedisValue Value { get; }

        public TimeSpan Validity { get; }

        public string LockId { get; set; }
    }

    private class Redlock
    {
        private const int DEFAULT_RETRY_COUNT = 3;
        private const double CLOCK_DRIVE_FACTOR = 0.01;

        private const string UNLOCK_SCRIPT = @"
            local currentVal = redis.call('get', KEYS[1])
            if (currentVal == false) then
                return 1
            elseif currentVal == ARGV[1] then
                return redis.call('del', KEYS[1])
            else
                return 0
            end";

        private const string EXTEND_SCRIPT = @"
            local currentVal = redis.call('get', KEYS[1])
            if (currentVal == false) then
	            return redis.call('set', KEYS[1], ARGV[1], 'PX', ARGV[2]) and 1 or 0
            elseif (currentVal == ARGV[1]) then
	            return redis.call('pexpire', KEYS[1], ARGV[2])
            else
	            return -1
            end";

        private static readonly TimeSpan _defaultRetryDelay = new(0, 0, 0, 0, 200);
        protected readonly Dictionary<string, ConnectionMultiplexer> _redisMasterDictionary = new();

        public Redlock(params ConnectionMultiplexer[] list)
        {
            foreach (var item in list)
            {
                this._redisMasterDictionary.Add(item.GetEndPoints().First().ToString(), item);
            }
        }

        private int Quorum => (this._redisMasterDictionary.Count / 2) + 1;

        private static byte[] CreateUniqueLockId() => Guid.NewGuid().ToByteArray();

        private async Task<bool> LockInstanceAsync(string redisServer, string resource, byte[] val, TimeSpan ttl)
        {
            bool succeeded;
            try
            {
                var redis = this._redisMasterDictionary[redisServer];
                succeeded = await redis.GetDatabase().StringSetAsync(resource, val, ttl, When.NotExists)
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
                RedisKey[] key = {resource};
                RedisValue[] values = {val};
                var redis = this._redisMasterDictionary[redisServer];
                var extendResult = (long)await redis.GetDatabase().ScriptEvaluateAsync(UNLOCK_SCRIPT, key, values)
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
                var redis = this._redisMasterDictionary[redisServer];
                RedisKey[] key = {resource};
                RedisValue[] values = {val, (long)ttl.TotalMilliseconds};
                // Returns 1 on success, 0 on failure setting expiry or key not existing, -1 if the key value didn't match
                var extendResult = (long)await redis.GetDatabase().ScriptEvaluateAsync(EXTEND_SCRIPT, key, values)
                    .ConfigureAwait(false);
                succeeded = extendResult == 1;
            }
            catch (Exception)
            {
                succeeded = false;
            }

            return succeeded;
        }

        public async Task<(bool, DistributedLock)> LockAsync(RedisKey resource, TimeSpan ttl)
        {
            var val = CreateUniqueLockId();
            DistributedLock innerLock = null;
            var successful = await Policy.HandleResult(false)
                .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                {
                    var maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                    var rnd = new Random();
                    return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                })
                .ExecuteAsync(async () =>
                {
                    try
                    {
                        var n = 0;
                        var startTime = DateTime.Now;
                        // Use keys
                        await this._redisMasterDictionary.ParallelForEachAsync(async kvp =>
                        {
                            if (await this.LockInstanceAsync(kvp.Key, resource, val, ttl).ConfigureAwait(false))
                            {
                                n++;
                            }
                        }).ConfigureAwait(false);

                        /*
                         * Add 2 milliseconds to the drift to account for Redis expires
                         * precision, which is 1 millisecond, plus 1 millisecond min drift
                         * for small TTLs.
                         */
                        var drift = Convert.ToInt32((ttl.TotalMilliseconds * CLOCK_DRIVE_FACTOR) + 2);
                        var validity_time = ttl - (DateTime.Now - startTime) - new TimeSpan(0, 0, 0, 0, drift);
                        if (n >= this.Quorum && validity_time.TotalMilliseconds > 0)
                        {
                            innerLock = new DistributedLock(resource, val, validity_time);
                            return true;
                        }

                        await this._redisMasterDictionary.ParallelForEachAsync(async kvp =>
                        {
                            await this.UnlockInstanceAsync(kvp.Key, resource, val).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                        return false;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }).ConfigureAwait(false);

            return (successful, innerLock);
        }

        public async Task UnlockAsync(DistributedLock lockObject) =>
            await Policy.HandleResult(false)
                .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                {
                    var maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                    var rnd = new Random();
                    return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                })
                .ExecuteAsync(async () =>
                {
                    try
                    {
                        var n = 0;
                        await this._redisMasterDictionary.ParallelForEachAsync(async kvp =>
                        {
                            if (await this.UnlockInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value)
                                    .ConfigureAwait(false))
                            {
                                n++;
                            }
                        }).ConfigureAwait(false);
                        if (n >= this.Quorum)
                        {
                            return true;
                        }

                        return false;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }).ConfigureAwait(false);

        public async Task<bool> ExtendAsync(DistributedLock lockObject) =>
            await Policy.HandleResult(false)
                .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                {
                    var maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                    var rnd = new Random();
                    return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                })
                .ExecuteAsync(async () =>
                {
                    try
                    {
                        var n = 0;
                        var startTime = DateTime.Now;
                        // Use keys
                        await this._redisMasterDictionary.ParallelForEachAsync(async kvp =>
                        {
                            if (await this.ExtendInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value,
                                    lockObject.Validity).ConfigureAwait(false))
                            {
                                n++;
                            }
                        }).ConfigureAwait(false);

                        /*
                         * Add 2 milliseconds to the drift to account for Redis expires
                         * precision, which is 1 millisecond, plus 1 millisecond min drift
                         * for small TTLs.
                         */
                        var drift = Convert.ToInt32((lockObject.Validity.TotalMilliseconds * CLOCK_DRIVE_FACTOR) + 2);
                        var validity_time = lockObject.Validity - (DateTime.Now - startTime) -
                                            new TimeSpan(0, 0, 0, 0, drift);
                        if (n >= this.Quorum && validity_time.TotalMilliseconds > 0)
                        {
                            return true;
                        }

                        await this._redisMasterDictionary.ParallelForEachAsync(async kvp =>
                        {
                            await this.UnlockInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value)
                                .ConfigureAwait(false);
                        }).ConfigureAwait(false);
                        return false;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }).ConfigureAwait(false);
    }
}
