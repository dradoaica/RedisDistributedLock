using RedisDistributedLock.Abstractions;
using System;

namespace RedisDistributedLock;

public class LinearSpeedupStrategy : IDelayStrategy
{
    private readonly int failureSpeedupDivisor;
    private readonly TimeSpan minimumInterval;
    private readonly TimeSpan normalInterval;
    private TimeSpan currentInterval;

    public LinearSpeedupStrategy(TimeSpan normalInterval, TimeSpan minimumInterval, int failureSpeedupDivisor = 2)
    {
        if (normalInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalInterval), "The TimeSpan must not be negative.");
        }

        if (minimumInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "The TimeSpan must not be negative.");
        }

        if (minimumInterval.Ticks > normalInterval.Ticks)
        {
            throw new ArgumentException(
                "The minimumInterval must not be greater than the normalInterval.",
                nameof(minimumInterval)
            );
        }

        if (failureSpeedupDivisor < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureSpeedupDivisor),
                "The failureSpeedupDivisor must not be less than 1."
            );
        }

        this.normalInterval = normalInterval;
        this.minimumInterval = minimumInterval;
        this.failureSpeedupDivisor = failureSpeedupDivisor;
        currentInterval = normalInterval;
    }

    public TimeSpan GetNextDelay(bool executionSucceeded)
    {
        if (executionSucceeded)
        {
            currentInterval = normalInterval;
        }
        else
        {
            var speedupInterval = new TimeSpan(currentInterval.Ticks / failureSpeedupDivisor);
            currentInterval = Max(speedupInterval, minimumInterval);
        }

        return currentInterval;
    }

    private static TimeSpan Max(TimeSpan x, TimeSpan y) => x.Ticks > y.Ticks ? x : y;
}
