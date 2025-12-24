namespace RedisDistributedLock;

public class RedisDistributedLockOptions
{
    public string RedisConnectionString { get; set; } = null!;

    /// <summary>Lock prefix.</summary>
    public string LockPrefix { get; set; } = "lock_";
}
