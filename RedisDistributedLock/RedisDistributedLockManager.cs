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

        public Task ReleaseLockAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
        {
            DistributedLock entry = (DistributedLock)lockHandle;
            try
            {
             	_redlock.Unlock(entry);
            }
            catch
            {
            }
            finally
            {
            	_distributedLocks.TryRemove(entry.LockId, out _);
            }

            return Task.CompletedTask;
        }

        public Task<bool> RenewAsync(IDistributedLock lockHandle, CancellationToken cancellationToken)
        {
            DistributedLock entry = (DistributedLock)lockHandle;
	    bool result = _redlock.Extend(entry);      
            return Task.FromResult(result);
        }

        public Task<IDistributedLock> TryLockAsync(string account, string lockId, string lockOwnerId, string proposedLeaseId, TimeSpan lockPeriod, CancellationToken cancellationToken)
        {
            DistributedLock entry = null;
	    if (!_distributedLocks.ContainsKey(lockId) && _redlock.Lock(lockId, lockPeriod, out DistributedLock distributedLock))
            {
	    	distributedLock.LockId = lockId;
                if (_distributedLocks.TryAdd(lockId, distributedLock))
                {
                    entry = distributedLock;
                }
            }

            return Task.FromResult<IDistributedLock>(entry);
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
            if redis.call(""get"",KEYS[1]) == ARGV[1] then
                return redis.call(""del"",KEYS[1])
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
            private readonly TimeSpan _defaultRetryDelay = new TimeSpan(0, 0, 0, 0, 200);
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

            private bool LockInstance(string redisServer, string resource, byte[] val, TimeSpan ttl)
            {
                bool succeeded;
                try
                {
                    ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                    succeeded = redis.GetDatabase().StringSet(resource, val, ttl, When.NotExists);
                }
                catch (Exception)
                {
                    succeeded = false;
                }

                return succeeded;
            }

            private void UnlockInstance(string redisServer, string resource, byte[] val)
            {
                RedisKey[] key = { resource };
                RedisValue[] values = { val };
                ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                redis.GetDatabase().ScriptEvaluate(UNLOCK_SCRIPT, key, values);
            }

            private bool ExtendInstance(string redisServer, string resource, byte[] val, TimeSpan ttl)
            {
                bool succeeded;
                try
                {
                    ConnectionMultiplexer redis = _redisMasterDictionary[redisServer];
                    RedisKey[] key = { resource };
                    RedisValue[] values = { val, (long)ttl.TotalMilliseconds };
                    // Returns 1 on success, 0 on failure setting expiry or key not existing, -1 if the key value didn't match
                    long extendResult = (long)redis.GetDatabase().ScriptEvaluate(EXTEND_SCRIPT, key, values);
                    succeeded = extendResult == 1;
                }
                catch (Exception)
                {
                    succeeded = false;
                }

                return succeeded;
            }

            private void ForEachRedisRegistered(Action<string> action)
            {
                foreach (KeyValuePair<string, ConnectionMultiplexer> item in _redisMasterDictionary)
                {
                    action(item.Key);
                }
            }

            private bool Retry(int retryCount, TimeSpan retryDelay, Func<bool> action)
            {
                int maxRetryDelay = (int)retryDelay.TotalMilliseconds;
                Random rnd = new Random();
                int currentRetry = 0;
                while (currentRetry++ < retryCount)
                {
                    if (action())
                    {
                        return true;
                    }

                    Thread.Sleep(rnd.Next(maxRetryDelay));
                }

                return false;
            }

            public bool Lock(RedisKey resource, TimeSpan ttl, out DistributedLock lockObject)
            {
                byte[] val = CreateUniqueLockId();
                DistributedLock innerLock = null;
                bool successfull = Retry(DEFAULT_RETRY_COUNT, _defaultRetryDelay, () =>
                {
                    try
                    {
                        int n = 0;
                        DateTime startTime = DateTime.Now;
                        // Use keys
                        ForEachRedisRegistered(
                            redis =>
                            {
                                if (LockInstance(redis, resource, val, ttl))
                                {
                                    n++;
                                }
                            }
                        );

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
                            ForEachRedisRegistered(
                                redis => UnlockInstance(redis, resource, val)
                            );
                            return false;
                        }
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                });

                lockObject = innerLock;
                return successfull;
            }

            public void Unlock(DistributedLock lockObject)
            {
                ForEachRedisRegistered(redis => UnlockInstance(redis, lockObject.Resource, lockObject.Value));
            }

            public bool Extend(DistributedLock lockObject)
            {
                bool successfull = Retry(DEFAULT_RETRY_COUNT, _defaultRetryDelay, () =>
                {
                    try
                    {
                        int n = 0;
                        DateTime startTime = DateTime.Now;
                        // Use keys
                        ForEachRedisRegistered(
                            redis =>
                            {
                                if (ExtendInstance(redis, lockObject.Resource, lockObject.Value, lockObject.Validity))
                                {
                                    n++;
                                }
                            }
                        );

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
                            ForEachRedisRegistered(
                                redis => UnlockInstance(redis, lockObject.Resource, lockObject.Value)
                            );
                            return false;
                        }
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                });

                return successfull;
            }
        }
    }
}
