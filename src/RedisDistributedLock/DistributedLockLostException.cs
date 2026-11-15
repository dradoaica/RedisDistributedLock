using System;

namespace RedisDistributedLock;

/// <summary>Thrown when a lock can no longer be renewed because it expired or is held by someone else.</summary>
public class DistributedLockLostException(string lockId) : Exception($"The distributed lock '{lockId}' was lost.")
{
    public string LockId { get; } = lockId;
}
