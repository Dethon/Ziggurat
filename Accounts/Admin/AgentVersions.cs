using Accounts.Settings;
using Accounts.Store;
using Domain.DTOs.Admin;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Admin;

// The names and custom instructions a household's agents had before an owner's change replaced them,
// the last few per agent (AdminSettings.AgentVersionsKept), so a change the owner regrets is put back
// by restoring one. Written by the admin API alone: an operator's set-household-config replaces a
// whole section and keeps no history of it.
//
// Its own table rather than a field of the agents section: no turn reads it, and the section is what
// the agent is sent with every view, migrated by every step and rewritten whole by the operator's
// command, none of which history needs.
public sealed class AgentVersions(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock, AccountsSettings settings)
{
    // What an agent was before a change that stood, by whom it was replaced; the oldest beyond the
    // number kept are let go.
    public async Task RecordAsync(string householdId, string agentId, AdminAgent replaced, string replacedBy, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.AgentVersions.Add(new AgentVersion
        {
            Id = Tokens.NewId(),
            TenantId = householdId,
            AgentId = agentId,
            Name = replaced.Name,
            CustomInstructions = replaced.CustomInstructions,
            ReplacedBy = replacedBy,
            ReplacedAt = clock.Time.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);

        var beyond = await Newest(db, householdId, agentId).Skip(Math.Max(settings.Admin.AgentVersionsKept, 0)).ToListAsync(ct);
        db.AgentVersions.RemoveRange(beyond);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AdminAgentVersion>> ListAsync(string householdId, string agentId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var versions = await Newest(db, householdId, agentId).AsNoTracking().ToListAsync(ct);
        var people = versions.Select(v => v.ReplacedBy).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => people.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return
        [
            .. versions.Select(v => new AdminAgentVersion(
                v.Id, v.Name, v.CustomInstructions, v.ReplacedAt, names.GetValueOrDefault(v.ReplacedBy, "")))
        ];
    }

    public async Task<AgentVersion?> FindAsync(string householdId, string agentId, string versionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.AgentVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == versionId && v.TenantId == householdId && v.AgentId == agentId, ct);
    }

    // A removed agent's history goes with it, so an agent added later under the same id starts with none.
    public async Task DropAsync(string householdId, string agentId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.AgentVersions.Where(v => v.TenantId == householdId && v.AgentId == agentId).ExecuteDeleteAsync(ct);
    }

    private static IQueryable<AgentVersion> Newest(AccountsDbContext db, string householdId, string agentId) =>
        db.AgentVersions
            .Where(v => v.TenantId == householdId && v.AgentId == agentId)
            .OrderByDescending(v => v.ReplacedAt)
            .ThenByDescending(v => v.Id);
}