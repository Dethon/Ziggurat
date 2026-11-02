namespace Domain.DTOs.Tunnel;

// What the running agent says about a household's servers connected to it now, for the operator
// command that asks (list-household-servers). The registry lives in the serving process alone, so the
// command reads it through the agent's own endpoint on the deployment's network, under /internal,
// which the proxy never routes, behind the agent API's secret.
public static class ConnectedServers
{
    public const string Path = "/internal/tunnels";

    public static string For(string tenantId) => $"{Path}/{Uri.EscapeDataString(tenantId)}";

    // Every household's server of a name, for the agent's own key commands, which run in a process of
    // their own and hold no tunnel: each household that holds one, and whether its tunnel is open now
    // (HouseholdServer), and one household's server's /mcp reached down its tunnel.
    public const string HoldersRoute = "/servers/{serverName}";
    public const string McpRoute = "/{tenantId}/{serverName}/mcp";

    public static string Holders(string serverName) => $"{Path}/servers/{Uri.EscapeDataString(serverName)}";

    public static string Mcp(string tenantId, string serverName) =>
        $"{For(tenantId)}/{Uri.EscapeDataString(serverName)}/mcp";
}

// A household's server of a name, which the key commands must ask: the household (its id, which its
// call tokens' audience names, and its name, for the operator to read), whether it is the home, whether
// it is suspended, and whether its tunnel is open now.
public sealed record HouseholdServer(
    string TenantId, string Household, string ServerName, bool Home, bool Suspended, bool Connected);

// One open tunnel: the server's name, the opening it is on and when it opened, the tools it offered
// when asked through it just now — null when it did not answer the asking — and the id of the
// credential it was opened with, so an owner replacing a credential sees which one is in use.
public sealed record ConnectedServer(
    string ServerName,
    long Generation,
    DateTimeOffset OpenedAt,
    IReadOnlyList<string>? Tools,
    string? CredentialId = null);