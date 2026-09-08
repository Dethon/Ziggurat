using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorConversationContextTests
{
    [Fact]
    public async Task Monitor_InteractiveMessage_StampsOriginContextOnUserMessage()
    {
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test");
        var signalr = MonitorTestMocks.CreateChannel("signalr", message);
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        var context = userMessage.GetConversationContext().ShouldNotBeNull();
        context.AgentId.ShouldBe("jonas");
        context.ConversationId.ShouldBe("conv-1");
        context.UserId.ShouldBe("test");
        context.Origin.ShouldBe(new ReplyTarget("signalr", "conv-1"));
    }

    [Fact]
    public void BuildConversationContext_UsesFirstDeliveryTarget()
    {
        var channel = new FakeChannelConnection { ChannelId = "telegram" };
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "fire-1", channelId: "scheduling", agentId: "jonas");
        var targets = new[] { new DeliveryTarget(channel, "t-9") };

        var context = DeliveryTargetResolver.BuildConversationContext(message, targets);

        context.ConversationId.ShouldBe("t-9");
        context.Origin.ShouldBe(new ReplyTarget("telegram", "t-9"));
    }

    [Fact]
    public void BuildConversationContext_NoTargets_FallsBackToMessageOrigin()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-2", channelId: "voice", agentId: "jonas") with
        { SatelliteId = "fran-office-01" };

        var context = DeliveryTargetResolver.BuildConversationContext(message, []);

        context.ConversationId.ShouldBe("conv-2");
        context.Origin.ShouldBe(new ReplyTarget("voice", "conv-2", "fran-office-01"));
    }

    // An unprompted fire is on behalf of the person its author named: the watch's or the schedule's
    // `userId` is the user the agent runs as, not the sender label the channel stamps on the fire.
    [Fact]
    public void BuildConversationContext_PrefersTheFiresUserIdOverItsSender()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "watch-1", channelId: "homeassistant", agentId: "jonas") with
        { Sender = "watch", UserId = "fran" };

        var context = DeliveryTargetResolver.BuildConversationContext(message, []);

        context.UserId.ShouldBe("fran");
    }

    [Fact]
    public void BuildConversationContext_WithoutAUserId_TheSenderIsTheUser()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas") with
        { Sender = "test", UserId = null };

        var context = DeliveryTargetResolver.BuildConversationContext(message, []);

        context.UserId.ShouldBe("test");
    }
}