using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
    ) => services.AddRedisDistributedLock(options =>
        {
            options.RedisConnectionString = redisConnectionString;
            customiseRedisDistributedLockOptions?.Invoke(options);
        }
    );

    /// <summary>Registers a lock manager that uses the Redlock algorithm across multiple independent Redis masters.</summary>
    public static IServiceCollection AddRedisDistributedLock(
        this IServiceCollection services,
        IEnumerable<string> redisConnectionStrings,
        Action<RedisDistributedLockOptions>? customiseRedisDistributedLockOptions = null
    ) => services.AddRedisDistributedLock(options =>
        {
            options.RedisConnectionStrings = redisConnectionStrings.ToList();
            customiseRedisDistributedLockOptions?.Invoke(options);
        }
    );

    /// <summary>
    ///     Registers a lock manager configured only through its options, e.g. to reuse existing connections via
    ///     <see cref="RedisDistributedLockOptions.ConnectionMultiplexers" />.
    /// </summary>
    public static IServiceCollection AddRedisDistributedLock(
        this IServiceCollection services,
        Action<RedisDistributedLockOptions> configureRedisDistributedLockOptions
    )
    {
        services.Configure(configureRedisDistributedLockOptions);
        services.TryAddEnumerable(
            ServiceDescriptor
                .Singleton<IValidateOptions<RedisDistributedLockOptions>, RedisDistributedLockOptionsValidator>()
        );
        services.TryAddSingleton<IDistributedLockManager, RedisDistributedLockManager>();
        return services;
    }
}
