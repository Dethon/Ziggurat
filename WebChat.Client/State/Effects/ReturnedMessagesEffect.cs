using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Connection;
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
    private readonly Dictionary<string, IReadOnlyList<ReturnedMessage>> _held = new();
    private readonly IDisposable _returned;
    private readonly IDisposable _shown;

    public ReturnedMessagesEffect(
        Dispatcher dispatcher, TopicsStore topics, ConnectionStore connection, IChatMessagingService messagingService,
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

        // A page that was not showing the topic when told, or not live then, finds them as it opens it or
        // comes back.
        _shown = ShownTopicCatchUp.Subscribe(dispatcher, topics, connection,
            (topicId, moment) => TakeAsync(topicId).LogFaults(_logger, moment));
    }

    // Quiet when nothing could be taken: the hub keeps them, and the next open of the topic asks again.
    // The hub forgets what it handed over, so what lands after the person moved to another topic is kept
    // here for theirs, never placed in the composer of the topic now on screen.
    private async Task TakeAsync(string topicId)
    {
        IReadOnlyList<ReturnedMessage> taken = [];
        try
        {
            if (await _messagingService.TakeReturnedAsync(topicId) is { IsLive: true, Value: { } answered })
            {
                taken = answered;
            }
        }
        catch (HubException refused)
        {
            _logger.LogInformation("Returned messages of topic {TopicId} not taken: {Reason}", topicId, refused.Message);
        }

        IReadOnlyList<ReturnedMessage> waiting;
        lock (_held)
        {
            waiting = [.. _held.GetValueOrDefault(topicId) ?? [], .. taken];
            if (waiting.Count == 0)
            {
                return;
            }

            if (_topics.State.SelectedTopicId != topicId)
            {
                _held[topicId] = waiting;
                return;
            }

            _held.Remove(topicId);
        }

        _dispatcher.Dispatch(new ReturnToComposer(topicId, waiting));
    }

    public void Dispose()
    {
        _returned.Dispose();
        _shown.Dispose();
    }
}