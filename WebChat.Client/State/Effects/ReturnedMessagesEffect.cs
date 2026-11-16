using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// Messages a stop handed back to the person who queued them, taken into the composer of the topic they
// were queued in. The hub hands them over once, to whichever of the person's pages asks first, so a
// page asks only where it can place them: on the topic it shows.
public sealed class ReturnedMessagesEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topics;
    private readonly IChatMessagingService _messagingService;
    private readonly ILogger<ReturnedMessagesEffect> _logger;
    private readonly IDisposable _returned;

    public ReturnedMessagesEffect(
        Dispatcher dispatcher, TopicsStore topics, IChatMessagingService messagingService,
        ILogger<ReturnedMessagesEffect> logger)
    {
        _dispatcher = dispatcher;
        _topics = topics;
        _messagingService = messagingService;
        _logger = logger;
        _returned = dispatcher.RegisterHandler<QueueReturned>(a =>
        {
            if (_topics.State.SelectedTopicId == a.TopicId)
            {
                TakeAsync(a.TopicId).LogFaults(_logger, nameof(QueueReturned));
            }
        });
    }

    // Quiet when nothing could be taken: the hub keeps them, and the next open of the topic asks again.
    private async Task TakeAsync(string topicId)
    {
        HubResult<IReadOnlyList<ReturnedMessage>> taken;
        try
        {
            taken = await _messagingService.TakeReturnedAsync(topicId);
        }
        catch (HubException refused)
        {
            _logger.LogInformation("Returned messages of topic {TopicId} not taken: {Reason}", topicId, refused.Message);
            return;
        }

        if (taken is { IsLive: true, Value: { Count: > 0 } messages })
        {
            _dispatcher.Dispatch(new ReturnToComposer(topicId, messages));
        }
    }

    public void Dispose() => _returned.Dispose();
}