using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Internal;

public record ChannelSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null, string? TopicName = null)
{
    public ConversationIdentity Identity => new(ChatId, ThreadId);
}

public static class ChannelSessionTopics
{
    // The stored topic a session's conversation belongs to, which says who may act on it: sessions are
    // this process's and every connection's, so one existing says nothing about who opened it. Null
    // for a conversation no topic holds yet.
    public static Task<TopicMetadata?> TopicOfAsync(this IThreadStateStore store, ChannelSession session) =>
        store.GetTopicByChatIdAndThreadIdAsync(session.AgentId, session.ChatId, session.ThreadId);
}