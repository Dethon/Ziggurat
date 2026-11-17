using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// A parent hands a task to a subagent and gets its answer back, both turns answered by the fake model.
public class SubAgentTests(RedisFixture redisFixture)
    : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    // What the parent would be running: a worker names no model of its own.
    private const string WorkerModel = "~deepseek/deepseek-v4-flash-latest:nitro";

    private OpenRouterConfig CreateOpenRouterConfig() => new() { ApiUrl = _model.ApiUrl, ApiKey = "sk-or-fake" };

    private static MultiAgentFactory CreateFactory(OpenRouterConfig config)
    {
        var customAgentRegistry = new CustomAgentRegistry();
        var definitionProvider = FixedHouseholdAgents.Provider(customAgentRegistry);
        var domainToolRegistry = new DomainToolRegistry([]);
        return new MultiAgentFactory(
            null!,
            definitionProvider,
            config,
            domainToolRegistry,
            null);
    }

    [Fact]
    public async Task SubAgent_CompletesTask_ReturnsResult()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "echo-agent",
            Name = "Echo",
            Description = "Echoes back what you say",
            McpServerEndpoints = [],
            CustomInstructions = "You are a simple echo agent. Repeat back exactly what the user says, nothing more."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var factory = CreateFactory(openRouterConfig);
        var registryOptions = new SubAgentRegistryOptions { SubAgents = [subAgentDef] };

        var approvalHandler = new AutoApproveHandler();
        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => factory.CreateSubAgent(
                def, approvalHandler,
                new SpawnContext("conv-1", "test-user", ["domain__subagents__*"], UsesOutposts: false, WorkerModel)));

        var toolFeature = new SubAgentToolFeature(registryOptions);

        using var llmClient = FakeTurn.Client(_model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(5) }, TimeProvider.System);
        using var effectiveClient = new ToolApprovalChatClient(llmClient, approvalHandler, "conv-test", ["domain__subagents__*"]);

        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "parent-agent-test",
                CustomInstructions =
                    "You have access to a subagent tool. Use the echo-agent subagent to echo back: 'Hello from subagent'"
            },
            effectiveClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            toolFeature.GetTools(featureConfig).ToList(),
            []);

        // The parent calls the subagent, the subagent answers its task, and the parent is handed that.
        _model.Script(
            FakeOpenRouter.CallingToolNamed(SubAgentRunTool.Name,
                """{"subAgentId":"echo-agent","prompt":"Echo: 'Hello from subagent'"}"""),
            _ => FakeOpenRouter.Saying("Hello from subagent"));

        var responses = await FakeTurn.RunAsync(agent, "Use the run_subagent tool with echo-agent to echo: 'Hello from subagent'.");

        responses.ShouldContain(r => r.ToolCalls.Contains("run_subagent"),
            "The parent agent should have invoked the run_subagent tool");
        _model.Requests[1].SystemPrompt.ShouldNotBeNull().ShouldContain("echo agent");
        _model.Requests[^1].ToolOutputs.ShouldHaveSingleItem().ShouldContain("Hello from subagent");
    }

    [Fact]
    public async Task SubAgent_EphemeralState_NoRedisKeys()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "test-ephemeral",
            Name = "TestEphemeral",
            McpServerEndpoints = [],
            CustomInstructions = "Reply with exactly the word 'done'."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var factory = CreateFactory(openRouterConfig);
        var approvalHandler = new AutoApproveHandler();

        var server = redisFixture.Connection.GetServer(redisFixture.Connection.GetEndPoints()[0]);
        var keysBefore = server.Keys(pattern: "*").ToList();

        await using var agent = factory.CreateSubAgent(
            subAgentDef, approvalHandler,
            new SpawnContext("conv-1", "test-user", [], UsesOutposts: false, WorkerModel));
        var userMessage = new ChatMessage(ChatRole.User, "Say done");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await agent
            .RunStreamingAsync([userMessage], cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);
        var result = string.Join("", response.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));

        var keysAfter = server.Keys(pattern: "*").ToList();
        keysAfter.Count.ShouldBe(keysBefore.Count,
            "SubAgent should use NullThreadStateStore and write no Redis keys");
        result.ShouldNotBeNullOrEmpty();
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(ApprovalAsk ask, CancellationToken cancellationToken)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

file sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}