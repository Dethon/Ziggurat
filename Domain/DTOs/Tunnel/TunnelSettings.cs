namespace Domain.DTOs.Tunnel;

// A server's tunnel, on both of its ends. A server built on the hosting library binds it under its
// gate settings (MCP__TUNNEL__*): given a hub and a credential it opens its tunnel, and without them
// it is dialled at a URL as before. The relay reads the timings alone.
//
// The timings are one shared policy file (Domain/tunnel.json) rather than a block in each host's
// appsettings.json: every server built on the hosting library and the relay keep the same tunnel
// alive on the same schedule, and an operator tuning how fast a household's servers come back after
// a deploy edits one file.
public sealed record TunnelSettings
{
    public const string FileName = "tunnel.json";

    // Where the deployment's relay listens: `wss://<main host>/tunnel` through the proxy. Per
    // deployment, so the tenant kit sets it.
    public string Hub { get; init; } = "";

    // The credential the operator minted for this server, which the relay verifies on every open.
    // It is also what the server's /mcp asks for while tunnelled: the relay presents the credential it
    // was opened with, so no deployment holds a household server's secret.
    public string Credential { get; init; } = "";

    // The name the credential was minted for, which the tunnel claims.
    public string ServerName { get; init; } = "";

    // Both ends ping on this interval and give the tunnel up when a ping is not answered within the
    // timeout, so a dead link is noticed by the relay (the server goes offline) and by the server
    // (it reconnects) within the sum of the two.
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // The first wait before reconnecting, doubled on each failed attempt up to the most, and back to
    // the first once a tunnel is admitted. A refused tunnel waits the most: its credential will not
    // start working by itself.
    public TimeSpan ReconnectAfter { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectAtMost { get; init; } = TimeSpan.FromMinutes(1);

    // The back-calls this server makes (BackCallProtocol's tags), each at a loopback port of its own
    // (MCP__TUNNEL__BACKCALLS__<tag>): whatever the server dials there goes over its tunnel to that
    // deployment service, so a client pointed at `http://127.0.0.1:<port>` — the accounts resolver,
    // the topic minter, the sandbox's FUSE daemon — reaches it with nothing changed but its address.
    // Per server, so the tenant kit sets them; a server names only the ones it makes, because a port
    // here is reachable by anything running beside the server, a sandbox command included.
    public Dictionary<string, int> BackCalls { get; init; } = [];

    public bool IsConfigured => Hub.Length > 0 && Credential.Length > 0 && ServerName.Length > 0;
}