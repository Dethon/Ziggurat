using System.Globalization;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// Every topic from before topics had owners is marked shared, as the spec says, since nothing chose
// to keep it private; and given an owner, because only an owner may stop sharing one, so a topic
// nobody owns could never be made private (.claude/rules/channels.md). The owner is whoever started
// it — the first message's principal or sender, the one trace a topic keeps of who began it — and the
// household's owner where that is nobody the run knows. Only a starter is named as the topic's owner
// on its row; a topic the owner was given is not said to be theirs.
//
// Only a topic with neither owner nor household is one from before: a Telegram group's review copy is
// shared and the household's with no owner on purpose, and is left as it is.
public sealed class TopicOwners(IConnectionMultiplexer redis, IThreadStateStore threads) : IMigrationStep
{
    // RedisThreadStateStore's record key: topic:{agentId}:{chatId}:{topicId}, an agent id possibly
    // holding ':' itself, so the two ids are read from the end.
    private const string Pattern = "topic:*";

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Topics");
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var keys = new List<string>();
        await foreach (var key in server.KeysAsync(db.Database, Pattern).WithCancellation(ct))
        {
            keys.Add(key.ToString());
        }

        var owned = new List<(TopicMetadata Topic, MigratedPerson Owner, bool Started)>();
        foreach (var parts in keys.Select(Parts).OfType<(string AgentId, long ChatId, string TopicId)>())
        {
            if (await threads.GetTopicAsync(parts.AgentId, parts.ChatId, parts.TopicId) is not
                { OwnerId: null, TenantId: null } topic)
            {
                continue;
            }

            var starter = await StarterAsync(household, topic);
            var owner = starter ?? household.Owner;
            if (!dryRun)
            {
                await threads.SaveTopicAsync(
                    topic with
                    {
                        OwnerId = owner.UserId,
                        TenantId = household.TenantId,
                        Shared = true,
                        OwnerName = starter?.DisplayName
                    },
                    keepTtl: true);
            }

            owned.Add((topic, owner, starter is not null));
        }

        report.Changes = owned.Count;
        report.Lines.AddRange(owned
            .GroupBy(o => o.Owner)
            .Select(g => $"{g.Key.Label}: {Counted.Of(g.Count(), "shared topic")}"
                         + (g.Count(o => !o.Started) is > 0 and var given ? $", {given} of them started by nobody it knows" : "")));
        return report;
    }

    private async Task<MigratedPerson?> StarterAsync(Household household, TopicMetadata topic)
    {
        var messages = await threads.GetMessagesAsync(
            new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString());
        var first = messages?.FirstOrDefault(m => m.Role == ChatRole.User);
        var principal = Principal.Of(first?.GetConversationContext());
        return new[] { principal?.Account?.UserId, principal?.UserId, first?.GetSenderId() }
            .Select(household.Named)
            .FirstOrDefault(p => p is not null);
    }

    private static (string AgentId, long ChatId, string TopicId)? Parts(string key)
    {
        var segments = key.Split(':');
        return segments.Length >= 4
               && long.TryParse(segments[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId)
            ? (string.Join(':', segments[1..^2]), chatId, segments[^1])
            : null;
    }
}