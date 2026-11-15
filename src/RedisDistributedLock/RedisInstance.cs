using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace RedisDistributedLock;

/// <param name="multiplexer">The connection, which may still be connecting.</param>
/// <param name="endPoints">The endpoints rather than the configuration string, which may contain a password.</param>
internal sealed class RedisInstance(Task<IConnectionMultiplexer> multiplexer, IEnumerable<EndPoint> endPoints)
    : IRedisInstance
{
    private const string ReleaseScript = """
                                         local currentVal = redis.call('get', KEYS[1])
                                         if (currentVal == false) then
                                             return 1
                                         elseif currentVal == ARGV[1] then
                                             return redis.call('del', KEYS[1])
                                         else
                                             return 0
                                         end
                                         """;

    // Never recreates a missing key: it expired or was released, so another client may have acquired it since
    private const string ExtendScript = """
                                        local currentVal = redis.call('get', KEYS[1])
                                        if (currentVal == false) then
                                            return 0
                                        elseif (currentVal == ARGV[1]) then
                                            return redis.call('pexpire', KEYS[1], ARGV[2])
                                        else
                                            return -1
                                        end
                                        """;

    private const string ServerInfoScript = "return redis.call('info', 'server')";

    private const string RunIdField = "run_id:";

    public RedisInstance(IConnectionMultiplexer multiplexer)
        : this(Task.FromResult(multiplexer), multiplexer.GetEndPoints())
    {
    }

    public string Name { get; } = string.Join(",", endPoints.Select(endPoint => endPoint.ToString()));

    public async Task<IReadOnlyCollection<string>> GetServerIdsAsync()
    {
        var connection = await multiplexer.ConfigureAwait(false);
        string info;
        try
        {
            // Through a script: the client only sends INFO itself on connections that allow admin commands
            info =
                (string?)await connection.GetDatabase().ScriptEvaluateAsync(ServerInfoScript).ConfigureAwait(false) ??
                string.Empty;
        }
        catch (RedisServerException)
        {
            // The server answered, but doesn't let us ask, e.g. because of its access control list
            return [];
        }

        var serverIds = new List<string>();
        using var reader = new StringReader(info);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(RunIdField, StringComparison.Ordinal))
            {
                serverIds.Add(line.Substring(RunIdField.Length).Trim());
            }
        }

        return serverIds;
    }

    public async Task<bool> TryAcquireAsync(string key, byte[] value, TimeSpan ttl)
    {
        var connection = await multiplexer.ConfigureAwait(false);
        return await connection.GetDatabase().StringSetAsync(key, value, ttl, When.NotExists).ConfigureAwait(false);
    }

    public async Task<bool> TryExtendAsync(string key, byte[] value, TimeSpan ttl)
    {
        var connection = await multiplexer.ConfigureAwait(false);
        RedisKey[] keys = [key,];
        RedisValue[] values = [value, (long)ttl.TotalMilliseconds,];
        var result = (long)await connection.GetDatabase()
            .ScriptEvaluateAsync(ExtendScript, keys, values)
            .ConfigureAwait(false);
        return result == 1;
    }

    public async Task<bool> TryReleaseAsync(string key, byte[] value)
    {
        var connection = await multiplexer.ConfigureAwait(false);
        RedisKey[] keys = [key,];
        RedisValue[] values = [value,];
        var result = (long)await connection.GetDatabase()
            .ScriptEvaluateAsync(ReleaseScript, keys, values)
            .ConfigureAwait(false);
        return result == 1;
    }
}
