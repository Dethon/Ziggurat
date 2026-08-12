using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class SubAgentTests(RedisFixture redisFixture)
    : IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<SubAgentTests>()
        .Build();

    private static OpenRouterConfig CreateOpenRouterConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterConfig { ApiUrl = apiUrl, ApiKey = apiKey };
    }

    private static MultiAgentFactory CreateFactory(OpenRouterConfig config)
    {
        var registryOptions = Options.Create(new AgentRegistryOptions { Agents = [] });
        var monitor = new OptionsMonitorStub<AgentRegistryOptions>(registryOptions.Value);
        var customAgentRegistry = new CustomAgentRegistry();
        var definitionProvider = new AgentDefinitionProvider(monitor, customAgentRegistry);
        var domainToolRegistry = new DomainToolRegistry([]);
        return new MultiAgentFactory(
            null!,
            definitionProvider,
            config,
            domainToolRegistry,
            null);
    }

    [SkippableFact]
    public async Task SubAgent_CompletesTask_ReturnsResult()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "echo-agent",
            Name = "Echo",
            Description = "Echoes back what you say",
            Model = "~deepseek/deepseek-v4-flash-latest:nitro",
            McpServerEndpoints = [],
            CustomInstructions = "You are a simple echo agent. Repeat back exactly what the user says, nothing more."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var factory = CreateFactory(openRouterConfig);
        var registryOptions = new SubAgentRegistryOptions { SubAgents = [subAgentDef] };

        var approvalHandler = new AutoApproveHandler();
        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => factory.CreateSubAgent(def, approvalHandler, "conv-1", ["domain__subagents__*"], "test-user"));

        var toolFeature = new SubAgentToolFeature(registryOptions);

        var llmClient = new OpenRouterChatClient(
            openRouterConfig.ApiUrl, openRouterConfig.ApiKey, "~deepseek/deepseek-v4-flash-latest:nitro");
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(5), TimeProvider.System);
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

        var responses = await LlmAttempt.WithinAsync(LlmAttempt.Budget, ct => agent
            .RunStreamingAsync(
                "Use the run_subagent tool with echo-agent to echo: 'Hello from subagent'.",
                cancellationToken: ct)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(ct)
            .AsTask());

        responses.ShouldNotBeEmpty();

        responses.ShouldContain(r => r.ToolCalls.Contains("run_subagent"),
            "The parent agent should have invoked the run_subagent tool");

        var combined = string.Join(" ", responses.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));
        combined.ShouldContain("Hello from subagent", Case.Insensitive);
    }

    [SkippableFact]
    public async Task SubAgent_EphemeralState_NoRedisKeys()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "test-ephemeral",
            Name = "TestEphemeral",
            Model = "~deepseek/deepseek-v4-flash-latest:nitro",
            McpServerEndpoints = [],
            CustomInstructions = "Reply with exactly the word 'done'."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var factory = CreateFactory(openRouterConfig);
        var approvalHandler = new AutoApproveHandler();

        var server = redisFixture.Connection.GetServer(redisFixture.Connection.GetEndPoints()[0]);
        var keysBefore = server.Keys(pattern: "*").ToList();

        await using var agent = factory.CreateSubAgent(subAgentDef, approvalHandler, "conv-1", [], "test-user");
        var userMessage = new ChatMessage(ChatRole.User, "Say done");
        var response = await LlmAttempt.WithinAsync(LlmAttempt.Budget, ct => agent
            .RunStreamingAsync([userMessage], cancellationToken: ct)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(ct)
            .AsTask());

        var result = string.Join("", response.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));

        var keysAfter = server.Keys(pattern: "*").ToList();
        keysAfter.Count.ShouldBe(keysBefore.Count,
            "SubAgent should use NullThreadStateStore and write no Redis keys");
        result.ShouldNotBeNullOrEmpty();
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
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