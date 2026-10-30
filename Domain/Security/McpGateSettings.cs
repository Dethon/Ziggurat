using Domain.Security.Signing;

namespace Domain.Security;

// The MCP gate on both ends. A server binds `SharedSecret` (MCP__SHAREDSECRET), its own credential,
// which its /mcp asks for. The agent binds `Secrets` (MCP__SECRETS__<host>), every server's
// credential keyed by the host it dials, and presents each endpoint the one for its host.
//
// One credential per server rather than one for the deployment: holding a server's credential —
// the sandbox's, which runs whatever the model writes, included — opens that server and no other.
// Empty refuses every call, because a forgotten variable must not be an open door onto a server
// that believes whatever conversation context a call claims.
//
// The credential lets a caller talk to the server; it no longer says who a call acts for. A tool
// call must also carry the agent's call token for this server, and a server binds what it checks
// that with: its own name as the agent dials it (MCP__AUDIENCE, the audience the token must name)
// and where the agent publishes its keys (MCP__AGENTKEYSURL, on the deployment's own network).
// Either left empty refuses every tool call, for the same reason an empty secret does.
public sealed record McpGateSettings
{
    public string SharedSecret { get; init; } = "";

    public Dictionary<string, string> Secrets { get; init; } = [];

    public string Audience { get; init; } = "";

    public string AgentKeysUrl { get; init; } = "";

    // Read from Domain's token-verification.json, which every host that verifies a token ships.
    public TokenVerificationSettings TokenVerification { get; init; } = new();
}