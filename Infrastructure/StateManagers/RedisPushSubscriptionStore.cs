using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// A person's phones under `push:subs:{userId}` and each endpoint's owner under `push:ep:{endpoint}`,
// both per person; a household's space under `push:space@{tenantId}:{slug}`. The '@' keeps the space
// sets apart from the shape before households (`push:space:{slug}`), so the household migration's
// pattern for what is still unmoved can never match a household's.
public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";
    private const string EndpointPrefix = "push:ep:";
    private const string SpacePrefix = "push:space@";

    public static string SpaceKey(string tenantId, string spaceSlug) => $"{SpacePrefix}{tenantId}:{spaceSlug}";

    public async Task SaveAsync(Account person, PushSubscriptionDto subscription, string spaceSlug = "default",
        string? replacingEndpoint = null, CancellationToken ct = default)
    {
        var userId = person.UserId;
        var db = redis.GetDatabase();
        var endpointIndexKey = $"{EndpointPrefix}{subscription.Endpoint}";

        // Only the caller's own old phone is moved: another person's endpoint named here would carry
        // this phone into their spaces, another household's included.
        if (replacingEndpoint is not null && replacingEndpoint != subscription.Endpoint
            && await IsOwnedByAsync(db, replacingEndpoint, userId))
        {
            await TransferSpaceMembershipsAsync(db, replacingEndpoint, subscription.Endpoint);
            await db.HashDeleteAsync($"{KeyPrefix}{userId}", replacingEndpoint);
            await db.KeyDeleteAsync($"{EndpointPrefix}{replacingEndpoint}");
        }

        var previousOwner = await db.StringGetAsync(endpointIndexKey);
        if (previousOwner.HasValue)
        {
            if (previousOwner.ToString() != userId)
            {
                await db.HashDeleteAsync($"{KeyPrefix}{previousOwner}", subscription.Endpoint);

                // The spaces it was in were its last owner's household's, which may not be this one's:
                // left there, a push to that household's space would reach this person's phone.
                await RemoveFromAllSpaceSetsAsync(db, subscription.Endpoint);
            }
        }

        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth, SpaceSlug = spaceSlug });

        await db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
        await db.StringSetAsync(endpointIndexKey, userId);
        await db.SetAddAsync(SpaceKey(person.TenantId, spaceSlug), subscription.Endpoint);
    }

    private async Task TransferSpaceMembershipsAsync(IDatabase db, string oldEndpoint, string newEndpoint)
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{SpacePrefix}*"))
            {
                if (await db.SetContainsAsync(key, oldEndpoint))
                {
                    await db.SetAddAsync(key, newEndpoint);
                    await db.SetRemoveAsync(key, oldEndpoint);
                }
            }
        }
    }

    private async Task RemoveFromAllSpaceSetsAsync(IDatabase db, string endpoint)
    {
        foreach (var redisEndpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(redisEndpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{SpacePrefix}*"))
            {
                await db.SetRemoveAsync(key, endpoint);
            }
        }
    }

    private static async Task<bool> IsOwnedByAsync(IDatabase db, string endpoint, string userId) =>
        await db.StringGetAsync($"{EndpointPrefix}{endpoint}") is { HasValue: true } owner && owner.ToString() == userId;

    // A person unsubscribes their own phone alone: another's endpoint named here is left as it is.
    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        if (!await IsOwnedByAsync(db, endpoint, userId))
        {
            await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
            return;
        }

        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
        await db.KeyDeleteAsync($"{EndpointPrefix}{endpoint}");
        await RemoveFromAllSpaceSetsAsync(db, endpoint);
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var endpointIndexKey = $"{EndpointPrefix}{endpoint}";

        var userId = await db.StringGetAsync(endpointIndexKey);
        if (!userId.HasValue)
        {
            return;
        }

        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
        await db.KeyDeleteAsync(endpointIndexKey);
        await RemoveFromAllSpaceSetsAsync(db, endpoint);
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        // SCAN-based — only used in tests, not a hot path
        var db = redis.GetDatabase();
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in ScanKeysAsync())
        {
            ct.ThrowIfCancellationRequested();

            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await db.HashGetAllAsync(key);

            results.AddRange(entries.Select(entry =>
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value.ToString());
                return (userId, new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!));
            }));
        }

        return results;
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetBySpaceAsync(
        string tenantId, string spaceSlug, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var space = SpaceKey(tenantId, spaceSlug);
        var endpoints = await db.SetMembersAsync(space);
        var results = new List<(string, PushSubscriptionDto)>();

        foreach (var endpointValue in endpoints)
        {
            ct.ThrowIfCancellationRequested();

            var endpoint = endpointValue.ToString();
            var userId = await db.StringGetAsync($"{EndpointPrefix}{endpoint}");
            if (!userId.HasValue)
            {
                // Self-healing: stale index entry — remove from space set
                await db.SetRemoveAsync(space, endpoint);
                continue;
            }

            var hashValue = await db.HashGetAsync($"{KeyPrefix}{userId}", endpoint);
            if (!hashValue.HasValue)
            {
                // Self-healing: stale index entries — clean up both
                await db.SetRemoveAsync(space, endpoint);
                await db.KeyDeleteAsync($"{EndpointPrefix}{endpoint}");
                continue;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(hashValue.ToString());
            results.Add((userId.ToString(), new PushSubscriptionDto(
                endpoint,
                data.GetProperty("P256dh").GetString()!,
                data.GetProperty("Auth").GetString()!)));
        }

        return results;
    }

    private async IAsyncEnumerable<RedisKey> ScanKeysAsync()
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
            {
                yield return key;
            }
        }
    }
}