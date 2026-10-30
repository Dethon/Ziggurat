using Domain.Contracts;
using Domain.Security;
using Domain.Tools.Scheduling.Vfs;

namespace McpServerScheduling.Services;

// The schedules' delegations as the agent's operator commands reach them. A schedule's recorded
// writer is offered for one written before delegations, whose household migration stamped it.
public sealed class ScheduleDelegationStore(IScheduleStore schedules) : IDelegationStore
{
    public async Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct) =>
    [
        .. (await schedules.ListAsync(ct)).Select(s => new StoredDelegation(
            DelegatedObject.Schedule, s.Id, s.Delegation, ScheduleDelegation.Of(s).Description,
            s.CreatedBy is { Account: not null } writer ? writer : null))
    ];

    public async Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct)
    {
        if (kind != DelegatedObject.Schedule || await schedules.GetAsync(id, ct) is not { } schedule)
        {
            return false;
        }

        await schedules.CreateAsync(schedule with { Delegation = token }, ct);
        return true;
    }
}