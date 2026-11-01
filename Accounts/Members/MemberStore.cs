using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Members;

public sealed record Member(
    string Id, string DisplayName, Role Role, bool IsOperator, bool SeesAllConversations, bool Disabled = false);

public enum RoleChange
{
    Changed,
    NotFound,
    LastOwner
}

public enum Disabling
{
    Disabled,
    NotFound,
    LastOwner,

    // The operator's account is the deployment's way onto its dashboard and back in: no owner may
    // lock it, as no owner may mint it a recovery link.
    Operator
}

// A household's people, as the members page and the admin API manage them. Every call names the
// household, so an owner reaches no one outside their own.
public sealed class MemberStore(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    public async Task<IReadOnlyList<Member>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new Member(u.Id, u.DisplayName, u.Role, u.IsOperator, u.SeesAllConversations, u.DisabledAt != null))
            .ToListAsync(ct);
    }

    // The members an owner disabled, for the agent's view of the household: it refuses their turns and
    // believes no delegation naming them.
    public async Task<IReadOnlyList<string>> DisabledAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users.Where(u => u.TenantId == tenantId && u.DisabledAt != null).Select(u => u.Id).ToListAsync(ct);
    }

    // One statement, the check inside it: two owners demoting each other at once cannot both pass a
    // count read before either wrote, which would leave a household nobody can manage. A disabled
    // owner manages nothing, so only an enabled one counts.
    public async Task<RoleChange> ChangeRoleAsync(string tenantId, string userId, Role role, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var changed = await db.Users
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .Where(u => role == Role.Owner
                        || db.Users.Any(o => o.TenantId == tenantId && o.Role == Role.Owner && o.DisabledAt == null && o.Id != userId))
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

    // The member and every session they hold, in one transaction: nothing signed in survives the
    // disabling, and the last enabled owner is checked inside the same statement as the role change's.
    // Nothing else of theirs is touched — their passkeys, links and identities simply stop answering
    // for them (every store that vouches for somebody reads DisabledAt), so re-enabling them is one
    // column. Disabling somebody disabled already keeps when it first happened.
    public async Task<Disabling> DisableAsync(string tenantId, string userId, CancellationToken ct)
    {
        var now = clock.Time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Users
            .Where(u => u.Id == userId && u.TenantId == tenantId && !u.IsOperator)
            .Where(u => u.Role != Role.Owner
                        || db.Users.Any(o => o.TenantId == tenantId && o.Role == Role.Owner && o.DisabledAt == null && o.Id != userId))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DisabledAt, x => x.DisabledAt ?? now), ct);
        if (changed == 0)
        {
            var person = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
            return person is null ? Disabling.NotFound
                : person.IsOperator ? Disabling.Operator
                : Disabling.LastOwner;
        }

        await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await transaction.CommitAsync(ct);
        return Disabling.Disabled;
    }

    // Their passkeys, Telegram, voice profile and links answer for them again; their sessions stay
    // ended, so they sign in again with a passkey they kept, or a recovery link.
    public async Task<bool> EnableAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DisabledAt, (DateTimeOffset?)null), ct) > 0;
    }
}