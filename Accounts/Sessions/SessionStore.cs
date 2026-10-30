using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Sessions;

// The value goes into the cookie and is never kept; the id is what a device list and a revoke name.
public sealed record IssuedSession(string Id, string Token);

// A signed-in browser as the account page lists it; the device is the user agent it signed in with.
public sealed record SignedInDevice(string Id, string? Device, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

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

    // A session by its id, while it lives, named by its owner as well (the ids are on the account
    // page, so they are no secret): how the chat hub learns its connection's person again for every
    // turn, and learns nothing once the session is revoked. Last-seen is the session check's to keep.
    public async Task<SessionIdentity?> LiveAsync(string userId, string sessionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .Join(db.Users, s => s.UserId, u => u.Id, (_, u) => u)
            .SingleOrDefaultAsync(ct);
        return user is null
            ? null
            : new SessionIdentity(sessionId, user.Id, user.TenantId, Roles.Of(user), user.DisplayName, user.AvatarUrl);
    }

    public async Task<IReadOnlyList<SignedInDevice>> ListAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s => new SignedInDevice(s.Id, s.Device, s.CreatedAt, s.LastSeenAt))
            .ToListAsync(ct);
    }

    // Named by its owner as well as its id: a session id is shown on a page, so it is no secret, and
    // naming somebody else's ends nothing.
    public async Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.Time.GetUtcNow();
        return await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct) > 0;
    }
}