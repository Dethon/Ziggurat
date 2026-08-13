using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

// Cursor tracking, page appending and deduplication by topic id, kept out of the sidebar
// component so all of it can be tested without rendering anything.
//
// Paging only ever fetches backwards. New activity arrives as a push and goes in at the top,
// which is what covers a topic bumped from below the cursor to above it: the row the cursor will
// now never reach is delivered rather than paged to.
public sealed record TopicPaging
{
    public static readonly TopicPaging Empty = new();

    public IReadOnlyList<StoredTopic> Topics { get; init; } = [];

    public string? Cursor { get; init; }

    // True before the first page, because that is what makes the first fetch happen at all, and
    // false once a page comes back saying the range ended.
    public bool HasMore { get; init; } = true;

    // A page fetched below the cursor. A topic already held keeps one row and the fresher of
    // the two copies, so a row that gained a message after being paged past is not shown twice
    // — and not reverted when the page that left before the push finally lands.
    public TopicPaging AppendPage(IReadOnlyList<StoredTopic> page, string? nextCursor) => this with
    {
        Topics = Merge(page, Topics),
        Cursor = nextCursor,
        HasMore = nextCursor is not null
    };

    // The first page of a list being started over — a first load, an agent change, or catch-up
    // after an interruption. The cursor goes with the rows it belonged to.
    public static TopicPaging FirstPage(IReadOnlyList<StoredTopic> page, string? nextCursor) =>
        Empty.AppendPage(page, nextCursor);

    // A push. A topic already held gains the new copy in place rather than a second row.
    public TopicPaging Upsert(StoredTopic topic) => this with { Topics = Merge([topic], Topics) };

    public TopicPaging Insert(StoredTopic topic) =>
        Topics.Any(t => t.TopicId == topic.TopicId) ? this : Upsert(topic);

    public TopicPaging Remove(string topicId) => this with
    {
        Topics = Topics.Where(t => t.TopicId != topicId).ToList()
    };

    // Each topic keeps the copy that has seen more of its conversation: the later write, or on
    // the same write the higher message count. A fetched page can be older than a row it lands
    // under — a topic that gained a push during the round trip — so arrival order alone is not
    // freshness. Only a perfect tie falls back to first wins, so whichever side is presumed
    // fresher is passed first.
    private static IReadOnlyList<StoredTopic> Merge(
        IEnumerable<StoredTopic> fresher, IEnumerable<StoredTopic> held) =>
        fresher.Concat(held)
            .GroupBy(t => t.TopicId)
            .Select(copies => copies
                .OrderByDescending(t => t.LastWriteAt)
                .ThenByDescending(t => t.MessageCount)
                .First())
            .OrderByDescending(t => t.LastWriteAt)
            .ToList();
}