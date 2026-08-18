using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record AgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }

    // Absolute reply language. Null leaves the agent free to follow the conversation.
    public string? Language { get; init; }
    public string? TelegramBotToken { get; init; }
    public string[] EnabledFeatures { get; init; } = [];

    // Prompt sections this agent is given by name, from `PromptManifest.SelectableSections`. It is
    // how behaviour that used to sit in `customInstructions` as unreviewable prose becomes a
    // section with a purpose, a budget and a stated position in every conflict it takes part in.
    public string[] PromptSections { get; init; } = [];

    // Whether this agent may see outposts — filesystems on real machines that announce themselves.
    // Nothing is opted in by default: an agent that exists to search for downloads has no business
    // reaching somebody's laptop, and a new machine appearing on the network must not silently
    // widen what any agent can touch.
    public bool UsesOutposts { get; init; }
    public int? MaxContextTokens { get; init; }
    public string? ReasoningEffort { get; init; }
    public ProviderRouting? ProviderRouting { get; init; }
}