using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

// Marking a topic read, from wherever it happens. The row clears at once so the badge does not
// wait on a round trip, and the server works out the position it clears to — its count is always
// newer than the one the client is holding.
public static class TopicReadState
{
    public static async Task MarkReadAsync(
        StoredTopic topic, IDispatcher dispatcher, ITopicService topicService)
    {
        if (topic.UnreadCount > 0)
        {
            dispatcher.Dispatch(new UpdateTopic(topic.WithReadPosition(topic.MessageCount)));
        }

        // Asked even when this client counts nothing unread: its count can be one the store has
        // outgrown (a topic an agent opened arrives holding nothing).
        await topicService.MarkTopicReadAsync(topic.AgentId, topic.ChatId, topic.TopicId);
    }
}