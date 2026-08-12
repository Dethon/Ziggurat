using System.Globalization;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(
    IConnectionMultiplexer redis, RetentionSettings retention, TimeProvider time)
    : IThreadStateStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    // Every key a topic owns carries the same expiry, refreshed on every write, so a purge takes
    // the whole conversation at once rather than in pieces.
    private readonly TimeSpan _expiration = retention.PurgeHorizon;

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
        await _db.KeyExpireAsync(key, _expiration);
    }

    public async Task AppendMessagesAsync(string key, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await _db.ListRightPushAsync(key, Serialize(messages));
        await _db.KeyExpireAsync(key, _expiration);
        await StampLastWriteAsync(key, messages);
    }

    // Every retention decision reads this one value, so it is stamped wherever a topic's history
    // is appended to rather than by the browser's streaming handler alone: a conversation nobody
    // is watching is ordered and retained on the same terms as one that is. The row's preview is
    // written here too, on the same pass, so drawing a row costs no history read.
    private async Task StampLastWriteAsync(string historyKey, IReadOnlyList<ChatMessage> appended)
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

        await SaveTopicAsync(topic with
        {
            LastMessageAt = time.GetUtcNow(),
            LastMessageSnippet = Snippet(appended) ?? topic.LastMessageSnippet,
            MessageCount = await _db.ListLengthAsync(historyKey)
        });
    }

    // Asked of the store rather than told by a browser, because the browser's idea of the count
    // is always one round trip behind: a reply that landed while it was reading would come back
    // as unread on the conversation the person is looking at.
    public async Task MarkTopicReadAsync(string agentId, long chatId, string topicId)
    {
        if (await ReadTopicAsync(TopicKey(agentId, chatId, topicId)) is not { } topic)
        {
            return;
        }

        await SaveTopicAsync(topic with { ReadPosition = await _db.ListLengthAsync(HistoryKey(topic)) });
    }

    // The last thing said that had anything to say. A message carrying only files leaves the
    // previous preview standing rather than blanking the row.
    private string? Snippet(IEnumerable<ChatMessage> messages)
    {
        var text = messages
            .Select(m => string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text)))
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return text is null
            ? null
            : text.Length > retention.SnippetLength ? text[..retention.SnippetLength] : text;
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
        string agentId, string spaceSlug, string? cursor, int pageSize, bool archived = false)
    {
        await TrimPurgedAsync(agentId, spaceSlug);

        var topics = await ReadIndexRangeAsync(agentId, spaceSlug, cursor, pageSize, archived);

        // A short page is the end of the range, so the client stops asking rather than finding
        // out by getting nothing back.
        return new TopicPage(
            topics,
            topics.Count < pageSize ? null : Cursor(topics[^1]));
    }

    // The one read path. Members are ordered by last write, so the order the sidebar shows is the
    // order the structure already holds and nothing sorts in memory. Paging is keyset paging over
    // that same order: the cursor is the last row's score, and the next page starts below it.
    //
    // Archived is a position in this range and never a field on a topic. The cutoff is subtracted
    // from the current time here, so changing the horizon takes effect on the next read with no
    // migration and no backfill, and the two ranges partition the index exactly.
    private async Task<IReadOnlyList<TopicMetadata>> ReadIndexRangeAsync(
        string agentId, string spaceSlug, string? cursor, int take, bool archived)
    {
        var cutoff = (time.GetUtcNow() - retention.ArchiveHorizon).ToUnixTimeMilliseconds();
        var members = await _db.SortedSetRangeByScoreAsync(
            IndexKey(agentId, spaceSlug),
            start: archived ? double.NegativeInfinity : cutoff,
            stop: ParseCursor(cursor) ?? (archived ? cutoff : double.PositiveInfinity),

            // The archived range's own top is the cutoff, which belongs to the ordinary list, so
            // its stop is exclusive whether or not a cursor supplied it.
            exclude: archived || cursor is not null ? Exclude.Stop : Exclude.None,
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
        await _db.StringSetAsync(TopicKey(topic.AgentId, topic.ChatId, topic.TopicId), json, _expiration);

        // The conversation a history key names, pointing at the topic that owns it. Without it
        // the only way from a conversation back to its topic is a scan of every key in the
        // database, which is the cost this whole area exists to remove.
        await _db.StringSetAsync(
            ConversationKey(topic.AgentId, topic.ChatId, topic.ThreadId), topic.TopicId, _expiration);

        await IndexAsync(topic);
    }

    // Key expiry drops a purged topic's record and leaves its index member behind. Those members
    // sit below the archive horizon where nothing reads, so nothing would ever notice them. The
    // score is last write, which makes everything below the purge cutoff expired by definition:
    // one range removal, no scan, and no job to run it.
    private Task TrimPurgedAsync(string agentId, string spaceSlug) =>
        _db.SortedSetRemoveRangeByScoreAsync(
            IndexKey(agentId, spaceSlug),
            double.NegativeInfinity,
            (time.GetUtcNow() - retention.PurgeHorizon).ToUnixTimeMilliseconds());

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
                await SaveTopicAsync(await BackfillAsync(topic));
                await _db.KeyExpireAsync(HistoryKey(topic), _expiration);
            }
        }
    }

    // Everything a record written before the index carries none of. The row's preview comes off
    // the tail of the history rather than the whole of it, because the preview is one message.
    private async Task<TopicMetadata> BackfillAsync(TopicMetadata topic)
    {
        var count = await _db.ListLengthAsync(HistoryKey(topic));
        var tail = await GetTailMessagesAsync(HistoryKey(topic), 20);

        return topic with
        {
            LastMessageSnippet = topic.LastMessageSnippet ?? Snippet(tail ?? []),
            MessageCount = count,

            // Nobody's stored read position survives the change of meaning. Every topic is
            // marked read once, here, rather than resolved: a sidebar of badges nobody earned
            // is worse than none at all.
            ReadPosition = count
        };
    }

    private static string HistoryKey(TopicMetadata topic) =>
        new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString();

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