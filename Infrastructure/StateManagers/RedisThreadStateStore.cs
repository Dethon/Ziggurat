using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis, TimeSpan expiration, TimeProvider time)
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
        await StampLastWriteAsync(key);
    }

    // Every retention decision reads this one value, so it is stamped wherever a topic's history
    // is appended to rather than by the browser's streaming handler alone: a conversation nobody
    // is watching is ordered and retained on the same terms as one that is.
    private async Task StampLastWriteAsync(string historyKey)
    {
        if (HistoryKeyParts(historyKey) is not var (agentId, chatId, threadId))
        {
            return;
        }

        var topic = await GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId);
        if (topic is null)
        {
            return;
        }

        await SaveTopicAsync(topic with { LastMessageAt = time.GetUtcNow() });
    }

    // "agent-key:{agentId}:{chatId}:{threadId}", the one spelling AgentKey produces. Anything
    // else — the GUID a session falls back to when it has no key yet — belongs to no topic and
    // stamps nothing.
    private static (string AgentId, long ChatId, long ThreadId)? HistoryKeyParts(string historyKey)
    {
        const string prefix = "agent-key:";
        if (!historyKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = historyKey[prefix.Length..].Split(':');
        return parts.Length >= 3
               && long.TryParse(parts[^2], out var chatId)
               && long.TryParse(parts[^1], out var threadId)
            ? (string.Join(':', parts[..^2]), chatId, threadId)
            : null;
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

        // The conversation a history key names, pointing at the topic that owns it. Without it
        // the only way from a conversation back to its topic is a scan of every key in the
        // database, which is the cost this whole area exists to remove.
        await _db.StringSetAsync(
            ConversationKey(topic.AgentId, topic.ChatId, topic.ThreadId), topic.TopicId, expiration);
    }

    public async Task DeleteTopicAsync(string agentId, long chatId, string topicId)
    {
        var topic = await ReadTopicAsync(TopicKey(agentId, chatId, topicId));
        await _db.KeyDeleteAsync(TopicKey(agentId, chatId, topicId));

        if (topic is not null)
        {
            await _db.KeyDeleteAsync(ConversationKey(agentId, chatId, topic.ThreadId));
        }
    }

    private async Task<TopicMetadata?> ReadTopicAsync(string topicKey)
    {
        var json = await _db.StringGetAsync(topicKey);
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<TopicMetadata>(json.ToString());
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
        var topicId = await _db.StringGetAsync(ConversationKey(agentId, chatId, threadId));
        return topicId.IsNullOrEmpty
            ? null
            : await ReadTopicAsync(TopicKey(agentId, chatId, topicId.ToString()));
    }

    private static string TopicKey(string agentId, long chatId, string topicId)
    {
        return $"topic:{agentId}:{chatId}:{topicId}";
    }

    private static string ConversationKey(string agentId, long chatId, long threadId)
    {
        return $"topic-of:{agentId}:{chatId}:{threadId}";
    }
}