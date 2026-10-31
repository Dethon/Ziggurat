using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Settings;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Tests.E2E.Fixtures;

namespace Tests;

// The agents every test stack drives, as the tests' own copies of their definitions rather than
// the agent's settings file: the eval, the prompt snapshots and the WebChat stack would otherwise
// measure whatever a household happens to be configured with, and stop working the day the file
// carries no agents. TestAgentsTests holds the copies equal to today's definitions.
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

    // An agent's definition as JSON, to splice into a container's settings file.
    public string Json(string id) =>
        _snapshot.Value.Document["agents"]!.AsArray()
            .SingleOrDefault(agent => agent!["id"]!.GetValue<string>() == id)?.ToJsonString()
        ?? throw new InvalidOperationException($"No agent '{id}' in {path}");

    // The agent's settings driving these agents in place of whichever the settings carried.
    // Replaced whole rather than layered as a second configuration source: configuration merges
    // arrays by index, so a shorter list would keep the tail of the longer one beneath it.
    public AgentSettings Into(AgentSettings settings) =>
        settings with { Agents = [.. Agents], SubAgents = [.. SubAgents], AgentDefaults = Defaults };

    private sealed record Snapshot(
        JsonObject Document,
        IReadOnlyList<AgentDefinition> Agents,
        IReadOnlyList<SubAgentDefinition> SubAgents,
        AgentDefaults Defaults)
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
                configuration.GetSection("agentDefaults").Get<AgentDefaults>() ?? new AgentDefaults());
        }
    }
}