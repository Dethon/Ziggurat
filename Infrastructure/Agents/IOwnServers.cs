namespace Infrastructure.Agents;

// Where a household's own server named in an agent's definition is reached, asked when a session is
// built: the deployment's answer is the server's open tunnel (RelayedOwnServers); a stack that hosts
// its servers in process — the eval's, an integration test's — answers with the server it hosts, by
// its URL, and no tunnel is involved. Null is a server that cannot be reached now: its household has
// no tunnel of that name open, or the turn names no household.
public interface IOwnServers
{
    McpServerEndpoint? Resolve(string? tenantId, string serverName);
}