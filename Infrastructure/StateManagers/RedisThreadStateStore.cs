using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis, TimeSpan expiration)
    : IThreadStateStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    public async Task DeleteAsync(AgentKey key)
    {
        await _db.KeyDeleteAsync(key.ToString());
    }

    public async Task<ChatMessage[]?> GetMessagesAsync(string key)
    {
        var values = await _db.ListRangeAsync(key);
        return values.Length == 0
            ? null
            : values.Select(v => JsonSerializer.Deserialize<ChatMessage>(v.ToString())!).ToArray();
    }

    public async Task<long> GetMessageCountAsync(string key)
    {
        return await _db.ListLengthAsync(key);
    }

    public async Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount)
    {
        var values = await _db.ListRangeAsync(key, -maxCount, -1);
        return values.Length == 0
            ? null
            : values.Select(v => JsonSerializer.Deserialize<ChatMessage>(v.ToString())!).ToArray();
    }

    public async Task SetMessagesAsync(string key, ChatMessage[] messages)
    {
        await _db.KeyDeleteAsync(key);
        if (messages.Length == 0)
        {
            return;
        }

        await _db.ListRightPushAsync(key, Serialize(messages));
        await _db.KeyExpireAsync(key, expiration);
    }

    public async Task AppendMessagesAsync(string key, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await _db.ListRightPushAsync(key, Serialize(messages));
        await _db.KeyExpireAsync(key, expiration);
    }

    private static RedisValue[] Serialize(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => (RedisValue)JsonSerializer.Serialize(m)).ToArray();

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
        await _db.StringSetAsync(TopicKey(topic.AgentId, topic.ChatId, topic.TopicId), json, expiration);
    }

    public async Task DeleteTopicAsync(string agentId, long chatId, string topicId)
    {
        await _db.KeyDeleteAsync(TopicKey(agentId, chatId, topicId));
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return await _db.KeyExistsAsync(key);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId)
    {
        var messages = await GetMessagesAsync(new AgentKey($"{chatId}:{threadId}", agentId).ToString());

        return messages is null
            ? []
            : messages
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

    public async Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(
        string agentId, long chatId, long threadId, CancellationToken ct = default)
    {
        var topics = await GetAllTopicsAsync(agentId);
        return topics.FirstOrDefault(t => t.ChatId == chatId && t.ThreadId == threadId);
    }

    private static string TopicKey(string agentId, long chatId, string topicId)
    {
        return $"topic:{agentId}:{chatId}:{topicId}";
    }
}