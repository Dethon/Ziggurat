namespace Domain.DTOs.Tunnel;

// What the running agent says about a household's servers connected to it now, for the operator
// command that asks (list-household-servers). The registry lives in the serving process alone, so the
// command reads it through the agent's own endpoint on the deployment's network, under /internal,
// which the proxy never routes, behind the agent API's secret.
public static class ConnectedServers
{
    public const string Path = "/internal/tunnels";

    public static string For(string tenantId) => $"{Path}/{Uri.EscapeDataString(tenantId)}";

    // The home household's server of a name, for the agent's own key commands, which run in a process
    // of their own and hold no tunnel: whether its tunnel is open and whose household it is (HomeServer,
    // 404 when it is not open), and its /mcp reached down that tunnel.
    public const string HomeRoute = "/home/{serverName}";
    public const string HomeMcpRoute = HomeRoute + "/mcp";

    public static string Home(string serverName) => $"{Path}/home/{Uri.EscapeDataString(serverName)}";

    public static string HomeMcp(string serverName) => $"{Home(serverName)}/mcp";
}

// The home household's open server of a name: the household, which its call tokens' audience names.
public sealed record HomeServer(string TenantId, string ServerName);

// One open tunnel: the server's name, the opening it is on and when it opened, the tools it offered
// when asked through it just now — null when it did not answer the asking — and the id of the
// credential it was opened with, so an owner replacing a credential sees which one is in use.
public sealed record ConnectedServer(
    string ServerName,
    long Generation,
    DateTimeOffset OpenedAt,
    IReadOnlyList<string>? Tools,
    string? CredentialId = null);