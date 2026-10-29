using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Links;

public sealed record MintedLink(string UserId, string TenantId, string DisplayName, Uri Url, DateTimeOffset ExpiresAt);

// A link an owner minted on the members page, shown to them once: only its hash is kept.
public sealed record IssuedLink(Uri Url, DateTimeOffset ExpiresAt);

public sealed record TelegramCode(string Code, DateTimeOffset ExpiresAt);

// A link still waiting to be redeemed. An invite names nobody yet, so its display name and role are
// the ones it will create the person with; any other kind is for the person it names.
public sealed record OpenLink(string LinkId, LinkKind Kind, string TenantId, string? UserId, string DisplayName, Role? Role);

public sealed class LinkStore(IDbContextFactory<AccountsDbContext> contexts, AccountsSettings settings, AccountsClock clock)
{
    public const string EnrollPath = "/enroll";

    // What the enrollment page and its ceremony open. A Telegram code is not among them: it links a
    // chat, and a passkey minted from it would be a way in nobody handed out.
    public static readonly IReadOnlyList<LinkKind> Enrollable = [LinkKind.Invite, LinkKind.Recovery];

    // The operator's own way in, which needs nobody else: the first time it creates the operator in
    // the home household, and every later time it is a recovery of that same account, for when
    // every passkey is gone.
    public async Task<(MintedLink Link, bool Created)> MintForOperatorAsync(string? displayName, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.IsOperator, ct);
        if (existing is not null)
        {
            return (await MintRecoveryAsync(db, existing, createdBy: null, ct), false);
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("the operator does not exist yet, so it needs a display name");
        }

        var home = await db.Tenants.SingleAsync(t => t.IsHome, ct);
        var created = new User
        {
            Id = Tokens.NewId(),
            TenantId = home.Id,
            DisplayName = displayName.Trim(),
            Role = Role.Owner,
            IsOperator = true,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.Users.Add(created);
        return (await MintRecoveryAsync(db, created, createdBy: null, ct), true);
    }

    // The operator's command, which may recover anyone.
    public async Task<MintedLink?> MintRecoveryAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : await MintRecoveryAsync(db, user, createdBy: null, ct);
    }

    // An owner's, on the members page: only someone in their own household.
    public async Task<MintedLink?> MintRecoveryAsync(string tenantId, string createdBy, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        return user is null ? null : await MintRecoveryAsync(db, user, createdBy, ct);
    }

    public async Task<IssuedLink> MintInviteAsync(
        string tenantId, string createdBy, string displayName, Role role, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (token, link) = await AddAsync(db, LinkKind.Invite, tenantId, userId: null, displayName, role, createdBy, ct);
        return new IssuedLink(UrlOf(token), link.ExpiresAt);
    }

    // The code itself, which is the bot's start parameter: 43 base64url characters, inside the 64 of
    // [A-Za-z0-9_-] Telegram passes on.
    public async Task<TelegramCode> MintTelegramCodeAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (token, link) = await AddAsync(db, LinkKind.TelegramLink, tenantId, userId, null, null, userId, ct);
        return new TelegramCode(token, link.ExpiresAt);
    }

    public Task<OpenLink?> FindOpenAsync(string token, CancellationToken ct) => FindOpenAsync(token, Enrollable, ct);

    public async Task<OpenLink?> FindOpenAsync(string token, IReadOnlyCollection<LinkKind> kinds, CancellationToken ct)
    {
        var hash = Tokens.Hash(token);
        var now = clock.Time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var found = await db.Links
            .Where(l => l.TokenHash == hash && l.UsedAt == null && l.ExpiresAt > now)
            .Select(l => new
            {
                Link = l,
                Named = db.Users.Where(u => u.Id == l.UserId).Select(u => u.DisplayName).FirstOrDefault()
            })
            .SingleOrDefaultAsync(ct);
        return found is null || !kinds.Contains(found.Link.Kind)
            ? null
            : new OpenLink(found.Link.Id, found.Link.Kind, found.Link.TenantId, found.Link.UserId,
                found.Named ?? found.Link.DisplayName ?? "", found.Link.Role);
    }

    // Spends the link inside the caller's transaction, so the passkey it enrolls and the link being
    // used are one change: of two ceremonies racing on one link, only one gets to write.
    public async Task<bool> SpendAsync(AccountsDbContext db, string linkId, CancellationToken ct)
    {
        var now = clock.Time.GetUtcNow();
        return await db.Links
            .Where(l => l.Id == linkId && l.UsedAt == null && l.ExpiresAt > now)
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.UsedAt, now), ct) == 1;
    }

    // Spends an invite and creates the person it names, with the id the caller chose, inside the
    // caller's transaction: the passkey ceremony adds the passkey in it, and Telegram's start
    // parameter will add the chat's id, so a person never exists without the way in that made them.
    public async Task<User?> RedeemInviteAsync(AccountsDbContext db, string linkId, string userId, CancellationToken ct)
    {
        var invite = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.Kind == LinkKind.Invite, ct);
        if (invite is null || !await SpendAsync(db, linkId, ct))
        {
            return null;
        }

        var user = new User
        {
            Id = userId,
            TenantId = invite.TenantId,
            DisplayName = invite.DisplayName ?? "",
            Role = invite.Role ?? Role.Member,
            CreatedAt = clock.Time.GetUtcNow()
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public Uri UrlOf(string token) =>
        new(settings.PublicBase, $"{AccountsApp.PathBase.TrimStart('/')}{EnrollPath}/{token}");

    private async Task<MintedLink> MintRecoveryAsync(AccountsDbContext db, User user, string? createdBy, CancellationToken ct)
    {
        var (token, link) = await AddAsync(db, LinkKind.Recovery, user.TenantId, user.Id, null, null, createdBy, ct);
        return new MintedLink(user.Id, user.TenantId, user.DisplayName, UrlOf(token), link.ExpiresAt);
    }

    private async Task<(string Token, Link Link)> AddAsync(
        AccountsDbContext db, LinkKind kind, string tenantId, string? userId, string? displayName, Role? role,
        string? createdBy, CancellationToken ct)
    {
        var token = Tokens.NewSecret();
        var now = clock.Time.GetUtcNow();
        var link = new Link
        {
            Id = Tokens.NewId(),
            TokenHash = Tokens.Hash(token),
            Kind = kind,
            TenantId = tenantId,
            UserId = userId,
            DisplayName = displayName,
            Role = role,
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now.AddDays(settings.LinkLifetimeDays)
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return (token, link);
    }
}