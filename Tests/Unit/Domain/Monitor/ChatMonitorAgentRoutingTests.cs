using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// The agent a message is for is resolved once, before the message is grouped, so the group key,
// the agent built for it and the context stamped on the turn all name the same one.
public class ChatMonitorAgentRoutingTests
{
    private static readonly AgentDefaults _defaults = new()
    {
        Fallback = "jonas",
        ByChannel = new Dictionary<string, string> { ["voice"] = "nabu" }
    };

    [Theory]
    [InlineData("voice", null, "nabu")]
    [InlineData("telegram", null, "jonas")]
    [InlineData("voice", "jack", "jack")]
    public async Task ChannelWithoutAnAgentId_BuildsTheChannelsDefaultAgent(
        string channelId, string? agentId, string expected)
    {
        var message = MonitorTestMocks.CreateChannelMessage(channelId: channelId, agentId: agentId);
        var channel = MonitorTestMocks.CreateChannel(channelId, message);
        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [channel],
            factory,
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier(),
            _defaults);

        await monitor.Monitor(CancellationToken.None);

        var created = factory.Created.ShouldHaveSingleItem();
        created.AgentId.ShouldBe(expected);
        // The same id keyed the group, so a defaulted message and one naming the agent
        // explicitly share a conversation instead of running two agents over it.
        created.Key.AgentId.ShouldBe(expected);
    }

    // A chat-client turn's custom agent is filed under the person's account, while its sender is
    // only the name the account goes by: the agent is built for the account the turn acts for, and
    // the sender still names the person to everything keyed by it.
    [Fact]
    public async Task AChatClientTurn_BuildsItsAgentForTheAccountItActsFor()
    {
        var account = new Account("home", "acct-1");
        var message = MonitorTestMocks.CreateChannelMessage(channelId: "signalr", agentId: "custom-1") with
        {
            Sender = "Fran",
            Principal = new Principal("signalr", "acct-1", account)
        };
        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel("signalr", message)],
            factory,
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier(),
            _defaults);

        await monitor.Monitor(CancellationToken.None);

        var created = factory.Created.ShouldHaveSingleItem();
        created.Account.ShouldBe(account);
        created.UserId.ShouldBe("Fran");
    }

    // The defaults reach the monitor through the container, and the parameter carrying them has a
    // default value — so a registration that never happened resolves silently to "no defaults" and
    // every unattributed message starts failing instead of routing. Resolved the way the host
    // resolves it.
    [Fact]
    public async Task ResolvedFromTheContainer_TheMonitor_RoutesWithTheRegisteredDefaults()
    {
        var message = MonitorTestMocks.CreateChannelMessage(channelId: "telegram", agentId: null);
        var factory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());
        var recallHook = new Mock<IMemoryRecallHook>();
        recallHook
            .Setup(h => h.EnrichAsync(
                It.IsAny<ChatMessage>(), It.IsAny<Account?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<AgentSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IReadOnlyList<IChannelConnection>>(
                [MonitorTestMocks.CreateChannel("telegram", message)])
            .AddSingleton<IAgentFactory>(factory)
            .AddSingleton(MonitorTestMocks.CreateThreadResolver())
            .AddSingleton(new Mock<IMetricsPublisher>().Object)
            .AddSingleton(recallHook.Object)
            .AddSingleton(_defaults)
            .AddSingleton(TestAssertions.Verifier())
            .AddSingleton<ChatMonitor>()
            .BuildServiceProvider();

        await provider.GetRequiredService<ChatMonitor>().Monitor(CancellationToken.None);

        factory.Created.ShouldHaveSingleItem().AgentId.ShouldBe("jonas");
    }
}