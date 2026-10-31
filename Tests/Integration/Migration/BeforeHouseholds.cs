using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Tests.Integration.Migration;

// What the shared stores held before they were keyed by household, written as those stores wrote it,
// so the migration is run against data in the shape a deployment really has. The stores themselves
// write only the household shape now.
public static class BeforeHouseholds
{
    public static async Task<Schedule> ScheduleAsync(IDatabase db, Schedule schedule)
    {
        await db.StringSetAsync($"schedule:{schedule.Id}", JsonSerializer.Serialize(schedule));
        await db.SetAddAsync("schedules", schedule.Id);
        if (schedule.NextRunAt is { } next)
        {
            await db.SortedSetAddAsync("schedules:due", schedule.Id, next.Ticks);
        }

        return schedule;
    }

    // A topic as the thread store kept it: its record, its conversation's pointer to it, its place in its
    // space's ordering and its search document, all under its agent with no household, expiring together.
    public static async Task<TopicMetadata> TopicAsync(IDatabase db, TopicMetadata topic, string? text = null)
    {
        var expiry = TimeSpan.FromDays(30);
        var (agent, chat, id) = (topic.AgentId, topic.ChatId, topic.TopicId);
        var score = topic.LastWriteAt.ToUnixTimeMilliseconds();
        await db.StringSetAsync($"topic:{agent}:{chat}:{id}", JsonSerializer.Serialize(topic), expiry);
        await db.StringSetAsync($"topic-of:{agent}:{chat}:{topic.ThreadId}", id, expiry);
        await db.SortedSetAddAsync($"topics:{agent}:{topic.SpaceSlug}", $"{chat}:{id}", score);
        await db.HashSetAsync($"topicdoc:{agent}:{chat}:{id}",
        [
            new HashEntry("topicId", id), new HashEntry("chatId", chat), new HashEntry("agentId", agent),
            new HashEntry("space", topic.SpaceSlug), new HashEntry("name", topic.Name), new HashEntry("score", score),
            .. text is null ? Array.Empty<HashEntry>() : [new HashEntry("text", text)]
        ]);
        await db.KeyExpireAsync($"topicdoc:{agent}:{chat}:{id}", expiry);
        return topic;
    }

    // The search index as it was, with no household field, over the documents' old prefix.
    public static async Task SearchIndexAsync(IDatabase db)
    {
        try
        {
            await db.ExecuteAsync("FT.CREATE", "idx:topics", "ON", "HASH", "PREFIX", "1", "topicdoc:", "SCHEMA",
                "agentId", "TAG", "space", "TAG", "name", "TEXT", "text", "TEXT", "score", "NUMERIC", "SORTABLE");
        }
        catch (RedisServerException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    // A phone the chat client subscribed under the name its picker sent, in a space every household
    // shared.
    public static async Task PhoneAsync(IDatabase db, string userId, PushSubscriptionDto phone, string spaceSlug = "default")
    {
        await db.HashSetAsync($"push:subs:{userId}", phone.Endpoint,
            JsonSerializer.Serialize(new { phone.P256dh, phone.Auth, SpaceSlug = spaceSlug }));
        await db.StringSetAsync($"push:ep:{phone.Endpoint}", userId);
        await db.SetAddAsync($"push:space:{spaceSlug}", phone.Endpoint);
    }
}