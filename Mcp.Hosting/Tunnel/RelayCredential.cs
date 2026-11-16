using Domain.Security;
using Microsoft.AspNetCore.Http;

namespace Mcp.Hosting.Tunnel;

// Whether a request is the relay's: it presents the credential this server's tunnel was opened with,
// as it does on every exchange it forwards to the mcp listener. What a tunnelled server's own
// endpoints beside /mcp ask for when a shared deployment server reaches them through the relay
// (the voice server's announce API, rung by the timers server), so the deployment's secret for that
// API never has to be handed to a household. Registered only while tunnelled.
public sealed class RelayCredential(string credential)
{
    public bool PresentedBy(HttpRequest request) =>
        SharedSecret.Matches(request.Headers.Authorization.ToString(), credential);
}