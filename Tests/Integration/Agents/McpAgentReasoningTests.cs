using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class McpAgentReasoningTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentReasoningTests>()
        .Build();

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["openRouter:reasoningModel"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    [SkippableFact]
    public async Task Agent_WithReasoningEffortConfigured_StreamsReasoningContent()
    {
        // Drives a real OpenRouter call through McpAgent with reasoningEffort = "low"
        // and asserts that the model returns reasoning content — proves the per-agent
        // reasoning configuration actually reaches the wire and is honored end-to-end.
        var (apiUrl, apiKey, model) = GetConfig();

        using var openRouter = new OpenRouterChatClient(apiUrl, apiKey, model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));

        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "reasoning-agent",
                UserId = "reasoning-test-user",
                ReasoningEffort = "low"
            },
            openRouter,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

        var reasoning = await LlmAttempt.WithinAsync(LlmAttempt.Budget, async ct =>
        {
            var reasoningChunks = new List<string>();
            await foreach (var update in agent.RunStreamingAsync(
                "Compare 9.11 and 9.9. Which is larger? Show your reasoning.",
                cancellationToken: ct))
            {
                foreach (var content in update.Contents.OfType<TextReasoningContent>())
                {
                    reasoningChunks.Add(content.Text);
                }
            }

            return string.Concat(reasoningChunks);
        });

        reasoning.ShouldNotBeNullOrWhiteSpace(
            "McpAgent should propagate reasoningEffort='low' to OpenRouter so the provider streams reasoning tokens back.");
    }

    [SkippableFact]
    public async Task Agent_WithReasoningEffortNone_StreamsNoReasoningContent()
    {
        // With reasoningEffort = "none", the provider should NOT stream reasoning tokens —
        // proves that effort=none disables reasoning end-to-end (not just any non-null value
        // forces it on).
        var (apiUrl, apiKey, model) = GetConfig();

        using var openRouter = new OpenRouterChatClient(apiUrl, apiKey, model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));

        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "no-effort-agent",
                UserId = "no-effort-test-user",
                ReasoningEffort = "none"
            },
            openRouter,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

        var reasoning = await LlmAttempt.WithinAsync(LlmAttempt.Budget, async ct =>
        {
            var reasoningChunks = new List<string>();
            await foreach (var update in agent.RunStreamingAsync(
                "Compare 9.11 and 9.9. Which is larger? Show your reasoning.",
                cancellationToken: ct))
            {
                foreach (var content in update.Contents.OfType<TextReasoningContent>())
                {
                    reasoningChunks.Add(content.Text);
                }
            }

            return string.Concat(reasoningChunks);
        });

        reasoning.ShouldBeNullOrWhiteSpace(
            "McpAgent with reasoningEffort='none' should suppress reasoning tokens.");
    }

}

// Runs without Docker: a fake IChatClient captures the ChatOptions McpAgent builds, so both
// halves of the ConfigPatch — reasoning effort and model — can be asserted on what the agent
// actually produces, without a live OpenRouter call or a Redis-backed IThreadStateStore.
public class McpAgentReasoningTestsConfigPatch
{
    private const string ConfiguredModel = "openai/gpt-5.6-luna";
    private static readonly string[] _whitelist = ["openai/gpt-5.6-luna", "z-ai/glm-5.2"];

    private static (McpAgent Agent, List<ChatOptions?> Captured, IReadOnlyCollection<string> Warnings) CreateAgent(
        string? reasoningEffort = null)
    {
        var captured = new List<ChatOptions?>();
        var logProvider = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (_, options, _) => captured.Add(options))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());

        var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                UserId = "fran",
                Model = ConfiguredModel,
                ReasoningEffort = reasoningEffort,
                PatchableModelIds = _whitelist
            },
            chatClient.Object,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            LoggerFactory.Create(b => b.AddProvider(logProvider)));

        return (agent, captured, logProvider.Messages);
    }

    private static async Task<(ChatOptions Options, IReadOnlyCollection<string> Warnings)> RunWithPatchAsync(
        AgentConfigPatch? patch, string? reasoningEffort = null)
    {
        var (agent, captured, warnings) = CreateAgent(reasoningEffort);
        await using var _ = agent;

        var userMessage = new ChatMessage(ChatRole.User, "hi");
        if (patch is not null)
        {
            userMessage.SetConfigPatch(patch);
        }

        await agent.RunStreamingAsync([userMessage]).ToListAsync();

        return (captured.ShouldHaveSingleItem().ShouldNotBeNull(), warnings);
    }

    [Fact]
    public async Task RunStreaming_UserMessageWithEffortPatch_OverridesConfiguredEffort()
    {
        var (options, warnings) = await RunWithPatchAsync(
            new AgentConfigPatch { ReasoningEffort = "high" }, reasoningEffort: "low");

        options.Reasoning.ShouldNotBeNull();
        options.Reasoning.Effort.ShouldBe(ReasoningEffort.High);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_UserMessageWithInvalidEffortPatch_FallsBackToConfiguredAndWarns()
    {
        var (options, warnings) = await RunWithPatchAsync(
            new AgentConfigPatch { ReasoningEffort = "turbo" }, reasoningEffort: "low");

        options.Reasoning.ShouldNotBeNull();
        options.Reasoning.Effort.ShouldBe(ReasoningEffort.Low);
        warnings.ShouldContain(m => m.Contains("reasoningEffort") && m.Contains("turbo") && m.Contains("Low"));
    }

    [Fact]
    public async Task RunStreaming_WhitelistedModelPatch_PutsItOnTheTurnOptions()
    {
        var (options, warnings) = await RunWithPatchAsync(new AgentConfigPatch { Model = "z-ai/glm-5.2" });

        options.ModelId.ShouldBe("z-ai/glm-5.2");
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_NonWhitelistedModelPatch_KeepsConfiguredModelAndWarns()
    {
        var (options, warnings) = await RunWithPatchAsync(new AgentConfigPatch { Model = "evil/model" });

        options.ModelId.ShouldBeNull();
        warnings.ShouldContain(m => m.Contains("model") && m.Contains("evil/model") && m.Contains(ConfiguredModel));
    }

    [Fact]
    public async Task RunStreaming_ModelPatchMatchingConfiguredModel_IsNotAnOverride()
    {
        var (options, warnings) = await RunWithPatchAsync(new AgentConfigPatch { Model = ConfiguredModel });

        options.ModelId.ShouldBeNull();
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_WithoutPatch_LeavesTheModelUnset()
    {
        var (options, warnings) = await RunWithPatchAsync(null);

        options.ModelId.ShouldBeNull();
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_ModelPatchInDifferentCasing_UsesTheWhitelistCanonicalCasing()
    {
        // Provider model ids are lowercase slugs; echoing the caller's casing can turn a valid
        // override into a model-not-found error.
        var (options, warnings) = await RunWithPatchAsync(new AgentConfigPatch { Model = "Z-AI/GLM-5.2" });

        options.ModelId.ShouldBe("z-ai/glm-5.2");
        warnings.ShouldBeEmpty();
    }

    // A caller supplying its own options skips the agent's instructions, tools, reasoning effort
    // and config patch. The capability stays; the silence does not.
    [Fact]
    public async Task RunStreaming_WithCallerSuppliedOptions_RunsThemAndWarns()
    {
        var (agent, captured, warnings) = CreateAgent();
        await using var _ = agent;

        var supplied = new ChatClientAgentRunOptions(new ChatOptions { ModelId = "caller/model" });
        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")], options: supplied).ToListAsync();

        captured.ShouldHaveSingleItem().ShouldNotBeNull().ModelId.ShouldBe("caller/model");
        warnings.ShouldContain(m => m.Contains("AgentRunOptions"));
    }
}