using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Domain.DTOs.Tunnel;

// What a household's server and the relay say to each other inside a tunnel, apart from the bytes
// of the streams it carries (Mcp.Hosting's TunnelMux). Spelled once here, so both ends read the same
// records: the server opens the tunnel claiming a name, the relay answers who the tunnel is for and
// what the agent's keys are, and sends the keys again whenever they change.
public static class TunnelProtocol
{
    // The proxy's one route for tunnels, on the deployment's main host, answered by the relay.
    public const string Path = "/tunnel";

    // The name a tunnel claims, on its upgrade; the credential goes in Authorization as a bearer, so
    // neither lands in a URL a proxy logs.
    public const string ServerHeader = "X-Ziggurat-Tunnel-Server";

    // What the relay opens a stream for: the server's /mcp listener. A stream is a TCP connection to
    // one of the server's own listeners, so its gates see what they always saw.
    public const string McpTag = "mcp";

    // The web search server's pages listener, which the proxy's /browse/* reaches through the relay:
    // a login handoff's live view and a person's logins page.
    public const string PagesTag = "pages";

    // A household's Lemonade forwarder's one listener, which passes the agent's requests to its box.
    // Not "lemonade": that is voice's back-call to the deployment's own speech box, the other way.
    public const string ChatTag = "chat";

    // How a tunnel ends, as the WebSocket close's description. A refusal is a credential's reason
    // (TunnelCredentialRefusal); the rest are the relay's own.
    public const string Unverified = "Unverified";
    public const string Replaced = "Replaced";
    public const string Restarting = "Restarting";
    public const string Revoked = nameof(TunnelCredentialRefusal.Revoked);
    public const string Suspended = nameof(TunnelCredentialRefusal.Suspended);

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);
}

// A control message, sent as a WebSocket text message.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TunnelAdmitted), "admitted")]
[JsonDerivedType(typeof(TunnelKeys), "keys")]
public abstract record TunnelControl;

// The relay's answer to a credential it verified: the household and the server the tunnel is for,
// the name the agent's call tokens for it are signed for (its synthetic ServerAddress.Name), and the
// agent's published keys (a JWKS) to check them with. Nothing else tells a tunnelled server who it is.
[PublicAPI]
public sealed record TunnelAdmitted(string TenantId, string ServerName, string Audience, string Keys) : TunnelControl;

// The agent's published keys again, after a rotation or a retirement, before any call signed with a
// key the server has not been handed.
[PublicAPI]
public sealed record TunnelKeys(string Keys) : TunnelControl;