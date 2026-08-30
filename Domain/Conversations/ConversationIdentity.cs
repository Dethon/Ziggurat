namespace Domain.Conversations;

public readonly record struct ConversationIdentity(long ChatId, long ThreadId)
{
    public string ConversationId => $"{ChatId}:{ThreadId}";

    // The non-forum rule: a chat with no thread stores its chat id in the thread slot, so an
    // equal pair means there is no thread to address.
    public long? MessageThreadId => ThreadId == ChatId ? null : ThreadId;

    public static ConversationIdentity ForChat(long chatId, long? messageThreadId) =>
        new(chatId, messageThreadId ?? chatId);

    // A value that does not parse as a conversation identity is some other channel's address
    // passing through — never guess. Only the canonical spelling parses, so parse is the exact
    // inverse of ConversationId.
    public static ConversationIdentity? Parse(string conversationId)
    {
        var parts = conversationId.Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], out var chatId)
            || !long.TryParse(parts[1], out var threadId))
        {
            return null;
        }

        var identity = new ConversationIdentity(chatId, threadId);
        return identity.ConversationId == conversationId ? identity : null;
    }
}