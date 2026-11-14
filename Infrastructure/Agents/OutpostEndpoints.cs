using Domain.Contracts;
using Domain.Outposts;
using Domain.Security;
using Domain.Tools;

namespace Infrastructure.Agents;

// What one session build was given, and which of those endpoints were machines.
internal sealed record ComposedEndpoints(
    IReadOnlyList<McpServerEndpoint> Endpoints,
    IReadOnlyList<LiveOutpost> Outposts)
{
    // The same machines on the same openings of their tunnels, in the same (name) order: a machine
    // that opened, closed or reopened since makes a session built from the other a stale one.
    public bool ReachesTheSameMachinesAs(ComposedEndpoints other) => ServerState.Same(Machines, other.Machines);

    private IReadOnlyList<ServerState> Machines =>
        [.. Outposts.Select(o => new ServerState(o.Name, Online: true, o.Endpoint.Generation))];
}

// The household's live outposts joining an agent's endpoint list, asked for at the moment a session
// is built.
//
// Asked then rather than when the agent is created, because an agent outlives many sessions and an
// outpost's tunnel outlives none: asked at every turn's start, a machine whose tunnel opened, closed
// or reopened since the last turn makes that turn build a new session (ReachesTheSameMachinesAs), and
// nothing ever mutates a session that already exists.
// That is the same rule ADR-0012 sets for a channel server's tool set, for the same reason.
//
// Configured endpoints stay first and outposts follow in name order. Mount order is what decides a
// name collision — the existing mount always wins — and since an outpost is addressed as a machine
// rather than as a path in the deployment's tree, and a household holds one tunnel per server name,
// the collision it can lose is to a definition naming its tunnel's server outright, which is skipped
// here rather than dialled twice.
internal static class OutpostEndpoints
{
    public static ComposedEndpoints Compose(
        IReadOnlyList<McpServerEndpoint> configured,
        IOutposts? outposts,
        bool usesOutposts,
        string? tenantId)
    {
        // Nothing is opted in by default. An agent that exists to search for downloads has no
        // business reaching somebody's laptop, and a new machine appearing on the network must not
        // silently widen what any agent can touch.
        if (!usesOutposts || outposts is null)
        {
            return new ComposedEndpoints(configured, []);
        }

        var named = configured.Select(e => e.Server).OfType<ServerAddress>().ToHashSet();
        var live = outposts.Live(tenantId).Where(o => o.Endpoint.Server is not { } server || !named.Contains(server)).ToList();
        return new ComposedEndpoints([.. configured, .. live.Select(o => o.Endpoint)], live);
    }

    // A machine whose tunnel was open and which did not answer, told to the registry that will be asked
    // about it by name. The open tunnel is the evidence it is a real thing rather than a typo, and the
    // failed dial is the evidence it is not here now — which together are the one absence worth trying
    // again.
    //
    // Judged by the server each dial reached rather than by the mount name, because a configured
    // mount can hold the same name and would otherwise vouch for a machine nobody reached.
    public static void DeclareUnreachable(
        IVirtualFileSystemRegistry? registry,
        IReadOnlyList<LiveOutpost> outposts,
        IReadOnlyList<ServerAddress> dialled)
    {
        if (registry is null)
        {
            return;
        }

        foreach (var outpost in outposts.Where(o => o.Endpoint.Server is not { } server || !dialled.Contains(server)))
        {
            registry.DeclareAbsence(
                OutpostMountPoint.For(outpost.Name),
                CapabilityState.Unavailable,
                "the machine's tunnel was open but it did not answer when this conversation started");
        }
    }
}