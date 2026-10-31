using System.Text.Json;
using Domain.DTOs;
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
}