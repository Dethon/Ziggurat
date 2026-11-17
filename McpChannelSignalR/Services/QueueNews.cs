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
    // space it shows: only the sender's own pages may take them. Each telling names the sends it hands
    // back by the ids their pages sent them with, so a page that drew one as sent takes it off, whichever
    // page takes the words. Told after the stop, which a failed telling must not fail: a page finds them
    // anyway as it next opens the topic.
    public Task TellReturnedOrLogAsync(IEnumerable<(SenderSnapshot Sender, string? CorrelationId)> returned) =>
        TellReturnedOrLogAsync(returned, unshared: false);

    // Messages unsharing the topic took out of it, told the same way to each of their senders, marked so
    // their pages take them from the hub by person (ChatHub.TakeUnshared): the topic is no longer theirs.
    public Task TellUnsharedOrLogAsync(IEnumerable<(SenderSnapshot Sender, string? CorrelationId)> moved) =>
        TellReturnedOrLogAsync(moved, unshared: true);

    private Task TellReturnedOrLogAsync(IEnumerable<(SenderSnapshot Sender, string? CorrelationId)> returned, bool unshared) =>
        Task.WhenAll(returned
            .GroupBy(r => r.Sender.UserId)
            .Select(theirs => TellReturnedOrLogAsync(
                theirs.First().Sender, [.. theirs.Select(r => r.CorrelationId).OfType<string>()], unshared)));

    private async Task TellReturnedOrLogAsync(SenderSnapshot to, IReadOnlyList<string> sends, bool unshared)
    {
        try
        {
            await hubSender.SendToGroupAsync(
                TopicAudience.Account(to.UserId), "OnQueueReturned",
                new QueueReturnedNotification(to.TopicId, to.SpaceSlug, sends.Count > 0 ? sends : null, unshared));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell {UserId} their queued messages in topic {TopicId} were returned",
                to.UserId, to.TopicId);
        }
    }
}