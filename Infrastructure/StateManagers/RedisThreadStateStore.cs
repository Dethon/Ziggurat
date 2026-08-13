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
    private const string MigrationMarkerKey = "topics:migrated";

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    // Every key a topic owns carries the same expiry, refreshed on every write, so a purge takes
    // the whole conversation at once rather than in pieces.
    private readonly TimeSpan _expiration = retention.PurgeHorizon;

    private readonly TopicSearchDocuments _searchDocuments = new(redis, retention.PurgeHorizon);

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

    // The stamp runs on the Agent host while mark-read runs on the channel host, against the same
    // record. Each patches only its own fields, inside Redis, because a whole-record
    // read-modify-write loses the other writer's fields on the designed flow: mark-read fires
    // while the reply is being stamped, and the viewed conversation comes back badged unread.
    private const string StampScript =
        """
        local json = redis.call('GET', KEYS[1])
        if not json then return nil end
        local topic = cjson.decode(json)
        topic['LastMessageAt'] = ARGV[1]
        topic['LastWriteAt'] = ARGV[1]
        if ARGV[2] ~= '' then topic['LastMessageSnippet'] = ARGV[2] end
        topic['MessageCount'] = (topic['MessageCount'] or 0) + tonumber(ARGV[3])
        local updated = cjson.encode(topic)
        redis.call('SET', KEYS[1], updated, 'PX', ARGV[4])
        return updated
        """;

    private const string MarkReadScript =
        """
        local json = redis.call('GET', KEYS[1])
        if not json then return nil end
        local topic = cjson.decode(json)
        topic['ReadPosition'] = topic['MessageCount'] or 0
        local updated = cjson.encode(topic)
        redis.call('SET', KEYS[1], updated, 'PX', ARGV[1])
        return updated
        """;

    // Every retention decision reads this one value, so it is stamped wherever a topic's history
    // is appended to rather than by the browser's streaming handler alone: a conversation nobody
    // is watching is ordered and retained on the same terms as one that is. The row's preview is
    // written here too, on the same pass, so drawing a row costs no history read.
    private async Task StampLastWriteAsync(string historyKey, IReadOnlyList<ChatMessage> appended)
    {
        if (AgentKey.ChatConversationParts(historyKey) is not { } key)
        {
            return;
        }

        var (agentId, chatId, threadId) = key;

        var topicId = await _db.StringGetAsync(ConversationKey(agentId, chatId, threadId));
        if (topicId.IsNullOrEmpty)
        {
            return;
        }

        var result = await _db.ScriptEvaluateAsync(
            StampScript,
            [TopicKey(agentId, chatId, topicId.ToString())],
            [
                time.GetUtcNow().ToString("O"),
                Snippet(appended) ?? "",

                // Counted in the same messages a reader is shown, so a badge means what a person
                // would count on screen. The raw list holds tool and system turns too, and one
                // reply with six tool calls would otherwise badge as seven unread.
                Readable(appended).Count(),
                (long)_expiration.TotalMilliseconds
            ]);

        if (result.IsNull)
        {
            return;
        }

        var stamped = JsonSerializer.Deserialize<TopicMetadata>(result.ToString())!;
        await _db.KeyExpireAsync(ConversationKey(agentId, chatId, threadId), _expiration);
        await IndexAsync(stamped);

        // What the conversation is searched by grows on the same write that moved it, so it is
        // never a separate thing to remember to maintain.
        await _searchDocuments.WriteAsync(stamped, Text(appended));
    }

    // The messages a reader is shown: what the transcript renders, what a badge counts and what
    // a conversation is searched by are all the same set. Role alone is not enough — a tool-using
    // turn persists assistant-role function calls with nothing to render, and a message carrying
    // only files must still survive, or an image-only message would vanish on reload.
    private static IEnumerable<ChatMessage> Readable(IEnumerable<ChatMessage> messages) =>
        messages.Where(m =>
            (m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            && (!string.IsNullOrWhiteSpace(TextOf(m)) || m.GetAttachments() is { Count: > 0 }));

    private static string TextOf(ChatMessage message) =>
        string.Join("", message.Contents.OfType<TextContent>().Select(c => c.Text));

    private static string Text(IEnumerable<ChatMessage> messages) =>
        string.Join(
            ' ',
            messages.Select(TextOf).Where(t => !string.IsNullOrWhiteSpace(t)));

    // Asked of the store rather than told by a browser, because the browser's idea of the count
    // is always one round trip behind: a reply that landed while it was reading would come back
    // as unread on the conversation the person is looking at. The read position is set from the
    // count inside the script, so a stamp landing between a read and this write loses nothing.
    public async Task MarkTopicReadAsync(string agentId, long chatId, string topicId)
    {
        var result = await _db.ScriptEvaluateAsync(
            MarkReadScript,
            [TopicKey(agentId, chatId, topicId)],
            [(long)_expiration.TotalMilliseconds]);

        if (result.IsNull)
        {
            return;
        }

        var topic = JsonSerializer.Deserialize<TopicMetadata>(result.ToString())!;
        await _db.KeyExpireAsync(ConversationKey(agentId, chatId, topic.ThreadId), _expiration);
        await IndexAsync(topic);
        await _searchDocuments.WriteAsync(topic);
    }

    // The last thing said that had anything to say. A message carrying only files leaves the
    // previous preview standing rather than blanking the row.
    private string? Snippet(IEnumerable<ChatMessage> messages)
    {
        var text = messages
            .Select(TextOf)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return text is null
            ? null
            : text.Length > retention.SnippetLength ? text[..retention.SnippetLength] + "…" : text;
    }

    private static RedisValue[] Serialize(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => (RedisValue)JsonSerializer.Serialize(m)).ToArray();

    public async Task<TopicPage> GetTopicPageAsync(
        string agentId, string spaceSlug, string? cursor, int pageSize, bool archived = false)
    {
        await TrimPurgedAsync(agentId, spaceSlug);

        var members = await ReadIndexRangeAsync(agentId, spaceSlug, cursor, pageSize, archived);
        var topics = await ResolveAsync(agentId, members);

        // Decided on what the index held, not on what resolved: a member whose record is gone is
        // one fewer row, not the end of the range, and counting rows would stop the list short of
        // everything below it. The cursor is the last member's score and name for the same reason.
        return new TopicPage(
            topics,
            members.Count < pageSize ? null : Cursor(members[^1]));
    }

    private async Task<IReadOnlyList<TopicMetadata>> ResolveAsync(
        string agentId, IReadOnlyList<SortedSetEntry> members)
    {
        var topics = await Task.WhenAll(
            members.Select(m => ReadIndexedTopicAsync(agentId, m.Element.ToString())));

        return topics.OfType<TopicMetadata>().ToList();
    }

    // Score plus member, because the score alone cannot say where inside a run of tied scores a
    // page ended: two topics written the same millisecond share one, and an exclusive stop on it
    // would lose whichever tied row the page break split off.
    private static string Cursor(SortedSetEntry member) =>
        $"{member.Score.ToString(CultureInfo.InvariantCulture)}:{member.Element}";

    // The one read path. Members are ordered by last write, so the order the sidebar shows is the
    // order the structure already holds and nothing sorts in memory. Paging is keyset paging over
    // that same order: the cursor is the last row's score and member, and the next page starts
    // right below that row.
    //
    // Archived is a position in this range and never a field on a topic. The cutoff is subtracted
    // from the current time here, so changing the horizon takes effect on the next read with no
    // migration and no backfill, and the two ranges partition the index exactly.
    private async Task<IReadOnlyList<SortedSetEntry>> ReadIndexRangeAsync(
        string agentId, string spaceSlug, string? cursor, int take, bool archived)
    {
        var cutoff = (time.GetUtcNow() - retention.ArchiveHorizon).ToUnixTimeMilliseconds();
        var key = IndexKey(agentId, spaceSlug);
        var from = ParseCursor(cursor);

        // The cursor's own score is read again inclusively — it may still hold tied rows the page
        // break split off — and what the previous page already showed is dropped by member. The
        // over-fetch covers exactly the tied members that may be dropped.
        var served = from is null
            ? 0L
            : await _db.SortedSetLengthAsync(key, from.Value.Score, from.Value.Score);

        var entries = await _db.SortedSetRangeByScoreWithScoresAsync(
            key,
            start: archived ? double.NegativeInfinity : cutoff,
            stop: from?.Score ?? (archived ? cutoff : double.PositiveInfinity),

            // The archived range's own top is the cutoff, which belongs to the ordinary list, so
            // its stop is exclusive when no cursor supplied it.
            exclude: archived && from is null ? Exclude.Stop : Exclude.None,
            order: Order.Descending,
            skip: 0,
            take: take + served);

        // Equal scores order members reverse-lexicographically, so at the cursor's score
        // everything at or above the cursor member has been served already.
        return entries
            .Where(e => from is not { } c
                || e.Score < c.Score
                || string.CompareOrdinal(e.Element.ToString(), c.Member) < 0)
            .Take(take)
            .ToList();
    }

    private static (double Score, string Member)? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        // A cursor issued before the member rode along is a bare score; an empty member skips the
        // whole tied run, which is exactly what that cursor always did.
        var separator = cursor.IndexOf(':');
        var scorePart = separator < 0 ? cursor : cursor[..separator];
        return double.TryParse(scorePart, CultureInfo.InvariantCulture, out var score)
            ? (score, separator < 0 ? "" : cursor[(separator + 1)..])
            : null;
    }

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
        await _searchDocuments.WriteAsync(topic);
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
            await _searchDocuments.DeleteAsync(topic);
        }
    }

    // Both ranges at once, deliberately: search is the way into the archive, so a cutoff has no
    // business in it.
    public async Task<TopicPage> SearchTopicsAsync(
        string agentId, string spaceSlug, string query, string? cursor, int pageSize)
    {
        var hits = await _searchDocuments.SearchAsync(agentId, spaceSlug, query, cursor, pageSize);

        var topics = await Task.WhenAll(
            hits.Select(hit => ReadTopicAsync(TopicKey(agentId, hit.ChatId, hit.TopicId))));

        var found = topics.OfType<TopicMetadata>().ToList();
        return new TopicPage(
            found,
            hits.Count < pageSize ? null : TopicSearchDocuments.CursorFor(hits[^1]));
    }

    // Once per deployment, from the Agent host. Conversations stored before the index existed
    // have no member in it, and a channel reading only the index would serve an empty sidebar
    // for them forever. This is the one place a scan of the keyspace still happens, and it is
    // what makes the listing scan removable.
    public async Task MigrateTopicsAsync(CancellationToken ct = default)
    {
        // A rerun is not harmless: the backfill clears every badge earned since the first run,
        // every TTL stretches to a fresh horizon, and the search documents append the whole
        // history again. The marker is what makes "once" true across restarts.
        if (await _db.KeyExistsAsync(MigrationMarkerKey))
        {
            return;
        }

        await foreach (var key in _server.KeysAsync(pattern: "topic:*").WithCancellation(ct))
        {
            if (await ReadTopicAsync(key!) is { } topic)
            {
                var backfilled = await BackfillAsync(topic);
                await SaveTopicAsync(backfilled);
                await _db.KeyExpireAsync(HistoryKey(topic), _expiration);

                // A conversation stored before the index existed has nothing to be found by, so
                // its document is built from the history it already has.
                await _searchDocuments.WriteAsync(
                    backfilled, Text(await GetMessagesAsync(HistoryKey(topic)) ?? []));
            }
        }

        // Set on completion rather than on entry, so a run that died half-way is retried by the
        // next start rather than remembered as done.
        await _db.StringSetAsync(MigrationMarkerKey, time.GetUtcNow().ToString("O"));
    }

    // Everything a record written before the index carries none of. The row's preview comes off
    // the tail of the history rather than the whole of it, because the preview is one message.
    private async Task<TopicMetadata> BackfillAsync(TopicMetadata topic)
    {
        var stored = await GetMessagesAsync(HistoryKey(topic)) ?? [];
        var count = Readable(stored).LongCount();
        var tail = stored.TakeLast(20).ToArray();

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

        // Projected from the same set a badge counts, so unread never says more than the
        // transcript shows.
        return messages is null
            ? []
            : Readable(messages)
                .Select(m => new ChatHistoryMessage(
                    m.MessageId,
                    m.Role.Value,
                    TextOf(m),
                    m.GetSenderId(),
                    m.GetTimestamp(),
                    m.GetAttachments()))
                .ToList();
    }

    public Task<TopicMetadata?> GetTopicAsync(string agentId, long chatId, string topicId) =>
        ReadTopicAsync(TopicKey(agentId, chatId, topicId));

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

    // Shared with the search documents, so a topic sorts identically in the index and in a
    // search's results.
    internal static double LastWriteScore(TopicMetadata topic)
    {
        return topic.LastWriteAt.ToUnixTimeMilliseconds();
    }
}