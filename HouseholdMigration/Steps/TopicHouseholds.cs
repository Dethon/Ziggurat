using System.Globalization;
using System.Text.Json;
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// Every topic is kept under its household now (RedisThreadStateStore): its record, the space's ordering
// it sorts in, the pointer its conversation leads to it by and its search document, which also gains a
// household field the new index filters on. So each topic still under the shape from before households
// is moved into the household it records — the home household where it records none, which after
// TopicOwners is none of the deployment's — its expiry, its place in the list and its searchable text
// as they were, and the search index from before households (`idx:topics`, with no household field) is
// dropped for the store's own (`idx:household-topics`), which indexes every document already under its
// prefix as it is made. The new shapes' '@' keeps a household's topics out of this step's patterns, so
// a rerun after a second household exists moves nothing of theirs.
public sealed class TopicHouseholds(IConnectionMultiplexer redis, RedisThreadStateStore threads) : IMigrationStep
{
    // The old shapes, spelled here only to read and clear what is still under them.
    public const string UnmovedRecordPrefix = "topic:";
    private const string UnmovedIndexPrefix = "topics:";
    private const string UnmovedDocumentPrefix = "topicdoc:";
    private const string UnmovedSearchIndex = "idx:topics";

    // The record's key: topic:{agentId}:{chatId}:{topicId}, an agent id possibly holding ':' itself, so
    // the two ids are read from the end.
    public static (string AgentId, long ChatId, string TopicId)? Unmoved(string key)
    {
        var segments = key[UnmovedRecordPrefix.Length..].Split(':');
        return segments.Length >= 3
               && long.TryParse(segments[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
            ? (string.Join(':', segments[..^2]), chatId, segments[^1])
            : null;
    }

    public static string UnmovedKey(string agentId, long chatId, string topicId) =>
        $"{UnmovedRecordPrefix}{agentId}:{chatId}:{topicId}";

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Topics into their household");
        var db = redis.GetDatabase();
        var moved = 0;
        foreach (var key in await KeysAsync(db, $"{UnmovedRecordPrefix}*", ct))
        {
            var json = await db.StringGetAsync(key);
            if (Unmoved(key) is not (var agentId, var chatId, var topicId) || json.IsNullOrEmpty)
            {
                continue;
            }

            var topic = JsonSerializer.Deserialize<TopicMetadata>(json.ToString())!;
            var into = topic.TenantId ?? household.TenantId;
            if (await db.KeyExistsAsync(RedisThreadStateStore.TopicKey(into, agentId, chatId, topicId)))
            {
                report.Problems.Add($"topic '{topicId}': its household already has one by that id");
                continue;
            }

            moved++;
            if (!dryRun)
            {
                await MoveAsync(db, key, topic with { TenantId = into }, into);
            }
        }

        var dangling = await ClearDanglingAsync(db, dryRun, ct);
        var dropped = await DropUnmovedIndexAsync(db, dryRun);
        if (!dryRun)
        {
            await threads.EnsureSearchIndexAsync();
        }

        report.Changes = moved + dangling + (dropped ? 1 : 0);
        if (moved > 0)
        {
            report.Lines.Add($"{Counted.Of(moved, "topic")} into the household it records, or the home household");
        }

        if (dangling > 0)
        {
            report.Lines.Add($"{Counted.Of(dangling, "entry")} of topics already purged cleared");
        }

        if (dropped)
        {
            report.Lines.Add($"the search index from before households ({UnmovedSearchIndex}) dropped; its documents kept");
        }

        return report;
    }

    // One topic, all of a piece: the record under its household with the expiry it had, the
    // conversation's pointer naming the household beside it, its place in its space's ordering and its
    // search document, renamed so its text and expiry go with it.
    private static async Task MoveAsync(IDatabase db, string key, TopicMetadata topic, string into)
    {
        var (agentId, chatId, topicId) = (topic.AgentId, topic.ChatId, topic.TopicId);
        var ttl = await db.KeyTimeToLiveAsync(key);
        var pointer = RedisThreadStateStore.ConversationKey(agentId, chatId, topic.ThreadId);
        var pointsHere = await db.StringGetAsync(pointer) == topicId;
        var unmovedIndex = $"{UnmovedIndexPrefix}{agentId}:{topic.SpaceSlug}";
        var member = $"{chatId}:{topicId}";
        var score = await db.SortedSetScoreAsync(unmovedIndex, member);
        var document = $"{UnmovedDocumentPrefix}{agentId}:{chatId}:{topicId}";
        var hasDocument = await db.KeyExistsAsync(document);

        var move = db.CreateTransaction();
        _ = move.StringSetAsync(
            RedisThreadStateStore.TopicKey(into, agentId, chatId, topicId), JsonSerializer.Serialize(topic),
            expiry: ttl, keepTtl: false);
        if (pointsHere)
        {
            _ = move.StringSetAsync(pointer, RedisThreadStateStore.Holder(into, topicId), expiry: null, keepTtl: true);
        }

        if (score is { } s)
        {
            _ = move.SortedSetAddAsync(RedisThreadStateStore.IndexKey(into, agentId, topic.SpaceSlug), member, s);
            _ = move.SortedSetRemoveAsync(unmovedIndex, member);
        }

        if (hasDocument)
        {
            var moved = RedisThreadStateStore.SearchDocumentKey(into, agentId, chatId, topicId);
            _ = move.KeyRenameAsync(document, moved);
            _ = move.HashSetAsync(moved, "household", into);
        }

        _ = move.KeyDeleteAsync(key);
        await move.ExecuteAsync();
    }

    // What the old shapes still hold once every record has moved points at topics already purged: an
    // ordering's members whose record expired, and a document that outlived its record. Neither leads
    // anywhere, so they are cleared rather than moved.
    private async Task<int> ClearDanglingAsync(IDatabase db, bool dryRun, CancellationToken ct)
    {
        var cleared = 0;
        // A space's ordering is topics:{agentId}:{space}, a sorted set. Anything else under the prefix is
        // no index (the retired index migration's once-only marker, topics:migrated, a string) and is
        // none of this step's to clear.
        var orderings = (await KeysAsync(db, $"{UnmovedIndexPrefix}*", ct))
            .Where(index => index.LastIndexOf(':') > UnmovedIndexPrefix.Length);
        foreach (var index in orderings)
        {
            if (await db.KeyTypeAsync(index) != RedisType.SortedSet)
            {
                continue;
            }

            var agentId = index[UnmovedIndexPrefix.Length..index.LastIndexOf(':')];
            foreach (var member in await db.SortedSetRangeByRankAsync(index))
            {
                if (await db.KeyExistsAsync($"{UnmovedRecordPrefix}{agentId}:{member}"))
                {
                    continue;
                }

                cleared++;
                if (!dryRun)
                {
                    await db.SortedSetRemoveAsync(index, member);
                }
            }
        }

        foreach (var document in await KeysAsync(db, $"{UnmovedDocumentPrefix}*", ct))
        {
            if (await db.KeyExistsAsync($"{UnmovedRecordPrefix}{document[UnmovedDocumentPrefix.Length..]}"))
            {
                continue;
            }

            cleared++;
            if (!dryRun)
            {
                await db.KeyDeleteAsync(document);
            }
        }

        return cleared;
    }

    // Dropped and not its documents (no DD): they have all moved under the new prefix by now, and a
    // Redis with no RediSearch has no index to drop.
    private static async Task<bool> DropUnmovedIndexAsync(IDatabase db, bool dryRun)
    {
        try
        {
            var indexes = (RedisResult[])(await db.ExecuteAsync("FT._LIST"))!;
            if (!indexes.Any(i => i.ToString() == UnmovedSearchIndex))
            {
                return false;
            }

            if (!dryRun)
            {
                await db.ExecuteAsync("FT.DROPINDEX", UnmovedSearchIndex);
            }

            return true;
        }
        catch (RedisServerException)
        {
            return false;
        }
    }

    private async Task<List<string>> KeysAsync(IDatabase db, string pattern, CancellationToken ct)
    {
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(db.Database, pattern).WithCancellation(ct))
        {
            keys.Add(key.ToString());
        }

        return keys;
    }
}