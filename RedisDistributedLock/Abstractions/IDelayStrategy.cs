using System;

namespace RedisDistributedLock.Abstractions;

public interface IDelayStrategy
{
    TimeSpan GetNextDelay(bool executionSucceeded);
}
