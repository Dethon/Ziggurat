using System.Globalization;
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

    public async Task<TopicPage> GetTopicPageAsync(
        string agentId, string spaceSlug, string? cursor, int pageSize)
    {
        var topics = await ReadIndexRangeAsync(agentId, spaceSlug, cursor, pageSize);

        // A short page is the end of the range, so the client stops asking rather than finding
        // out by getting nothing back.
        return new TopicPage(
            topics,
            topics.Count < pageSize ? null : Cursor(topics[^1]));
    }

    // The one read path. Members are ordered by last write, so the order the sidebar shows is the
    // order the structure already holds and nothing sorts in memory. Paging is keyset paging over
    // that same order: the cursor is the last row's score, and the next page starts below it.
    private async Task<IReadOnlyList<TopicMetadata>> ReadIndexRangeAsync(
        string agentId, string spaceSlug, string? cursor, int take)
    {
        var members = await _db.SortedSetRangeByScoreAsync(
            IndexKey(agentId, spaceSlug),
            start: double.NegativeInfinity,
            stop: ParseCursor(cursor) ?? double.PositiveInfinity,
            exclude: cursor is null ? Exclude.None : Exclude.Stop,
            order: Order.Descending,
            skip: 0,
            take: take);

        var topics = await Task.WhenAll(members.Select(m => ReadIndexedTopicAsync(agentId, m.ToString())));
        return topics.OfType<TopicMetadata>().ToList();
    }

    private static string Cursor(TopicMetadata topic) =>
        LastWriteScore(topic).ToString(CultureInfo.InvariantCulture);

    private static double? ParseCursor(string? cursor) =>
        double.TryParse(cursor, CultureInfo.InvariantCulture, out var score) ? score : null;

    private Task<TopicMetadata?> ReadIndexedTopicAsync(string agentId, string member)
    {
        var separator = member.IndexOf(':');
        return separator > 0 && long.TryParse(member[..separator], out var chatId)
            ? ReadTopicAsync(TopicKey(agentId, chatId, member[(separator + 1)..]))
            : Task.FromResult<TopicMetadata?>(null);
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

        await IndexAsync(topic);
    }

    private Task IndexAsync(TopicMetadata topic) =>
        _db.SortedSetAddAsync(
            IndexKey(topic.AgentId, topic.SpaceSlug), IndexMember(topic), LastWriteScore(topic));

    public async Task DeleteTopicAsync(string agentId, long chatId, string topicId)
    {
        var topic = await ReadTopicAsync(TopicKey(agentId, chatId, topicId));
        await _db.KeyDeleteAsync(TopicKey(agentId, chatId, topicId));

        if (topic is not null)
        {
            await _db.KeyDeleteAsync(ConversationKey(agentId, chatId, topic.ThreadId));
            await _db.SortedSetRemoveAsync(IndexKey(agentId, topic.SpaceSlug), IndexMember(topic));
        }
    }

    // Once per deployment, from the Agent host. Conversations stored before the index existed
    // have no member in it, and a channel reading only the index would serve an empty sidebar
    // for them forever. This is the one place a scan of the keyspace still happens, and it is
    // what makes the listing scan removable.
    public async Task MigrateTopicsAsync(CancellationToken ct = default)
    {
        await foreach (var key in _server.KeysAsync(pattern: "topic:*").WithCancellation(ct))
        {
            if (await ReadTopicAsync(key!) is { } topic)
            {
                await IndexAsync(topic);
                await _db.StringSetAsync(
                    ConversationKey(topic.AgentId, topic.ChatId, topic.ThreadId), topic.TopicId, expiration);
                await _db.KeyExpireAsync(key, expiration);
            }
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

    private static string IndexKey(string agentId, string spaceSlug)
    {
        return $"topics:{agentId}:{spaceSlug}";
    }

    // Carries the chat id because the topic key needs it, so reading a page never costs a lookup
    // to find out where each member's record lives.
    private static string IndexMember(TopicMetadata topic)
    {
        return $"{topic.ChatId}:{topic.TopicId}";
    }

    private static double LastWriteScore(TopicMetadata topic)
    {
        return (topic.LastMessageAt ?? topic.CreatedAt).ToUnixTimeMilliseconds();
    }
}