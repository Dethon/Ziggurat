using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;

namespace Tests;

// The deployment secret every MCP server a test boots through the hosting library is gated
// behind, and the one way a test's own client presents it. One value for the whole suite, so a
// fixture, the agent it serves and a test's own client agree without being told about each other.
//
// A server also asks every tool call for the agent's call token. A test's client stands in for the
// agent, so it signs the way the agent's clients do — the same transport, over the suite's fixed
// agent key (TestSigningKeys.Agent) — for whoever the call's own _meta names, and nobody when it names
// nobody. The suite's servers check those tokens against the key's published half (TestJwks), and
// answer to "localhost", the host every test server is dialled at.
internal static class McpTestSecret
{
    public const string Value = "test-mcp-secret";

    // The name every in-process test server is dialled at, and so the audience its gate checks for.
    public const string Audience = "localhost";

    // A server's own credential, and the agent's credential for every server a test dials: the
    // suite's servers listen on loopback, so one entry per loopback spelling covers them all.
    public static McpGateSettings Gate => new()
    {
        SharedSecret = Value,
        Secrets = new() { ["localhost"] = Value, ["127.0.0.1"] = Value },
        Audience = Audience,
        AgentKeysUrl = TestJwks.AgentUrl
    };

    public static McpCredentials Credentials => McpCredentials.From(Gate);

    // The agent's call tokens as every test and eval stack signs them: the suite's fixed agent key.
    public static CallTokens CallTokens { get; } =
        new(TestSigningKeys.Store(TestSigningKeys.Agent), TimeProvider.System);

    // A credential of its own per server, for a stack that wires containers as compose does.
    public static string For(string host) => $"{Value}-{host}";

    public static Dictionary<string, string> Headers =>
        new() { ["Authorization"] = SharedSecret.Header(Value) };

    // Signed as the agent signs: every tool call carries a call token for this endpoint's host — or,
    // for a server a test reaches through a published port, for the name the stack's agent dials it
    // at, which is the name the server answers to.
    public static IClientTransport Transport(string endpoint, string secret = Value, string? audience = null) =>
        new SigningClientTransport(
            UnsignedTransport(endpoint, secret), CallTokens, audience ?? TokenAudiences.CallTo(endpoint));

    // The credential alone, for a test that presents a token of its own making, or none.
    public static HttpClientTransport UnsignedTransport(string endpoint, string secret = Value) =>
        new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
        });
}