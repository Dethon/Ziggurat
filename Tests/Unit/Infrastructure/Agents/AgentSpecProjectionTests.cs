using System.Text.RegularExpressions;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public sealed class AgentSpecProjectionTests
{
    private static readonly ProviderRouting _globalRouting = new() { Sort = ProviderSort.Throughput };
    private static readonly ProviderRouting _declaredRouting = new() { Sort = ProviderSort.Latency };

    private static readonly OpenRouterConfig _openRouter = new()
    {
        ApiUrl = "http://test",
        ApiKey = "test-key",
        MaxContextTokens = 200_000,
        ProviderRouting = _globalRouting,
        PatchableModelIds = ["model-a", "model-b"]
    };

    private static readonly AgentDefinition _agentDefinition = new()
    {
        Id = "jack",
        Name = "Jack",
        Description = "The agent",
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = ["http://tools"],
        WhitelistPatterns = ["allow-*"],
        CustomInstructions = "Be helpful",
        Language = "es",
        EnabledFeatures = ["memory", "subagents", "filesystem.text_read"],
        MaxContextTokens = 100_000,
        ReasoningEffort = "high",
        ProviderRouting = _declaredRouting
    };

    // Declares no routing, so it inherits the global default that the agent above overrides.
    private static readonly SubAgentDefinition _subAgentDefinition = new()
    {
        Id = "worker",
        Name = "Worker",
        Description = "The subagent",
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = ["http://tools"],
        CustomInstructions = "Be brief",
        Language = "es",
        EnabledFeatures = ["memory", "subagents", "filesystem.text_read"],
        MaxContextTokens = 50_000,
        ReasoningEffort = "low"
    };

    private static AgentSpec AgentSpec() => AgentSpecProjection.ForAgent(
        _agentDefinition, new AgentKey("conv-1", "jack"), "fran", _openRouter,
        new FixedPatchableModelSource(["model-a", "model-b"]), null);

    private static AgentSpec SubAgentSpec() => AgentSpecProjection.ForSubAgent(
        _subAgentDefinition, new SpawnContext("conv-1", "fran", ["allow-*"], UsesOutposts: false),
        _openRouter, null);

    // One row per field the two paths resolve, so a new difference is a new row rather than a
    // new test. A row whose two expectations are equal is a difference this change removed on
    // purpose, and the table is where that is stated.
    public static IEnumerable<object?[]> FieldsToCompare =>
    [
        Row("display name", s => s.DisplayName, "Jack-conv-1", "subagent-worker"),
        Row("routing session id", s => WithoutSpawnId(s.RoutingSessionId),
            "jack:conv-1", "subagent-worker:<spawn>"),
        Row("conversation id", s => s.ConversationId, "conv-1", "conv-1"),
        Row("metrics identity", s => s.MetricsAgentId, "Jack", "Worker"),
        Row("keeps history", s => s.KeepsHistory, true, false),
        // A mount verdict answers "did the agent you registered with mount you", so a delegated
        // task's collision set is nobody's business at the machine.
        Row("records outpost verdicts", s => s.RecordsOutpostVerdicts, true, false),
        // The agent's list is whatever source the factory hands it; a subagent's is empty by
        // construction, so no patch copied down from a parent's message ever wins there.
        Row("patchable model ids", s => s.PatchableModels.Ids,
            new[] { "model-a", "model-b" }, Array.Empty<string>()),
        Row("enabled features", s => s.EnabledFeatures,
            new[] { "memory", "subagents", "filesystem.text_read" },
            new[] { "memory", "filesystem.text_read" }),
        Row("filesystem enabled tools", s => s.FilesystemEnabledTools,
            new[] { "text_read" }, new[] { "text_read" }),
        Row("whitelist patterns", s => s.WhitelistPatterns,
            new[] { "allow-*" }, new[] { "allow-*" }),
        // The agent declares its own routing wholesale; the subagent declares none and inherits
        // the global default. Both arrive on the spec already resolved.
        Row("provider routing", s => s.ProviderRouting, _declaredRouting, _globalRouting),
        // An endpoint stops being a bare string here. Everything the projection reads came out of
        // appsettings.json, so everything it composes is marked configured; a dynamic one is
        // contributed later, by the registry of live outposts, and never by a definition.
        Row("mcp server endpoints", s => s.McpServerEndpoints,
            new[] { McpServerEndpoint.Configured("http://tools") },
            new[] { McpServerEndpoint.Configured("http://tools") })
    ];

    [Theory]
    [MemberData(nameof(FieldsToCompare))]
    public void Project_AComparedField_HasTheStatedValueOnEachPath(
        string _, Func<AgentSpec, object?> read, object? expectedForAgent, object? expectedForSubAgent)
    {
        read(AgentSpec()).ShouldBe(expectedForAgent);
        read(SubAgentSpec()).ShouldBe(expectedForSubAgent);
    }

    // Two flags rather than one, and the whole table is the argument for it: the parent is the
    // ceiling, so a worker cannot reach a machine its parent cannot, and the worker's own
    // definition is the second yes, so adding a narrow profile to the shipped settings does not
    // hand it every registered laptop. ADR-0028 has the alternatives and why they lose.
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ForSubAgent_TheOutpostsOptIn_IsTheParentsAndItsOwn(
        bool parent, bool ownDefinition, bool expected)
    {
        AgentSpecProjection.ForSubAgent(
                _subAgentDefinition with { UsesOutposts = ownDefinition },
                new SpawnContext("conv-1", "fran", ["allow-*"], UsesOutposts: parent),
                _openRouter, null)
            .UsesOutposts.ShouldBe(expected);
    }

    // The session id is what OpenRouter sticks a prompt cache to, so two spawns of the same
    // subagent definition must not land on one.
    [Fact]
    public void ForSubAgent_TwoSpawns_GetDistinctRoutingSessionIds()
    {
        SubAgentSpec().RoutingSessionId.ShouldNotBe(SubAgentSpec().RoutingSessionId);
    }

    private static object?[] Row(
        string field, Func<AgentSpec, object?> read, object? forAgent, object? forSubAgent) =>
        [field, read, forAgent, forSubAgent];

    private static string WithoutSpawnId(string routingSessionId) =>
        Regex.Replace(routingSessionId, "(?<=^subagent-[^:]{1,64}:)[0-9a-f]{32}$", "<spawn>");
}