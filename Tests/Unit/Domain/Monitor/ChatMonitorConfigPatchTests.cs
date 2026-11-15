using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorConfigPatchTests
{
    [Fact]
    public async Task Monitor_MessageWithConfigPatch_StampsPatchOnUserMessage()
    {
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var patch = new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" };
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test")
            with
        { ConfigPatch = patch };
        var signalr = MonitorTestMocks.CreateChannel("signalr", message);
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        userMessage.GetConfigPatch().ShouldBe(patch);
    }

    // A reroll answers the message the conversation already ends on, so its turn carries that message
    // under its own entry id (RedisChatMessageStore reads and keeps it once).
    [Fact]
    public async Task Monitor_ARerun_CarriesTheMessagesEntryId()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test")
            with
        { RerunOf = "entry-7" };
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel("signalr", message)],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.ShouldHaveSingleItem().GetEntryId().ShouldBe("entry-7");
    }

    // Continue's hidden ask is marked as one on the turn's message, so it is never drawn, counted or
    // remembered.
    [Fact]
    public async Task Monitor_AContinuation_IsMarkedOnItsMessage()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test")
            with
        { Continuation = true };
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel("signalr", message)],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.ShouldHaveSingleItem().IsContinuation().ShouldBeTrue();
    }
}