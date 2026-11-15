using System;
using Xunit;

namespace RedisDistributedLock.UnitTests;

public class DelayStrategyTests
{
    private static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Normal = TimeSpan.FromSeconds(8);

    [Fact]
    public void LinearSpeedup_HalvesTheDelayOnFailureDownToTheMinimum_AndResetsOnSuccess()
    {
        var strategy = new LinearSpeedupStrategy(Normal, Minimum);

        Assert.Equal(Normal, strategy.GetNextDelay(true));
        Assert.Equal(TimeSpan.FromSeconds(4), strategy.GetNextDelay(false));
        Assert.Equal(TimeSpan.FromSeconds(2), strategy.GetNextDelay(false));
        for (var i = 0; i < 10; i++)
        {
            strategy.GetNextDelay(false);
        }

        Assert.Equal(Minimum, strategy.GetNextDelay(false));
        Assert.Equal(Normal, strategy.GetNextDelay(true));
    }

    [Fact]
    public void LinearSpeedup_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentException>(() => new LinearSpeedupStrategy(Minimum, Normal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearSpeedupStrategy(Normal, Minimum, 0));
    }

    [Fact]
    public void ExponentialBackoff_RetriesAtTheMinimumThenBacksOffUpToTheMaximum_AndResetsOnSuccess()
    {
        var strategy = new RandomizedExponentialBackoffStrategy(Minimum, Normal);

        Assert.Equal(Normal, strategy.GetNextDelay(true));
        Assert.Equal(Minimum, strategy.GetNextDelay(false));

        var previous = Minimum;
        for (var i = 0; i < 20; i++)
        {
            var delay = strategy.GetNextDelay(false);
            Assert.InRange(delay, previous, Normal);
            previous = delay;
        }

        Assert.Equal(Normal, previous);
        Assert.Equal(Normal, strategy.GetNextDelay(true));
        Assert.Equal(Minimum, strategy.GetNextDelay(false));
    }
}
