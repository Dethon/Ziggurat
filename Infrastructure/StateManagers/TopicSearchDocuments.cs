using System.Globalization;
using Domain.DTOs.WebChat;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// One search hit: which conversation, and the last-write score paging continues below.
internal sealed record TopicSearchHit(long ChatId, string TopicId, double Score);

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

    // The most tied hits one cursor score is re-read for. Ties share a millisecond, so a run
    // longer than this is out of scope.
    private const int MaxTiedHits = 256;

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly SearchCommands _ft = redis.GetDatabase().FT();
    private int _indexState;

    private enum IndexState
    {
        Unknown = 0,
        Ready = 1,
        Unavailable = 2
    }

    public async Task WriteAsync(TopicMetadata topic, string? appendedText = null, bool keepTtl = false)
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
            new("score", RedisThreadStateStore.LastWriteScore(topic)),
            .. text is null ? Array.Empty<HashEntry>() : [new HashEntry("text", text)]
        ];

        await _db.HashSetAsync(key, entries);

        // A rename keeps the clock still: the document expires with the conversation it
        // describes, so only the conversation's own writes push that back.
        if (!keepTtl)
        {
            await _db.KeyExpireAsync(key, expiration);
        }
    }

    public async Task DeleteAsync(TopicMetadata topic)
    {
        if (await EnsureIndexAsync())
        {
            await _db.KeyDeleteAsync(DocumentKey(topic));
        }
    }

    // Ordered by last write like every other list of topics, and paged the same way: the cursor
    // is the last hit's score and identity, and the next page starts right below that hit. The
    // range is deliberately both sides of the archive cutoff — search is the way into the archive.
    public async Task<IReadOnlyList<TopicSearchHit>> SearchAsync(
        string agentId, string spaceSlug, string query, string? cursor, int pageSize)
    {
        var terms = Terms(query);
        if (terms.Length == 0 || !await EnsureIndexAsync())
        {
            return [];
        }

        if (ParseCursor(cursor) is not { } from)
        {
            return await RunAsync(agentId, spaceSlug, terms, "-inf +inf", pageSize);
        }

        // The cursor's own score is read again inclusively and cut by hit identity, because the
        // score alone cannot say where inside a run of tied hits the page ended: an exclusive
        // bound loses whatever the page break split off the run.
        var score = from.Score.ToString(CultureInfo.InvariantCulture);
        var tied = AfterCursor(
            await RunAsync(agentId, spaceSlug, terms, $"{score} {score}", MaxTiedHits),
            from.Member);

        var page = tied.Take(pageSize).ToList();
        return page.Count < pageSize
            ? [.. page, .. await RunAsync(agentId, spaceSlug, terms, $"-inf ({score}", pageSize - page.Count)]
            : page;
    }

    private async Task<List<TopicSearchHit>> RunAsync(
        string agentId, string spaceSlug, string terms, string range, int take)
    {
        var search = new Query(
                $"@agentId:{{{Tag(agentId)}}} @space:{{{Tag(spaceSlug)}}} "
                + $"@score:[{range}] (@name|text:({terms}))")
            .SetSortBy("score", ascending: false)
            .Limit(0, take)
            .Dialect(2);

        try
        {
            var result = await _ft.SearchAsync(IndexName, search);
            return
            [
                .. result.Documents
                    .Select(d => new TopicSearchHit(
                        (long)d["chatId"],
                        d["topicId"].ToString(),
                        (double)d["score"]))
            ];
        }
        catch (RedisServerException)
        {
            // A query the index cannot answer is an empty result, not a broken sidebar.
            return [];
        }
    }

    // Tied hits come back in a stable order, so the previous page's tail cuts the run. A cursor
    // hit already purged keeps the whole run instead: a repeated row is recoverable, a skipped
    // one is not.
    private static IEnumerable<TopicSearchHit> AfterCursor(List<TopicSearchHit> tied, string member) =>
        tied.Skip(tied.FindIndex(h => Member(h) == member) + 1);

    internal static string CursorFor(TopicSearchHit hit) =>
        $"{hit.Score.ToString(CultureInfo.InvariantCulture)}:{Member(hit)}";

    private static string Member(TopicSearchHit hit) => $"{hit.ChatId}:{hit.TopicId}";

    private static (double Score, string Member)? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        var separator = cursor.IndexOf(':');
        var scorePart = separator < 0 ? cursor : cursor[..separator];
        return double.TryParse(scorePart, CultureInfo.InvariantCulture, out var score)
            ? (score, separator < 0 ? "" : cursor[(separator + 1)..])
            : null;
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
    private static string Terms(string query) =>
        string.Join(
            ' ',
            new string([.. query.Select(c => char.IsLetterOrDigit(c) ? c : ' ')])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Tag(string value) => value.Replace("-", "\\-");

    private static string DocumentKey(TopicMetadata topic) =>
        $"{KeyPrefix}{topic.AgentId}:{topic.ChatId}:{topic.TopicId}";
}