using System.Globalization;
using StackExchange.Redis;

namespace McpChannelTelegram.Services;

// Which agent a chat on the shared bot picked with /agent, kept per household and chat so it outlives
// a restart. None is a chat nobody picked in, which the household's default answers.
public interface IChatAgentChoices
{
    Task<string?> GetAsync(string householdId, long chatId, CancellationToken ct);

    Task SetAsync(string householdId, long chatId, string agentId, CancellationToken ct);
}

// Keyed by the household as every shared store is, so a choice is only ever read for the household it
// was made among.
public sealed class RedisChatAgentChoices(IConnectionMultiplexer redis) : IChatAgentChoices
{
    public async Task<string?> GetAsync(string householdId, long chatId, CancellationToken ct) =>
        await redis.GetDatabase().StringGetAsync(Key(householdId, chatId)) is { HasValue: true } value
            ? value.ToString()
            : null;

    public Task SetAsync(string householdId, long chatId, string agentId, CancellationToken ct) =>
        redis.GetDatabase().StringSetAsync(Key(householdId, chatId), agentId);

    private static RedisKey Key(string householdId, long chatId) =>
        $"telegram:chat-agent@{householdId}:{chatId.ToString(CultureInfo.InvariantCulture)}";
}