using Accounts.Links;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Internal;

// What a channel server asks: whose account is the identity it saw. Asked server to server with the
// credential the caller already holds for its own /mcp (InternalApi), never by a browser.
public sealed record IdentityQuery(string Channel, string Identity);

public sealed record ResolvedIdentity(string TenantId, string UserId, string DisplayName);

public static class IdentityResolution
{
    public const string Path = "/resolve";

    // The channels whose identity is the account's own id: the chat client's turns carry the id the
    // session check named, and a voice profile is named after the user id it belongs to.
    private static readonly string[] _accountIdChannels = ["signalr", "voice"];

    // The channels whose identity a row of its own links to a person: a numeric Telegram id, written
    // when the bot redeemed the person's invite or link code.
    private static readonly string[] _linkedIdentityChannels = [TelegramLinks.Channel];

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (IdentityQuery query, IDbContextFactory<AccountsDbContext> contexts, CancellationToken ct) =>
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            var people = _linkedIdentityChannels.Contains(query.Channel)
                ? db.LinkedIdentities
                    .Where(i => i.Channel == query.Channel && i.Identity == query.Identity)
                    .Join(db.Users, i => i.UserId, u => u.Id, (_, u) => u)
                : _accountIdChannels.Contains(query.Channel)
                    ? db.Users.Where(u => u.Id == query.Identity)
                    : null;
            if (people is null)
            {
                return Results.BadRequest();
            }

            return await people.FirstOrDefaultAsync(ct) is { } user
                ? Results.Ok(new ResolvedIdentity(user.TenantId, user.Id, user.DisplayName))
                : Results.NotFound();
        });
}