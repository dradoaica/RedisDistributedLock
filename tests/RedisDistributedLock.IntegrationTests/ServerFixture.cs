using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace RedisDistributedLock.IntegrationTests;

/// <summary>
///     Real, independent masters of one kind of server: by default the three started by
///     <c>docker compose up -d --wait</c>, or the semicolon-separated connection strings in an environment variable. The
///     tests are skipped when the masters can't be reached, unless <c>REDIS_TESTS_REQUIRED</c> is <c>true</c> (as in CI),
///     in which case they fail.
/// </summary>
public abstract class ServerFixture(
    string serverName,
    bool isValkey,
    string connectionStringsVariable,
    string defaultConnectionStrings
) : IAsyncLifetime
{
    public IReadOnlyList<string> ConnectionStrings { get; } =
        (Environment.GetEnvironmentVariable(connectionStringsVariable) ?? defaultConnectionStrings).Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        foreach (var connectionString in ConnectionStrings)
        {
            try
            {
                var options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = true;
                options.ConnectTimeout = 1000;
                options.ConnectRetry = 1;
                // For INFO
                options.AllowAdmin = true;
                await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

                // Valkey reports a valkey_version next to the redis_version it is compatible with. Checked so that the
                // tests of one kind of server can't pass against the other.
                var info = (string?)await multiplexer.GetDatabase().ExecuteAsync("INFO", "server") ?? string.Empty;
                if (info.Contains("valkey_version:", StringComparison.Ordinal) != isValkey)
                {
                    Unavailable = $"The server at {connectionString} is not {serverName}.";
                    break;
                }
            }
            catch (Exception ex)
            {
                Unavailable = $"{serverName} at {connectionString} can't be reached: {ex.Message}";
                break;
            }
        }
    }

    public ValueTask DisposeAsync() => default;

    public RedisDistributedLockManager CreateManager(params string[] extraConnectionStrings) => new(
        Options.Create(
            new RedisDistributedLockOptions
            {
                RedisConnectionStrings = ConnectionStrings.Concat(extraConnectionStrings).ToList(),
            }
        )
    );

    public void SkipIfUnavailable()
    {
        if (Unavailable == null)
        {
            return;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("REDIS_TESTS_REQUIRED"),
                "true",
                StringComparison.OrdinalIgnoreCase
            ))
        {
            Assert.Fail(Unavailable);
        }

        Assert.Skip(Unavailable);
    }
}

/// <summary>The Redis masters, overridable with <c>REDIS_TEST_CONNECTION_STRINGS</c>.</summary>
public sealed class RedisFixture() : ServerFixture(
    "Redis",
    false,
    "REDIS_TEST_CONNECTION_STRINGS",
    "localhost:6379;localhost:6380;localhost:6381"
);

/// <summary>The Valkey masters, overridable with <c>VALKEY_TEST_CONNECTION_STRINGS</c>.</summary>
public sealed class ValkeyFixture() : ServerFixture(
    "Valkey",
    true,
    "VALKEY_TEST_CONNECTION_STRINGS",
    "localhost:6382;localhost:6383;localhost:6384"
);
