using Domain.DTOs;

namespace Infrastructure.Agents;

// Everything needed to build one running agent, resolved at the moment it is built. A plain
// value with no live collaborators, so it can be logged and asserted on field by field as
// data. Every difference between a top-level agent and a subagent is a field with a value
// here, which is what lets the build step assemble both without ever asking which one it is.
public sealed record AgentSpec
{
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
    public required IReadOnlyList<string> EnabledFeatures { get; init; }

    // Derived from the enabled features by the projection rather than handed to the agent
    // separately: an omitted argument there disables the whole virtual filesystem in silence.
    public required IReadOnlySet<string> FilesystemEnabledTools { get; init; }
    public required string[] WhitelistPatterns { get; init; }
    public string? CustomInstructions { get; init; }
    public string? Language { get; init; }
    public required bool KeepsHistory { get; init; }
    public required IReadOnlyList<string> PatchableModelIds { get; init; }
}