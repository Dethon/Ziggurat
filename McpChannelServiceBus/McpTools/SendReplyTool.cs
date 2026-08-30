using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelServiceBus.Services;
using ModelContextProtocol.Server;

namespace McpChannelServiceBus.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Send a response chunk to a Service Bus conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID (correlationId)")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services,
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        // Accepted and ignored: a broker correlation id already names exactly one request, so there
        // is nothing here for a turn key to disambiguate.
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

        var accumulator = services.GetRequiredService<MessageAccumulator>();
        var responseSender = services.GetRequiredService<ResponseSender>();

        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
                // ServiceBus doesn't need intermediate updates
                return "ok";

            case ReplyContentType.Error:
                var errorContent = accumulator.Flush(p.ConversationId);
                var errorMessage = string.IsNullOrEmpty(errorContent)
                    ? p.Content
                    : $"{errorContent}\n\nError: {p.Content}";
                await responseSender.SendResponseAsync(p.ConversationId, errorMessage);
                return "ok";

            case ReplyContentType.StreamComplete:
                var accumulated = accumulator.Flush(p.ConversationId);
                if (!string.IsNullOrEmpty(accumulated))
                {
                    await responseSender.SendResponseAsync(p.ConversationId, accumulated);
                }
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);

                if (p.IsComplete)
                {
                    var flushed = accumulator.Flush(p.ConversationId);
                    if (!string.IsNullOrEmpty(flushed))
                    {
                        await responseSender.SendResponseAsync(p.ConversationId, flushed);
                    }
                }

                return "ok";
        }
    }
}