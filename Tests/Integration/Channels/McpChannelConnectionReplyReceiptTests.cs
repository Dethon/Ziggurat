using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Infrastructure.Clients.Channels;
using Mcp.Hosting;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// What the chat hub answers send_reply with reaches the agent over the real hop: the hub's own tool, its
// answer read by the agent's connection. A turn's end answered with the turn keys a stop handed back is
// how the agent learns, before it starts its next turn, not to run a steer its sender has back.
public class McpChannelConnectionReplyReceiptTests
{
    private sealed record ProbeSettings(string Name) : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    // A stop handed back "steer-1" in conversation conv-1: a turn's end there is answered with it.
    private sealed class StoppedStreams : IStreamService
    {
        public Task WriteReplyAsync(SendReplyParams p) => Task.CompletedTask;

        public ReplyReceipt ReceiptFor(SendReplyParams p) =>
            p is { IsComplete: true, ConversationId: "conv-1" } ? new ReplyReceipt(["steer-1"]) : ReplyReceipt.None;
    }

    [Theory]
    [InlineData("conv-1", true, new[] { "steer-1" })]
    [InlineData("conv-1", false, new string[0])]
    [InlineData("conv-2", true, new string[0])]
    public async Task ATurnsEnd_IsAnsweredWithTheTurnKeysTheHubSays_AndAnythingElseWithNone(
        string conversation, bool isComplete, string[] returned)
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddSingleton<IStreamService, StoppedStreams>()
            .AddToolServer(new ProbeSettings("probe"))
            .WithTools<SendReplyTool>());
        await using var connection = new McpChannelConnection("signalr", mcpSecret: McpTestSecret.Value, callTokens: McpTestSecret.CallTokens);
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var receipt = await connection.SendReplyAsync(new SendReplyParams
        {
            ConversationId = conversation,
            Content = "",
            ContentType = isComplete ? ReplyContentType.StreamComplete : ReplyContentType.Text,
            IsComplete = isComplete
        }, CancellationToken.None);

        receipt.ReturnedTurnKeys.ShouldBe(returned);
    }
}