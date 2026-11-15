using System;

namespace RedisDistributedLock.Abstractions;

public interface IDistributedLock
{
    /// <summary>The Lock identity, as passed when acquiring the lock.</summary>
    string LockId { get; }

    /// <summary>The key that holds the lock in the underlying store: the lock id with the configured prefix.</summary>
    string Key { get; }

    /// <summary>How long the lock can still be assumed to be held, unless it is renewed. Zero once it expired or was released.</summary>
    TimeSpan RemainingValidity { get; }

    /// <summary>Whether the lock can still be assumed to be held.</summary>
    bool IsValid { get; }
}
