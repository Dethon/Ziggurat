using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// Who wrote each schedule, read from the conversations that wrote them. A schedule from before
// principals were recorded names nobody, but the turn that wrote it left the schedule mount's own
// answer in its topic — a created or edited schedule.json at /schedules/<agent>/<id>/ — after the
// message of the person it was for. A schedule runs as whoever last wrote it, so the latest write wins.
//
// Read where every topic from before households still is, under the shape TopicHouseholds then moves.
public static class ScheduleWrites
{
    private static readonly string[] _writes = ["created", "edited"];

    public static async Task<IReadOnlyDictionary<string, MigratedPerson>> WritersAsync(
        IConnectionMultiplexer redis, IThreadStateStore threads, Household household, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var writes = new List<(string ScheduleId, MigratedPerson Writer, DateTimeOffset At)>();
        await foreach (var key in server.KeysAsync(db.Database, $"{TopicHouseholds.UnmovedRecordPrefix}*").WithCancellation(ct))
        {
            if (TopicHouseholds.Unmoved(key.ToString()) is null
                || await db.StringGetAsync(key) is not { IsNullOrEmpty: false } json
                || JsonSerializer.Deserialize<TopicMetadata>(json.ToString()) is not { } topic)
            {
                continue;
            }

            var messages = await threads.GetMessagesAsync(
                new AgentKey($"{topic.ChatId}:{topic.ThreadId}", topic.AgentId).ToString()) ?? [];
            writes.AddRange(WritesIn(household, messages));
        }

        return writes
            .GroupBy(w => w.ScheduleId)
            .ToDictionary(g => g.Key, g => g.MaxBy(w => w.At).Writer);
    }

    private static IEnumerable<(string ScheduleId, MigratedPerson Writer, DateTimeOffset At)> WritesIn(
        Household household, IReadOnlyList<ChatMessage> messages)
    {
        MigratedPerson? asker = null;
        var at = DateTimeOffset.MinValue;
        foreach (var message in messages)
        {
            at = message.GetTimestamp() ?? at;
            if (message.Role == ChatRole.User)
            {
                asker = TopicOwners.SenderOf(household, message);
                continue;
            }

            foreach (var id in message.Contents.OfType<FunctionResultContent>().Select(r => WrittenSchedule(r.Result)))
            {
                if (id is not null && asker is not null)
                {
                    yield return (id, asker, at);
                }
            }
        }
    }

    // The schedule a mount's answer says it created or edited, if that is what it says.
    private static string? WrittenSchedule(object? result)
    {
        var element = result switch
        {
            JsonElement e => e,
            null => default,
            var other => JsonSerializer.SerializeToElement(other)
        };
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("status", out var status) || !_writes.Contains(status.GetString())
            || !element.TryGetProperty("filePath", out var path) || path.GetString() is not { } filePath)
        {
            return null;
        }

        var parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["schedules", _, var id, "schedule.json"] ? id : null;
    }
}