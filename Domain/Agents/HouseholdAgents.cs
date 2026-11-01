using Domain.DTOs;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Prompts;

namespace Domain.Agents;

// A household's agents as its turns run them, built from its configuration (.claude/rules/household-config.md):
// each entry of its agents section is a shipped template given the household's name and custom
// instructions for it, or a full definition seeded for it (the home's jack, jonas, nabu and sugary);
// the household's language is every agent's that pins none; and its features withhold the shared
// servers and the domain features it turned off. A household whose section names no agent has the
// two templates, as a new household does.
public sealed record HouseholdAgents
{
    private const string MemoryFeature = "memory";
    private const string WorkersFeature = "subagents";

    // Null for agents no household's configuration gave: a stack that configures its agents in code.
    public string? HouseholdId { get; init; }

    public required IReadOnlyList<AgentDefinition> Agents { get; init; }

    // Who answers a message whose channel named no agent and has no default of its own.
    public string? DefaultAgent { get; init; }

    // The zone the household's days are read in (an IANA name); null where no household said, and
    // the host's own is used.
    public string? TimeZone { get; init; }

    public FeaturesSection Features { get; init; } = new();

    // The shared servers the household's features turned off: no agent of its, and no worker one
    // spawns, reaches them.
    public IReadOnlyList<string> WithheldServers { get; init; } = [];

    // Entries that could not be built — a template this build does not ship — left out rather than
    // taking the household's other agents with them, for the host to report.
    public IReadOnlyList<string> Problems { get; init; } = [];

    // What the agent read of the household, for what is decided by it beyond its agents (whose key
    // pays). Null for agents no household's configuration gave.
    public AgentHouseholdConfig? Configuration { get; init; }

    // Who pays for its model calls: the home household's, like those of agents no household's
    // configuration gave, go out on the deployment's own key, and any other household's on its own.
    // Null for a household of its own that set none: it pays for nothing, so its turns are refused —
    // its agents are still listed, since naming them costs nothing.
    public ModelPayer? Payer => Configuration switch
    {
        null or { IsHome: true } => new ModelPayer(HouseholdId, null),
        { OpenRouterApiKey: { Length: > 0 } key } => new ModelPayer(HouseholdId, new ModelKey(key)),
        _ => null
    };

    // A new household's agents: one of each shipped template, under the template's own id and name.
    public static IReadOnlyList<HouseholdAgent> Starting { get; } =
    [
        .. new[] { AgentTemplates.Jonas, AgentTemplates.Sugary }.Select(name => new HouseholdAgent
        {
            Id = name, Template = name, Name = AgentTemplates.Named(name)!.Name
        })
    ];

    public static HouseholdAgents Of(AgentHouseholdConfig configuration, SharedServers shared)
    {
        var (entries, defaultAgent) = Entries(configuration.Agents);
        var withheld = shared.WithheldBy(configuration.Features);
        var built = entries
            .Select(entry => (Entry: entry, Definition: Definition(entry, shared)))
            .ToList();
        var household = new HouseholdAgents
        {
            HouseholdId = configuration.HouseholdId,
            Agents = [],
            DefaultAgent = defaultAgent,
            TimeZone = configuration.Settings.TimeZone,
            Features = configuration.Features,
            WithheldServers = withheld,
            Configuration = configuration,
            Problems =
            [
                .. built.Where(b => b.Definition is null)
                    .Select(b => $"agent {b.Entry.Id} names the template '{b.Entry.Template}', which this deployment does not ship")
            ]
        };

        return household with
        {
            Agents =
            [
                .. built.Select(b => b.Definition).OfType<AgentDefinition>()
                    .Select(definition => household.Offered(definition with
                    {
                        Language = definition.Language ?? configuration.Settings.Language
                    }))
            ]
        };
    }

    // The household's agents as a person picks among them (the shared Telegram bot's /agent, the chat
    // client's picker): the very ones its turns run, an entry this build cannot build left out as it is
    // there.
    public static HouseholdAgentNames Names(AgentsSection section)
    {
        var (entries, defaultAgent) = Entries(section);
        return new HouseholdAgentNames(defaultAgent,
        [
            .. entries.Select(entry => Definition(entry, new SharedServers())).OfType<AgentDefinition>()
                .Select(definition => new HouseholdAgentName(
                    definition.Id, definition.Name, definition.Description, definition.Model, definition.ReasoningEffort))
        ]);
    }

    // The section as the household's turns read it: one that names no agent is the starting templates
    // with jonas answering by default. An owner's first change to such a household is made to this, so
    // it keeps the agents it had rather than only the one changed.
    public static AgentsSection Effective(AgentsSection section) =>
        section.Agents.Count == 0 ? new AgentsSection { DefaultAgent = AgentTemplates.Jonas, Agents = Starting } : section;

    private static (IReadOnlyList<HouseholdAgent> Entries, string? DefaultAgent) Entries(AgentsSection section)
    {
        var effective = Effective(section);
        return (effective.Agents, effective.DefaultAgent);
    }

    // Agents a stack names in code, offered as a household's would be with every feature on.
    public static HouseholdAgents Fixed(IReadOnlyList<AgentDefinition> agents, string? defaultAgent = null) =>
        new() { Agents = agents, DefaultAgent = defaultAgent };

    // A definition as this household's features let its agents have it: without the shared servers
    // they withhold, and without memory or workers where those are off. Applied to a person's own
    // agents as much as to the household's.
    public AgentDefinition Offered(AgentDefinition definition) =>
        definition with
        {
            McpServerEndpoints =
            [
                .. definition.McpServerEndpoints.Where(endpoint => !SharedServers.Names(WithheldServers, endpoint))
            ],
            EnabledFeatures =
            [
                .. definition.EnabledFeatures.Where(feature => !Withheld(feature))
            ]
        };

    public AgentDefinition? Find(string agentId) =>
        Agents.FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));

    private bool Withheld(string feature) =>
        (feature.Equals(MemoryFeature, StringComparison.OrdinalIgnoreCase) && !Features.Memory)
        || (feature.Equals(WorkersFeature, StringComparison.OrdinalIgnoreCase) && !Features.Workers);

    // A template's entry is the template under the entry's id, name and instructions, reaching those
    // of its servers this deployment runs as shared servers: a template is the repo's, and names the
    // compose hosts a deployment runs them at, which one that runs no such server does not answer at.
    // A definition entry is its definition, which carries its own and is this deployment's.
    private static AgentDefinition? Definition(HouseholdAgent entry, SharedServers shared) =>
        entry switch
        {
            { Definition: { } definition } => definition with { Id = entry.Id },
            { Template: { } template } => AgentTemplates.Named(template) is { } shipped
                ? shipped with
                {
                    Id = entry.Id,
                    Name = entry.Name ?? shipped.Name,
                    CustomInstructions = entry.CustomInstructions,
                    McpServerEndpoints =
                    [
                        .. shipped.McpServerEndpoints.Where(endpoint =>
                            TunnelCredentialProtocol.IsServerName(endpoint) || SharedServers.Names(shared.All, endpoint))
                    ]
                }
                : null,
            _ => null
        };
}