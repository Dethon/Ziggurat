using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// What one session build was given, and which of those endpoints were machines. The names are kept
// because the verdict written back afterwards is per outpost, and a mount name that belongs to the
// deployment's own filesystem is nobody's registration to write on.
internal sealed record ComposedEndpoints(
    IReadOnlyList<McpServerEndpoint> Endpoints,
    IReadOnlyList<string> Outposts);

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
    public static async Task<ComposedEndpoints> ComposeAsync(
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
            return new ComposedEndpoints(configured, []);
        }

        try
        {
            var live = await registry.ListAsync(ct);
            return live.Count == 0
                ? new ComposedEndpoints(configured, [])
                : new ComposedEndpoints(
                    [.. configured, .. live.Select(outpost => McpServerEndpoint.Dynamic(outpost.Endpoint))],
                    [.. live.Select(outpost => outpost.Name)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registry being unreachable costs this session its outposts and nothing else: a
            // turn that could still be answered from the deployment's own filesystems must not
            // fail because a machine's registration could not be looked up.
            logger?.LogWarning(ex,
                "Live outposts could not be read, so this session is built from configured endpoints alone");
            return new ComposedEndpoints(configured, []);
        }
    }

    // What the build made of each machine, written back onto its registration. This is the only
    // moment it can be known: a mount name lives inside a server's own filesystem resource, so the
    // collision is discovered here, long after the registration succeeded and nowhere the machine
    // can see. The next keepalive carries it home.
    //
    // Only the outposts this session was given are written, and only where the build reached a
    // decision about one. An outpost that could not be dialled is left as it was, because "the hub
    // could not reach you" is not a verdict on a mount and calling it shadowed would name the
    // wrong problem.
    public static async Task RecordVerdictsAsync(
        IOutpostRegistry? registry,
        IReadOnlyList<string> outposts,
        IReadOnlyList<string> mounted,
        IReadOnlyList<string> shadowed,
        ILogger? logger,
        CancellationToken ct)
    {
        if (registry is null || outposts.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(outposts
                .Select(name => (Name: name, Verdict: Verdict(name, mounted, shadowed)))
                .Where(v => v.Verdict is not null)
                .Select(v => registry.RecordVerdictAsync(v.Name, v.Verdict!.Value, ct)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The verdict is feedback, not function. A session that built must not fail because
            // the machine it built from cannot be told about it.
            logger?.LogWarning(ex, "The mount verdicts for this session could not be recorded");
        }
    }

    // Shadowed first: a name that is both mounted and shadowed is exactly the collision case, where
    // the mount that is there belongs to somebody else.
    private static OutpostVerdict? Verdict(
        string name, IReadOnlyList<string> mounted, IReadOnlyList<string> shadowed) =>
        shadowed.Contains(name, StringComparer.Ordinal) ? OutpostVerdict.Shadowed
        : mounted.Contains(name, StringComparer.Ordinal) ? OutpostVerdict.Mounted
        : null;
}