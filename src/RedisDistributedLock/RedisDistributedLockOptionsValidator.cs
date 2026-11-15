using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RedisDistributedLock;

internal sealed class RedisDistributedLockOptionsValidator : IValidateOptions<RedisDistributedLockOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisDistributedLockOptions options)
    {
        var failures = new List<string>();
        if (options.RedisConnectionStrings == null)
        {
            failures.Add($"{nameof(options.RedisConnectionStrings)} must not be null.");
        }

        if (options.ConnectionMultiplexers == null)
        {
            failures.Add($"{nameof(options.ConnectionMultiplexers)} must not be null.");
        }
        else if (options.ConnectionMultiplexers.Any(multiplexer => multiplexer == null))
        {
            failures.Add($"{nameof(options.ConnectionMultiplexers)} must not contain null.");
        }

        if (string.IsNullOrWhiteSpace(options.RedisConnectionString) &&
            options.RedisConnectionStrings?.Any(cs => !string.IsNullOrWhiteSpace(cs)) != true &&
            options.ConnectionMultiplexers?.Count is null or 0)
        {
            failures.Add("At least one Redis connection string or connection multiplexer must be configured.");
        }

        if (options.LockPrefix == null)
        {
            failures.Add($"{nameof(options.LockPrefix)} must not be null; use an empty string for no prefix.");
        }

        if (options.InstanceTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.InstanceTimeout)} must be positive, or null for the default.");
        }

        if (options.RetryCount < 0)
        {
            failures.Add($"{nameof(options.RetryCount)} must not be negative.");
        }

        if (options.RetryDelay < TimeSpan.Zero)
        {
            failures.Add($"{nameof(options.RetryDelay)} must not be negative.");
        }

        if (options.TimeProvider == null)
        {
            failures.Add($"{nameof(options.TimeProvider)} must not be null.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>For options that didn't come through the options pipeline, which validates them itself.</summary>
    public static void ThrowIfInvalid(RedisDistributedLockOptions options)
    {
        var result = new RedisDistributedLockOptionsValidator().Validate(null, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(RedisDistributedLockOptions),
                result.Failures
            );
        }
    }
}
