using System.Globalization;
using Accounts.Store;
using Domain.DTOs.Channel;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Links;

public enum StartOutcome
{
    // The invite created its person, linked to the Telegram that redeemed it.
    Invited,

    // The account page's code linked its person to the Telegram that redeemed it.
    Linked,

    // Nothing open goes by that code: never minted, used already, past its week, or of a kind the
    // start parameter does not redeem.
    CodeRefused,

    // The Telegram that sent it is somebody else's already, or anybody's for an invite.
    TelegramTaken
}

public sealed record StartRedemption(StartOutcome Outcome, User? Person = null);

public enum WebLinkOutcome
{
    Minted,
    NotLinked,

    // The operator's way to a new passkey is their account page or the container's command: their
    // Telegram must not be a way onto the dashboard.
    Operator
}

public sealed record WebLink(WebLinkOutcome Outcome, MintedLink? Link = null);

public enum BindOutcome
{
    Bound,
    CodeRefused,
    BoundElsewhere
}

public sealed record GroupBinding(BindOutcome Outcome, string? TenantId = null);

// Telegram's ways in, each of them asked by the bot with the numeric ids it saw and never by a
// browser: a start parameter redeemed and recorded against the sender, a passkey link for somebody
// already linked, and a group bound to a household.
public sealed class TelegramLinks(IDbContextFactory<AccountsDbContext> contexts, LinkStore links, AccountsClock clock)
{
    public const string Channel = ChannelIds.Telegram;

    private static readonly IReadOnlyList<LinkKind> _startable = [LinkKind.Invite, LinkKind.TelegramLink];
    private static readonly IReadOnlyList<LinkKind> _bindable = [LinkKind.TelegramGroup];

    public static string IdentityOf(long telegramId) => telegramId.ToString(CultureInfo.InvariantCulture);

    // The code is spent and the Telegram recorded in one transaction, so a person never exists
    // without the way in that made them, and a code refused for a taken Telegram stays open.
    public async Task<StartRedemption> RedeemStartAsync(long telegramId, string code, CancellationToken ct)
    {
        if (await links.FindOpenAsync(code, _startable, ct) is not { } link)
        {
            return new StartRedemption(StartOutcome.CodeRefused);
        }

        var identity = IdentityOf(telegramId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var holder = await db.LinkedIdentities
            .Where(i => i.Channel == Channel && i.Identity == identity)
            .Select(i => i.UserId)
            .FirstOrDefaultAsync(ct);

        // An invite makes somebody new, so a Telegram that is anyone's already would become two
        // people; the account page's code may link its own person again.
        if (holder is not null && (link.Kind == LinkKind.Invite || holder != link.UserId))
        {
            return new StartRedemption(StartOutcome.TelegramTaken);
        }

        var person = link.Kind == LinkKind.Invite
            ? await links.RedeemInviteAsync(db, link.LinkId, Tokens.NewId(), ct)
            : await links.SpendAsync(db, link.LinkId, ct)
                ? await db.Users.SingleAsync(u => u.Id == link.UserId, ct)
                : null;
        if (person is null)
        {
            return new StartRedemption(StartOutcome.CodeRefused);
        }

        // At most one Telegram per person: linking another one replaces it.
        await db.LinkedIdentities.Where(i => i.UserId == person.Id && i.Channel == Channel).ExecuteDeleteAsync(ct);
        db.LinkedIdentities.Add(new LinkedIdentity
        {
            Id = Tokens.NewId(),
            TenantId = person.TenantId,
            UserId = person.Id,
            Channel = Channel,
            Identity = identity,
            CreatedAt = clock.Time.GetUtcNow()
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another redemption recorded this Telegram first; the unique index is what decided it.
            return new StartRedemption(StartOutcome.TelegramTaken);
        }

        await transaction.CommitAsync(ct);
        return new StartRedemption(link.Kind == LinkKind.Invite ? StartOutcome.Invited : StartOutcome.Linked, person);
    }

    public async Task<User?> PersonAsync(long telegramId, CancellationToken ct)
    {
        var identity = IdentityOf(telegramId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.LinkedIdentities
            .Where(i => i.Channel == Channel && i.Identity == identity)
            .Join(db.Users, i => i.UserId, u => u.Id, (_, u) => u)
            .FirstOrDefaultAsync(ct);
    }

    // A recovery link the person asked for themselves, from their own Telegram.
    public async Task<WebLink> MintWebLinkAsync(long telegramId, CancellationToken ct)
    {
        if (await PersonAsync(telegramId, ct) is not { } person)
        {
            return new WebLink(WebLinkOutcome.NotLinked);
        }

        if (person.IsOperator)
        {
            return new WebLink(WebLinkOutcome.Operator);
        }

        return new WebLink(WebLinkOutcome.Minted, await links.MintRecoveryAsync(person.TenantId, person.Id, person.Id, ct));
    }

    public async Task<GroupBinding> BindGroupAsync(long chatId, string code, CancellationToken ct)
    {
        if (await links.FindOpenAsync(code, _bindable, ct) is not { } link)
        {
            return new GroupBinding(BindOutcome.CodeRefused);
        }

        var chat = IdentityOf(chatId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var boundTo = await db.BoundChats
            .Where(c => c.Channel == Channel && c.ChatId == chat)
            .Select(c => c.TenantId)
            .FirstOrDefaultAsync(ct);
        if (boundTo is not null && boundTo != link.TenantId)
        {
            return new GroupBinding(BindOutcome.BoundElsewhere);
        }

        if (!await links.SpendAsync(db, link.LinkId, ct))
        {
            return new GroupBinding(BindOutcome.CodeRefused);
        }

        if (boundTo is null)
        {
            db.BoundChats.Add(new BoundChat
            {
                Id = Tokens.NewId(),
                TenantId = link.TenantId,
                Channel = Channel,
                ChatId = chat,
                BoundBy = await db.Links.Where(l => l.Id == link.LinkId).Select(l => l.CreatedBy).SingleAsync(ct),
                CreatedAt = clock.Time.GetUtcNow()
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return new GroupBinding(BindOutcome.BoundElsewhere);
            }
        }

        await transaction.CommitAsync(ct);
        return new GroupBinding(BindOutcome.Bound, link.TenantId);
    }

    // The Telegram a person linked, which is also their private chat with the bot; null for a person
    // who linked none, or one the household named does not hold.
    public async Task<long?> TelegramIdOfAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var identity = await db.LinkedIdentities
            .Where(l => l.Channel == Channel && l.UserId == userId)
            .Join(db.Users.Where(u => u.TenantId == tenantId), l => l.UserId, u => u.Id, (l, _) => l.Identity)
            .FirstOrDefaultAsync(ct);
        return long.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var telegramId)
            ? telegramId
            : null;
    }

    public async Task<string?> GroupTenantAsync(long chatId, CancellationToken ct)
    {
        var chat = IdentityOf(chatId);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.BoundChats
            .Where(c => c.Channel == Channel && c.ChatId == chat)
            .Select(c => c.TenantId)
            .FirstOrDefaultAsync(ct);
    }
}