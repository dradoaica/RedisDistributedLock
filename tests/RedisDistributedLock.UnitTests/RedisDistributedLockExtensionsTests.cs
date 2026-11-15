using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RedisDistributedLock.Abstractions;
using System;
using System.Linq;
using Xunit;

namespace RedisDistributedLock.UnitTests;

public class RedisDistributedLockExtensionsTests
{
    [Fact]
    public void AddRedisDistributedLock_CalledTwice_RegistersOneManagerAndMergesTheOptions()
    {
        var services = new ServiceCollection();

        services.AddRedisDistributedLock("redis1:6379", options => options.LockPrefix = "custom_");
        services.AddRedisDistributedLock(["redis2:6379", "redis3:6379",]);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDistributedLockManager));
        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<RedisDistributedLockOptions>>().Value;
        Assert.Equal("redis1:6379", options.RedisConnectionString);
        Assert.Equal(["redis2:6379", "redis3:6379",], options.RedisConnectionStrings.ToArray());
        Assert.Equal("custom_", options.LockPrefix);
    }

    [Fact]
    public void AddRedisDistributedLock_WithInvalidOptions_FailsWhenTheOptionsAreResolved()
    {
        var services = new ServiceCollection();
        services.AddRedisDistributedLock("redis1:6379", options => options.InstanceTimeout = TimeSpan.FromSeconds(-5));
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IDistributedLockManager>()
        );

        Assert.Contains(nameof(RedisDistributedLockOptions.InstanceTimeout), exception.Message);
    }
}
