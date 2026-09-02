using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

// A long conversation switched to a Lemonade model still gets an answer: the turn is cut to the
// smaller of the agent's window and the window the box loaded the model with. Built through the
// factory, because the rule lives where the agent's window and the host's answer are both in reach.
public sealed class LemonadeTurnFitTests
{
    private const int AgentWindow = 1000;

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly CapturingSseHandler _openRouter = new();
    private readonly CapturingSseHandler _lemonade = new();

    private static readonly FixedLemonadeModelSource _models = new([
        new LemonadeModel("small", [], 80),
        new LemonadeModel("unknown", [], null),
        new LemonadeModel("large", [], 5000)
    ]);

    [Fact]
    public async Task ALemonadeTurn_IsCutToTheModelsWindowWhenItIsTheSmaller()
    {
        using var client = Client();

        await client.GetStreamingResponseAsync(History(400), Options("lemonade/small")).ToListAsync();

        var truncation = _metrics.Published.OfType<ContextTruncationEvent>().ShouldHaveSingleItem();
        truncation.MaxContextTokens.ShouldBe(80);
        truncation.Model.ShouldBe("lemonade/small");
    }

    [Fact]
    public async Task ALemonadeTurn_IsCutToTheAgentsWindowWhenThatIsTheSmaller()
    {
        using var client = Client();

        await client.GetStreamingResponseAsync(History(1500), Options("lemonade/large")).ToListAsync();

        _metrics.Published.OfType<ContextTruncationEvent>().ShouldHaveSingleItem()
            .MaxContextTokens.ShouldBe(AgentWindow);
    }

    [Fact]
    public async Task ALemonadeModelWithNoKnownWindow_UsesTheAgents()
    {
        using var client = Client();

        await client.GetStreamingResponseAsync(History(400), Options("lemonade/unknown")).ToListAsync();

        _metrics.Published.OfType<ContextTruncationEvent>().ShouldBeEmpty();
        _lemonade.CapturedBody.ShouldNotBeNull();
    }

    // On the dashboard a Lemonade turn is Lemonade's: the model dimension carries the namespaced
    // id, never the file path the box reports as its served model, and the cost is zero because
    // nothing was bought. What the box said about tokens is kept.
    [Fact]
    public async Task ALemonadeTurn_IsCountedUnderTheNamespacedIdAtZeroCost()
    {
        var box = new LemonadeResponsesSseHandler();
        using var client = Client(box);

        var updates = await client.GetStreamingResponseAsync(History(10), Options("lemonade/small")).ToListAsync();

        updates.ToChatResponse().Text.ShouldBe("ok");
        // The box's response id must not pose as a conversation to continue server-side: the
        // agent keeps its own history and refuses a turn that offers one.
        updates.ShouldAllBe(u => u.ConversationId == null);
        var usage = _metrics.Published.OfType<TokenUsageEvent>().ShouldHaveSingleItem();
        usage.Model.ShouldBe("lemonade/small");
        usage.Cost.ShouldBe(0m);
        usage.InputTokens.ShouldBe(18);
        usage.OutputTokens.ShouldBe(2);
        usage.Sender.ShouldBe("alice");
    }

    [Fact]
    public async Task AnOpenRouterTurn_KeepsTheAgentsWindow()
    {
        using var client = Client();

        await client.GetStreamingResponseAsync(History(400), Options("z-ai/glm-5.2")).ToListAsync();
        await client.GetStreamingResponseAsync(History(1500), Options("z-ai/glm-5.2")).ToListAsync();

        _metrics.Published.OfType<ContextTruncationEvent>().ShouldHaveSingleItem()
            .MaxContextTokens.ShouldBe(AgentWindow);
        _openRouter.CapturedBody.ShouldNotBeNull();
    }

    private IChatClient Client(HttpMessageHandler? lemonade = null)
    {
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ILemonadeModelSource))).Returns(_models);
        serviceProvider.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://box.test:13305/api/v1" });

        var factory = new MultiAgentFactory(
            serviceProvider.Object,
            Mock.Of<IAgentDefinitionProvider>(),
            new OpenRouterConfig { ApiUrl = "http://openrouter.test/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

        return factory.CreateChatClient(
            "configured/model", _metrics, AgentWindow,
            transportHandler: _openRouter, lemonadeTransportHandler: lemonade ?? _lemonade);
    }

    // Four characters to a token in the estimator, so this many tokens of history plus the last
    // short message the truncator keeps whatever happens.
    private static List<ChatMessage> History(int tokens)
    {
        var sender = new ChatMessage(ChatRole.User, "hi");
        sender.SetSenderId("alice");
        return
        [
            new ChatMessage(ChatRole.User, new string('a', tokens * 2)),
            new ChatMessage(ChatRole.Assistant, new string('b', tokens * 2)),
            sender
        ];
    }

    private static ChatOptions Options(string model) => new() { ModelId = model };
}