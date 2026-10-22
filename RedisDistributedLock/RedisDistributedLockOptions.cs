using System.Collections.Generic;

namespace RedisDistributedLock;

public class RedisDistributedLockOptions
{
    public string RedisConnectionString { get; set; } = null!;

    /// <summary>
    ///     Connection strings of independent Redis masters (not replicas or nodes of the same cluster), used together
    ///     with <see cref="RedisConnectionString" />. A lock is acquired when a majority of them agree (Redlock).
    /// </summary>
    public IList<string> RedisConnectionStrings { get; set; } = new List<string>();

    /// <summary>Lock prefix.</summary>
    public string LockPrefix { get; set; } = "lock_";
}
