using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// Everything below the constructor is otherwise invisible to tests: MultiAgentFactoryTests
// replace the whole client, OpenRouterHttpHelpersTests call the static helper directly, and
// the metrics tests use the inner-IChatClient ctor that never builds the HTTP pipeline. This
// is the only test that proves the ctor's providerRouting/sessionId survive the
// CreateHttpClient -> ReasoningHandler hop onto an actual outgoing request.
public sealed class OpenRouterChatClientTests
{
    // The span this feature exists to create: a real agent over a real chat client, so
    // deleting the line that puts the resolved patch on the turn's options fails here.
    // An assertion on the options alone would pass while the wire stayed wrong.
    [Fact]
    public async Task RunStreaming_UserMessageWithPatchedModel_ReachesTheOutgoingRequestBody()
    {
        var handler = new CapturingSseHandler();
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1",
            "test-key",
            "configured/model",
            providerRouting: new ProviderRouting { Sort = ProviderSort.Latency },
            transportHandler: handler);
        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                UserId = "fran",
                Model = "configured/model",
                PatchableModels = new FixedPatchableModelSource(["z-ai/glm-5.2"])
            },
            client,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = "z-ai/glm-5.2" });

        await agent.RunStreamingAsync([message]).ToListAsync();

        var body = JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("z-ai/glm-5.2");
        // Routing is a deployment constraint that applies to override turns too.
        body["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
    }

    [Fact]
    public async Task RunStreaming_WithoutPatch_KeepsTheConfiguredModelInTheRequestBody()
    {
        var handler = new CapturingSseHandler();
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1", "test-key", "configured/model", transportHandler: handler);
        await using var agent = new McpAgent(
            TestAgentSpec.Default with
            {
                UserId = "fran",
                Model = "configured/model",
                PatchableModels = new FixedPatchableModelSource(["z-ai/glm-5.2"])
            },
            client,
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var body = JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("configured/model");
    }
}