using Accounts.Tunnels;
using Domain.DTOs.Admin;
using Domain.DTOs.Tunnel;
using Domain.Outposts;
using Microsoft.AspNetCore.Mvc;

namespace Accounts.Admin;

// The household's own servers as its owners manage them, over 4a's tunnel credentials and the agent's
// relay: a credential minted for a server by name and shown once, each server's live credentials
// beside what the relay says of it now, and a credential revoked, which the relay hears and closes
// the tunnel it opened. Every route acts on the caller's household alone — revoke-tunnel-credential
// names an id and nothing else, so here an id of another household's is nobody's.
//
// An outpost is a household's server too (`outpost-<NAME>`): owners mint a machine's credential here
// as they mint any other, and the list tells it apart by its name, as the relay does.
public static class HouseholdServersAdmin
{
    // The revocation stood, but the relay did not hear it: it refuses the credential on the tunnel's
    // next open, and until then the server stays connected.
    public const string NotToldWarning =
        "Revoked, but the relay could not be told just now: the server stays connected until its tunnel next reopens, and is refused then.";

    private static readonly AdminRefusal _nobody = new("The household has no such tunnel credential.", []);

    public static void Map(RouteGroupBuilder household)
    {
        var servers = AdminApi.Under(AdminProtocol.Household, AdminProtocol.Servers);

        household.MapGet(servers, async (
            AdminCaller caller, TunnelCredentialStore store, ConnectedServersClient relay, CancellationToken ct) =>
            Results.Ok(await AnswerAsync(caller.HouseholdId, store, relay, closing: null, ct)));

        household.MapPost(servers, async (
            AdminCaller caller, [FromBody] ServerAddition? addition, HttpContext http, TunnelCredentialStore store, AdminLog log,
            CancellationToken ct) =>
        {
            var name = addition?.Name?.Trim() ?? "";
            if (!TunnelCredentialProtocol.IsServerName(name))
            {
                return Results.BadRequest(new AdminRefusal("No credential was minted.",
                [
                    "a server's name is lowercase letters, digits and inner hyphens, "
                    + $"at most {TunnelCredentialProtocol.ServerNameMaxLength} characters: it is part of its tunnel's address"
                ]));
            }

            if (await store.MintAsync(caller.HouseholdId, name, ct) is not { } minted)
            {
                return Results.NotFound(_nobody);
            }

            await log.RecordAsync(http, caller, $"Minted a tunnel credential for server {name}", ct);
            // Not told, the agent reaches the new server within its cache's lifetime rather than next turn.
            return Results.Ok(new AdminSaved<MintedServerCredential>(
                new MintedServerCredential(minted.ServerName, minted.Id, minted.Credential),
                minted.NotTold is null ? null : AdminSections.NotToldWarning));
        });

        household.MapPost(AdminApi.Under(AdminProtocol.Household, AdminProtocol.RevokeServerCredentialRoute), async (
            AdminCaller caller, string credentialId, HttpContext http, TunnelCredentialStore store,
            ConnectedServersClient relay, AdminLog log, CancellationToken ct) =>
        {
            if (await store.RevokeAsync(credentialId, caller.HouseholdId, ct) is not { } revoked)
            {
                return Results.NotFound(_nobody);
            }

            await log.RecordAsync(http, caller, $"Revoked a tunnel credential of server {revoked.ServerName}", ct);
            var told = revoked.NotTold is null;
            return Results.Ok(new AdminSaved<AdminServers>(
                await AnswerAsync(caller.HouseholdId, store, relay, closing: told ? revoked.Id : null, ct),
                told ? null : NotToldWarning));
        });
    }

    // Every server name that holds a live credential or has a tunnel open — one whose credentials were
    // all revoked while the relay could not be told is still connected, and shown so. Closing is a
    // credential the relay was just told of: it takes the tunnel out of its registry first thing, so
    // the answer does not wait for the close to land before saying so.
    private static async Task<AdminServers> AnswerAsync(
        string householdId, TunnelCredentialStore store, ConnectedServersClient relay, string? closing, CancellationToken ct)
    {
        var credentials = (await store.LiveOfAsync(householdId, ct)).ToLookup(c => c.ServerName);
        var connected = (await relay.ListAsync(householdId, ct))?
            .Where(s => closing is null || s.CredentialId != closing)
            .ToDictionary(s => s.ServerName);
        return new AdminServers(
        [
            .. credentials.Select(c => c.Key)
                .Union(connected?.Keys ?? Enumerable.Empty<string>())
                .Order(StringComparer.Ordinal)
                .Select(name =>
                {
                    var tunnel = connected?.GetValueOrDefault(name);
                    return new AdminServer(
                        name,
                        OutpostServerName.NameOf(name) is not null,
                        connected is null ? ServerConnection.Unknown
                        : tunnel is null ? ServerConnection.Offline
                        : ServerConnection.Connected,
                        tunnel?.OpenedAt,
                        tunnel?.Tools,
                        [.. credentials[name].Select(c => new AdminServerCredential(c.Id, c.CreatedAt, c.Id == tunnel?.CredentialId))]);
                })
        ]);
    }
}