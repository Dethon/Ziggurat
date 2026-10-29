using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Sessions;

// The value goes into the cookie and is never kept; the id is what a device list and a revoke name.
public sealed record IssuedSession(string Id, string Token);

public sealed record SessionIdentity(
    string SessionId, string UserId, string TenantId, IReadOnlyList<string> Roles, string DisplayName, string? AvatarUrl);

// Sessions last until revoked. Issuing one is a service of its own, apart from the ceremonies, so a
// test stack can sign a seeded person in without a passkey (the `mint-session` command).
public sealed class SessionStore(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
{
    // The check runs on every request through the proxy, so last-seen is written at this grain
    // rather than on each of them: a device list needs the day, not the second.
    private static readonly TimeSpan _seenGrain = TimeSpan.FromMinutes(5);

    public async Task<IssuedSession?> IssueAsync(string userId, string? credentialId, string? device, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) is not { } user)
        {
            return null;
        }

        var token = Tokens.NewSecret();
        var now = clock.Time.GetUtcNow();
        var session = new Session
        {
            Id = Tokens.NewId(),
            TokenHash = Tokens.Hash(token),
            TenantId = user.TenantId,
            UserId = user.Id,
            CredentialId = credentialId,
            Device = device is { Length: > 500 } ? device[..500] : device,
            CreatedAt = now,
            LastSeenAt = now
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new IssuedSession(session.Id, token);
    }

    public async Task<SessionIdentity?> VerifyAsync(string token, CancellationToken ct)
    {
        var hash = Tokens.Hash(token);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var found = await db.Sessions
            .Where(s => s.TokenHash == hash && s.RevokedAt == null)
            .Join(db.Users, s => s.UserId, u => u.Id, (session, user) => new { Session = session, User = user })
            .SingleOrDefaultAsync(ct);
        if (found is null)
        {
            return null;
        }

        var now = clock.Time.GetUtcNow();
        if (now - found.Session.LastSeenAt >= _seenGrain)
        {
            found.Session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new SessionIdentity(
            found.Session.Id, found.User.Id, found.User.TenantId, Roles.Of(found.User), found.User.DisplayName,
            found.User.AvatarUrl);
    }

    public async Task<bool> RevokeAsync(string sessionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.Time.GetUtcNow();
        return await db.Sessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct) > 0;
    }
}