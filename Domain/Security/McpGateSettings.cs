namespace Domain.Security;

// The MCP gate on both ends. A server binds `SharedSecret` (MCP__SHAREDSECRET), its own credential,
// which its /mcp asks for. The agent binds `Secrets` (MCP__SECRETS__<host>), every server's
// credential keyed by the host it dials, and presents each endpoint the one for its host.
//
// One credential per server rather than one for the deployment: holding a server's credential —
// the sandbox's, which runs whatever the model writes, included — opens that server and no other.
// Empty refuses every call, because a forgotten variable must not be an open door onto a server
// that believes whatever conversation context a call claims.
public sealed record McpGateSettings
{
    public string SharedSecret { get; init; } = "";

    public Dictionary<string, string> Secrets { get; init; } = [];
}