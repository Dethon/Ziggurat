using System.Text.Json;
using Accounts.Announcements;
using Domain.DTOs;
using StackExchange.Redis;

namespace Accounts.Admin;

// How many of a household's schedules an agent runs, read where the scheduler keeps them (ScheduleKeys)
// in the deployment's Redis — the accounts service owns no schedule and changes none. Asked before an
// agent is removed: one removed with schedules left them refused at every fire, with nobody told.
public sealed class HouseholdSchedules(AccountsRedis redis)
{
    // Null when they could not be counted: no Redis configured (a stack with no scheduler) answers
    // none, since there is nothing there to run.
    public async Task<int?> CountOfAgentAsync(string tenantId, string agentId, CancellationToken ct)
    {
        if (!redis.Configured)
        {
            return 0;
        }

        try
        {
            var db = (await redis.OpenAsync(ct)).GetDatabase();
            var ids = await db.SetMembersAsync(ScheduleKeys.Household(tenantId));
            var kept = await db.StringGetAsync([.. ids.Select(id => (RedisKey)ScheduleKeys.Schedule(tenantId, id.ToString()))]);
            return kept.Where(json => !json.IsNullOrEmpty)
                .Select(json => JsonSerializer.Deserialize<Schedule>(json.ToString()))
                .Count(schedule => schedule?.AgentId == agentId);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }
}