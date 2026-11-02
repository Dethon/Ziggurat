using Domain.DTOs;

namespace Domain.Agents;

// What the parent contributes to a spawn. Every field is "what the parent was" rather than
// anything the subagent's own definition says, which is why they travel as one value: the next
// such value is a field here rather than a sixth positional parameter on the factory.
//
// Nothing binds this from configuration, so it lives beside the agent key rather than among the
// definitions in Domain.DTOs.
public sealed record SpawnContext(
    string ConversationId,
    string UserId,
    string[] WhitelistPatterns,
    // The parent's own opt-in, and the ceiling on the subagent's: a subagent acts on the
    // parent's behalf, so it can never reach a machine the parent could not.
    bool UsesOutposts,
    // The model the parent is configured to run and the routing it resolved for it, already
    // resolved: a worker names neither, so one profile serves every agent on that agent's own
    // model. Routing says who serves a model, which is why the two travel together.
    string Model,
    ProviderRouting? ProviderRouting = null,
    // The household the parent's turns are for: a worker reaches the household servers its parent
    // reaches, resolved against the same household.
    string? TenantId = null,
    // The shared servers that household's features turned off, which its workers do not reach any
    // more than its agents do, whatever their profile names.
    IReadOnlyList<string>? WithheldServers = null,
    // That household's zone, whose day the worker's prompt names as its parent's does.
    string? TimeZone = null,
    // The key that household pays its parent's calls with, which pays the worker's too; null is the
    // deployment's (the home household's, or nobody's).
    ModelKey? Key = null,
    // The household's own servers the parent names, which bound the worker's as the parent's opt-in
    // bounds its machines: a worker reaches none its parent could not. Null puts no bound (a spawn no
    // parent agent made).
    IReadOnlyList<string>? OwnServers = null);