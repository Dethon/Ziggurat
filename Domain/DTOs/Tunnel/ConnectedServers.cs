namespace Domain.DTOs.Tunnel;

// What the running agent says about a household's servers connected to it now, for the operator
// command that asks (list-household-servers). The registry lives in the serving process alone, so the
// command reads it through the agent's own endpoint on the deployment's network, under /internal,
// which the proxy never routes, behind the agent API's secret.
public static class ConnectedServers
{
    public const string Path = "/internal/tunnels";

    public static string For(string tenantId) => $"{Path}/{Uri.EscapeDataString(tenantId)}";
}

// One open tunnel: the server's name, the opening it is on and when it opened, and the tools it
// offered when asked through it just now — null when it did not answer the asking.
public sealed record ConnectedServer(
    string ServerName,
    long Generation,
    DateTimeOffset OpenedAt,
    IReadOnlyList<string>? Tools);