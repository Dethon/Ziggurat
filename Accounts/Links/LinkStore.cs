using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Links;

public sealed record MintedLink(string UserId, string TenantId, string DisplayName, Uri Url, DateTimeOffset ExpiresAt);

// A link an owner minted on the members page, shown to them once: only its hash is kept. The token is
// also what an invite's Telegram deep link carries as the bot's start parameter.
public sealed record IssuedLink(string Token, Uri Url, DateTimeOffset ExpiresAt);

public sealed record TelegramCode(string Code, DateTimeOffset ExpiresAt);

// What asking for a person's recovery link came to: the link, nobody by that id there, or a member an
// owner disabled, whose link would open nothing until they are re-enabled.
public abstract record RecoveryMint
{
    public sealed record Minted(MintedLink Link) : RecoveryMint;

    public sealed record Nobody : RecoveryMint;

    public sealed record Disabled : RecoveryMint;
}

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

    // A household the operator creates from the dashboard, its one owner and the owner's way in, as
    // one change: the owner exists from now on, with no passkey until the link enrolls one, so a lost
    // link is replaced by a recovery like any owner's. Nothing of its configuration is written: a
    // section it never saved reads as its defaults.
    public async Task<MintedLink> CreateHouseholdAsync(string name, string ownerName, string createdBy, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = clock.Time.GetUtcNow();
        var household = new Tenant { Id = Tokens.NewId(), Name = name, CreatedAt = now };
        var owner = new User
        {
            Id = Tokens.NewId(), TenantId = household.Id, DisplayName = ownerName, Role = Role.Owner, CreatedAt = now
        };
        db.Tenants.Add(household);
        db.Users.Add(owner);
        return await MintRecoveryAsync(db, owner, createdBy, ct);
    }

    // The operator's command, which may recover anyone an owner has not disabled.
    public async Task<RecoveryMint> MintRecoveryAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await RecoverAsync(db, await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct), createdBy: null, ct);
    }

    // An owner's, from the members page or the admin API, and the operator's for another household's
    // owner: only someone in the household named.
    public async Task<RecoveryMint> MintRecoveryAsync(string tenantId, string createdBy, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, ct);
        return await RecoverAsync(db, user, createdBy, ct);
    }

    // Every recovery a person is named for comes through here, so a disabled member is minted none by
    // any of them: FindOpenAsync would refuse the link until they are re-enabled, and a link that
    // silently opens nothing is worse than being told why.
    private async Task<RecoveryMint> RecoverAsync(AccountsDbContext db, User? user, string? createdBy, CancellationToken ct) =>
        user switch
        {
            null => new RecoveryMint.Nobody(),
            { DisabledAt: not null } => new RecoveryMint.Disabled(),
            _ => new RecoveryMint.Minted(await MintRecoveryAsync(db, user, createdBy, ct))
        };

    public async Task<IssuedLink> MintInviteAsync(
        string tenantId, string createdBy, string displayName, Role role, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (token, link) = await AddAsync(db, LinkKind.Invite, tenantId, userId: null, displayName, role, createdBy, ct);
        return new IssuedLink(token, UrlOf(token), link.ExpiresAt);
    }

    // The code itself, which is the bot's start parameter: 43 base64url characters, inside the 64 of
    // [A-Za-z0-9_-] Telegram passes on.
    public async Task<TelegramCode> MintTelegramCodeAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (token, link) = await AddAsync(db, LinkKind.TelegramLink, tenantId, userId, null, null, userId, ct);
        return new TelegramCode(token, link.ExpiresAt);
    }

    // An owner's code that binds a Telegram group to their household, sent to the bot in that group.
    public async Task<TelegramCode> MintGroupCodeAsync(string tenantId, string createdBy, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (token, link) = await AddAsync(db, LinkKind.TelegramGroup, tenantId, null, null, null, createdBy, ct);
        return new TelegramCode(token, link.ExpiresAt);
    }

    public Task<OpenLink?> FindOpenAsync(string token, CancellationToken ct) => FindOpenAsync(token, Enrollable, ct);

    // A link naming a member an owner disabled opens nothing while they are: a recovery link would add
    // them a way in, and a Telegram code a way to be somebody on Telegram. One still in date opens again
    // once they are re-enabled.
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
                Named = db.Users.Where(u => u.Id == l.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                Disabled = db.Users.Any(u => u.Id == l.UserId && u.DisabledAt != null)
            })
            .SingleOrDefaultAsync(ct);
        return found is null || found.Disabled || !kinds.Contains(found.Link.Kind)
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

    // A person's newest recovery link closes their earlier ones (their week ends now, so each reads as
    // any spent link does): one sent to the wrong person is taken back by minting another, which is
    // what an owner does on finding out. An invite or a Telegram code is another kind and stays.
    private async Task<MintedLink> MintRecoveryAsync(AccountsDbContext db, User user, string? createdBy, CancellationToken ct)
    {
        var now = clock.Time.GetUtcNow();
        await db.Links
            .Where(l => l.Kind == LinkKind.Recovery && l.UserId == user.Id && l.UsedAt == null && l.ExpiresAt > now)
            .ExecuteUpdateAsync(l => l.SetProperty(x => x.ExpiresAt, now), ct);
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