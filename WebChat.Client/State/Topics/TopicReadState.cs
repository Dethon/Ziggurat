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
        if (topic.UnreadCount == 0)
        {
            return;
        }

        dispatcher.Dispatch(new UpdateTopic(topic.WithReadPosition(topic.MessageCount)));

        await topicService.MarkTopicReadAsync(topic.AgentId, topic.ChatId, topic.TopicId);
    }
}