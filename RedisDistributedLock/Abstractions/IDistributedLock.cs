namespace RedisDistributedLock.Abstractions;

public interface IDistributedLock
{
    /// <summary>The Lock identity.</summary>
    string LockId { get; }
}
