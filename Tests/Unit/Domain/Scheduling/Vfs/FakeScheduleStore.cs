using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Unit.Domain.Scheduling.Vfs;

// Keyed as the real store keys them, by household and id, so a view of one household sees nothing
// of another's. Items holds every household's by id, for the tests that seed one household.
public sealed class FakeScheduleStore : IScheduleStore
{
    private readonly Dictionary<(string TenantId, string Id), Schedule> _byHousehold = new();

    public IReadOnlyDictionary<string, Schedule> Items =>
        _byHousehold.Values.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

    public Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        _byHousehold[(schedule.TenantId, schedule.Id)] = schedule;
        return Task.FromResult(schedule);
    }

    public Task<Schedule?> GetAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult(_byHousehold.GetValueOrDefault((tenantId, id)));

    public Task<IReadOnlyList<Schedule>> ListAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(_byHousehold.Values.Where(s => s.TenantId == tenantId).ToList());

    public Task<IReadOnlyList<Schedule>> ListEveryHouseholdAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(_byHousehold.Values.ToList());

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _byHousehold.Remove((tenantId, id));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(_byHousehold.Values.Where(s => s.NextRunAt <= asOf).ToList());

    public Task UpdateLastRunAsync(string tenantId, string id, DateTime? lastRunAt, DateTime? nextRunAt,
        CancellationToken ct = default)
    {
        if (_byHousehold.TryGetValue((tenantId, id), out var s))
        {
            _byHousehold[(tenantId, id)] = s with { LastRunAt = lastRunAt ?? s.LastRunAt, NextRunAt = nextRunAt };
        }

        return Task.CompletedTask;
    }
}