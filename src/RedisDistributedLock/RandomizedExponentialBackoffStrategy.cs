using RedisDistributedLock.Abstractions;
using System;

namespace RedisDistributedLock;

/// <summary>
///     Uses the maximum interval while executions succeed. After a failure, retries at the minimum interval and backs
///     off exponentially (randomized) up to the maximum interval.
/// </summary>
internal class RandomizedExponentialBackoffStrategy : IDelayStrategy
{
    private const double RandomizationFactor = 0.2;
    private readonly TimeSpan deltaBackoff;
    private readonly TimeSpan maximumInterval;
    private readonly TimeSpan minimumInterval;
    private uint backoffExponent;
    private TimeSpan currentInterval;
    private Random? random;

    public RandomizedExponentialBackoffStrategy(TimeSpan minimumInterval, TimeSpan maximumInterval)
        : this(minimumInterval, maximumInterval, minimumInterval)
    {
    }

    public RandomizedExponentialBackoffStrategy(
        TimeSpan minimumInterval,
        TimeSpan maximumInterval,
        TimeSpan deltaBackoff
    )
    {
        if (minimumInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "The TimeSpan must not be negative.");
        }

        if (maximumInterval.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInterval), "The TimeSpan must not be negative.");
        }

        if (minimumInterval.Ticks > maximumInterval.Ticks)
        {
            throw new ArgumentException(
                "The minimumInterval must not be greater than the maximumInterval.",
                nameof(minimumInterval)
            );
        }

        this.minimumInterval = minimumInterval;
        this.maximumInterval = maximumInterval;
        this.deltaBackoff = deltaBackoff;
    }

    public TimeSpan GetNextDelay(bool executionSucceeded)
    {
        if (executionSucceeded)
        {
            // Back to the normal (maximum) interval
            currentInterval = maximumInterval;
            backoffExponent = 0;
        }
        else if (backoffExponent == 0)
        {
            // First failure after a success: retry as soon as possible
            currentInterval = minimumInterval;
            backoffExponent = 1;
        }
        else if (currentInterval != maximumInterval)
        {
            var incrementMsec = RandomNext(1.0 - RandomizationFactor, 1.0 + RandomizationFactor) *
                                Math.Pow(2.0, backoffExponent - 1) *
                                deltaBackoff.TotalMilliseconds;
            var backoffInterval = minimumInterval + TimeSpan.FromMilliseconds(incrementMsec);

            if (backoffInterval < maximumInterval)
            {
                currentInterval = backoffInterval;
                backoffExponent++;
            }
            else
            {
                currentInterval = maximumInterval;
            }
        }
        // else do nothing and keep current interval equal to max

        return currentInterval;
    }

    private double RandomNext(double minimum, double maximum)
    {
        if (random == null)
        {
            random = new Random();
        }

        return (random.NextDouble() * (maximum - minimum)) + minimum;
    }
}
