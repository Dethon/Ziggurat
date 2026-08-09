using System.Text.Json;
using Domain.Agents;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace McpChannelSignalR.Services;

public sealed class RedisStateService(IConnectionMultiplexer redis)
{
    private static readonly TimeSpan _expiration = TimeSpan.FromDays(30);

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string? spaceSlug = null)
    {
        var topics = new List<TopicMetadata>();

        await foreach (var key in _server.KeysAsync(pattern: $"topic:{agentId}:*"))
        {
            var json = await _db.StringGetAsync(key);
            if (json.IsNullOrEmpty)
            {
                continue;
            }

            var topic = JsonSerializer.Deserialize<TopicMetadata>(json.ToString());
            if (topic is not null)
            {
                topics.Add(topic);
            }
        }

        var filtered = spaceSlug is not null
            ? topics.Where(t => t.SpaceSlug == spaceSlug)
            : topics;

        return filtered.OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt).ToList();
    }

    public async Task SaveTopicAsync(TopicMetadata topic)
    {
        var json = JsonSerializer.Serialize(topic);
        await _db.StringSetAsync(TopicKey(topic.AgentId, topic.ChatId, topic.TopicId), json, _expiration);
    }

    public async Task DeleteTopicAsync(string agentId, long chatId, string topicId)
    {
        await _db.KeyDeleteAsync(TopicKey(agentId, chatId, topicId));
    }

    public async Task DeleteMessagesAsync(AgentKey agentKey)
    {
        await _db.KeyDeleteAsync(agentKey.ToString());
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId)
    {
        var key = new AgentKey($"{chatId}:{threadId}", agentId).ToString();

        // History is stored as a Redis List (one JSON ChatMessage per element),
        // matching RedisThreadStateStore.
        var values = await _db.ListRangeAsync(key);
        if (values.Length == 0)
        {
            return [];
        }

        return values
            .Select(v => JsonSerializer.Deserialize<ChatMessage>(v.ToString())!)
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .Select(m => new ChatHistoryMessage(
                m.MessageId,
                m.Role.Value,
                string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text)),
                m.GetSenderId(),
                m.GetTimestamp(),
                m.GetAttachments()))
            // A message that carried only files has no text and must still survive: dropping it
            // would make an image-only message vanish on reload.
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) || m.Attachments is { Count: > 0 })
            .ToList();
    }

    private static string TopicKey(string agentId, long chatId, string topicId)
    {
        return $"topic:{agentId}:{chatId}:{topicId}";
    }
}