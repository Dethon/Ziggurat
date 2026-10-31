using Accounts.Links;
using Accounts.Signing;
using Accounts.Store;
using Domain.DTOs.Channel;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Internal;

// What a channel server asks: whose account is the identity it saw. Asked server to server with the
// credential the caller already holds for its own /mcp (InternalApi), never by a browser.
public sealed record IdentityQuery(string Channel, string Identity);

// The assertion vouches for the rest: signed for the channel asked about, it is what the channel
// server carries on the turn it starts and what the agent believes.
public sealed record ResolvedIdentity(string TenantId, string UserId, string DisplayName, string Assertion);

public sealed record GuestQuery(string Channel);

public sealed record HouseholdGuest(string TenantId, string Assertion);

public static class IdentityResolution
{
    public const string Path = "/resolve";
    public const string GuestPath = "/guest";

    // The channels whose identity is the account's own id: a voice profile is named after the user id
    // it belongs to. The chat client's person is not resolved here by id, which would let whoever
    // holds the chat hub's credential mint anybody: it is a live session's (SessionRefresh).
    private static readonly string[] _accountIdChannels = [ChannelIds.Voice];

    // The channels whose identity a row of its own links to a person: a numeric Telegram id, written
    // when the bot redeemed the person's invite or link code.
    private static readonly string[] _linkedIdentityChannels = [TelegramLinks.Channel];

    // The channels with a household guest: a satellite hears whoever is in the house, and somebody
    // no voice profile matched is still somebody the house works for, as no person. Every other
    // channel knows who sent what it carries.
    private static readonly string[] _guestChannels = [ChannelIds.Voice];

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost(Path, async (
            HttpContext http, IdentityQuery query, IDbContextFactory<AccountsDbContext> contexts, Assertions assertions,
            CancellationToken ct) =>
        {
            if (!_linkedIdentityChannels.Contains(query.Channel) && !_accountIdChannels.Contains(query.Channel))
            {
                return Results.BadRequest();
            }

            if (!InternalCallers.MayAskAbout(http, query.Channel))
            {
                return InternalCallers.NotYours;
            }

            await using var db = await contexts.CreateDbContextAsync(ct);
            var people = _linkedIdentityChannels.Contains(query.Channel)
                ? db.LinkedIdentities
                    .Where(i => i.Channel == query.Channel && i.Identity == query.Identity)
                    .Join(db.Users, i => i.UserId, u => u.Id, (_, u) => u)
                : db.Users.Where(u => u.Id == query.Identity);

            return await people.FirstOrDefaultAsync(ct) is { } user
                ? Results.Ok(new ResolvedIdentity(user.TenantId, user.Id, user.DisplayName, assertions.Of(user, query.Channel)))
                : Results.NotFound();
        });

        // The home household's, while it is the only one; a satellite's own household is the
        // Tenants phase's.
        routes.MapPost(GuestPath, async (
            HttpContext http, GuestQuery query, IDbContextFactory<AccountsDbContext> contexts, Assertions assertions,
            CancellationToken ct) =>
        {
            if (!_guestChannels.Contains(query.Channel))
            {
                return Results.BadRequest();
            }

            if (!InternalCallers.MayAskAbout(http, query.Channel))
            {
                return InternalCallers.NotYours;
            }

            await using var db = await contexts.CreateDbContextAsync(ct);
            var home = await db.Tenants.SingleAsync(t => t.IsHome, ct);
            return Results.Ok(new HouseholdGuest(home.Id, assertions.OfGuest(home.Id, query.Channel)));
        });
    }
}