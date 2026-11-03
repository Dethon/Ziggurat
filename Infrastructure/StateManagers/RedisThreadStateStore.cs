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

// A topic is its household's, and every key that holds one says whose: the record
// `topic@{tenantId}:{agentId}:{chatId}:{topicId}`, the space's ordering `topics@{tenantId}:{agentId}:{slug}`
// and the search document (TopicSearchDocuments). A page, a search and a read by id all name the
// household, so the store can never answer one household with another's topic. The '@' keeps the shape
// apart from the one before households (`topic:{agentId}:...`, `topics:{agentId}:{slug}`), so the
// household migration's pattern for what is still unmoved can never match a household's.
//
// The conversation's pointer to its topic (`topic-of:{agentId}:{chatId}:{threadId}`) is the one key
// with no household in it, deliberately: a conversation's id is the same whichever household holds it,
// and the agent appending to a history knows only that id. Its value names the household beside the
// topic (`{tenantId}:{topicId}`), so the topic a conversation leads to says whose it is, and whoever
// asks by conversation still applies TopicViewer to it. Histories are keyed by conversation too.
//
// A topic recording no household — one a turn acting for nobody opened — is kept under NoHousehold,
// which no viewer is of, so nobody lists or searches it.
public sealed class RedisThreadStateStore(
    IConnectionMultiplexer redis, RetentionSettings retention, TimeProvider time)
    : IThreadStateStore
{
    // Not a household id (those are hex), so no viewer is ever of it.
    public const string NoHousehold = "none";

    private readonly IDatabase _db = redis.GetDatabase();

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

    public async Task DropTailAsync(string key, int count)
    {
        if (count <= 0 || await GetTailMessagesAsync(key, count) is not { } dropped)
        {
            return;
        }

        await _db.ListTrimAsync(key, 0, -(dropped.Length + 1));
        if (AgentKey.ChatConversationParts(key) is not { } parts
            || await HolderAsync(parts.AgentId, parts.ChatId, parts.ThreadId) is not (var household, var topicId))
        {
            return;
        }

        await _db.ScriptEvaluateAsync(
            _uncountScript, [TopicKey(household, parts.AgentId, parts.ChatId, topicId)], [Readable(dropped).Count()]);
    }

    // Lua numbers are doubles and a WebChat chat id is a 63-bit hash, so the id is noise from
    // the moment the record is decoded. Encoding writes that noise back over the record, after
    // which every read of the topic throws — so the identity fields cross the round trip as the
    // raw text they already were. Matching the quoted field name in the raw JSON is safe: inside
    // a string value its quotes would be escaped.
    private const string KeepWideIds =
        """
        local updated = cjson.encode(topic)
        for _, field in ipairs({'ChatId', 'ThreadId'}) do
            local wide = string.match(json, '"' .. field .. '":(%-?%d+)')
            if wide then
                updated = string.gsub(updated, '"' .. field .. '":[^,}]+', '"' .. field .. '":' .. wide, 1)
            end
        end
        """;

    // The stamp runs on the Agent host while mark-read runs on the channel host, against the same
    // record. Each patches only its own fields, inside Redis, because a whole-record
    // read-modify-write loses the other writer's fields on the designed flow: mark-read fires
    // while the reply is being stamped, and the viewed conversation comes back badged unread.
    private static readonly string _stampScript =
        $$"""
        local json = redis.call('GET', KEYS[1])
        if not json then return nil end
        local topic = cjson.decode(json)
        topic['LastMessageAt'] = ARGV[1]
        topic['LastWriteAt'] = ARGV[1]
        if ARGV[2] ~= '' then topic['LastMessageSnippet'] = ARGV[2] end
        topic['MessageCount'] = (topic['MessageCount'] or 0) + tonumber(ARGV[3])
        {{KeepWideIds}}
        redis.call('SET', KEYS[1], updated, 'PX', ARGV[4])
        return updated
        """;

    // A count taken back, for messages a retry replaced; a read position past what is left is what
    // is left. KEEPTTL: the append that follows stamps the write.
    private static readonly string _uncountScript =
        $$"""
        local json = redis.call('GET', KEYS[1])
        if not json then return nil end
        local topic = cjson.decode(json)
        local count = math.max(0, (topic['MessageCount'] or 0) - tonumber(ARGV[1]))
        topic['MessageCount'] = count
        if (topic['ReadPosition'] or 0) > count then topic['ReadPosition'] = count end
        {{KeepWideIds}}
        redis.call('SET', KEYS[1], updated, 'KEEPTTL')
        """;

    // KEEPTTL, because reading a conversation is not writing to it: a mark-read that reset the
    // clock would keep the record alive months past the history it describes.
    private static readonly string _markReadScript =
        $$"""
        local json = redis.call('GET', KEYS[1])
        if not json then return nil end
        local topic = cjson.decode(json)
        topic['ReadPosition'] = topic['MessageCount'] or 0
        {{KeepWideIds}}
        redis.call('SET', KEYS[1], updated, 'KEEPTTL')
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

        if (await HolderAsync(agentId, chatId, threadId) is not (var household, var topicId))
        {
            return;
        }

        var result = await _db.ScriptEvaluateAsync(
            _stampScript,
            [TopicKey(household, agentId, chatId, topicId)],
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
    // The record is all it touches: the index score is the last write, which reading is not.
    public async Task MarkTopicReadAsync(string tenantId, string agentId, long chatId, string topicId)
    {
        await _db.ScriptEvaluateAsync(_markReadScript, [TopicKey(tenantId, agentId, chatId, topicId)]);
    }

    // The last thing said that had anything to say, as text — a reply's markdown read, not shown
    // raw in the list. A message carrying only files leaves the previous preview standing rather
    // than blanking the row.
    private string? Snippet(IEnumerable<ChatMessage> messages)
    {
        var text = messages
            .Select(m => TextOf(m).MarkdownToPlainText())
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

        return text is null
            ? null
            : text.Length > retention.SnippetLength ? text[..retention.SnippetLength] + "…" : text;
    }

    private static RedisValue[] Serialize(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => (RedisValue)JsonSerializer.Serialize(m)).ToArray();

    public async Task<TopicPage> GetTopicPageAsync(
        string agentId, string spaceSlug, TopicViewer viewer, string? cursor, int pageSize, bool archived = false,
        IReadOnlyList<string>? alsoSpaces = null)
    {
        IReadOnlyList<string> slugs = [spaceSlug, .. alsoSpaces ?? []];
        await Task.WhenAll(slugs.Select(slug => TrimPurgedAsync(viewer.TenantId, agentId, slug)));

        return await FillPageAsync(
            viewer, cursor, pageSize,
            from => ReadIndexRangesAsync(viewer.TenantId, agentId, slugs, from, pageSize, archived),
            member => ReadIndexedTopicAsync(viewer.TenantId, agentId, member.Element.ToString()),
            Cursor);
    }

    // Several spaces' orderings read as one: each read from the same cursor, then merged in the
    // order a single sorted set answers — score descending, ties by member descending — so the
    // cursor of the merged run is a cursor of every one of them. A topic is indexed under one space
    // alone, so no member is read twice.
    private async Task<IReadOnlyList<SortedSetEntry>> ReadIndexRangesAsync(
        string household, string agentId, IReadOnlyList<string> slugs, string? cursor, int take, bool archived)
    {
        if (slugs.Count == 1)
        {
            return await ReadIndexRangeAsync(household, agentId, slugs[0], cursor, take, archived);
        }

        var runs = await Task.WhenAll(slugs.Select(slug => ReadIndexRangeAsync(household, agentId, slug, cursor, take, archived)));
        return
        [
            .. runs.SelectMany(run => run)
                .OrderByDescending(e => e.Score)
                .ThenByDescending(e => e.Element.ToString(), StringComparer.Ordinal)
                .Take(take)
        ];
    }

    // One page of what the viewer may see, read a run of the ordering at a time until the page is
    // full or the ordering runs out. The end is decided on what the run held, not on what resolved
    // or what the viewer was shown: a member whose record is gone, or a topic the viewer may not
    // know exists, is one fewer row and not the end of the range, and counting rows would stop the
    // list short of everything below it. The cursor is the entry the page filled on, so the next
    // page starts right below the last row shown and passes over nothing twice.
    private static async Task<TopicPage> FillPageAsync<TEntry>(
        TopicViewer viewer,
        string? cursor,
        int pageSize,
        Func<string?, Task<IReadOnlyList<TEntry>>> readRun,
        Func<TEntry, Task<TopicMetadata?>> resolve,
        Func<TEntry, string> cursorOf)
    {
        List<TopicMetadata> shown = [];
        var from = cursor;
        while (pageSize > 0)
        {
            var run = await readRun(from);
            var resolved = await Task.WhenAll(run.Select(resolve));
            foreach (var (entry, topic) in run.Zip(resolved))
            {
                if (topic is not null && viewer.Sees(topic))
                {
                    shown.Add(topic);
                }

                if (shown.Count == pageSize)
                {
                    return new TopicPage(shown, cursorOf(entry));
                }
            }

            if (run.Count < pageSize)
            {
                break;
            }

            // A run that ends where it began cannot move the page on; ending short beats spinning.
            var next = cursorOf(run[^1]);
            if (next == from)
            {
                break;
            }

            from = next;
        }

        return new TopicPage(shown, null);
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
        string household, string agentId, string spaceSlug, string? cursor, int take, bool archived)
    {
        var cutoff = (time.GetUtcNow() - retention.ArchiveHorizon).ToUnixTimeMilliseconds();
        var key = IndexKey(household, agentId, spaceSlug);
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

    private Task<TopicMetadata?> ReadIndexedTopicAsync(string household, string agentId, string member)
    {
        var separator = member.IndexOf(':');
        return separator > 0 && long.TryParse(member[..separator], out var chatId)
            ? ReadTopicAsync(TopicKey(household, agentId, chatId, member[(separator + 1)..]))
            : Task.FromResult<TopicMetadata?>(null);
    }

    public async Task SaveTopicAsync(TopicMetadata topic, bool keepTtl = false)
    {
        var json = JsonSerializer.Serialize(topic);
        var household = HouseholdOf(topic);
        await _db.StringSetAsync(
            TopicKey(household, topic.AgentId, topic.ChatId, topic.TopicId), json,
            expiry: keepTtl ? null : _expiration, keepTtl: keepTtl);

        // The conversation a history key names, pointing at the topic that owns it. Without it
        // the only way from a conversation back to its topic is a scan of every key in the
        // database, which is the cost this whole area exists to remove.
        await _db.StringSetAsync(
            ConversationKey(topic.AgentId, topic.ChatId, topic.ThreadId), Holder(household, topic.TopicId),
            expiry: keepTtl ? null : _expiration, keepTtl: keepTtl);

        await IndexAsync(topic);
        await _searchDocuments.WriteAsync(topic, keepTtl: keepTtl);
    }

    // Key expiry drops a purged topic's record and leaves its index member behind. Those members
    // sit below the archive horizon where nothing reads, so nothing would ever notice them. The
    // score is last write, which makes everything below the purge cutoff expired by definition:
    // one range removal, no scan, and no job to run it.
    private Task TrimPurgedAsync(string household, string agentId, string spaceSlug) =>
        _db.SortedSetRemoveRangeByScoreAsync(
            IndexKey(household, agentId, spaceSlug),
            double.NegativeInfinity,
            (time.GetUtcNow() - retention.PurgeHorizon).ToUnixTimeMilliseconds());

    private Task IndexAsync(TopicMetadata topic) =>
        _db.SortedSetAddAsync(
            IndexKey(HouseholdOf(topic), topic.AgentId, topic.SpaceSlug), IndexMember(topic), LastWriteScore(topic));

    public async Task DeleteTopicAsync(string tenantId, string agentId, long chatId, string topicId)
    {
        var topic = await ReadTopicAsync(TopicKey(tenantId, agentId, chatId, topicId));
        await _db.KeyDeleteAsync(TopicKey(tenantId, agentId, chatId, topicId));

        if (topic is not null)
        {
            // The pointer goes only while it still leads here: a conversation is one household's
            // topic, and deleting a record of another's is no reason to cut that one off.
            if (await HolderAsync(agentId, chatId, topic.ThreadId) == (tenantId, topicId))
            {
                await _db.KeyDeleteAsync(ConversationKey(agentId, chatId, topic.ThreadId));
            }

            await _db.SortedSetRemoveAsync(IndexKey(tenantId, agentId, topic.SpaceSlug), IndexMember(topic));
            await _searchDocuments.DeleteAsync(topic);
        }
    }

    // Both ranges at once, deliberately: search is the way into the archive, so a cutoff has no
    // business in it.
    public Task<TopicPage> SearchTopicsAsync(
        string agentId, string spaceSlug, TopicViewer viewer, string query, string? cursor, int pageSize,
        IReadOnlyList<string>? alsoSpaces = null) =>
        FillPageAsync(
            viewer, cursor, pageSize,
            from => _searchDocuments.SearchAsync(viewer.TenantId, agentId, [spaceSlug, .. alsoSpaces ?? []], query, from, pageSize),
            hit => ReadTopicAsync(TopicKey(viewer.TenantId, agentId, hit.ChatId, hit.TopicId)),
            TopicSearchDocuments.CursorFor);

    // A scan of the household's orderings, since nothing else names a slug the household no longer
    // has: the hub asks once as a connection joins its default space, not per page.
    public async Task<IReadOnlyList<string>> SpacesHoldingTopicsAsync(string tenantId)
    {
        var prefix = $"topics@{tenantId}:";
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in redis.GetEndPoints())
        {
            await foreach (var key in redis.GetServer(endpoint).KeysAsync(_db.Database, pattern: $"{prefix}*", pageSize: 1000))
            {
                var name = key.ToString();
                slugs.Add(name[(name.LastIndexOf(':') + 1)..]);
            }
        }

        return [.. slugs];
    }

    // Whether topics can be searched, the index made if it is missing: the household migration's,
    // once it has moved the documents under their households.
    public Task<bool> EnsureSearchIndexAsync() => _searchDocuments.EnsureIndexAsync();

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
                    m.GetAuthorId(),
                    m.GetTimestamp(),
                    m.GetAttachments(),
                    m.GetSenderId(),
                    IsError: m.Role == ChatRole.Assistant && m.IsFailedTurn()))
                .ToList();
    }

    public Task<TopicMetadata?> GetTopicAsync(string tenantId, string agentId, long chatId, string topicId) =>
        ReadTopicAsync(TopicKey(tenantId, agentId, chatId, topicId));

    public async Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(
        string agentId, long chatId, long threadId, CancellationToken ct = default)
    {
        return await HolderAsync(agentId, chatId, threadId) is (var household, var topicId)
            ? await ReadTopicAsync(TopicKey(household, agentId, chatId, topicId))
            : null;
    }

    // Who holds a conversation, as its pointer says. A pointer from before households held the topic
    // id alone, which leads nowhere now: the household migration rewrites it.
    private async Task<(string Household, string TopicId)?> HolderAsync(string agentId, long chatId, long threadId)
    {
        var value = (await _db.StringGetAsync(ConversationKey(agentId, chatId, threadId))).ToString();
        var separator = value.IndexOf(':');
        return separator > 0 ? (value[..separator], value[(separator + 1)..]) : null;
    }

    public static string HouseholdOf(TopicMetadata topic) => topic.TenantId ?? NoHousehold;

    public static string Holder(string household, string topicId) => $"{household}:{topicId}";

    public static string TopicKey(string household, string agentId, long chatId, string topicId)
    {
        return $"topic@{household}:{agentId}:{chatId}:{topicId}";
    }

    public static string ConversationKey(string agentId, long chatId, long threadId)
    {
        return $"topic-of:{agentId}:{chatId}:{threadId}";
    }

    public static string IndexKey(string household, string agentId, string spaceSlug)
    {
        return $"topics@{household}:{agentId}:{spaceSlug}";
    }

    public static string SearchDocumentKey(string household, string agentId, long chatId, string topicId) =>
        TopicSearchDocuments.DocumentKey(household, agentId, chatId, topicId);

    public const string SearchIndexName = TopicSearchDocuments.IndexName;

    private static string IndexMember(TopicMetadata topic)
    {
        return TopicIndexMember.Compose(topic.ChatId, topic.TopicId);
    }

    // Shared with the search documents, so a topic sorts identically in the index and in a
    // search's results.
    internal static double LastWriteScore(TopicMetadata topic)
    {
        return topic.LastWriteAt.ToUnixTimeMilliseconds();
    }
}