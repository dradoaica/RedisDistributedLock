using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RedisDistributedLock.UnitTests;

public enum FakeRedisMode
{
    Healthy,

    /// <summary>Every command fails right away.</summary>
    Down,

    /// <summary>Commands are never answered.</summary>
    Hung,
}

/// <summary>An in-memory stand-in for one Redis master that can be taken down, hung or wiped.</summary>
internal sealed class FakeRedisInstance(string name, TimeProvider timeProvider) : IRedisInstance
{
    private readonly Dictionary<string, (byte[] Value, long ExpiresAt)> keys = new();
    private int extensions;

    public FakeRedisMode Mode { get; set; } = FakeRedisMode.Healthy;

    /// <summary>What identifies the server; two instances with the same one are the same master.</summary>
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>How many times a key was extended.</summary>
    public int Extensions => Volatile.Read(ref extensions);

    public string Name { get; } = name;

    public Task<IReadOnlyCollection<string>> GetServerIdsAsync() =>
        ExecuteAsync<IReadOnlyCollection<string>>(() => [ServerId,]);

    public Task<bool> TryAcquireAsync(string key, byte[] value, TimeSpan ttl) => ExecuteAsync(() =>
        {
            if (TryGet(key, out _))
            {
                return false;
            }

            keys[key] = (value, GetExpiry(ttl));
            return true;
        }
    );

    public Task<bool> TryExtendAsync(string key, byte[] value, TimeSpan ttl) => ExecuteAsync(() =>
        {
            if (!TryGet(key, out var current) || !current.SequenceEqual(value))
            {
                return false;
            }

            keys[key] = (value, GetExpiry(ttl));
            Interlocked.Increment(ref extensions);
            return true;
        }
    );

    public Task<bool> TryReleaseAsync(string key, byte[] value) => ExecuteAsync(() =>
        {
            if (!TryGet(key, out var current))
            {
                return true;
            }

            return current.SequenceEqual(value) && keys.Remove(key);
        }
    );

    public bool HasKey(string key)
    {
        lock (keys)
        {
            return TryGet(key, out _);
        }
    }

    /// <summary>Loses all keys, like a master restarted without persistence.</summary>
    public void Clear()
    {
        lock (keys)
        {
            keys.Clear();
        }
    }

    private long GetExpiry(TimeSpan ttl) =>
        timeProvider.GetTimestamp() + (long)(ttl.TotalSeconds * timeProvider.TimestampFrequency);

    private Task<T> ExecuteAsync<T>(Func<T> command)
    {
        switch (Mode)
        {
            case FakeRedisMode.Down:
                return Task.FromException<T>(new InvalidOperationException($"{Name} is down."));
            case FakeRedisMode.Hung:
                return new TaskCompletionSource<T>().Task;
            default:
                lock (keys)
                {
                    return Task.FromResult(command());
                }
        }
    }

    private bool TryGet(string key, out byte[] value)
    {
        if (keys.TryGetValue(key, out var entry) && timeProvider.GetTimestamp() < entry.ExpiresAt)
        {
            value = entry.Value;
            return true;
        }

        keys.Remove(key);
        value = [];
        return false;
    }
}
