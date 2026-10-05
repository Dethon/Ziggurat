namespace Infrastructure.Agents;

// The MCP servers the deployment's own settings name — every built-in agent's endpoints. It is
// what decides where the deployment secret may go: that secret opens every deployment server, and
// a registered agent's endpoints are whatever the registration said, so the secret follows an
// address only when the deployment named that server itself.
//
// Compared as servers, not as strings: scheme, host, port and path, with an address that carries
// credentials of its own (`http://mcp-vault:8080@elsewhere/mcp`) never matching.
internal sealed class DeploymentEndpoints(IEnumerable<string> addresses)
{
    private readonly HashSet<(string Scheme, string Host, int Port, string Path)> _servers =
        [.. addresses.Select(Server).OfType<(string, string, int, string)>()];

    public bool Names(string address) => Server(address) is { } server && _servers.Contains(server);

    private static (string Scheme, string Host, int Port, string Path)? Server(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0
            ? (uri.Scheme, uri.Host.ToLowerInvariant(), uri.Port, uri.AbsolutePath)
            : null;
}