namespace Infrastructure.Agents;

// One machine live for a household now: its NAME (the mount is outpost:<NAME>) and where it is
// reached — its open tunnel, bound to that opening.
public sealed record LiveOutpost(string Name, McpServerEndpoint Endpoint);

// The outposts a household has open, asked when a session is built — the moment its own servers are
// resolved too, and for the same reason: an agent outlives many sessions, a tunnel opening is one
// generation, and nothing mutates a session already built (ADR 0012). The deployment's answer is its
// open outpost tunnels (TunnelledOutposts); a turn that names no household has none.
public interface IOutposts
{
    IReadOnlyList<LiveOutpost> Live(string? tenantId);
}