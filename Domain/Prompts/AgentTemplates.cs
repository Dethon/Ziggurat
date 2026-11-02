using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Household;

namespace Domain.Prompts;

// The shipped agent templates: what a household's agent starts from when its configuration names a
// template rather than carrying a definition (CONTEXT.md, "Agent template"). Each is a document beside
// the prompt sections, Agents/<name>.md, its header the definition and its words the description the
// identity section reads after the agent's name. A household gives a template's agent its id, its
// name and its custom instructions, and nothing else (HouseholdAgents).
public static class AgentTemplates
{
    public const string Jonas = "jonas";
    public const string Sugary = "sugary";

    // The id a household's voice server addresses (VoiceSettings.AgentId, agentDefaults.byChannel.voice),
    // so an agent added from this template under its own id answers aloud with no setting of its own.
    public const string Nabu = "nabu";

    // What an agent template's header may say: the definition's fields, spelled as a header spells.
    private static readonly HashSet<string> _keys =
    [
        "name", "purpose", "agent-name", "model", "reasoning-effort", "provider-routing", "servers", "features",
        "whitelist", "prompt-sections", "uses-outposts", "no-tools"
    ];

    public static IReadOnlyList<AgentDefinition> All { get; } = [.. PromptDocuments.AgentTemplates.Select(Read)];

    public static AgentDefinition? Named(string name) =>
        All.FirstOrDefault(template => template.Id.Equals(name, StringComparison.Ordinal));

    public static AgentDefinition Read(PromptDocument document)
    {
        var unknown = document.Header.Keys.Where(key => !_keys.Contains(key)).ToList();
        if (unknown.Count > 0)
        {
            throw new FormatException($"{document.Source}: an agent template's header has no '{string.Join("', '", unknown)}'");
        }

        if (!HouseholdSections.IsAgentId(document.Name))
        {
            throw new FormatException($"{document.Source}: a template is named as an agent id is, not '{document.Name}'");
        }

        if (document.Claims.Count > 0 || document.Parts.Count > 0)
        {
            throw new FormatException($"{document.Source}: an agent template carries no claims and no parts");
        }

        return new AgentDefinition
        {
            Id = document.Name,
            Name = Required(document, "agent-name"),
            // Its words, as they stand: a template has nothing for the host to fill.
            Description = document.Text,
            Model = Required(document, "model"),
            ReasoningEffort = document.Value("reasoning-effort"),
            ProviderRouting = document.Value("provider-routing") is { } routing ? Routing(document, routing) : null,
            McpServerEndpoints = List(document.Value("servers")),
            EnabledFeatures = List(document.Value("features")),
            WhitelistPatterns = List(document.Value("whitelist")),
            PromptSections = List(document.Value("prompt-sections")),
            UsesOutposts = Flag(document, "uses-outposts"),
            NoTools = Flag(document, "no-tools")
        };
    }

    private static string Required(PromptDocument document, string key) =>
        document.Value(key) is { Length: > 0 } value
            ? value
            : throw new FormatException($"{document.Source}: the header has no '{key}'");

    private static bool Flag(PromptDocument document, string key) =>
        document.Value(key) switch
        {
            null or "false" => false,
            "true" => true,
            var other => throw new FormatException($"{document.Source}: '{key}' is true or false, not '{other}'")
        };

    // A routing object as a seeded definition carries one (SectionSchema.Json), on one line: a
    // threshold is an object here, never the bare number the configuration binder takes.
    private static ProviderRouting Routing(PromptDocument document, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<ProviderRouting>(value, SectionSchema.Json)
                   ?? throw new FormatException($"{document.Source}: 'provider-routing' is null");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{document.Source}: 'provider-routing' is not a routing object: {ex.Message}");
        }
    }

    private static string[] List(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}