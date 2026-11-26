using Domain.Agents;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

// What one session of an agent was found to reach when it was asked: the household's own servers as
// they stood (OwnServerEndpoints) and the machines joining them (OutpostEndpoints). Two reaches that
// are the same name the same servers on the same openings of their tunnels, so a session built over
// the first serves the second.
//
// A reach narrowed to a built app's ask names the servers it was narrowed to: a shared server's address has
// no state to compare, so two asks of one conversation declaring different servers would otherwise reach
// "the same" and the second be offered the first's tools.
internal sealed record ServerReach(
    OwnServerResolution OwnServers, ComposedEndpoints Composed, IReadOnlyList<string>? NarrowedTo = null)
{
    public bool ReachesTheSameAs(ServerReach other) =>
        OwnServers.ReachesTheSameServersAs(other.OwnServers) && Composed.ReachesTheSameMachinesAs(other.Composed)
        && (NarrowedTo is null
            ? other.NarrowedTo is null
            : other.NarrowedTo is not null && NarrowedTo.SequenceEqual(other.NarrowedTo, StringComparer.Ordinal));
}

// Everything a session is built with besides the servers it reaches: the agent's name and what it
// offers on top of its servers' tools.
internal sealed record SessionShape(
    string Name,
    string UserId,
    string Description,
    IReadOnlyList<AIFunction> DomainTools,
    IReadOnlySet<string> FilesystemEnabledTools,
    ILoggerFactory? LoggerFactory,
    McpPromptCache? PromptCache,
    ReadImageSupport? ReadImages,
    IVfsBridge? Bridge,
    bool NoTools,
    IReadOnlySet<string> WithheldTools,
    CallTokens? CallTokens,
    TimeZoneInfo LocalZone);

// The servers one agent's sessions reach, asked afresh each time, and the one build of a session over
// them: a turn's (McpAgent, reconciled at every turn's start) and a registry read outside any
// conversation (AgentReadRegistries) are built here alike, so the second mounts what the first would.
//
// Asking is a lookup, never a dial: the household's servers and machines are asked where they are
// now, and the own servers it never set up are withheld by name (HouseholdAgents.NotSetUp, asked of
// its view each time, never applied to a definition — a server set up later must reach the next
// session).
internal sealed class SessionReach(
    IReadOnlyList<McpServerEndpoint> endpoints,
    string? tenantId,
    IReadOnlyList<string> withheldServers,
    Func<CancellationToken, Task<HouseholdAgents>>? household,
    IOwnServers? ownServers,
    IOutposts? outposts,
    bool usesOutposts,
    IReadOnlyList<string>? narrowedTo = null)
{
    public async Task<ServerReach> ResolveAsync(CancellationToken ct)
    {
        var own = OwnServerEndpoints.Resolve(endpoints, tenantId, ownServers, await WithheldNowAsync(ct));
        // An agent outlives many sessions and an outpost's tunnel outlives none, so a machine that
        // opened, closed or reopened since the last session is reconciled like a household server,
        // and nothing mutates a session already built.
        return new ServerReach(own, OutpostEndpoints.Compose(own.Endpoints, outposts, usesOutposts, tenantId), narrowedTo);
    }

    // The same agent's servers a built app's ask declared (`servers`, by the names their tools carry), in
    // the definition's order: narrowed before anything is resolved, so an undeclared household server is not
    // even looked up, and never joined by a machine, whose tunnel is somebody's own computer — an app reaches
    // the household's data, as its reads do. Null when the agent dials none of them.
    public SessionReach? NarrowedTo(IReadOnlyList<string> servers)
    {
        List<McpServerEndpoint> declared =
            [.. endpoints.Where(e => e.Qualifier is { } name && servers.Contains(name, StringComparer.Ordinal))];
        return declared.Count == 0
            ? null
            : new SessionReach(declared, tenantId, withheldServers, household, ownServers, outposts: null, usesOutposts: false,
                [.. declared.Select(e => e.Qualifier!)]);
    }

    public static async Task<ThreadSession> BuildAsync(ServerReach reach, SessionShape shape, CancellationToken ct)
    {
        var session = await ThreadSession.CreateAsync(
            reach.Composed.Endpoints, shape.Name, shape.UserId, shape.Description,
            shape.DomainTools, shape.FilesystemEnabledTools, shape.LoggerFactory,
            ct, shape.PromptCache, shape.ReadImages, shape.Bridge, shape.NoTools, shape.WithheldTools, shape.CallTokens,
            shape.LocalZone);

        // Asked about by name later, when the model tries a path on a machine that is not here.
        OutpostEndpoints.DeclareUnreachable(
            session.FileSystemRegistry, reach.Composed.Outposts, session.ClientManager.DialledServers);
        return session;
    }

    // What the household keeps from this session: what it kept when the agent was built, and the own
    // servers it has not set up as of now — asked of its view, which the reader keeps a moment and
    // drops when the accounts service announces a credential minted or revoked.
    private async Task<IReadOnlyList<string>> WithheldNowAsync(CancellationToken ct) =>
        household is null
            ? withheldServers
            : [.. withheldServers, .. (await household(ct)).NotSetUp(endpoints.Select(e => e.Address))];
}