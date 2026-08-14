namespace WebChat.Client.State.Topics;

// Unread is the difference between what a topic holds and what its reader has seen, both carried
// on the topic itself. Nothing here reads a message: a badge used to cost the client the whole
// conversation, which is the cost lazy history removes.
public static class UnreadSelectors
{
    public static IReadOnlyDictionary<string, int> ComputeUnreadCounts(TopicsState topicsState) =>
        topicsState.Topics
            // The conversation on screen is being read as it arrives, so it never carries a badge.
            .Where(t => t.TopicId != topicsState.SelectedTopicId && t.UnreadCount > 0)
            .ToDictionary(t => t.TopicId, t => t.UnreadCount);
}