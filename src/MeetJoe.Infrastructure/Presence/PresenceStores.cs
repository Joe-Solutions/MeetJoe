using System.Collections.Concurrent;
using MeetJoe.Application.Common;
using StackExchange.Redis;

namespace MeetJoe.Infrastructure.Presence;

/// <summary>Connection tracking in process memory. Correct for a single API instance.</summary>
public sealed class InMemoryPresenceStore : IPresenceStore
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();

    public Task<bool> AddConnectionAsync(Guid userId, string connectionId)
    {
        var set = _connections.GetOrAdd(userId, _ => []);
        lock (set)
        {
            set.Add(connectionId);
            return Task.FromResult(set.Count == 1);
        }
    }

    public Task<bool> RemoveConnectionAsync(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var set)) return Task.FromResult(false);
        lock (set)
        {
            return Task.FromResult(set.Remove(connectionId) && set.Count == 0);
        }
    }

    public Task<IReadOnlySet<Guid>> GetOnlineAsync(IReadOnlyCollection<Guid> userIds)
    {
        var online = new HashSet<Guid>();
        foreach (var userId in userIds)
        {
            if (!_connections.TryGetValue(userId, out var set)) continue;
            lock (set)
            {
                if (set.Count > 0) online.Add(userId);
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(online);
    }
}

/// <summary>
/// Connection tracking in Redis: one set of connection ids per user. <see cref="ResetAsync"/>
/// clears it on startup, because the connections it describes ended with the previous process.
/// That is only correct while a single API instance runs.
/// </summary>
public sealed class RedisPresenceStore(IConnectionMultiplexer redis) : IPresenceStore
{
    private const string Prefix = "meetjoe:presence:";

    private IDatabase Db => redis.GetDatabase();

    private static RedisKey Key(Guid userId) => $"{Prefix}{userId:N}";

    public async Task<bool> AddConnectionAsync(Guid userId, string connectionId)
    {
        var key = Key(userId);
        await Db.SetAddAsync(key, connectionId);
        return await Db.SetLengthAsync(key) == 1;
    }

    public async Task<bool> RemoveConnectionAsync(Guid userId, string connectionId)
    {
        var key = Key(userId);
        var removed = await Db.SetRemoveAsync(key, connectionId);
        return removed && await Db.SetLengthAsync(key) == 0;
    }

    public async Task<IReadOnlySet<Guid>> GetOnlineAsync(IReadOnlyCollection<Guid> userIds)
    {
        var ids = userIds.ToArray();
        var batch = Db.CreateBatch();
        var lengths = ids.Select(id => batch.SetLengthAsync(Key(id))).ToArray();
        batch.Execute();
        await Task.WhenAll(lengths);

        var online = new HashSet<Guid>();
        for (var i = 0; i < ids.Length; i++)
            if (lengths[i].Result > 0)
                online.Add(ids[i]);
        return online;
    }

    public async Task ResetAsync()
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{Prefix}*"))
                await Db.KeyDeleteAsync(key);
        }
    }
}
