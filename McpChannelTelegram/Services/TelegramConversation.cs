using Domain.Conversations;

namespace McpChannelTelegram.Services;

// The Telegram projection of the conversation identity: the type owns the spelling and the
// non-forum rule; only the narrowing to the Bot API's int thread id lives here.
public static class TelegramConversation
{
    public static (long ChatId, int? MessageThreadId) Resolve(string conversationId)
    {
        var identity = ConversationIdentity.Parse(conversationId)
                       ?? throw new InvalidOperationException(
                           $"'{conversationId}' is not a conversation identity — a Telegram tool only addresses chatId:threadId");

        return (identity.ChatId,
            identity.MessageThreadId is { } thread ? Convert.ToInt32(thread) : null);
    }
}