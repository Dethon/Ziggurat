using Domain.Conversations;

namespace McpChannelSignalR.Internal;

public record ChannelSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null, string? TopicName = null)
{
    public ConversationIdentity Identity => new(ChatId, ThreadId);
}