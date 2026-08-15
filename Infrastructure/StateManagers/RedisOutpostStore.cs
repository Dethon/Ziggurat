using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// One key per outpost registration, and Redis's own TTL as the expiry. There is no reaper, no
// sweep and no timer: a machine that stops asking simply stops existing, which is what losing
// power looks like from here.
//
// The index of names is a plain set with no expiry of its own, so it outlives the entries it
// points at. That is deliberate — a lapse has to have something to be noticed against, and the
// index member left behind by an expired key is the only evidence anywhere that the machine was
// ever there. Reading reports each such member once and then drops it, the same self-healing shape
// the download routing store uses.
public sealed class RedisOutpostStore(IConnectionMultiplexer redis) : IOutpostStore
{
    private const string IndexKey = "outposts";

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetAsync(
        OutpostRegistration registration, TimeSpan expiry, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.StringSetAsync(
            EntryKey(registration.Name), JsonSerializer.Serialize(registration), expiry);
        _ = transaction.SetAddAsync(IndexKey, registration.Name);
        await transaction.ExecuteAsync();
    }

    // KeyExpire against a key that is not there answers false, which is exactly the question the
    // caller is asking: a machine quiet long enough to lapse is re-registering, not refreshing.
    // The entry comes back with it because the keepalive's answer carries the verdict written onto
    // it, and asking twice would let a registration expire between the two calls.
    public async Task<OutpostRegistration?> RefreshAsync(
        string name, TimeSpan expiry, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        var refreshed = transaction.KeyExpireAsync(EntryKey(name), expiry);
        var entry = transaction.StringGetAsync(EntryKey(name));
        await transaction.ExecuteAsync();

        return await refreshed && !(await entry).IsNullOrEmpty
            ? JsonSerializer.Deserialize<OutpostRegistration>((await entry).ToString())
            : null;
    }

    // keepTtl, so a verdict landing does not extend a registration the machine is keeping alive on
    // its own schedule. A registration that lapsed between the read and the write is simply not
    // written back: the machine is gone, and reviving it here would resurrect a mount nobody holds.
    public async Task RecordVerdictAsync(
        string name, OutpostVerdict verdict, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(EntryKey(name));
        if (json.IsNullOrEmpty
            || JsonSerializer.Deserialize<OutpostRegistration>(json.ToString()) is not { } registration
            || registration.Verdict == verdict)
        {
            return;
        }

        await _db.StringSetAsync(
            EntryKey(name),
            JsonSerializer.Serialize(registration with { Verdict = verdict }),
            expiry: null,
            keepTtl: true,
            when: When.Exists);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        var deleted = transaction.KeyDeleteAsync(EntryKey(name));
        _ = transaction.SetRemoveAsync(IndexKey, name);
        await transaction.ExecuteAsync();
        return await deleted;
    }

    public async Task<OutpostSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var names = await _db.SetMembersAsync(IndexKey);
        var entries = await Task.WhenAll(names.Select(async name =>
        {
            var json = await _db.StringGetAsync(EntryKey(name!));
            return (Name: name.ToString(), Registration: json.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<OutpostRegistration>(json.ToString()));
        }));

        var lapsed = entries.Where(e => e.Registration is null).Select(e => e.Name).ToList();
        if (lapsed.Count > 0)
        {
            await _db.SetRemoveAsync(IndexKey, [.. lapsed.Select(name => (RedisValue)name)]);
        }

        return new OutpostSnapshot(
            [.. entries.Where(e => e.Registration is not null).Select(e => e.Registration!)],
            lapsed);
    }

    private static string EntryKey(string name) => $"outpost:{name}";
}