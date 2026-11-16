using Domain.Contracts;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

// A topic's queue told to whoever follows its stream: a private topic's owner and whoever sees every
// topic, any other topic's space (TopicAudience), as its stream's start is. Told whole, with the
// revision it was read at, by whichever side changed it — the hub as a message is queued, the
// dispatcher as one leaves — so the two may arrive the other way round and a page keeps the later.
public sealed class QueueNews(
    StreamService streamService,
    IThreadStateStore threadStore,
    IHubNotificationSender hubSender,
    ILogger<QueueNews> logger)
{
    public Task TellAsync(SenderSnapshot of) =>
        TellAsync(of.TopicId, of.AgentId, of.Conversation.ChatId, of.Conversation.ThreadId, of.SpaceSlug);

    // For a change already made, which a failed telling must not undo or fail: a turn dispatched, a
    // message withdrawn. The pages catch up on the queue as they open the topic.
    public Task TellOrLogAsync(SenderSnapshot of) => OrLogAsync(of.TopicId, () => TellAsync(of));

    public Task TellOrLogAsync(QueueChange change) =>
        OrLogAsync(change.TopicId, () => TellAsync(
            change.TopicId, change.Session.AgentId, change.Session.ChatId, change.Session.ThreadId,
            change.Session.SpaceSlug ?? SpaceConfig.DefaultSlug));

    private async Task TellAsync(string topicId, string agentId, long chatId, long threadId, string spaceSlug)
    {
        var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId);
        var queue = streamService.QueueOf(topicId);
        await hubSender.SendToTopicAsync(topic, spaceSlug, "OnQueueChanged", queue.ToNotification(topicId, spaceSlug));
    }

    private async Task OrLogAsync(string topicId, Func<Task> tell)
    {
        try
        {
            await tell();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell topic {TopicId} its queue changed", topicId);
        }
    }

    // Messages a stop handed back, told to each of their senders on every page they have open, whatever
    // space it shows: only the sender's own pages may take them. Told after the stop, which a failed
    // telling must not fail: a page finds them anyway as it next opens the topic.
    public Task TellReturnedOrLogAsync(IEnumerable<SenderSnapshot> senders) =>
        Task.WhenAll(senders
            .DistinctBy(s => s.UserId)
            .Select(TellReturnedOrLogAsync));

    private async Task TellReturnedOrLogAsync(SenderSnapshot to)
    {
        try
        {
            await hubSender.SendToGroupAsync(
                TopicAudience.Account(to.UserId), "OnQueueReturned", new QueueReturnedNotification(to.TopicId, to.SpaceSlug));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell {UserId} their queued messages in topic {TopicId} were returned",
                to.UserId, to.TopicId);
        }
    }
}