using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// The turn the agent runs carries the references and never the bytes. Reading a conversation's
// history therefore costs the same whether or not files were ever sent, and the bytes are put
// back only on the way to the model (ADR 0020).
public class ChatMonitorAttachmentTests
{
    private static readonly AttachmentReference _photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 5
    };

    [Fact]
    public async Task AMessageWithAttachments_PutsTheReferencesOnTheUserTurn()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "signalr", agentId: "jonas") with
        {
            Attachments = [_photo]
        };
        var agent = MonitorTestMocks.CreateAgent();

        await RunAsync(message, agent);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        userMessage.GetAttachments().ShouldBe([_photo]);

        // The channel that took the file is what the hydration step asks for it back, so the turn
        // has to name it.
        userMessage.GetAttachmentChannelId().ShouldBe("signalr");
    }

    [Fact]
    public async Task AMessageWithAttachments_PutsNoBytesOnTheUserTurn()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "signalr", agentId: "jonas") with
        {
            Attachments = [_photo]
        };
        var agent = MonitorTestMocks.CreateAgent();

        await RunAsync(message, agent);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.ShouldHaveSingleItem().Contents.OfType<DataContent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task AMessageWithNoAttachments_LeavesTheUserTurnUnmarked()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "signalr", agentId: "jonas");
        var agent = MonitorTestMocks.CreateAgent();

        await RunAsync(message, agent);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        messages!.ShouldHaveSingleItem().GetAttachments().ShouldBeNull();
    }

    private static async Task RunAsync(ChannelMessage message, FakeAiAgent agent)
    {
        var channel = MonitorTestMocks.CreateChannel(message.ChannelId, message);
        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);
    }
}