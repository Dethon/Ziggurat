using Domain.Contracts;
using Domain.DTOs.WebChat;

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

    // For a change already made, which a failed telling must not undo or fail: a turn dispatched, a
    // message withdrawn. The pages catch up on the queue as they open the topic.
    public async Task TellOrLogAsync(SenderSnapshot of, ILogger logger)
    {
        try
        {
            await TellAsync(of);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell topic {TopicId} its queue changed", of.TopicId);
        }
    }

    // Messages a stop handed back, told to each of their senders on every page they have open, whatever
    // space it shows: only the sender's own pages may take them.
    public Task TellReturnedAsync(IReadOnlyList<QueuedMessage> returned) =>
        Task.WhenAll(returned
            .DistinctBy(m => m.Sender.UserId)
            .Select(m => hubSender.SendToGroupAsync(
                TopicAudience.Account(m.Sender.UserId), "OnQueueReturned",
                new QueueReturnedNotification(m.Sender.TopicId, m.Sender.SpaceSlug))));
}