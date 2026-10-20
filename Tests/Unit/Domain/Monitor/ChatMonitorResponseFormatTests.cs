using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

// The format rides the turn's user message beside the config patch, which is the only thing that
// reaches the agent from a channel message; the agent reads it off that message alone.
public class ChatMonitorResponseFormatTests
{
    private static readonly JsonElement _schema = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"carbs":{"type":"number"}},"required":["carbs"],"additionalProperties":false}
        """);

    [Fact]
    public async Task AMessageWithAResponseFormat_StampsItOnTheUserMessage()
    {
        var format = new ResponseFormat { Name = "meal_estimate", Schema = _schema };
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "sugary", agentId: "sugary", sender: "app")
            with
        { ResponseFormat = format };

        var userMessage = await RunAsync(message);

        var stamped = userMessage.GetResponseFormat();
        stamped.ShouldNotBeNull();
        stamped.Name.ShouldBe("meal_estimate");
        JsonElement.DeepEquals(stamped.Schema, _schema).ShouldBeTrue();
    }

    [Fact]
    public async Task AMessageWithoutAResponseFormat_StampsNone()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "fran");

        var userMessage = await RunAsync(message);

        userMessage.GetResponseFormat().ShouldBeNull();
    }

    private static async Task<ChatMessage> RunAsync(global::Domain.DTOs.ChannelMessage message)
    {
        var channel = MonitorTestMocks.CreateChannel(message.ChannelId, message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        return messages!.ShouldHaveSingleItem();
    }
}