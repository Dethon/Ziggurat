using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record SubAgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string? CustomInstructions { get; init; }

    // Absolute reply language. Null leaves the subagent free to follow the conversation.
    public string? Language { get; init; }
    public string[] EnabledFeatures { get; init; } = [];

    // Prompt sections this agent is given by name, from `PromptManifest.SelectableSections`. It is
    // how behaviour that used to sit in `customInstructions` as unreviewable prose becomes a
    // section with a purpose, a budget and a stated position in every conflict it takes part in.
    public string[] PromptSections { get; init; } = [];

    // Whether this subagent may see outposts, named as it is on an agent definition and half of
    // the answer: it reaches them only where its parent is opted in too. Nothing is opted in by
    // default, so a worker profile added to the file hands itself no machines — the same rule as
    // for an agent, applied a second time because the list of subagents is shared by every agent
    // that enables the feature. See docs/adr/0028.
    public bool UsesOutposts { get; init; }
    public int MaxExecutionSeconds { get; init; } = 120;
    public int? MaxContextTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
}