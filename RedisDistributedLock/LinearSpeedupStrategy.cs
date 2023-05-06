namespace RedisDistributedLock;

using System;
using System.Threading.Tasks;
using Abstractions;

public class LinearSpeedupStrategy : IDelayStrategy
{
    private readonly int failureSpeedupDivisor;
    private readonly TimeSpan minimumInterval;
    private readonly TimeSpan normalInterval;
    private TimeSpan currentInterval;

    public LinearSpeedupStrategy(TimeSpan normalInterval, TimeSpan minimumInterval)
        : this(normalInterval, minimumInterval, 2)
    {
    }

    public LinearSpeedupStrategy(TimeSpan normalInterval, TimeSpan minimumInterval, int failureSpeedupDivisor)
    {
        if (normalInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException("normalInterval", "The TimeSpan must not be negative.");
        }

        if (minimumInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException("minimumInterval", "The TimeSpan must not be negative.");
        }

        if (minimumInterval.Ticks > normalInterval.Ticks)
        {
            throw new ArgumentException("The minimumInterval must not be greater than the normalInterval.",
                "minimumInterval");
        }

        if (failureSpeedupDivisor < 1)
        {
            throw new ArgumentOutOfRangeException("failureSpeedupDivisor",
                "The failureSpeedupDivisor must not be less than 1.");
        }

        this.normalInterval = normalInterval;
        this.minimumInterval = minimumInterval;
        this.failureSpeedupDivisor = failureSpeedupDivisor;
        this.currentInterval = normalInterval;
    }

    public TimeSpan GetNextDelay(bool executionSucceeded)
    {
        if (executionSucceeded)
        {
            this.currentInterval = this.normalInterval;
        }
        else
        {
            var speedupInterval = new TimeSpan(this.currentInterval.Ticks / this.failureSpeedupDivisor);
            this.currentInterval = Max(speedupInterval, this.minimumInterval);
        }

        return this.currentInterval;
    }

    private static TimeSpan Max(TimeSpan x, TimeSpan y) => x.Ticks > y.Ticks ? x : y;

    public static ITaskSeriesTimer CreateTimer(IRecurrentCommand command, TimeSpan normalInterval,
        TimeSpan minimumInterval)
    {
        IDelayStrategy delayStrategy = new LinearSpeedupStrategy(normalInterval, minimumInterval);
        ITaskSeriesCommand timerCommand = new RecurrentTaskSeriesCommand(command, delayStrategy);
        return new TaskSeriesTimer(timerCommand, Task.Delay(normalInterval));
    }
}
