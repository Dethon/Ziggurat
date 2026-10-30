using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Tools.Web;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

// Every tool call one of the agent's MCP clients sends carries the agent's call token for the one
// server the client is dialled to, beside the conversation context the call site already wrote.
// Signing where the message leaves rather than where each call is made: a tool the model calls, a
// filesystem operation, a send_reply and an approval all go out through here, so no call site — a
// new one included — can send a call unsigned, and the audience is the client's own endpoint by
// construction.
//
// Who the token names is read off the context in the same _meta (CallIdentity), so a worker that
// carries its parent's context signs for its parent's person, and a call whose _meta names no turn —
// the channel plumbing, a fire nobody vouched for — is signed as nobody. Anything a caller put under
// the token's key is overwritten: only this signs.
//
// A call that creates or rewrites something a later turn will act on also carries a delegation for
// it, signed here as the same person: a filesystem write says what it delegates under the agent's own
// request key (McpFileSystemBackend, for the schedules and watches it writes), and web_login is a
// login handoff by its name, delegated off the asking turn's own context. A call acting for no person
// delegates nothing, and nothing a caller wrote under either key leaves unsigned.
public sealed class SigningClientTransport(IClientTransport inner, CallTokens tokens, string audience) : IClientTransport
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new Signing(await inner.ConnectAsync(cancellationToken), tokens, audience);

    private sealed class Signing(ITransport inner, CallTokens tokens, string audience) : ITransport
    {
        public string? SessionId => inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            inner.SendMessageAsync(Signed(message), cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private JsonRpcMessage Signed(JsonRpcMessage message)
        {
            if (message is not JsonRpcRequest { Method: RequestMethods.ToolsCall } request)
            {
                return message;
            }

            var parameters = request.Params as JsonObject ?? new JsonObject();
            if (parameters["_meta"] is not JsonObject meta)
            {
                meta = new JsonObject();
                parameters["_meta"] = meta;
            }

            var turn = TurnOf(meta);
            var identity = CallIdentity.Of(turn);
            meta[ChannelProtocol.CallTokenMetaKey] = tokens.For(identity, audience);

            var delegated = Requested(meta) ?? (IsLogin(parameters) && turn is not null ? LoginDelegation.Of(turn) : null);
            meta.Remove(ChannelProtocol.DelegateMetaKey);
            meta.Remove(ChannelProtocol.DelegationMetaKey);
            if (delegated is not null && identity.Account is not null)
            {
                meta[ChannelProtocol.DelegationMetaKey] = tokens.Delegate(identity, delegated);
            }

            request.Params = parameters;
            return request;
        }

        private static DelegatedObject? Requested(JsonObject meta) =>
            Read<DelegatedObject>(meta, ChannelProtocol.DelegateMetaKey);

        private static bool IsLogin(JsonObject parameters) =>
            parameters["name"] is JsonValue name && name.TryGetValue<string>(out var tool) && tool == WebLoginTool.Name;

        // A context that does not read as one names nobody; the server says what is wrong with it.
        private static ConversationContext? TurnOf(JsonObject meta) =>
            Read<ConversationContext>(meta, ChannelProtocol.ConversationContextMetaKey);

        // What the `_meta` carries under a key, or nothing when it does not read as one.
        private static T? Read<T>(JsonObject meta, string key) where T : class
        {
            try
            {
                return meta[key]?.Deserialize<T>(ChannelProtocol.SerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}