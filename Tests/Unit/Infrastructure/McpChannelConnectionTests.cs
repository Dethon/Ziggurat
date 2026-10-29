using System.Text.Json;
using Domain.DTOs.Channel;
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

    // A fire's stamped principal crosses the wire with it: it is the only evidence of who set the
    // fire going, and a message that dropped it would run as nobody.
    [Fact]
    public async Task HandleNotification_CarriesTheStampedPrincipal()
    {
        var sut = new McpChannelConnection("scheduling");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "sched-1",
            content = "check my orders",
            sender = "scheduler",
            principal = new { channelId = "telegram", userId = "FranTG" }
        });

        sut.HandleChannelMessageNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in sut.Messages.WithCancellation(cts.Token))
        {
            msg.Principal.ShouldBe(new Principal("telegram", "FranTG"));
            break;
        }
    }

    // A chat-client turn names the person's account beside the channel's own user id, and the
    // account is what everything keyed by a person reads once it moves over, so it must survive the
    // hop as the channel spelled it.
    [Fact]
    public async Task HandleNotification_CarriesThePrincipalsAccount()
    {
        var sut = new McpChannelConnection("signalr");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "7:42",
            content = "hello",
            sender = "Fran",
            principal = new
            {
                channelId = "signalr",
                userId = "0123456789abcdef0123456789abcdef",
                account = new { tenantId = "fedcba9876543210fedcba9876543210", userId = "0123456789abcdef0123456789abcdef" }
            }
        });

        sut.HandleChannelMessageNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in sut.Messages.WithCancellation(cts.Token))
        {
            msg.Principal.ShouldBe(new Principal(
                "signalr",
                "0123456789abcdef0123456789abcdef",
                new Account("fedcba9876543210fedcba9876543210", "0123456789abcdef0123456789abcdef")));
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