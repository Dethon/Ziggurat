using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
//
// The agent signs only an identity its conversation group verified (ConversationContext.Identity).
// A test's context that names a person's account and no identity stands for a turn the agent did
// verify, so the stand-in vouches for that account first (Vouching); a context that names an identity
// is signed as it, and one naming neither is nobody's, as the agent would sign them.
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
        new Vouching(new SigningClientTransport(
            UnsignedTransport(endpoint, secret), CallTokens, audience ?? TokenAudiences.CallTo(endpoint)));

    // The credential alone, for a test that presents a token of its own making, or none.
    public static HttpClientTransport UnsignedTransport(string endpoint, string secret = Value) =>
        new(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
        });

    // The agent's conversation group, as far as a test's call needs one: a context naming a person's
    // account with no verified identity is given that person's, as the group would have given it on
    // the turn's assertion, before the signing transport reads it.
    private sealed class Vouching(IClientTransport inner) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new Connected(await inner.ConnectAsync(cancellationToken));

        private sealed class Connected(ITransport inner) : ITransport
        {
            public string? SessionId => inner.SessionId;

            public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

            public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            {
                if (message is JsonRpcRequest { Method: RequestMethods.ToolsCall, Params: JsonObject parameters }
                    && parameters["_meta"] is JsonObject meta
                    && TurnOf(meta) is { Identity: null, Principal: { Account: { } account } principal } context)
                {
                    meta[ChannelProtocol.ConversationContextMetaKey] = JsonSerializer.SerializeToNode(
                        context with { Identity = IdentityClaims.Person(account.UserId, account.TenantId, principal.ChannelId, []) },
                        ChannelProtocol.SerializerOptions);
                }

                return inner.SendMessageAsync(message, cancellationToken);
            }

            // A context that does not read as one is a test of what the server says about it, and is
            // sent as written.
            private static ConversationContext? TurnOf(JsonObject meta)
            {
                try
                {
                    return meta[ChannelProtocol.ConversationContextMetaKey]?.Deserialize<ConversationContext>(
                        ChannelProtocol.SerializerOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}