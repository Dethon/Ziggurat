using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Links;

public sealed record MintedLink(string UserId, string TenantId, string DisplayName, Uri Url, DateTimeOffset ExpiresAt);

// A link still waiting to be redeemed, and whom it adds a passkey for.
public sealed record OpenLink(string LinkId, string UserId, string DisplayName);

public sealed class LinkStore(IDbContextFactory<AccountsDbContext> contexts, AccountsSettings settings, AccountsClock clock)
{
    public const string EnrollPath = "/enroll";

    // The operator's own way in, which needs nobody else: the first time it creates the operator in
    // the home household, and every later time it is a recovery of that same account, for when
    // every passkey is gone.
    public async Task<(MintedLink Link, bool Created)> MintForOperatorAsync(string? displayName, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.Users.FirstOrDefaultAsync(u => u.IsOperator, ct);
        if (existing is not null)
        {
            return (await MintAsync(db, existing, ct), false);
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
        return (await MintAsync(db, created, ct), true);
    }

    public async Task<MintedLink?> MintRecoveryAsync(string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : await MintAsync(db, user, ct);
    }

    public async Task<OpenLink?> FindOpenAsync(string token, CancellationToken ct)
    {
        var hash = Tokens.Hash(token);
        var now = clock.Time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Links
            .Where(l => l.TokenHash == hash && l.Kind == LinkKind.Recovery && l.UsedAt == null && l.ExpiresAt > now)
            .Join(db.Users, l => l.UserId, u => u.Id, (link, user) => new OpenLink(link.Id, user.Id, user.DisplayName))
            .SingleOrDefaultAsync(ct);
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

    public Uri UrlOf(string token) =>
        new(settings.PublicBase, $"{AccountsApp.PathBase.TrimStart('/')}{EnrollPath}/{token}");

    private async Task<MintedLink> MintAsync(AccountsDbContext db, User user, CancellationToken ct)
    {
        var token = Tokens.NewSecret();
        var now = clock.Time.GetUtcNow();
        var link = new Link
        {
            Id = Tokens.NewId(),
            TokenHash = Tokens.Hash(token),
            Kind = LinkKind.Recovery,
            TenantId = user.TenantId,
            UserId = user.Id,
            CreatedAt = now,
            ExpiresAt = now.AddDays(settings.LinkLifetimeDays)
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(ct);
        return new MintedLink(user.Id, user.TenantId, user.DisplayName, UrlOf(token), link.ExpiresAt);
    }
}