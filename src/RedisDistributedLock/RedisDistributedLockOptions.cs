using StackExchange.Redis;
using System;
using System.Collections.Generic;

namespace RedisDistributedLock;

public class RedisDistributedLockOptions
{
    public string? RedisConnectionString { get; set; }

    /// <summary>
    ///     Connection strings of independent Redis masters (not replicas or nodes of the same cluster), used together
    ///     with <see cref="RedisConnectionString" />. A lock is acquired when a majority of them agree (Redlock).
    /// </summary>
    public IList<string> RedisConnectionStrings { get; set; } = new List<string>();

    /// <summary>
    ///     Existing connections to independent Redis masters, used together with the connection strings. The lock manager
    ///     doesn't dispose of them; their owner does.
    /// </summary>
    public IList<IConnectionMultiplexer> ConnectionMultiplexers { get; set; } = new List<IConnectionMultiplexer>();

    /// <summary>Lock prefix, prepended to every lock id to build the Redis key.</summary>
    public string LockPrefix { get; set; } = "lock_";

    /// <summary>
    ///     How long to wait for a single Redis master before counting it as unreachable, so that a slow or down master
    ///     can't use up the validity time of a lock the others granted. Defaults to a quarter of the lock period, and is never
    ///     longer than half of it.
    /// </summary>
    public TimeSpan? InstanceTimeout { get; set; }

    /// <summary>How many times acquiring, renewing or releasing is retried when a quorum isn't reached.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>The upper bound of the random delay before each retry.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The clock that lock periods, timeouts and delays are measured with. Replaceable for tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
