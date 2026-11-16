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

    // A household's own server keeps its files, and another household's server of the same name keeps
    // others: the turn names whose connection took them, which the hydration asks.
    [Fact]
    public async Task AMessageThroughAHouseholdsOwnServer_NamesThatHouseholdForItsAttachments()
    {
        var lucia = new Account("casa", "0123456789abcdef0123456789abcdef");
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "voice", agentId: "jonas") with
        {
            Attachments = [_photo],
            Principal = new Principal("voice", lucia.UserId, lucia)
        };
        var agent = MonitorTestMocks.CreateAgent();
        var channel = new FakeChannelConnection { ChannelId = "voice", Household = ChannelHousehold.Own("casa", home: false) };
        channel.WriteMessage(message);
        channel.Complete();

        await RunAsync(channel, agent);

        agent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        userMessage.GetAttachmentChannelId().ShouldBe("voice");
        userMessage.GetAttachmentHouseholdId().ShouldBe("casa");
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

    private static Task RunAsync(ChannelMessage message, FakeAiAgent agent) =>
        RunAsync(MonitorTestMocks.CreateChannel(message.ChannelId, message), agent);

    private static async Task RunAsync(FakeChannelConnection channel, FakeAiAgent agent)
    {
        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object,
            TestAssertions.Verifier());

        await monitor.Monitor(CancellationToken.None);
    }
}