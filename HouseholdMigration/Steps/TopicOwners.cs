using System.Text.Json;
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
//
// Every topic from before owners is also from before households, so it is read where it still is,
// under the shape TopicHouseholds then moves into its household (`topic:{agentId}:{chatId}:{topicId}`),
// and given its owner in place; a household's topic is never touched.
public sealed class TopicOwners(IConnectionMultiplexer redis, IThreadStateStore threads) : IMigrationStep
{
    private const string Pattern = $"{TopicHouseholds.UnmovedRecordPrefix}*";

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
        foreach (var key in keys.Where(k => TopicHouseholds.Unmoved(k) is not null))
        {
            var json = await db.StringGetAsync(key);
            if (json.IsNullOrEmpty
                || JsonSerializer.Deserialize<TopicMetadata>(json.ToString()) is not { OwnerId: null, TenantId: null } topic)
            {
                continue;
            }

            var starter = await StarterAsync(household, topic);
            var owner = starter ?? household.Owner;
            if (!dryRun)
            {
                // Its own key and its own expiry; the space's ordering and the search document hold
                // nothing an owner changes.
                await db.StringSetAsync(
                    key,
                    JsonSerializer.Serialize(topic with
                    {
                        OwnerId = owner.UserId,
                        TenantId = household.TenantId,
                        Shared = true,
                        OwnerName = starter?.DisplayName
                    }),
                    expiry: null, keepTtl: true, when: When.Exists);
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
        return SenderOf(household, messages?.FirstOrDefault(m => m.Role == ChatRole.User));
    }

    // Who a person's message was from, as far as anything persisted before accounts can say. A message
    // persisted then carries no principal, only the channel's user id on its context: read here as the
    // migration's evidence of who said it, which nothing that acts for a person believes any more
    // (Principal.Of).
    internal static MigratedPerson? SenderOf(Household household, ChatMessage? message)
    {
        var context = message?.GetConversationContext();
        var principal = Principal.Of(context);
        var userId = principal?.UserId ?? (context is { OriginKind: null } ? context.UserId : null);
        return new[] { principal?.Account?.UserId, userId, message?.GetSenderId() }
            .Select(household.Named)
            .FirstOrDefault(p => p is not null);
    }
}