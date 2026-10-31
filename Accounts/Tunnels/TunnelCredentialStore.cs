using Accounts.Store;
using Domain.DTOs.Tunnel;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Tunnels;

// The credential goes to the household's server and is never kept; the id is what a list and a
// revoke name.
public sealed record MintedTunnelCredential(string Id, string TenantId, string ServerName, string Credential);

public sealed record ListedTunnelCredential(
    string Id, string TenantId, string ServerName, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

// NotTold is why the listeners were not told, or null when they were: the revocation stands either
// way, and the relay refuses it on the tunnel's next open.
public sealed record RevokedTunnelCredential(
    string Id, string TenantId, string ServerName, DateTimeOffset RevokedAt, bool AlreadyRevoked, string? NotTold);

public abstract record TunnelVerdict
{
    public sealed record Opens(TunnelCredentialVerified Credential) : TunnelVerdict;

    public sealed record Refused(TunnelCredentialRefusal Reason) : TunnelVerdict;
}

// A tunnel credential belongs to one household and one server name. A server may hold more than one
// live credential at a time, so a credential is replaced without a gap: mint the new one, start the
// server with it, revoke the old.
public sealed class TunnelCredentialStore(
    IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock, TunnelRevocationPublisher revocations)
{
    public async Task<MintedTunnelCredential?> MintAsync(string tenantId, string serverName, CancellationToken ct)
    {
        if (!TunnelCredentialProtocol.IsServerName(serverName))
        {
            throw new ArgumentException(
                $"'{serverName}' is not a server name: lowercase letters, digits and inner hyphens, "
                + $"at most {TunnelCredentialProtocol.ServerNameMaxLength} characters");
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct))
        {
            return null;
        }

        var credential = Tokens.NewSecret();
        var row = new TunnelCredential
        {
            Id = Tokens.NewId(),
            CredentialHash = Tokens.Hash(credential),
            TenantId = tenantId,
            ServerName = serverName,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.TunnelCredentials.Add(row);
        await db.SaveChangesAsync(ct);
        return new MintedTunnelCredential(row.Id, row.TenantId, row.ServerName, credential);
    }

    // A credential is 256 random bits, so there is no dictionary to run against its hash: SHA-256 is
    // as good as a slow password hash here and costs the relay nothing on every open. It is found by
    // its hash, as a session is, so the only thing a lookup's timing could tell is how much of a
    // hash matched — which says nothing about any credential that would produce it.
    public async Task<TunnelVerdict> VerifyAsync(string credential, string serverName, CancellationToken ct)
    {
        var hash = Tokens.Hash(credential);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.TunnelCredentials.AsNoTracking().SingleOrDefaultAsync(t => t.CredentialHash == hash, ct) switch
        {
            null => new TunnelVerdict.Refused(TunnelCredentialRefusal.Unknown),
            { RevokedAt: not null } => new TunnelVerdict.Refused(TunnelCredentialRefusal.Revoked),
            var row when row.ServerName != serverName => new TunnelVerdict.Refused(TunnelCredentialRefusal.AnotherServer),
            var row => new TunnelVerdict.Opens(new TunnelCredentialVerified(
                row.Id, row.TenantId, row.ServerName,
                Home: await db.Tenants.AnyAsync(t => t.Id == row.TenantId && t.IsHome, ct)))
        };
    }

    // Whose live credential this is, whatever server it was minted for: what the internal API asks of
    // a household's server whose back-call the relay showed it, and null for anything not live.
    public async Task<TunnelCredentialVerified?> LiveAsync(string credential, CancellationToken ct)
    {
        var hash = Tokens.Hash(credential);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.TunnelCredentials.AsNoTracking()
            .Where(t => t.CredentialHash == hash && t.RevokedAt == null)
            .Select(t => new TunnelCredentialVerified(t.Id, t.TenantId, t.ServerName))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ListedTunnelCredential>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.TunnelCredentials.AsNoTracking().ToListAsync(ct);
        return
        [
            .. rows
                .OrderBy(t => t.TenantId, StringComparer.Ordinal)
                .ThenBy(t => t.ServerName, StringComparer.Ordinal)
                .ThenBy(t => t.CreatedAt)
                .Select(t => new ListedTunnelCredential(t.Id, t.TenantId, t.ServerName, t.CreatedAt, t.RevokedAt))
        ];
    }

    // Revoking one already revoked tells the listeners again, so running the command once more is
    // the retry when they could not be told the first time.
    public async Task<RevokedTunnelCredential?> RevokeAsync(string id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.TunnelCredentials.SingleOrDefaultAsync(t => t.Id == id, ct) is not { } row)
        {
            return null;
        }

        var alreadyRevoked = row.RevokedAt is not null;
        row.RevokedAt ??= clock.Time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var notTold = await revocations.TellAsync(new TunnelCredentialRevoked(row.Id, row.TenantId, row.ServerName), ct);
        return new RevokedTunnelCredential(row.Id, row.TenantId, row.ServerName, row.RevokedAt.Value, alreadyRevoked, notTold);
    }
}