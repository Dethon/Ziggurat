using System.Collections.Concurrent;
using Infrastructure.Agents;

namespace Tests;

// What a stack that hosts its servers in process — the eval's, an integration test's — configures
// for a household's own servers: each name is the server the stack started, dialled at its URL with
// the suite's credential and checked for the name it answers to there (McpTestSecret.Audience),
// whatever the household. No tunnel is involved; the tunnel is tested at its own seam
// (TunnelRelayTests). It remembers what it was asked, so a test can say whose household a session
// was built for.
public sealed class InProcessOwnServers(IReadOnlyDictionary<string, string> hosted) : IOwnServers
{
    private readonly ConcurrentQueue<(string? TenantId, string ServerName)> _asked = new();

    public IReadOnlyList<(string? TenantId, string ServerName)> Asked => [.. _asked];

    public McpServerEndpoint? Resolve(string? tenantId, string serverName)
    {
        _asked.Enqueue((tenantId, serverName));
        return hosted.TryGetValue(serverName, out var endpoint)
            ? McpServerEndpoint.Configured(endpoint, McpTestSecret.Value)
            : null;
    }
}