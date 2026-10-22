using Microsoft.Extensions.DependencyInjection;
using RedisDistributedLock.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>Registers a lock manager that uses the Redlock algorithm across multiple independent Redis masters.</summary>
    public static IServiceCollection AddRedisDistributedLock(
        this IServiceCollection services,
        IEnumerable<string> redisConnectionStrings,
        Action<RedisDistributedLockOptions>? customiseRedisDistributedLockOptions = null
    )
    {
        services.Configure<RedisDistributedLockOptions>(options =>
            {
                options.RedisConnectionStrings = redisConnectionStrings.ToList();
                customiseRedisDistributedLockOptions?.Invoke(options);
            }
        );
        services.AddSingleton<IDistributedLockManager, RedisDistributedLockManager>();
        return services;
    }
}
