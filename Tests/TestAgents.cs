using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Settings;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Tests.E2E.Fixtures;

namespace Tests;

// The agents every test stack drives, as the tests' own copies of their definitions rather than any
// household's configuration: the eval, the prompt snapshots and the WebChat stack would otherwise
// measure whatever a household happens to be configured with. TestAgentsTests holds the copies equal
// to the home household's seeded definitions and to the deployment's worker profiles and channel
// defaults.
//
// Read once per instance, from one read of the file: a pass's stacks all start from the same
// definitions, and an edit saved mid-pass reaches none of them.
public sealed class TestAgents(string path)
{
    public static TestAgents Repository { get; } =
        new(System.IO.Path.Combine(TestHelpers.FindSolutionRoot(), "Tests", "TestAgents.json"));

    private readonly Lazy<Snapshot> _snapshot =
        new(() => Snapshot.Read(path), LazyThreadSafetyMode.ExecutionAndPublication);

    public string Path => path;

    public IReadOnlyList<AgentDefinition> Agents => _snapshot.Value.Agents;

    public IReadOnlyList<SubAgentDefinition> SubAgents => _snapshot.Value.SubAgents;

    public AgentDefaults Defaults => _snapshot.Value.Defaults;

    // The home household's default: who answers a message whose channel has no default of its own.
    public string? DefaultAgent => _snapshot.Value.DefaultAgent;

    // An agent's definition as JSON, to seed into a stack's household.
    public string Json(string id) =>
        _snapshot.Value.Document["agents"]!.AsArray()
            .SingleOrDefault(agent => agent!["id"]!.GetValue<string>() == id)?.ToJsonString()
        ?? throw new InvalidOperationException($"No agent '{id}' in {path}");

    // The agent's settings driving these agents in place of whichever the settings carried.
    // Replaced whole rather than layered as a second configuration source: configuration merges
    // arrays by index, so a shorter list would keep the tail of the longer one beneath it.
    public TestAgentSettings Into(AgentSettings settings) =>
        new(settings)
        {
            Agents = [.. Agents],
            DefaultAgent = DefaultAgent,
            SubAgents = [.. SubAgents],
            AgentDefaults = Defaults
        };

    private sealed record Snapshot(
        JsonObject Document,
        IReadOnlyList<AgentDefinition> Agents,
        IReadOnlyList<SubAgentDefinition> SubAgents,
        AgentDefaults Defaults,
        string? DefaultAgent)
    {
        // Bound through configuration, the way the agent binds its own settings, so a copy reads
        // exactly as the same definition would in a deployment.
        public static Snapshot Read(string path)
        {
            var text = File.ReadAllText(path);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var document = JsonNode.Parse(
                text,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
                })!.AsObject();

            return new Snapshot(
                document,
                configuration.GetSection("agents").Get<AgentDefinition[]>() ?? [],
                configuration.GetSection("subAgents").Get<SubAgentDefinition[]>() ?? [],
                configuration.GetSection("agentDefaults").Get<AgentDefaults>() ?? new AgentDefaults(),
                configuration["defaultAgent"]);
        }
    }
}

// The agent's settings as a test stack runs it: the deployment's settings, which carry no agents, beside
// the agents a household's configuration would have given it, named in code. The stack hands them to
// the agent as every household's (FixedHouseholdAgents) after AddAgent.
public sealed record TestAgentSettings : AgentSettings
{
    public TestAgentSettings()
    {
    }

    [SetsRequiredMembers]
    public TestAgentSettings(AgentSettings settings) : base(settings)
    {
    }

    public AgentDefinition[] Agents { get; init; } = [];

    public string? DefaultAgent { get; init; }

    public FixedHouseholdAgents Households => new([.. Agents]) { DefaultAgent = DefaultAgent };
}