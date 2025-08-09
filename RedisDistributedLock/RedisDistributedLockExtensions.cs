using Microsoft.Extensions.DependencyInjection;
using RedisDistributedLock.Abstractions;
using System;

namespace RedisDistributedLock;

public static class RedisDistributedLockExtensions
{
    public static IServiceCollection AddRedisDistributedLock(
        this IServiceCollection services,
        string redisConnectionString,
        Action<RedisDistributedLockOptions>? customiseRedisDistributedLockOptions = null
    )
    {
        services.Configure<RedisDistributedLockOptions>(options =>
            {
                options.RedisConnectionString = redisConnectionString;
                customiseRedisDistributedLockOptions?.Invoke(options);
            }
        );
        services.AddSingleton<IDistributedLockManager, RedisDistributedLockManager>();
        return services;
    }
}
