using Domain.Contracts;

namespace McpChannelSignalR.Services;

// A topic's queue told to whoever follows its stream: a private topic's owner and whoever sees every
// topic, any other topic's space (TopicAudience), as its stream's start is. Told whole, with the
// revision it was read at, by whichever side changed it — the hub as a message is queued, the
// dispatcher as one leaves — so the two may arrive the other way round and a page keeps the later.
internal sealed class QueueNews(StreamService streamService, IThreadStateStore threadStore, IHubNotificationSender hubSender)
{
    public async Task TellAsync(SenderSnapshot of)
    {
        var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(
            of.AgentId, of.Conversation.ChatId, of.Conversation.ThreadId);
        var queue = streamService.QueueOf(of.TopicId);
        await hubSender.SendToTopicAsync(
            topic, of.SpaceSlug, "OnQueueChanged", queue.ToNotification(of.TopicId, of.SpaceSlug));
    }
}