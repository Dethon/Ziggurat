using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// Everything an agent needs to reach the machines that registered themselves: where to ask which
// ones are live, and what to present when dialling one. They travel together because neither is
// any use without the other — a registration nobody may dial is a mount that cannot be reached.
public sealed record OutpostAccess(IOutpostRegistry Registry, string SharedSecret);

// What one session build was given, and which of those endpoints were machines. The registrations
// are kept whole because the verdict written back afterwards is per outpost and only for a machine
// whose own dial survived — the endpoint address is what ties a registration to its dial, since a
// mount name proves nothing: the deployment's own filesystem can hold the same one.
internal sealed record ComposedEndpoints(
    IReadOnlyList<McpServerEndpoint> Endpoints,
    IReadOnlyList<OutpostRegistration> Outposts);

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
        OutpostAccess? outposts,
        bool usesOutposts,
        ILogger? logger,
        CancellationToken ct)
    {
        var registry = outposts?.Registry;
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
                    [
                        .. configured,
                        .. live.Select(outpost =>
                            McpServerEndpoint.Dynamic(outpost.Endpoint, outposts!.SharedSecret))
                    ],
                    live);
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
    // decision about one: the machine's own dial produced a client, and its mount then happened or
    // was shadowed. An outpost that could not be dialled is left as it was, because "the hub could
    // not reach you" is not a verdict on a mount and calling it shadowed would name the wrong
    // problem — and its name alone proves nothing, since a configured mount holding the same name
    // would otherwise vouch for a machine the build never reached.
    //
    // A build that does not record — a subagent's — mounts the same machines and leaves their
    // registrations exactly as it found them. The verdict answers "did the agent you registered
    // with mount you", and a delegated task is not that agent.
    public static async Task RecordVerdictsAsync(
        OutpostAccess? access,
        bool recordsVerdicts,
        IReadOnlyList<OutpostRegistration> outposts,
        IReadOnlyList<string> mounted,
        IReadOnlyList<string> shadowed,
        IReadOnlyList<string> dialled,
        ILogger? logger,
        CancellationToken ct)
    {
        if (!recordsVerdicts || access?.Registry is not { } registry || outposts.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(outposts
                .Select(outpost => (outpost.Name, Verdict: Verdict(outpost, mounted, shadowed, dialled)))
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

    // Undialled first, judged by the endpoint address rather than the name, because the address is
    // the one thing that is the machine's own. Then shadowed before mounted: a name that is both
    // is exactly the collision case, where the mount that is there belongs to somebody else.
    private static OutpostVerdict? Verdict(
        OutpostRegistration outpost,
        IReadOnlyList<string> mounted,
        IReadOnlyList<string> shadowed,
        IReadOnlyList<string> dialled) =>
        !dialled.Contains(outpost.Endpoint, StringComparer.Ordinal) ? null
        : shadowed.Contains(outpost.Name, StringComparer.Ordinal) ? OutpostVerdict.Shadowed
        : mounted.Contains(outpost.Name, StringComparer.Ordinal) ? OutpostVerdict.Mounted
        : null;
}