using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// The whitelist a patch is checked against is asked per turn, not copied at construction: part
// of it is discovered while the agent runs, and an agent built before a model appeared must still
// honour a patch naming it — and refuse one naming a model that has since gone.
public sealed class McpAgentPatchableModelSourceTests
{
    private sealed class LiveSource : IPatchableModelSource
    {
        public IReadOnlyList<string> Ids { get; set; } = [];
    }

    [Fact]
    public async Task AModelTheSourceOffersAfterTheAgentWasBuilt_IsHonoured()
    {
        var source = new LiveSource();
        var handler = new CapturingSseHandler();
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1", "test-key", "configured/model", transportHandler: handler);
        await using var agent = Agent(source, client, out _);

        source.Ids = ["z-ai/glm-5.2"];
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = "z-ai/glm-5.2" });

        await agent.RunStreamingAsync([message]).ToListAsync();

        JsonNode.Parse(handler.CapturedBody!)!["model"]!.GetValue<string>().ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public async Task AModelTheSourceNoLongerOffers_IsRefusedWithAWarningAndTheAgentsModelRuns()
    {
        var source = new LiveSource { Ids = ["z-ai/glm-5.2"] };
        var handler = new CapturingSseHandler();
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1", "test-key", "configured/model", transportHandler: handler);
        await using var agent = Agent(source, client, out var warnings);

        source.Ids = [];
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = "z-ai/glm-5.2" });

        await agent.RunStreamingAsync([message]).ToListAsync();

        JsonNode.Parse(handler.CapturedBody!)!["model"]!.GetValue<string>().ShouldBe("configured/model");
        warnings.ShouldContain(m => m.Contains("Rejected config patch model=z-ai/glm-5.2"));
    }

    private static McpAgent Agent(
        IPatchableModelSource source, IChatClient client, out IReadOnlyCollection<string> warnings)
    {
        var logProvider = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        warnings = logProvider.Messages;
        return new McpAgent(
            TestAgentSpec.Default with
            {
                UserId = "fran",
                Model = "configured/model",
                PatchableModels = source
            },
            client,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            [],
            LoggerFactory.Create(b => b.AddProvider(logProvider)));
    }
}