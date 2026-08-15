using Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// Live outposts joining an agent's endpoint list, asked for at the moment a session is built.
//
// Asked then rather than when the agent is created, because an agent outlives many sessions and an
// outpost outlives none: a registration takes effect at the next session build and an expiry
// likewise, and nothing ever mutates a session that already exists. That is the same rule ADR-0012
// sets for a channel server's tool set, for the same reason.
//
// Configured endpoints stay first. Mount order is what decides a name collision — the existing
// mount always wins — so an outpost calling itself "vault" is shadowed deterministically rather
// than by whichever dial happened to finish first.
internal static class OutpostEndpoints
{
    public static async Task<IReadOnlyList<McpServerEndpoint>> ComposeAsync(
        IReadOnlyList<McpServerEndpoint> configured,
        IOutpostRegistry? registry,
        bool usesOutposts,
        ILogger? logger,
        CancellationToken ct)
    {
        // Nothing is opted in by default. An agent that exists to search for downloads has no
        // business reaching somebody's laptop, and a new machine appearing on the network must not
        // silently widen what any agent can touch.
        if (!usesOutposts || registry is null)
        {
            return configured;
        }

        try
        {
            var live = await registry.ListAsync(ct);
            return live.Count == 0
                ? configured
                : [.. configured, .. live.Select(outpost => McpServerEndpoint.Dynamic(outpost.Endpoint))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registry being unreachable costs this session its outposts and nothing else: a
            // turn that could still be answered from the deployment's own filesystems must not
            // fail because a machine's registration could not be looked up.
            logger?.LogWarning(ex,
                "Live outposts could not be read, so this session is built from configured endpoints alone");
            return configured;
        }
    }
}