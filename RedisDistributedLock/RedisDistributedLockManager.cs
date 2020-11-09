using Dasync.Collections;
using Polly;
using RedisDistributedLock.Interfaces;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock
{
    public class RedisDistributedLockManager : IDistributedLockManager
    {
        private readonly string _redisConnectionString;
        private readonly ConcurrentDictionary<string, DistributedLock> _distributedLocks = new ConcurrentDictionary<string, DistributedLock>();
        private readonly Redlock _redlock;

        public RedisDistributedLockManager(string redisConnectionString)
        {
            _redisConnectionString = redisConnectionString;
            _redlock = new Redlock(GetMultiplexer());
        }

        private ConnectionMultiplexer GetMultiplexer()
        {
            if (string.IsNullOrWhiteSpace(_redisConnectionString))
            {
                return null;
            }
            ConfigurationOptions options = ConfigurationOptions.Parse(_redisConnectionString);
            options.ClientName = $"{nameof(RedisDistributedLockManager)}";
            return ConnectionMultiplexer.ConnectAsync(options).GetAwaiter().GetResult();
        }

        public Task<string> GetLockOwnerAsync(string account, string lockId, CancellationToken cancellationToken)
        {
            return Task.FromResult<string>(null);
        }

        public async Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
        {
            DistributedLock entry = (DistributedLock)lockHandle;
            try
            {
                await _redlock.UnlockAsync(entry).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                _distributedLocks.TryRemove(entry.LockId, out _);
            }

            return;
        }

        public async Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
        {
            DistributedLock entry = (DistributedLock)lockHandle;
            bool result = await _redlock.ExtendAsync(entry).ConfigureAwait(false);
            return result;
        }

        public async Task<IDistributedLock> TryLockAsync(string account, string lockId, string lockOwnerId, string proposedLeaseId, TimeSpan lockPeriod, CancellationToken cancellationToken)
        {
            DistributedLock entry = null;
            if (!_distributedLocks.ContainsKey(lockId))
            {
                (bool successfull, DistributedLock distributedLock) = await _redlock.LockAsync(lockId, lockPeriod).ConfigureAwait(false);
                if (successfull)
                {
                    distributedLock.LockId = lockId;
                    if (_distributedLocks.TryAdd(lockId, distributedLock))
                    {
                        entry = distributedLock;
                    }
                }
            }

            return entry;
        }

        private class DistributedLock : IDistributedLock
        {
            public DistributedLock(RedisKey resource, RedisValue value, TimeSpan validity)
            {
                Resource = resource;
                Value = value;
                Validity = validity;
            }

            public string LockId { get; set; }

            public RedisKey Resource { get; }

            public RedisValue Value { get; }

            public TimeSpan Validity { get; }
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
            private static readonly TimeSpan _defaultRetryDelay = new TimeSpan(0, 0, 0, 0, 200);
            protected Dictionary<string, ConnectionMultiplexer> _redisMasterDictionary = new Dictionary<string, ConnectionMultiplexer>();

            public Redlock(params ConnectionMultiplexer[] list)
            {
                foreach (ConnectionMultiplexer item in list)
                {
                    _redisMasterDictionary.Add(item.GetEndPoints().First().ToString(), item);
                }
            }

            private int Quorum => (_redisMasterDictionary.Count / 2) + 1;

            private static byte[] CreateUniqueLockId()
            {
                return Guid.NewGuid().ToByteArray();
            }

            private async Task<bool> LockInstanceAsync(string redisServer, string resource, byte[] val, TimeSpan ttl)
            {
                bool succeeded;
                try
                {
                    ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                    succeeded = await redis.GetDatabase().StringSetAsync(resource, val, ttl, When.NotExists).ConfigureAwait(false);
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
                    RedisKey[] key = { resource };
                    RedisValue[] values = { val };
                    ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                    long extendResult = (long)await redis.GetDatabase().ScriptEvaluateAsync(UNLOCK_SCRIPT, key, values).ConfigureAwait(false);
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
                    ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                    RedisKey[] key = { resource };
                    RedisValue[] values = { val, (long)ttl.TotalMilliseconds };
                    // Returns 1 on success, 0 on failure setting expiry or key not existing, -1 if the key value didn't match
                    long extendResult = (long)await redis.GetDatabase().ScriptEvaluateAsync(EXTEND_SCRIPT, key, values).ConfigureAwait(false);
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
                byte[] val = CreateUniqueLockId();
                DistributedLock innerLock = null;
                bool successfull = await Policy.HandleResult(false)
                    .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                    {
                        int maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                        Random rnd = new Random();
                        return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                    })
                    .ExecuteAsync(async () =>
                    {
                        try
                        {
                            int n = 0;
                            DateTime startTime = DateTime.Now;
                            // Use keys
                            await _redisMasterDictionary.ParallelForEachAsync(async kvp =>
                            {
                                if (await LockInstanceAsync(kvp.Key, resource, val, ttl).ConfigureAwait(false))
                                {
                                    n++;
                                }
                            }).ConfigureAwait(false);

                            /*
                             * Add 2 milliseconds to the drift to account for Redis expires
                             * precision, which is 1 millisecond, plus 1 millisecond min drift 
                             * for small TTLs.        
                             */
                            int drift = Convert.ToInt32((ttl.TotalMilliseconds * CLOCK_DRIVE_FACTOR) + 2);
                            TimeSpan validity_time = ttl - (DateTime.Now - startTime) - new TimeSpan(0, 0, 0, 0, drift);
                            if (n >= Quorum && validity_time.TotalMilliseconds > 0)
                            {
                                innerLock = new DistributedLock(resource, val, validity_time);
                                return true;
                            }
                            else
                            {
                                await _redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                {
                                    await UnlockInstanceAsync(kvp.Key, resource, val).ConfigureAwait(false);
                                }).ConfigureAwait(false);
                                return false;
                            }
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }).ConfigureAwait(false);

                return (successfull, innerLock);
            }

            public async Task UnlockAsync(DistributedLock lockObject)
            {
                bool successfull = await Policy.HandleResult(false)
                    .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                    {
                        int maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                        Random rnd = new Random();
                        return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                    })
                    .ExecuteAsync(async () =>
                    {
                        try
                        {
                            int n = 0;
                            await _redisMasterDictionary.ParallelForEachAsync(async kvp =>
                            {
                                if (await UnlockInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value).ConfigureAwait(false))
                                {
                                    n++;
                                }
                            }).ConfigureAwait(false);
                            if (n >= Quorum)
                            {
                                return true;
                            }
                            else
                            {
                                return false;
                            }
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }).ConfigureAwait(false);
            }

            public async Task<bool> ExtendAsync(DistributedLock lockObject)
            {
                bool successfull = await Policy.HandleResult(false)
                    .WaitAndRetryAsync(DEFAULT_RETRY_COUNT, sleepDurationProvider =>
                    {
                        int maxRetryDelay = (int)_defaultRetryDelay.TotalMilliseconds;
                        Random rnd = new Random();
                        return TimeSpan.FromMilliseconds(rnd.Next(maxRetryDelay));
                    })
                    .ExecuteAsync(async () =>
                    {
                        try
                        {
                            int n = 0;
                            DateTime startTime = DateTime.Now;
                            // Use keys
                            await _redisMasterDictionary.ParallelForEachAsync(async kvp =>
                            {
                                if (await ExtendInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value, lockObject.Validity).ConfigureAwait(false))
                                {
                                    n++;
                                }
                            }).ConfigureAwait(false);

                            /*
                             * Add 2 milliseconds to the drift to account for Redis expires
                             * precision, which is 1 millisecond, plus 1 millisecond min drift 
                             * for small TTLs.        
                             */
                            int drift = Convert.ToInt32((lockObject.Validity.TotalMilliseconds * CLOCK_DRIVE_FACTOR) + 2);
                            TimeSpan validity_time = lockObject.Validity - (DateTime.Now - startTime) - new TimeSpan(0, 0, 0, 0, drift);
                            if (n >= Quorum && validity_time.TotalMilliseconds > 0)
                            {
                                return true;
                            }
                            else
                            {
                                await _redisMasterDictionary.ParallelForEachAsync(async kvp =>
                                {
                                    await UnlockInstanceAsync(kvp.Key, lockObject.Resource, lockObject.Value).ConfigureAwait(false);
                                }).ConfigureAwait(false);
                                return false;
                            }
                        }
                        catch (Exception)
                        {
                            return false;
                        }
                    }).ConfigureAwait(false);

                return successfull;
            }
        }
    }
}
