using Accounts.Links;

namespace Accounts.Internal;

public sealed record TelegramStart(long TelegramId, string Code);

public sealed record TelegramStarted(string Outcome, string TenantId, string UserId, string DisplayName);

public sealed record TelegramPerson(long TelegramId);

public sealed record TelegramWebLink(Uri Url, DateTimeOffset ExpiresAt);

public sealed record TelegramBind(long ChatId, string Code);

public sealed record TelegramChat(long ChatId);

public sealed record TelegramGroupTenant(string TenantId);

public sealed record TelegramAccount(string TenantId, string UserId);

public sealed record TelegramOfAccount(long TelegramId);

// What the Telegram bot asks with its own credential (InternalApi). Each answer is a status the bot
// tells its person apart by: 404 is a code nothing open goes by (or a sender or group nothing links),
// 409 a Telegram or a group that is somebody else's already, 403 the operator asking for a passkey
// link their Telegram must not hand out.
public static class TelegramLinking
{
    public const string Path = "/telegram";

    public static void Map(IEndpointRouteBuilder routes)
    {
        var telegram = routes.MapGroup(Path);

        telegram.MapPost("/start", async (TelegramStart start, TelegramLinks links, CancellationToken ct) =>
            await links.RedeemStartAsync(start.TelegramId, start.Code, ct) switch
            {
                { Outcome: StartOutcome.Invited or StartOutcome.Linked, Person: { } person } redeemed => Results.Ok(
                    new TelegramStarted(
                        redeemed.Outcome == StartOutcome.Invited ? "invited" : "linked",
                        person.TenantId, person.Id, person.DisplayName)),
                { Outcome: StartOutcome.TelegramTaken } => Results.Conflict(),
                _ => Results.NotFound()
            });

        telegram.MapPost("/web-link", async (TelegramPerson asker, TelegramLinks links, CancellationToken ct) =>
            await links.MintWebLinkAsync(asker.TelegramId, ct) switch
            {
                { Outcome: WebLinkOutcome.Minted, Link: { } link } => Results.Ok(new TelegramWebLink(link.Url, link.ExpiresAt)),
                { Outcome: WebLinkOutcome.Operator } => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            });

        telegram.MapPost("/bind", async (TelegramBind bind, TelegramLinks links, CancellationToken ct) =>
            await links.BindGroupAsync(bind.ChatId, bind.Code, ct) switch
            {
                { Outcome: BindOutcome.Bound, TenantId: { } tenantId } => Results.Ok(new TelegramGroupTenant(tenantId)),
                { Outcome: BindOutcome.BoundElsewhere } => Results.Conflict(),
                _ => Results.NotFound()
            });

        telegram.MapPost("/group", async (TelegramChat chat, TelegramLinks links, CancellationToken ct) =>
            await links.GroupTenantAsync(chat.ChatId, ct) is { } tenantId
                ? Results.Ok(new TelegramGroupTenant(tenantId))
                : Results.NotFound());

        // Where an approval is offered to its asker: their private chat with the bot is their own id.
        telegram.MapPost("/of-account", async (TelegramAccount account, TelegramLinks links, CancellationToken ct) =>
            await links.TelegramIdOfAsync(account.TenantId, account.UserId, ct) is { } telegramId
                ? Results.Ok(new TelegramOfAccount(telegramId))
                : Results.NotFound());
    }
}