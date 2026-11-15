using System;

namespace RedisDistributedLock.Abstractions;

internal interface IDelayStrategy
{
    TimeSpan GetNextDelay(bool executionSucceeded);
}
