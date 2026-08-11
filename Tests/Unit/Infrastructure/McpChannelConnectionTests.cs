using System.Text.Json;
using Infrastructure.Clients.Channels;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpChannelConnectionTests
{
    [Fact]
    public async Task HandleNotification_WritesChannelMessage()
    {
        var sut = new McpChannelConnection("ch-1");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-42",
            content = "Hello from MCP",
            sender = "user@test",
            agentId = "agent-7"
        });

        sut.HandleChannelMessageNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var messages = sut.Messages;
        await foreach (var msg in messages.WithCancellation(cts.Token))
        {
            msg.ConversationId.ShouldBe("conv-42");
            msg.Content.ShouldBe("Hello from MCP");
            msg.Sender.ShouldBe("user@test");
            msg.ChannelId.ShouldBe("ch-1");
            msg.AgentId.ShouldBe("agent-7");
            break;
        }
    }

    [Fact]
    public async Task HandleCancelNotification_WritesSyntheticCancelMessage()
    {
        var sut = new McpChannelConnection("ch-3");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-77",
            agentId = "agent-9"
        });

        sut.HandleChannelCancelNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in sut.Messages.WithCancellation(cts.Token))
        {
            msg.ConversationId.ShouldBe("conv-77");
            msg.Content.ShouldBe("/cancel");
            msg.ChannelId.ShouldBe("ch-3");
            msg.AgentId.ShouldBe("agent-9");
            break;
        }
    }
}