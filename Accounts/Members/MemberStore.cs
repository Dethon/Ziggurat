using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Members;

public sealed record Member(string Id, string DisplayName, Role Role, bool IsOperator, bool SeesAllConversations);

public enum RoleChange
{
    Changed,
    NotFound,
    LastOwner
}

// A household's people, as the members page manages them. Every call names the household, so an
// owner reaches no one outside their own.
public sealed class MemberStore(IDbContextFactory<AccountsDbContext> contexts)
{
    public async Task<IReadOnlyList<Member>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new Member(u.Id, u.DisplayName, u.Role, u.IsOperator, u.SeesAllConversations))
            .ToListAsync(ct);
    }

    // One statement, the check inside it: two owners demoting each other at once cannot both pass a
    // count read before either wrote, which would leave a household nobody can manage.
    public async Task<RoleChange> ChangeRoleAsync(string tenantId, string userId, Role role, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var changed = await db.Users
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .Where(u => role == Role.Owner
                        || db.Users.Any(o => o.TenantId == tenantId && o.Role == Role.Owner && o.Id != userId))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Role, role), ct);
        if (changed > 0)
        {
            return RoleChange.Changed;
        }

        return await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == tenantId, ct)
            ? RoleChange.LastOwner
            : RoleChange.NotFound;
    }

    public async Task<bool> SetSeesAllConversationsAsync(string tenantId, string userId, bool granted, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SeesAllConversations, granted), ct) > 0;
    }
}