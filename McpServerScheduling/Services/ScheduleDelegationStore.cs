using Domain.Contracts;
using Domain.Security;
using Domain.Tools.Scheduling.Vfs;

namespace McpServerScheduling.Services;

// The schedules' delegations as the agent's operator commands reach them. A schedule's recorded
// writer is offered for one written before delegations, whose household migration stamped it. The
// operator's housekeeping reaches every household's, so a delegation names its schedule by id alone.
public sealed class ScheduleDelegationStore(IScheduleStore schedules) : IDelegationStore
{
    public async Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct) =>
    [
        .. (await schedules.ListEveryHouseholdAsync(ct)).Select(s => new StoredDelegation(
            DelegatedObject.Schedule, s.Id, s.Delegation, ScheduleDelegation.Of(s).Description,
            s.CreatedBy is { Account: not null } writer ? writer : null))
    ];

    public async Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct)
    {
        // An id is unique within its household only, and the token is not this server's to read, so
        // an id two households both hold is refused rather than guessed at: replacing the wrong one
        // would make it fire as somebody of another household.
        if (kind != DelegatedObject.Schedule
            || (await schedules.ListEveryHouseholdAsync(ct)).Where(s => s.Id == id).ToList() is not [var schedule])
        {
            return false;
        }

        await schedules.CreateAsync(schedule with { Delegation = token }, ct);
        return true;
    }
}