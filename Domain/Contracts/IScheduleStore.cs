using Domain.DTOs;

namespace Domain.Contracts;

// A household's schedules, kept under its id: every read but the dispatcher's names the household,
// so the store can never answer one household with another's schedule, even under the same id.
public interface IScheduleStore
{
    // Kept under the schedule's own TenantId, which must name a household.
    Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default);
    Task<Schedule?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<Schedule>> ListAsync(string tenantId, CancellationToken ct = default);

    // Every household's, for the operator's housekeeping (redelegate) alone: no turn reads this.
    Task<IReadOnlyList<Schedule>> ListEveryHouseholdAsync(CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);

    // The deployment's one dispatcher fires every household's, each schedule saying whose it is.
    Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default);

    // A null lastRunAt leaves the existing value untouched (used when only nextRunAt changes).
    Task UpdateLastRunAsync(string tenantId, string id, DateTime? lastRunAt, DateTime? nextRunAt, CancellationToken ct = default);
}