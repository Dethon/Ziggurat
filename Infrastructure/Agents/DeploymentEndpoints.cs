using Domain.Security;

namespace Infrastructure.Agents;

// The MCP servers the deployment's own settings name — every built-in agent's endpoints. It is
// what decides whether an endpoint is handed a deployment credential at all. Each server has its
// own, found by the name it answers to, and a registered agent's endpoints are whatever the
// registration said: matched by name alone, any address on a server's host would be handed that
// server's credential, so a credential follows an address only when the deployment named that
// server itself.
//
// Compared as servers, not as strings (ServerAddress: scheme, host, port and path), with an address
// that carries credentials of its own (`http://mcp-vault:8080@elsewhere/mcp`) never matching.
internal sealed class DeploymentEndpoints(IEnumerable<string> addresses)
{
    private readonly HashSet<ServerAddress> _servers =
        [.. addresses.Select(ServerAddress.FromUrl).OfType<ServerAddress>().Where(s => !s.CarriesCredentials)];

    public bool Names(ServerAddress server) => !server.CarriesCredentials && _servers.Contains(server);

    public bool Names(string address) => ServerAddress.FromUrl(address) is { } server && Names(server);
}