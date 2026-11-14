using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

// Every key names the household: `schedule@{tenantId}:{id}` and the household's set
// `schedules@{tenantId}`. The '@' keeps the shape apart from the one before households
// (`schedule:{id}`, `schedules`), so the household migration's pattern for what is still unmoved can
// never match a household's schedule. What is due is one set for the deployment's one dispatcher,
// its members `{tenantId}:{id}` — a household id is hex, so the first ':' ends it.
//
// A one-shot's horizon is counted on the store's clock — the server's TimeProvider, which a stack with
// a pinned clock replaces — never the wall's, so a one-shot due on the pinned day is still there to read.
public sealed class RedisScheduleStore(IConnectionMultiplexer redis, TimeProvider? clock = null) : IScheduleStore
{
    public const string DueSetKey = "schedules-due";
    private static readonly TimeSpan _oneShotBuffer = TimeSpan.FromHours(1);

    private readonly IDatabase _db = redis.GetDatabase();

    public static string ScheduleKey(string tenantId, string id) => ScheduleKeys.Schedule(tenantId, id);

    public static string HouseholdSetKey(string tenantId) => ScheduleKeys.Household(tenantId);

    public static string DueMember(string tenantId, string id) => $"{tenantId}:{id}";

    public async Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schedule.TenantId, nameof(schedule.TenantId));
        var key = ScheduleKey(schedule.TenantId, schedule.Id);
        var json = JsonSerializer.Serialize(schedule);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(key, json);
        _ = transaction.SetAddAsync(HouseholdSetKey(schedule.TenantId), schedule.Id);

        if (schedule.NextRunAt.HasValue)
        {
            _ = transaction.SortedSetAddAsync(
                DueSetKey, DueMember(schedule.TenantId, schedule.Id), schedule.NextRunAt.Value.Ticks);
        }

        if (schedule.CronExpression is null && schedule.RunAt.HasValue)
        {
            _ = transaction.KeyExpireAsync(
                key, schedule.RunAt.Value.Add(_oneShotBuffer) - (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        }

        await transaction.ExecuteAsync();

        return schedule;
    }

    public async Task<Schedule?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(ScheduleKey(tenantId, id));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Schedule>(json.ToString());
    }

    public async Task<IReadOnlyList<Schedule>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(HouseholdSetKey(tenantId));
        var schedules = await Task.WhenAll(ids.Select(id => GetAsync(tenantId, id.ToString(), ct)));
        return schedules.OfType<Schedule>().OrderBy(s => s.NextRunAt ?? DateTime.MaxValue).ToList();
    }

    // A scan, which is fine for the operator's occasional housekeeping and nothing else.
    public async Task<IReadOnlyList<Schedule>> ListEveryHouseholdAsync(CancellationToken ct = default)
    {
        var households = new List<string>();
        foreach (var endpoint in redis.GetEndPoints())
        {
            await foreach (var key in redis.GetServer(endpoint)
                               .KeysAsync(_db.Database, $"{ScheduleKeys.HouseholdPrefix}*").WithCancellation(ct))
            {
                households.Add(key.ToString()[ScheduleKeys.HouseholdPrefix.Length..]);
            }
        }

        var lists = await Task.WhenAll(households.Distinct().Select(h => ListAsync(h, ct)));
        return [.. lists.SelectMany(l => l)];
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();

        _ = transaction.KeyDeleteAsync(ScheduleKey(tenantId, id));
        _ = transaction.SetRemoveAsync(HouseholdSetKey(tenantId), id);
        _ = transaction.SortedSetRemoveAsync(DueSetKey, DueMember(tenantId, id));

        await transaction.ExecuteAsync();
    }

    public async Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
    {
        var due = await _db.SortedSetRangeByScoreAsync(
            DueSetKey,
            stop: asOf.Ticks,
            take: 100);

        var schedules = await Task.WhenAll(due
            .Select(member => member.ToString())
            .Where(member => member.Contains(':'))
            .Select(member => GetAsync(member[..member.IndexOf(':')], member[(member.IndexOf(':') + 1)..], ct)));
        return schedules.OfType<Schedule>().ToList();
    }

    public async Task UpdateLastRunAsync(string tenantId, string id, DateTime? lastRunAt, DateTime? nextRunAt,
        CancellationToken ct = default)
    {
        var schedule = await GetAsync(tenantId, id, ct);
        if (schedule is null)
        {
            return;
        }

        var updated = schedule with
        {
            LastRunAt = lastRunAt ?? schedule.LastRunAt,
            NextRunAt = nextRunAt
        };

        var json = JsonSerializer.Serialize(updated);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(ScheduleKey(tenantId, id), json);

        _ = nextRunAt.HasValue
            ? transaction.SortedSetAddAsync(DueSetKey, DueMember(tenantId, id), nextRunAt.Value.Ticks)
            : transaction.SortedSetRemoveAsync(DueSetKey, DueMember(tenantId, id));

        await transaction.ExecuteAsync();
    }
}