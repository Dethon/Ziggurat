using System.Net;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// One chat client per agent, two hosts behind it. Which host a turn goes to is decided by the
// shape of the model the turn resolved to, and nothing else about the turn — tools, history,
// effort — changes with the decision. Asserted on the bytes that reach each stubbed host.
public sealed class HostRoutingChatClientTests
{
    private const string LemonadeAddress = "http://box.test:13305/api/v1";
    private const string LemonadeModel = "lemonade/Qwen3.8-27B-GGUF-UD-Q4_K_XL";

    [Fact]
    public async Task ALemonadeOverride_ReachesTheHostWithTheBareIdTheToolsAndTheEffort()
    {
        var openRouter = new CapturingSseHandler();
        var lemonade = new CapturingSseHandler();
        await using var agent = Agent(Routing(openRouter, lemonade, apiKey: "box-key"));

        await agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync();

        lemonade.CapturedUri!.ToString().ShouldBe($"{LemonadeAddress}/responses");
        lemonade.CapturedAuthorization.ShouldBe("Bearer box-key");
        var body = JsonNode.Parse(lemonade.CapturedBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("Qwen3.8-27B-GGUF-UD-Q4_K_XL");
        body["reasoning"]!["effort"]!.GetValue<string>().ShouldBe("high");
        body["tools"]!.AsArray().ShouldContain(t => t!["name"]!.GetValue<string>() == "ping");
        openRouter.CapturedBody.ShouldBeNull();
    }

    // "none" is an effort the box honours by not thinking at all, so it has to reach it as itself
    // rather than as an absent field.
    [Fact]
    public async Task AnEffortOfNone_ReachesTheHostAsNone()
    {
        var lemonade = new CapturingSseHandler();
        await using var agent = Agent(Routing(new CapturingSseHandler(), lemonade));

        var message = Patched(LemonadeModel);
        message.SetConfigPatch(new AgentConfigPatch { Model = LemonadeModel, ReasoningEffort = "none" });
        await agent.RunStreamingAsync([message]).ToListAsync();

        JsonNode.Parse(lemonade.CapturedBody!)!["reasoning"]!["effort"]!.GetValue<string>().ShouldBe("none");
    }

    [Fact]
    public async Task ALemonadeTurn_StampsItsLatencyUnderTheNamespacedId()
    {
        var metrics = new RecordingMetricsPublisher();
        await using var agent = Agent(Routing(new CapturingSseHandler(), new CapturingSseHandler()), metrics);

        await agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync();

        metrics.Published.OfType<LatencyEvent>().Where(e => e.Stage is LatencyStage.LlmTotal or LatencyStage.LlmFirstToken)
            .ShouldAllBe(e => e.Model == LemonadeModel);
    }

    // The routing client is transparent for an OpenRouter turn: the bytes it sends are the bytes
    // a bare OpenRouter client sends for the same turn at the same moment.
    [Fact]
    public async Task AnOpenRouterTurn_SendsByteForByteWhatTheBareClientSends()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        var routed = new CapturingSseHandler();
        var bare = new CapturingSseHandler();
        using var routing = new HostRoutingChatClient(
            OpenRouter(routed, clock),
            new LemonadeChatClient(new LemonadeChatHostOptions { ApiUrl = LemonadeAddress }));
        using var plain = OpenRouter(bare, clock);
        var turn = new ChatMessage(ChatRole.User, "hi");
        var options = new ChatOptions { ModelId = "z-ai/glm-5.2", Instructions = "be brief" };

        await routing.GetStreamingResponseAsync([turn], options).ToListAsync();
        await plain.GetStreamingResponseAsync([turn], options).ToListAsync();

        routed.CapturedBody.ShouldBe(bare.CapturedBody);
        routed.CapturedUri.ShouldBe(bare.CapturedUri);
    }

    [Fact]
    public async Task WithoutAKey_NoAuthorizationReachesTheHost()
    {
        var lemonade = new CapturingSseHandler();
        await using var agent = Agent(Routing(new CapturingSseHandler(), lemonade, apiKey: ""));

        await agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync();

        lemonade.CapturedAuthorization.ShouldBeNull();
    }

    [Fact]
    public async Task AnOpenRouterOverride_ReachesOpenRouterExactlyAsBefore()
    {
        var openRouter = new CapturingSseHandler();
        var lemonade = new CapturingSseHandler();
        await using var agent = Agent(Routing(openRouter, lemonade));

        await agent.RunStreamingAsync([Patched("z-ai/glm-5.2")]).ToListAsync();

        openRouter.CapturedUri!.ToString().ShouldStartWith("http://openrouter.test/api/v1/");
        var body = JsonNode.Parse(openRouter.CapturedBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("z-ai/glm-5.2");
        body["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
        lemonade.CapturedBody.ShouldBeNull();
    }

    [Fact]
    public async Task NoOverride_ReachesOpenRouterWithTheAgentsModel()
    {
        var openRouter = new CapturingSseHandler();
        var lemonade = new CapturingSseHandler();
        await using var agent = Agent(Routing(openRouter, lemonade));

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        JsonNode.Parse(openRouter.CapturedBody!)!["model"]!.GetValue<string>().ShouldBe("configured/model");
        lemonade.CapturedBody.ShouldBeNull();
    }

    [Fact]
    public async Task AHostThatRefusesTheConnection_FailsTheTurnByNameAndNothingAnswersInstead()
    {
        var openRouter = new CapturingSseHandler();
        var lemonade = new ScriptedHandler(_ => throw new HttpRequestException("Connection refused (box.test:13305)"));
        await using var agent = Agent(Routing(openRouter, lemonade));

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync().AsTask());

        error.Message.ShouldContain("Lemonade chat host");
        error.Message.ShouldContain(LemonadeAddress);
        TransientErrorFilter.IsTransientErrorMessage(error.Message).ShouldBeFalse();
        openRouter.CapturedBody.ShouldBeNull();
    }

    // The box has one LLM slot and answers a second request by closing the connection or with an
    // error; either way that turn fails once, and it is not sent again to the box or anywhere.
    [Fact]
    public async Task AHostThatAnswersAnError_FailsTheTurnOnceWithoutARetry()
    {
        var lemonade = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("model slot busy")
        });
        await using var agent = Agent(Routing(new CapturingSseHandler(), lemonade));

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync().AsTask());

        error.Message.ShouldContain("503");
        lemonade.Requests.ShouldBe(1);
    }

    // A connection the box drops is the kind of failure the SDK retries by default, and a retry
    // is a second turn sent to a box that just said no. One request, then the named error.
    [Fact]
    public async Task AConnectionTheHostDrops_IsNotRetried()
    {
        var lemonade = new ScriptedHandler(_ => throw new IOException("Connection reset by peer"));
        await using var agent = Agent(Routing(new CapturingSseHandler(), lemonade));

        await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync().AsTask());

        lemonade.Requests.ShouldBe(1);
    }

    // A timeout arrives as a cancellation, and WebChat's transient filter swallows the wording a
    // cancellation carries — so the named error must not carry it, or the person sees nothing.
    [Fact]
    public async Task AHostThatTimesOut_FailsWithWordingTheTransientFilterDoesNotSwallow()
    {
        var lemonade = new ScriptedHandler(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        await using var agent = Agent(Routing(new CapturingSseHandler(), lemonade));

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([Patched(LemonadeModel)]).ToListAsync().AsTask());

        TransientErrorFilter.IsTransientErrorMessage(error.Message).ShouldBeFalse();
        error.Message.ShouldContain(LemonadeAddress);
    }

    private static HostRoutingChatClient Routing(
        HttpMessageHandler openRouter, HttpMessageHandler lemonade, string? apiKey = null) =>
        new(
            OpenRouter(openRouter),
            new LemonadeChatClient(
                new LemonadeChatHostOptions { ApiUrl = LemonadeAddress, ApiKey = apiKey },
                transportHandler: lemonade));

    private static OpenRouterChatClient OpenRouter(HttpMessageHandler handler, TimeProvider? clock = null) =>
        new(
            "http://openrouter.test/api/v1", "or-key", "configured/model",
            sessionId: "session-1",
            timeProvider: clock,
            providerRouting: new ProviderRouting { Sort = ProviderSort.Latency },
            transportHandler: handler);

    private static McpAgent Agent(IChatClient client, IMetricsPublisher? metrics = null) =>
        new(
            TestAgentSpec.Default with
            {
                UserId = "fran",
                Model = "configured/model",
                ReasoningEffort = "high",
                PatchableModels = new FixedPatchableModelSource(["z-ai/glm-5.2", LemonadeModel])
            },
            client,
            new Mock<IThreadStateStore>().Object,
            metrics ?? NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [AIFunctionFactory.Create(() => "pong", "ping")],
            []);

    private static ChatMessage Patched(string model)
    {
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = model });
        return message;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}