using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Helpers;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// A topic's queue as the hub holds it, and the reader's own queued messages withdrawn from their chips.
// The hub decides a withdrawal: a message whose turn started first is answered "already sent", and the
// person is told so rather than shown a withdrawal that did not happen.
public sealed class QueueEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatMessagingService _messagingService;
    private readonly ILogger<QueueEffect> _logger;
    private readonly IDisposable[] _registrations;

    public QueueEffect(
        Dispatcher dispatcher, TopicsStore topics, ConnectionStore connection, IChatMessagingService messagingService,
        ILogger<QueueEffect> logger)
    {
        _dispatcher = dispatcher;
        _messagingService = messagingService;
        _logger = logger;
        _registrations =
        [
            ShownTopicCatchUp.Subscribe(dispatcher, topics, connection,
                (topicId, moment) => CatchUpAsync(topicId).LogFaults(logger, moment)),
            dispatcher.RegisterHandler<EditQueuedMessage>(a =>
                HandleEditAsync(a.TopicId, a.QueuedId).LogFaults(logger, nameof(EditQueuedMessage))),
            dispatcher.RegisterHandler<RemoveQueuedMessage>(a =>
                HandleRemoveAsync(a.TopicId, a.QueuedId).LogFaults(logger, nameof(RemoveQueuedMessage))),
            dispatcher.RegisterHandler<SteerQueuedMessage>(a =>
                HandleSteerAsync(a.TopicId, a.QueuedId).LogFaults(logger, nameof(SteerQueuedMessage)))
        ];
    }

    // Asked while pushes may still be on their way, so it goes through the reducer that keeps the later
    // of two. Quiet when it cannot be asked: the next open or the next push tells it.
    private async Task CatchUpAsync(string topicId)
    {
        try
        {
            if (await _messagingService.GetQueueAsync(topicId) is { IsLive: true, Value: { } queue })
            {
                _dispatcher.Dispatch(new QueueChanged(queue));
            }
        }
        catch (HubException refused)
        {
            _logger.LogInformation("Queue of topic {TopicId} not caught up: {Reason}", topicId, refused.Message);
        }
    }

    // Its files come back as ready attachments, already uploaded, so a rework never uploads them again.
    public async Task HandleEditAsync(string topicId, string queuedId)
    {
        if (await WithdrawAsync(topicId, queuedId, () => _messagingService.TakeBackQueuedAsync(topicId, queuedId))
            is { } returned)
        {
            _dispatcher.Dispatch(new ReturnToComposer(topicId, [returned]));
        }
    }

    public Task HandleRemoveAsync(string topicId, string queuedId) =>
        WithdrawAsync(topicId, queuedId, () => _messagingService.RemoveQueuedAsync(topicId, queuedId));

    // Sent into the running turn: the chip waits as steering until the page draws the message where the
    // hub writes it on the stream, once that turn has ended.
    public Task HandleSteerAsync(string topicId, string queuedId) =>
        WithdrawAsync(topicId, queuedId, () => _messagingService.SteerQueuedAsync(topicId, queuedId),
            new QueuedMessageSteered(topicId, queuedId));

    private async Task<T?> WithdrawAsync<T>(
        string topicId, string queuedId, Func<Task<HubResult<T>>> call, IAction? taken = null)
    {
        HubResult<T> answered;
        try
        {
            answered = await call();
        }
        catch (HubException refused)
        {
            _dispatcher.Dispatch(new ShowError(HubRefusal.MessageOf(refused)));
            return default;
        }

        if (!answered.IsLive)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return default;
        }

        _dispatcher.Dispatch(taken ?? new QueuedMessageWithdrawn(topicId, queuedId));
        return answered.Value;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}