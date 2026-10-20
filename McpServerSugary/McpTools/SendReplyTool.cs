using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerSugary.Channel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpServerSugary.McpTools;

// Where the sugary agent's reply comes back. Nothing is shown to anybody chunk by chunk: the app is
// waiting on one HTTP request, so the chunks are collected for the turn they answer and the whole
// reply is handed over when the turn ends.
[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Send a reply chunk to the Sugary app's waiting request")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services,
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        services.GetRequiredService<AgentTurns>().Receive(new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        });
        return "ok";
    }
}