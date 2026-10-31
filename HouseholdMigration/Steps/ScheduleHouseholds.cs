using System.Text.Json;
using Domain.DTOs;
using Infrastructure.StateManagers;
using StackExchange.Redis;

namespace HouseholdMigration.Steps;

// Every schedule is kept under its household now (`schedule@{tenantId}:{id}`, RedisScheduleStore), so
// each one still under the shape from before households — `schedule:{id}`, listed in the `schedules`
// set and due in `schedules:due` — is moved into the home household, its id, its expiry and its next
// run as they were. The new shape's '@' is what keeps a household's schedule out of this step's
// reach: nothing here can read one, so a rerun after a second household exists moves nothing of
// theirs. A schedule the home household already holds under the same id is left and reported.
public sealed class ScheduleHouseholds(IConnectionMultiplexer redis) : IMigrationStep
{
    // The old shapes, spelled here only to read and clear what is still under them.
    private const string UnmovedSetKey = "schedules";
    private const string UnmovedDueSetKey = "schedules:due";

    public static string UnmovedKey(string id) => $"schedule:{id}";

    // Every schedule still under the old shape, as it is there now.
    public static async Task<IReadOnlyList<Schedule>> UnmovedAsync(IDatabase db)
    {
        var ids = await db.SetMembersAsync(UnmovedSetKey);
        var found = await Task.WhenAll(ids.Select(async id => await db.StringGetAsync(UnmovedKey(id.ToString()))));
        return [.. found.Where(json => !json.IsNullOrEmpty).Select(json => JsonSerializer.Deserialize<Schedule>(json.ToString())!)];
    }

    public async Task<StepReport> RunAsync(Household household, bool dryRun, CancellationToken ct)
    {
        var report = new StepReport("Schedules into the household");
        var db = redis.GetDatabase();
        var home = household.TenantId;
        var ids = (await db.SetMembersAsync(UnmovedSetKey)).Select(id => id.ToString()).ToList();
        var (moved, stale) = (0, 0);
        foreach (var id in ids)
        {
            var json = await db.StringGetAsync(UnmovedKey(id));
            if (json.IsNullOrEmpty)
            {
                // A one-shot that expired left its id listed: nothing to move, only the listing to clear.
                stale++;
                if (!dryRun)
                {
                    await ClearAsync(db, id);
                }

                continue;
            }

            if (await db.KeyExistsAsync(RedisScheduleStore.ScheduleKey(home, id)))
            {
                report.Problems.Add($"schedule '{id}': the home household already has one by that id");
                continue;
            }

            moved++;
            if (dryRun)
            {
                continue;
            }

            var schedule = JsonSerializer.Deserialize<Schedule>(json.ToString())! with { TenantId = home };
            var ttl = await db.KeyTimeToLiveAsync(UnmovedKey(id));
            var due = await db.SortedSetScoreAsync(UnmovedDueSetKey, id);
            var move = db.CreateTransaction();
            _ = move.StringSetAsync(RedisScheduleStore.ScheduleKey(home, id), JsonSerializer.Serialize(schedule), expiry: ttl, keepTtl: false);
            _ = move.SetAddAsync(RedisScheduleStore.HouseholdSetKey(home), id);
            if (due is { } score)
            {
                _ = move.SortedSetAddAsync(RedisScheduleStore.DueSetKey, RedisScheduleStore.DueMember(home, id), score);
            }

            _ = move.KeyDeleteAsync(UnmovedKey(id));
            _ = move.SetRemoveAsync(UnmovedSetKey, id);
            _ = move.SortedSetRemoveAsync(UnmovedDueSetKey, id);
            await move.ExecuteAsync();
        }

        report.Changes = moved + stale;
        if (moved > 0)
        {
            report.Lines.Add($"{Counted.Of(moved, "schedule")} into the home household");
        }

        if (stale > 0)
        {
            report.Lines.Add($"{Counted.Of(stale, "expired schedule")} no longer listed");
        }

        return report;
    }

    private static Task ClearAsync(IDatabase db, string id) =>
        Task.WhenAll(db.SetRemoveAsync(UnmovedSetKey, id), db.SortedSetRemoveAsync(UnmovedDueSetKey, id));
}