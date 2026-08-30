namespace Infrastructure.StateManagers;

// The chat-and-topic index member: the sorted-set member and search-hit spelling that carries the
// chat id beside the topic id, so reading a page never costs a lookup to find out where each
// member's record lives. It shares the colon with the conversation identity ("chatId:threadId")
// and is NOT one — the right half is a topic id, and nothing may parse it as a conversation.
internal static class TopicIndexMember
{
    public static string Compose(long chatId, string topicId) => $"{chatId}:{topicId}";
}