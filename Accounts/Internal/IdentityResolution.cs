using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Internal;

// What a channel server asks: whose account is the identity it saw. Asked server to server with the
// credential the caller already holds for its own /mcp, never by a browser — the proxy answers every
// /accounts/internal path 404 before /accounts reaches this service.
public sealed record IdentityQuery(string Channel, string Identity);

public sealed record ResolvedIdentity(string TenantId, string UserId, string DisplayName);

public static class IdentityResolution
{
    public const string Path = "/internal/resolve";

    // The channels whose identity is the account's own id: the chat client's turns carry the id the
    // session check named, and a voice profile is named after the user id it belongs to.
    private static readonly string[] _accountIdChannels = ["signalr", "voice"];

    // A Telegram id is linked by a row of its own, which nothing writes yet: every one is nobody's
    // until the bot can link them.
    private static readonly string[] _linkedIdentityChannels = ["telegram"];

    public static void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(Path, async (
            HttpContext http, IdentityQuery query, AccountsSettings settings,
            IDbContextFactory<AccountsDbContext> contexts, CancellationToken ct) =>
        {
            var presented = http.Request.Headers.Authorization.ToString();
            if (!settings.Callers.Values.Any(credential => CallerCredential.Matches(presented, credential)))
            {
                return Results.Unauthorized();
            }

            if (_linkedIdentityChannels.Contains(query.Channel))
            {
                return Results.NotFound();
            }

            if (!_accountIdChannels.Contains(query.Channel))
            {
                return Results.BadRequest();
            }

            await using var db = await contexts.CreateDbContextAsync(ct);
            return await db.Users.FirstOrDefaultAsync(u => u.Id == query.Identity, ct) is { } user
                ? Results.Ok(new ResolvedIdentity(user.TenantId, user.Id, user.DisplayName))
                : Results.NotFound();
        });
}