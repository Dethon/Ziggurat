using Accounts.Announcements;
using Accounts.Store;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Outposts;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Tunnels;

// The credential goes to the household's server and is never kept; the id is what a list and a
// revoke name. NotTold is why the configuration's readers were not told the household set a server up,
// or null when they were: the credential stands either way, and the agent sees it within its cache's
// lifetime.
public sealed record MintedTunnelCredential(
    string Id, string TenantId, string ServerName, string Credential, string? NotTold = null);

public sealed record ListedTunnelCredential(
    string Id, string TenantId, string ServerName, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

// NotTold is why the relay was not told, or null when it was: the revocation stands either way, and
// the relay refuses it on the tunnel's next open. ReadersNotTold is why the configuration's readers
// were not told the household's servers changed: the agent then stops naming it within its cache's
// lifetime.
public sealed record RevokedTunnelCredential(
    string Id, string TenantId, string ServerName, DateTimeOffset RevokedAt, bool AlreadyRevoked, string? NotTold,
    string? ReadersNotTold = null);

public abstract record TunnelVerdict
{
    public sealed record Opens(TunnelCredentialVerified Credential) : TunnelVerdict;

    public sealed record Refused(TunnelCredentialRefusal Reason) : TunnelVerdict;
}

// A tunnel credential belongs to one household and one server name. A server may hold more than one
// live credential at a time, so a credential is replaced without a gap: mint the new one, start the
// server with it, revoke the old.
//
// A mint or a revocation changes which own servers the household set up — what the agent's view of it
// names — so each is announced as a save of its configuration is (HouseholdConfigProtocol.OwnServersChanged),
// and the agent's next turn reads the view again.
public sealed class TunnelCredentialStore(
    IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock, TunnelRevocationPublisher revocations,
    RedisAnnouncer announcer)
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
        return new MintedTunnelCredential(row.Id, row.TenantId, row.ServerName, credential, await AnnounceAsync(tenantId, ct));
    }

    // A credential is 256 random bits, so there is no dictionary to run against its hash: SHA-256 is
    // as good as a slow password hash here and costs the relay nothing on every open. It is found by
    // its hash, as a session is, so the only thing a lookup's timing could tell is how much of a
    // hash matched — which says nothing about any credential that would produce it.
    public async Task<TunnelVerdict> VerifyAsync(string credential, string serverName, CancellationToken ct)
    {
        var hash = Tokens.Hash(credential);
        await using var db = await contexts.CreateDbContextAsync(ct);
        // What the credential is comes first; whether its household runs only for one that would open.
        var found = await db.TunnelCredentials.AsNoTracking()
            .Where(t => t.CredentialHash == hash)
            .Join(db.Tenants, t => t.TenantId, h => h.Id, (row, household) => new { Row = row, Household = household })
            .SingleOrDefaultAsync(ct);
        return found switch
        {
            null => new TunnelVerdict.Refused(TunnelCredentialRefusal.Unknown),
            { Row.RevokedAt: not null } => new TunnelVerdict.Refused(TunnelCredentialRefusal.Revoked),
            _ when found.Row.ServerName != serverName => new TunnelVerdict.Refused(TunnelCredentialRefusal.AnotherServer),
            { Household.SuspendedAt: not null } => new TunnelVerdict.Refused(TunnelCredentialRefusal.Suspended),
            _ => new TunnelVerdict.Opens(new TunnelCredentialVerified(
                found.Row.Id, found.Row.TenantId, found.Row.ServerName, Home: found.Household.IsHome))
        };
    }

    // Whose live credential this is, whatever server it was minted for: what the internal API asks of
    // a household's server whose back-call the relay showed it, and null for anything not live — a
    // suspended household's included, whose servers ask nothing until it is resumed.
    public async Task<TunnelCredentialVerified?> LiveAsync(string credential, CancellationToken ct)
    {
        var hash = Tokens.Hash(credential);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.TunnelCredentials.AsNoTracking()
            .Where(t => t.CredentialHash == hash && t.RevokedAt == null)
            .Where(t => db.Tenants.Any(h => h.Id == t.TenantId && h.SuspendedAt == null))
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

    // Every household holding a live credential for the server, and the home whether it holds one or
    // not, home first: who the agent's key commands must ask for the delegations a server of that name
    // keeps. A suspended household is among them, since its server keeps its delegations while its
    // tunnel is refused, and they must verify again once it is resumed.
    public async Task<IReadOnlyList<TunnelServerHolder>> HoldersAsync(string serverName, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var households = await db.Tenants.AsNoTracking()
            .Where(h => h.IsHome || db.TunnelCredentials.Any(
                t => t.TenantId == h.Id && t.ServerName == serverName && t.RevokedAt == null))
            .ToListAsync(ct);
        return
        [
            .. households
                .OrderByDescending(h => h.IsHome)
                .ThenBy(h => h.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Id, StringComparer.Ordinal)
                .Select(h => new TunnelServerHolder(h.Id, h.Name, h.IsHome, h.SuspendedAt is not null))
        ];
    }

    // One household's credentials not revoked, by server name and when minted: what its owners see.
    public async Task<IReadOnlyList<ListedTunnelCredential>> LiveOfAsync(string tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.TunnelCredentials.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.RevokedAt == null)
            .ToListAsync(ct);
        return
        [
            .. rows
                .OrderBy(t => t.ServerName, StringComparer.Ordinal)
                .ThenBy(t => t.CreatedAt)
                .Select(t => new ListedTunnelCredential(t.Id, t.TenantId, t.ServerName, t.CreatedAt, t.RevokedAt))
        ];
    }

    private Task<string?> AnnounceAsync(string tenantId, CancellationToken ct) =>
        announcer.AnnounceAsync(HouseholdConfigProtocol.SavedChannel,
            new HouseholdConfigSaved(tenantId, HouseholdConfigProtocol.OwnServersChanged), ct);

    // The names of the household's own servers it set up — those it holds a live credential for, each
    // once — and never a machine's: an outpost named `vault` is not the household's vault server.
    public async Task<IReadOnlyList<string>> SetUpServersAsync(string tenantId, CancellationToken ct) =>
    [
        .. (await LiveOfAsync(tenantId, ct))
            .Select(t => t.ServerName)
            .Where(name => OutpostServerName.NameOf(name) is null)
            .Distinct()
    ];

    // Revoking one already revoked tells the listeners again, so running the command once more is
    // the retry when they could not be told the first time.
    public Task<RevokedTunnelCredential?> RevokeAsync(string id, CancellationToken ct) => RevokeAsync(id, tenantId: null, ct);

    // The same, for a credential of that household alone: an owner's revoke names an id, and an id of
    // another household's is nobody's to them.
    public async Task<RevokedTunnelCredential?> RevokeAsync(string id, string? tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.TunnelCredentials.SingleOrDefaultAsync(t => t.Id == id && (tenantId == null || t.TenantId == tenantId), ct)
            is not { } row)
        {
            return null;
        }

        var alreadyRevoked = row.RevokedAt is not null;
        row.RevokedAt ??= clock.Time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var notTold = await revocations.TellAsync(new TunnelCredentialRevoked(row.Id, row.TenantId, row.ServerName), ct);
        return new RevokedTunnelCredential(
            row.Id, row.TenantId, row.ServerName, row.RevokedAt.Value, alreadyRevoked, notTold,
            await AnnounceAsync(row.TenantId, ct));
    }
}