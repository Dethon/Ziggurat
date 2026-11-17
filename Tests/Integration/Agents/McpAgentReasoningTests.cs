using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Agents;

// A turn's reasoning setting, read on the wire the fake model received and on what the agent streams
// back from it: the fake streams a reasoning summary only to a request that asks for reasoning with a
// summary, as an untranslated OpenAI endpoint does, so an agent that dropped either half streams none.
public class McpAgentReasoningTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private FakeOpenRouter _model = null!;

    public async Task InitializeAsync() => _model = await FakeOpenRouter.StartAsync();

    public async Task DisposeAsync() => await _model.DisposeAsync();

    private async Task<string> ReasoningStreamedAsync(string? effort)
    {
        using var openRouter = FakeTurn.Client(_model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(10) }, TimeProvider.System);
        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "reasoning-agent",
                UserId = $"reasoning-test-user-{Guid.NewGuid():N}",
                ReasoningEffort = effort
            },
            openRouter,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync(
                           "Compare 9.11 and 9.9. Which is larger? Show your reasoning.", cancellationToken: cts.Token))
        {
            chunks.AddRange(update.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
        }

        return string.Concat(chunks);
    }

    private JsonNode? ReasoningSent => _model.Requests.ShouldHaveSingleItem().Body["reasoning"];

    [Fact]
    public async Task Agent_WithReasoningEffortConfigured_StreamsReasoningContent()
    {
        var reasoning = await ReasoningStreamedAsync("low");

        ReasoningSent?["effort"]?.GetValue<string>().ShouldBe("low");
        reasoning.ShouldNotBeNullOrWhiteSpace(
            "McpAgent should send reasoningEffort='low' with a summary request and stream the reasoning back.");
    }

    [Fact]
    public async Task Agent_WithReasoningEffortConfigured_AsksForTheSummaryOnTheWire()
    {
        await ReasoningStreamedAsync("medium");

        ReasoningSent?["summary"].ShouldNotBeNull(
            "An OpenAI model on the Responses wire returns readable reasoning only when the request " +
            "asks for a summary; McpAgent should ask for one whenever reasoning is enabled.");
    }

    [Fact]
    public async Task Agent_WithReasoningEffortNone_StreamsNoReasoningContent()
    {
        var reasoning = await ReasoningStreamedAsync("none");

        reasoning.ShouldBeNullOrWhiteSpace("McpAgent with reasoningEffort='none' should suppress reasoning tokens.");
        ReasoningSent?["summary"].ShouldBeNull();
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
                PatchableModels = new FixedPatchableModelSource(_whitelist)
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
    public async Task RunStreaming_WithReasoningEffort_AsksForTheReasoningSummary()
    {
        var (options, warnings) = await RunWithPatchAsync(null, reasoningEffort: "low");

        options.Reasoning.ShouldNotBeNull();
        options.Reasoning.Output.ShouldBe(ReasoningOutput.Summary);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_WithReasoningEffortNone_AsksForNoSummary()
    {
        var (options, warnings) = await RunWithPatchAsync(null, reasoningEffort: "none");

        options.Reasoning.ShouldNotBeNull();
        options.Reasoning.Output.ShouldBeNull();
        warnings.ShouldBeEmpty();
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