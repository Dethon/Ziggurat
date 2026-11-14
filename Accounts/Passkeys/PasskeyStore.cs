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
    // A person's own revocation: their last passkey stays, since nothing but an owner's recovery link
    // would let them back in. Counted inside the revocation's transaction, so two revocations at once
    // cannot take the last two.
    public async Task<OwnRevocation> RevokeOwnAsync(string userId, string passkeyId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var held = await db.Credentials.Where(c => c.UserId == userId && c.RevokedAt == null).Select(c => c.Id).ToListAsync(ct);
        if (!held.Contains(passkeyId))
        {
            return OwnRevocation.NotHeld;
        }

        if (held.Count == 1)
        {
            return OwnRevocation.Last;
        }

        await RevokeWithinAsync(db, passkeyId, ct);
        await transaction.CommitAsync(ct);
        return OwnRevocation.Revoked;
    }

    public async Task<bool> RevokeAsync(string userId, string passkeyId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Credentials.AnyAsync(c => c.Id == passkeyId && c.UserId == userId && c.RevokedAt == null, ct))
        {
            return false;
        }

        await RevokeWithinAsync(db, passkeyId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task RevokeWithinAsync(AccountsDbContext db, string passkeyId, CancellationToken ct)
    {
        var now = clock.Time.GetUtcNow();
        await db.Credentials
            .Where(c => c.Id == passkeyId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.RevokedAt, now), ct);
        await db.Sessions
            .Where(s => s.CredentialId == passkeyId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }
}

public enum OwnRevocation
{
    Revoked,
    NotHeld,
    Last
}