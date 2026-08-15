namespace Infrastructure.Agents;

// Where an endpoint came from. It is the only thing that says whether the endpoint being
// unreachable is a fault: a container named in the compose file being down is a bug, and a
// laptop that registered itself being asleep is Tuesday. The dial policy reads this and nothing
// else. See docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md.
public enum McpEndpointOrigin
{
    // Named in the deployment's own settings, and part of it.
    Configured,

    // Registered at runtime by the machine it serves, and gone when that machine is.
    Dynamic
}

// An MCP endpoint once it has reached a running agent. The typing stops at the spec boundary:
// agent and subagent definitions and the custom-agent registration keep plain strings, because
// they bind straight from appsettings.json and there is no distinction configuration can make —
// everything in a settings file is configured by definition. AgentSpecProjection composes these,
// which is also where live outposts are merged in as dynamic ones.
public sealed record McpServerEndpoint(string Address, McpEndpointOrigin Origin)
{
    // What this endpoint has to present to be allowed to talk, or null where the endpoint is on
    // the deployment's own network and the network is the boundary. An outpost is a port on
    // somebody's own computer, so it asks: without this, anyone who could reach that port would get
    // the machine's whole filesystem, fs_exec included, through nothing but a URL.
    public string? Secret { get; init; }

    public static McpServerEndpoint Configured(string address) =>
        new(address, McpEndpointOrigin.Configured);

    public static McpServerEndpoint Dynamic(string address, string? secret = null) =>
        new(address, McpEndpointOrigin.Dynamic) { Secret = secret };
}