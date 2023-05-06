namespace RedisDistributedLock.Abstractions;

using System;

public interface IDelayStrategy
{
    TimeSpan GetNextDelay(bool executionSucceeded);
}
