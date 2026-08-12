using System.Globalization;
using System.Text;
using Domain.DTOs.WebChat;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// One searchable document per topic, holding what it is called and what was said in it. Search
// returns topics rather than positions in them, because the sidebar's question is which
// conversation and not which message.
//
// The document carries the topic's own expiry and is refreshed by the same write, so it is purged
// with the conversation it describes and never outlives it.
//
// Every operation degrades to nothing on a Redis without RediSearch. A deployment can run the
// plain image — the E2E stack does — and an append that threw there would take the conversation
// down with it over a feature nobody asked that deployment for.
internal sealed class TopicSearchDocuments(IConnectionMultiplexer redis, TimeSpan expiration)
{
    private const string IndexName = "idx:topics";
    private const string KeyPrefix = "topicdoc:";

    // What one conversation contributes to the index. Bounded, because a document that grew with
    // the conversation would make an old chat cost more to keep searchable than to keep.
    private const int MaxIndexedTextLength = 20_000;

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly SearchCommands _ft = redis.GetDatabase().FT();
    private int _indexState;

    private enum IndexState
    {
        Unknown = 0,
        Ready = 1,
        Unavailable = 2
    }

    public async Task WriteAsync(TopicMetadata topic, string? appendedText = null)
    {
        if (!await EnsureIndexAsync())
        {
            return;
        }

        var key = DocumentKey(topic);
        var text = appendedText is { Length: > 0 }
            ? Append(await _db.HashGetAsync(key, "text"), appendedText)
            : (string?)null;

        HashEntry[] entries =
        [
            new("topicId", topic.TopicId),
            new("chatId", topic.ChatId),
            new("agentId", topic.AgentId),
            new("space", topic.SpaceSlug),
            new("name", topic.Name),
            new("score", LastWriteScore(topic)),
            .. text is null ? Array.Empty<HashEntry>() : [new HashEntry("text", text)]
        ];

        await _db.HashSetAsync(key, entries);
        await _db.KeyExpireAsync(key, expiration);
    }

    public async Task DeleteAsync(TopicMetadata topic)
    {
        if (await EnsureIndexAsync())
        {
            await _db.KeyDeleteAsync(DocumentKey(topic));
        }
    }

    // Ordered by last write like every other list of topics, and paged the same way: the cursor
    // is the last hit's score and the next page starts below it. The range is deliberately both
    // sides of the archive cutoff — search is the way into the archive.
    public async Task<IReadOnlyList<(long ChatId, string TopicId, double Score)>> SearchAsync(
        string agentId, string spaceSlug, string query, string? cursor, int pageSize)
    {
        var terms = Terms(query);
        if (terms.Length == 0 || !await EnsureIndexAsync())
        {
            return [];
        }

        var below = cursor is null
            ? "-inf +inf"
            : $"-inf ({cursor}";

        var search = new Query(
                $"@agentId:{{{Tag(agentId)}}} @space:{{{Tag(spaceSlug)}}} "
                + $"@score:[{below}] (@name|text:({terms}))")
            .SetSortBy("score", ascending: false)
            .Limit(0, pageSize)
            .Dialect(2);

        try
        {
            var result = await _ft.SearchAsync(IndexName, search);
            return
            [
                .. result.Documents
                    .Select(d => (
                        ChatId: (long)d["chatId"],
                        TopicId: d["topicId"].ToString(),
                        Score: (double)d["score"]))
            ];
        }
        catch (RedisServerException)
        {
            // A query the index cannot answer is an empty result, not a broken sidebar.
            return [];
        }
    }

    private async Task<bool> EnsureIndexAsync()
    {
        switch ((IndexState)Volatile.Read(ref _indexState))
        {
            case IndexState.Ready:
                return true;
            case IndexState.Unavailable:
                return false;
        }

        try
        {
            await _ft.InfoAsync(IndexName);
            Volatile.Write(ref _indexState, (int)IndexState.Ready);
            return true;
        }
        catch (RedisServerException)
        {
            return await CreateIndexAsync();
        }
    }

    private async Task<bool> CreateIndexAsync()
    {
        try
        {
            var schema = new Schema()
                .AddTagField("agentId")
                .AddTagField("space")
                .AddTextField("name")
                .AddTextField("text")
                .AddNumericField("score", sortable: true);

            await _ft.CreateAsync(
                IndexName,
                new FTCreateParams().On(IndexDataType.HASH).Prefix(KeyPrefix),
                schema);

            Volatile.Write(ref _indexState, (int)IndexState.Ready);
            return true;
        }
        catch (RedisServerException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            // Two processes starting together both found it missing. Whoever lost still has an
            // index.
            Volatile.Write(ref _indexState, (int)IndexState.Ready);
            return true;
        }
        catch (RedisServerException)
        {
            // No RediSearch on this Redis. Nothing is indexed and nothing is found, and every
            // other thing a topic does keeps working.
            Volatile.Write(ref _indexState, (int)IndexState.Unavailable);
            return false;
        }
    }

    private static string Append(RedisValue existing, string addition)
    {
        var combined = existing.IsNullOrEmpty ? addition : $"{existing} {addition}";

        // Kept from the end, because the recent part of a conversation is what anyone remembers
        // enough of to search for.
        return combined.Length <= MaxIndexedTextLength
            ? combined
            : combined[^MaxIndexedTextLength..];
    }

    // The query as terms the index can take. Anything that means something to RediSearch is
    // dropped rather than escaped: a person typing into a search box is naming words, not
    // writing a query.
    private static string Terms(string query)
    {
        var cleaned = new StringBuilder(query.Length);
        foreach (var c in query)
        {
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(
            ' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Tag(string value) => value.Replace("-", "\\-");

    private static string DocumentKey(TopicMetadata topic) =>
        $"{KeyPrefix}{topic.AgentId}:{topic.ChatId}:{topic.TopicId}";

    private static double LastWriteScore(TopicMetadata topic) =>
        (topic.LastMessageAt ?? topic.CreatedAt).ToUnixTimeMilliseconds();
}