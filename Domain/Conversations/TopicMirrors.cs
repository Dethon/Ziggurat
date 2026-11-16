using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// A topic over a conversation another channel goes on showing — Telegram's or Sugary's review copy,
// a voice conversation — shares its history with that channel, which still shows what it showed. Such
// a topic is forked, never rewritten. A topic from before the mark is told by its shape: a review copy
// is over a conversation its own id does not name, and a voice conversation began with a spoken turn.
public static class TopicMirrors
{
    public static bool IsMirrored(TopicMetadata topic, IReadOnlyList<ChatMessage> messages) =>
        topic.Mirrored
        || ConversationIdGenerator.CreateFor(topic.TopicId) != new ConversationIdentity(topic.ChatId, topic.ThreadId)
        || messages.FirstOrDefault(m => m.Role == ChatRole.User)?.GetConversationContext()?.Origin.ChannelId == ChannelIds.Voice;
}