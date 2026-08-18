using Domain.DTOs;
using Domain.Prompts;

namespace Infrastructure.Agents;

// Everything needed to build one running agent, resolved at the moment it is built. A plain
// value with no live collaborators, so it can be logged and asserted on field by field as
// data. Every difference between a top-level agent and a subagent is a field with a value
// here, which is what lets the build step assemble both without ever asking which one it is.
public sealed record AgentSpec
{
    // The configured id, which is what a prompt section's audience and a routing default name. The
    // display name beside it carries the conversation and belongs to metrics.
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string MetricsAgentId { get; init; }
    public required string RoutingSessionId { get; init; }
    public required string ConversationId { get; init; }
    public required string UserId { get; init; }
    public required string Model { get; init; }
    public int? MaxContextTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
    public required IReadOnlyList<McpServerEndpoint> McpServerEndpoints { get; init; }

    // Whether live outposts join the endpoint list above when a session is built. A deliberate
    // choice per agent, never a default.
    public bool UsesOutposts { get; init; }
    public required IReadOnlyList<string> EnabledFeatures { get; init; }

    // Derived from the enabled features by the projection rather than handed to the agent
    // separately: an omitted argument there disables the whole virtual filesystem in silence.
    public required IReadOnlySet<string> FilesystemEnabledTools { get; init; }
    public required string[] WhitelistPatterns { get; init; }
    public string? CustomInstructions { get; init; }

    // Resolved from the definition's section names while the spec is projected, so an agent that
    // names a section nothing declares is refused where the name was written rather than assembled
    // one section short.
    public required IReadOnlyList<PromptSection> PromptSections { get; init; }
    public string? Language { get; init; }
    public required bool KeepsHistory { get; init; }

    // Whether this build writes each outpost's mount verdict back onto its registration. True for
    // an agent and false for a subagent: the verdict is a machine's one feedback channel and it
    // answers "did the agent you registered with mount you", so a collision reached inside a
    // delegated task is not the operator's to be told about. Its own field rather than a second
    // meaning for KeepsHistory, which is about thread state and would answer this by coincidence.
    public required bool RecordsOutpostVerdicts { get; init; }
    public required IReadOnlyList<string> PatchableModelIds { get; init; }
}