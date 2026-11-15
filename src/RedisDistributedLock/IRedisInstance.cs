using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RedisDistributedLock;

/// <summary>The lock operations on a single Redis master. Implementations throw when the master can't be reached.</summary>
internal interface IRedisInstance
{
    /// <summary>A name for logging. Must not contain credentials.</summary>
    string Name { get; }

    /// <summary>
    ///     The identities of the servers behind this instance, which stay the same however the server is addressed. Two
    ///     instances that share one are the same master, which must not get two votes. Empty if the server doesn't tell.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetServerIdsAsync();

    /// <returns>True if the key was set, false if it already exists.</returns>
    Task<bool> TryAcquireAsync(string key, byte[] value, TimeSpan ttl);

    /// <returns>True if the key was extended, false if it no longer exists or holds another value.</returns>
    Task<bool> TryExtendAsync(string key, byte[] value, TimeSpan ttl);

    /// <returns>True if the key was deleted or didn't exist, false if it holds another value.</returns>
    Task<bool> TryReleaseAsync(string key, byte[] value);
}
