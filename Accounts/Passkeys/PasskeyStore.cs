using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Passkeys;

public sealed record Passkey(string Id, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

// A person's passkeys, as their account page lists and revokes them.
public sealed class PasskeyStore(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    public async Task<IReadOnlyList<Passkey>> ListAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Credentials
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new Passkey(c.Id, c.CreatedAt, c.LastUsedAt))
            .ToListAsync(ct);
    }

    // Named by its owner as well as its id, so one person can never revoke another's. Every session
    // the passkey began ends with it, in the same change: a key that might be compromised closes
    // what it opened.
    public async Task<bool> RevokeAsync(string userId, string passkeyId, CancellationToken ct)
    {
        var now = clock.Time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revoked = await db.Credentials
            .Where(c => c.Id == passkeyId && c.UserId == userId && c.RevokedAt == null)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.RevokedAt, now), ct);
        if (revoked == 0)
        {
            return false;
        }

        await db.Sessions
            .Where(s => s.CredentialId == passkeyId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}