using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Send a response chunk to a Telegram conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services,
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        // Telegram accepts the turn key and the agent-initiated flag and reads neither: one
        // conversation is one chat thread, and a late chunk arriving in it is a message like any
        // other. They are on the record so a channel that does care can start reading them.
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

        var registry = services.GetRequiredService<BotRegistry>();
        var accumulator = services.GetRequiredService<MessageAccumulator>();
        var (chatId, threadId) = TelegramConversation.Resolve(p.ConversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
                // Telegram doesn't show reasoning — ignore
                return "ok";

            case ReplyContentType.Error:
                await SendAccumulatedAsync(botClient, accumulator, p.ConversationId, chatId, threadId);
                await botClient.SendMessage(
                    chatId,
                    $"⚠️ {p.Content}",
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
                return "ok";

            case ReplyContentType.StreamComplete:
                await SendAccumulatedAsync(botClient, accumulator, p.ConversationId, chatId, threadId);
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);

                if (p.IsComplete)
                {
                    await SendAccumulatedAsync(botClient, accumulator, p.ConversationId, chatId, threadId);
                }

                return "ok";
        }
    }

    private static async Task SendAccumulatedAsync(
        ITelegramBotClient botClient,
        MessageAccumulator accumulator,
        string conversationId,
        long chatId,
        int? threadId)
    {
        var chunks = accumulator.Flush(conversationId);
        foreach (var chunk in chunks)
        {
            try
            {
                await botClient.SendMessage(
                    chatId,
                    chunk,
                    ParseMode.Markdown,
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
            }
            catch
            {
                // Markdown parse failure — retry as plain text
                await botClient.SendMessage(
                    chatId,
                    chunk,
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
            }
        }
    }
}