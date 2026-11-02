namespace Infrastructure.Agents;

// What one server a session is built with was found to be when a turn started — a household server
// named in the agent's definition, or a machine: online and on which opening of its tunnel (ADR 0012's
// generation), or not there. Two turns whose states are equal, in the same order, reach the same
// servers, so the session the first built serves the second.
public sealed record ServerState(string Name, bool Online, long? Generation, bool Withheld = false)
{
    public static bool Same(IReadOnlyList<ServerState> one, IReadOnlyList<ServerState> other) =>
        one.SequenceEqual(other);
}

// The household's servers as a turn found them: the endpoints to dial, in the definition's order,
// with every server that is not there left out; the names of those left out; and the states a later
// turn compares with its own.
public sealed record OwnServerResolution(
    IReadOnlyList<McpServerEndpoint> Endpoints,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<ServerState> States)
{
    // Those not there when the turn started, and any whose tunnel has closed since — one that went
    // while its session was being dialled is left out of it (McpClientManager) and is as offline.
    public IReadOnlyList<string> Offline =>
    [
        .. Unresolved,
        .. Endpoints.Where(e => e.Offline.IsCancellationRequested).Select(e => e.Server!.Qualifier)
    ];

    public bool ReachesTheSameServersAs(OwnServerResolution other) => ServerState.Same(States, other.States);
}

// A household's own servers named in an agent's endpoint list, resolved to where they are reached
// at the moment a turn starts — the moment outposts are asked for too, and for the same reason: an
// agent outlives many sessions, a tunnel opening is one generation, and nothing mutates a session
// already built (ADR 0012). Each keeps its place in the list, so mounts claim their names in the
// order the definition gave.
//
// A household server that cannot be reached — its tunnel is not open, or the turn names no household
// — is left out and named, never fatal. That is ADR 0027's rule turned round for these endpoints
// alone: a machine in somebody's home being off is not the deployment being broken. A shared
// server's URL endpoint still fails the session (.claude/rules/tunnel.md).
internal static class OwnServerEndpoints
{
    // One the household withholds, by its server name — a server it never set up, or one a worker's
    // parent does not name — is not asked for and not named: it is no part of the turn, so a household
    // is never told about a server it does not run. A shared server's address in the same list never
    // equals a name. It still has a state, so a turn after the household set it up builds a session
    // that reaches it.
    public static OwnServerResolution Resolve(
        IReadOnlyList<McpServerEndpoint> endpoints, string? tenantId, IOwnServers? servers,
        IReadOnlyList<string>? withheld = null)
    {
        var resolved = endpoints
            .Select(endpoint => (
                Named: endpoint,
                Withheld: endpoint.NamesOwnServer && (withheld ?? []).Contains(endpoint.Address, StringComparer.Ordinal)))
            .Select(r => (r.Named, r.Withheld, Reached: !r.Named.NamesOwnServer
                ? r.Named
                : r.Withheld ? null : servers?.Resolve(tenantId, r.Named.Address)))
            .ToList();
        var own = resolved.Where(r => r.Named.NamesOwnServer).ToList();

        return new OwnServerResolution(
            [.. resolved.Select(r => r.Reached).OfType<McpServerEndpoint>()],
            [.. own.Where(r => r.Reached is null && !r.Withheld).Select(r => r.Named.Address)],
            [.. own.Select(r => new ServerState(r.Named.Address, r.Reached is not null, r.Reached?.Generation, r.Withheld))]);
    }
}