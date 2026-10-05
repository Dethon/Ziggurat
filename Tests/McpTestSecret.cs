using Domain.Security;
using ModelContextProtocol.Client;

namespace Tests;

// The deployment secret every MCP server a test boots through the hosting library is gated
// behind, and the one way a test's own client presents it. One value for the whole suite, so a
// fixture, the agent it serves and a test's own client agree without being told about each other.
internal static class McpTestSecret
{
    public const string Value = "test-mcp-secret";

    public static McpGateSettings Gate => new() { SharedSecret = Value };

    public static Dictionary<string, string> Headers =>
        new() { ["Authorization"] = SharedSecret.Header(Value) };

    public static HttpClientTransport Transport(string endpoint) =>
        new(new HttpClientTransportOptions { Endpoint = new Uri(endpoint), AdditionalHeaders = Headers });
}