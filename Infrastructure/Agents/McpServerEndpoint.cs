using Domain.Security;

namespace Infrastructure.Agents;

// Where an endpoint came from. It is the only thing that says whether the endpoint being
// unreachable is a fault: a container named in the compose file being down is a bug, and a
// laptop whose tunnel is open being asleep is Tuesday. The dial policy reads this and nothing
// else. See docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md.
public enum McpEndpointOrigin
{
    // Named in the deployment's own settings, and part of it.
    Configured,

    // An outpost: there while the machine's tunnel is open, and gone when it closes.
    Dynamic
}

// An MCP endpoint once it has reached a running agent. The typing stops at the spec boundary:
// agent and subagent definitions and the custom-agent registration keep plain strings, because
// they bind straight from appsettings.json and there is no distinction configuration can make —
// everything in a settings file is configured by definition. AgentSpecProjection composes these;
// a session build resolves a household's servers, named rather than addressed, to their tunnels and
// merges live outposts in as dynamic ones.
public sealed record McpServerEndpoint(string Address, McpEndpointOrigin Origin)
{
    // What this endpoint has to present to be allowed to talk. Every dialled server asks for its own
    // credential, found by the name it answers to (ServerAddress), because a tool believes whatever
    // conversation context a call claims and the network is not a boundary worth trusting that to. A
    // server reached through its tunnel — a household's, an outpost — presents nothing here: the
    // tunnel's client presents the tunnel's credential. Null presents nothing, which a gated server refuses — and a secret set to nothing is
    // null here, so no dial sends a bearer header with nothing behind it.
    public string? Secret
    {
        get;
        init => field = string.IsNullOrEmpty(value) ? null : value;
    }

    // The server this endpoint reaches, which the call token's audience, the tool qualifier and the
    // prompt cache key on: a URL's own, or the synthetic address a household's server was admitted
    // with. Null for a household's server not yet resolved, and for an address that is no URL.
    public ServerAddress? Server
    {
        get => field ?? (NamesOwnServer ? null : ServerAddress.FromUrl(Address));
        private init;
    }

    // A household's own server, named rather than addressed (Address is its name): which one, and how
    // it is reached, is asked when a session is built, of the turn's household (IOwnServers).
    public bool NamesOwnServer { get; private init; }

    // What carries this endpoint's exchanges when they do not go onto the network: a household
    // server's tunnel, whose client presents the tunnel's own credential.
    public HttpClient? Through { get; private init; }

    // Which opening of a household server's tunnel this endpoint is bound to (ADR 0012: a server's
    // tool set is fixed for a connection generation). A turn compares it with the session's to know
    // whether the server it reached is still the one there; null for everything not on a tunnel.
    public long? Generation { get; private init; }

    // Cancelled once that tunnel has closed, which is the one thing that says a call failing on it
    // failed because the server went offline. Never cancelled for an endpoint not on a tunnel.
    public CancellationToken Offline { get; private init; }

    public static McpServerEndpoint Configured(string address, string? secret = null) =>
        new(address, McpEndpointOrigin.Configured) { Secret = secret };

    public static McpServerEndpoint Dynamic(string address, string? secret = null) =>
        new(address, McpEndpointOrigin.Dynamic) { Secret = secret };

    // A definition's endpoint that is a server's name (TunnelCredentialProtocol.IsServerName) rather
    // than a URL. Configured, because the deployment's own settings named it.
    public static McpServerEndpoint OwnServer(string serverName) =>
        new(serverName, McpEndpointOrigin.Configured) { NamesOwnServer = true };

    // A household's server through its open tunnel. Its address is a URL on the synthetic name, which
    // the client never resolves: every connection it makes is a stream of the tunnel.
    public static McpServerEndpoint Relayed(
        ServerAddress server, HttpClient through, long? generation = null, CancellationToken offline = default) =>
        new($"http://{server.Name}/mcp", McpEndpointOrigin.Configured)
        {
            Server = server, Through = through, Generation = generation, Offline = offline
        };

    // An outpost through its open tunnel: reached the same way, but dynamic — the machine decides
    // whether it is there, and a tunnel that closes between the session's compose and its dial costs
    // the session that mount and nothing else (ADR 0027).
    public static McpServerEndpoint RelayedOutpost(
        ServerAddress server, HttpClient through, long? generation = null, CancellationToken offline = default) =>
        Relayed(server, through, generation, offline) with { Origin = McpEndpointOrigin.Dynamic };
}