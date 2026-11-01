using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

// The transport the agent's sessions and channel connections dial an endpoint with, for a dial made
// outside them — the agent's key commands, which run in a process of their own.
public static class McpEndpointTransport
{
    public static IClientTransport For(McpServerEndpoint endpoint, CallTokens callTokens) =>
        McpClientManager.TransportFor(endpoint, callTokens);
}