using Domain.Security;
using Infrastructure.Agents;
using ModelContextProtocol.Client;

namespace Tests;

// The deployment secret every MCP server a test boots through the hosting library is gated
// behind, and the one way a test's own client presents it. One value for the whole suite, so a
// fixture, the agent it serves and a test's own client agree without being told about each other.
internal static class McpTestSecret
{
    public const string Value = "test-mcp-secret";

    // A server's own credential, and the agent's credential for every server a test dials: the
    // suite's servers listen on loopback, so one entry per loopback spelling covers them all.
    public static McpGateSettings Gate => new()
    {
        SharedSecret = Value,
        Secrets = new() { ["localhost"] = Value, ["127.0.0.1"] = Value }
    };

    public static McpCredentials Credentials => McpCredentials.From(Gate);

    // A credential of its own per server, for a stack that wires containers as compose does.
    public static string For(string host) => $"{Value}-{host}";

    public static Dictionary<string, string> Headers =>
        new() { ["Authorization"] = SharedSecret.Header(Value) };

    public static HttpClientTransport Transport(string endpoint, string secret = Value) =>
        new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
        });
}