using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Send a response chunk to a WebChat conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks into bubbles")] string? messageId,
        IServiceProvider services,
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        // WebChat's live stream is keyed per topic, not per turn; the stream service reads the turn key
        // only to tell a steer's own answer (a refusal as it arrived) from the turn it was sent into.
        var p = new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        };

        var streamService = services.GetRequiredService<IStreamService>();
        await streamService.WriteReplyAsync(p);
        return streamService.ReceiptFor(p).ToWire();
    }
}