namespace Domain.Security;

// The one deployment-wide secret every deployment MCP server's /mcp asks for, and the agent
// presents on every configured endpoint and channel connection. Bound under the same section name
// on both ends — `Mcp:SharedSecret`, `MCP__SHAREDSECRET` in the environment — so one line in the
// compose secrets file reaches every container and they cannot quietly disagree about its name.
//
// One secret rather than one per server, accepted with its cost: a leak from any container opens
// every server. Empty refuses every call, because a forgotten variable must not be an open door
// onto a server that believes whatever conversation context a call claims.
public sealed record McpGateSettings
{
    public string SharedSecret { get; init; } = "";
}