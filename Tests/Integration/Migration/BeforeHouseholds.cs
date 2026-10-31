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