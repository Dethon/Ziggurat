using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// One hash per owner, agents:custom:{householdId}:{userId}, an agent id to its definition. The
// owner is in the key rather than beside the id, so a lookup can only ever reach its own owner's
// hash. No expiry: an agent lasts until its person deletes it.
public sealed class RedisCustomAgentStore(IConnectionMultiplexer redis) : ICustomAgentStore
{
    private const string KeyPrefix = "agents:custom:";

    public static string KeyFor(Account owner) => $"{KeyPrefix}{owner.TenantId}:{owner.UserId}";

    public async Task SaveAsync(Account owner, AgentDefinition definition, CancellationToken ct = default) =>
        await redis.GetDatabase().HashSetAsync(KeyFor(owner), definition.Id, JsonSerializer.Serialize(definition));

    public async Task RemoveAsync(Account owner, string agentId, CancellationToken ct = default) =>
        await redis.GetDatabase().HashDeleteAsync(KeyFor(owner), agentId);

    // Read once, when the agent starts. Household and user ids are hex, so neither carries the
    // separator; a key that does not split into the two is not one of these and is skipped.
    public async Task<IReadOnlyList<OwnedAgent>> LoadAllAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var keys = await redis.GetServer(redis.GetEndPoints()[0])
            .KeysAsync(db.Database, $"{KeyPrefix}*")
            .ToListAsync(ct);

        var owned = await Task.WhenAll(keys.Select(async key =>
        {
            if (OwnerOf(key) is not { } owner)
            {
                return [];
            }

            var entries = await db.HashGetAllAsync(key);
            return entries
                .Select(entry => JsonSerializer.Deserialize<AgentDefinition>(entry.Value.ToString()))
                .OfType<AgentDefinition>()
                .Select(definition => new OwnedAgent(owner, definition));
        }));

        return owned.SelectMany(agents => agents).ToList();
    }

    private static Account? OwnerOf(RedisKey key) =>
        key.ToString()[KeyPrefix.Length..].Split(':') is [{ Length: > 0 } householdId, { Length: > 0 } userId]
            ? new Account(householdId, userId)
            : null;
}