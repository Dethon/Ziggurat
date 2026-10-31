using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Infrastructure.Agents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Tunnels;

// A shared deployment server's request to a household's own server, forwarded down that server's
// tunnel: the relay's endpoint is the only way a shared server reaches one, since a household's
// server has no address on the deployment's network.
//
// The caller is checked against what the agent already holds: it names itself (CallerHeader) and
// presents the credential its own /mcp asks for, which is the agent's credential for dialling it
// (McpCredentials, MCP__SECRETS__<name>) — so no secret exists for this endpoint, and holding one
// server's credential opens only what that server may reach. What it may reach is the list below,
// by household server and by path under it; anything else is refused before a byte is forwarded.
//
// The request goes on without the caller's credential, its name or the deployment's announce
// secret, presenting the household's server the credential of its own tunnel instead (the mcp tag's
// pool, RelayHandler), so nothing of the deployment's reaches a household. Which household:
// the home household's, in this phase (HomeHousehold).
public sealed class RelayedCalls(
    TunnelRegistry registry,
    TunnelForwarder forwarder,
    McpCredentials credentials,
    IMetricsPublisher metrics,
    ILogger<RelayedCalls> logger)
{
    public const string RefusedErrorType = "RelayedCallRefused";

    // The timers server rings, dismisses and validates satellites on the household's voice server, its
    // announce API beside /mcp on the same listener.
    private static readonly IReadOnlyList<Reach> _reaches =
    [
        new("mcp-timers", HouseholdServerNames.Voice, "/api/voice")
    ];

    // What the caller sent for the relay alone, and the deployment's own secret for the voice API,
    // which a household's server is never told.
    private static readonly string[] _withheld = [RelayedCallProtocol.CallerHeader, "X-Announce-Token"];

    public async Task ForwardAsync(HttpContext context, string server)
    {
        var caller = context.Request.Headers[RelayedCallProtocol.CallerHeader].ToString();
        // The path as Kestrel normalised it, never the route's raw value, so a dot segment cannot walk
        // out of what the caller may reach.
        context.Request.Path.StartsWithSegments(RelayedCallProtocol.For(server), out var rest);
        if (!Presents(context.Request, caller))
        {
            await RefuseAsync(context, StatusCodes.Status401Unauthorized, caller, server,
                "the caller did not present its own credential");
            return;
        }

        if (!_reaches.Any(r => r.Caller == caller && r.Server == server && rest.StartsWithSegments(r.Under)))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, caller, server,
                $"{caller} may not reach {server}{rest}");
            return;
        }

        switch (HomeHousehold.Find(registry, server))
        {
            case HouseholdServer.Open { Tunnel: var tunnel }:
                if (!await forwarder.ForwardAsync(context, tunnel, TunnelProtocol.McpTag, rest, _withheld)
                    && !context.Response.HasStarted)
                {
                    await UnreachableAsync(context, $"{server} went offline: its tunnel closed");
                }

                break;
            case HouseholdServer.Unreachable { Reason: var reason }:
                await UnreachableAsync(context, reason);
                break;
        }
    }

    private bool Presents(HttpRequest request, string caller) =>
        caller.Length > 0
        && credentials.For(ServerAddress.Named(caller)) is { } credential
        && SharedSecret.Matches(request.Headers.Authorization.ToString(), credential);

    // A gateway's answer, so the caller can tell its target being offline from the target refusing.
    private static async Task UnreachableAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync(reason, context.RequestAborted);
    }

    private async Task RefuseAsync(HttpContext context, int status, string caller, string server, string reason)
    {
        logger.LogWarning("Refused a relayed call to {Server} claiming to be {Caller}: {Reason}", server, caller, reason);
        metrics.Publish(new ErrorEvent
        {
            Service = "agent",
            ErrorType = RefusedErrorType,
            Message = $"A relayed call to household server '{server}' claiming '{caller}' was refused: {reason}"
        });
        context.Response.StatusCode = status;
        await context.Response.WriteAsync(reason, context.RequestAborted);
    }

    private sealed record Reach(string Caller, string Server, PathString Under);
}