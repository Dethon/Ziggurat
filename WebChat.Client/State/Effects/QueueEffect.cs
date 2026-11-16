using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Helpers;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Toast;

namespace WebChat.Client.State.Effects;

// The reader's own queued messages, withdrawn from their chips. The hub decides: a message whose turn
// started first is answered "already sent", and the person is told so rather than shown a withdrawal
// that did not happen.
public sealed class QueueEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IChatMessagingService _messagingService;
    private readonly IDisposable[] _registrations;

    public QueueEffect(Dispatcher dispatcher, IChatMessagingService messagingService, ILogger<QueueEffect> logger)
    {
        _dispatcher = dispatcher;
        _messagingService = messagingService;
        _registrations =
        [
            dispatcher.RegisterHandler<EditQueuedMessage>(a =>
                HandleEditAsync(a.TopicId, a.QueuedId).LogFaults(logger, nameof(EditQueuedMessage))),
            dispatcher.RegisterHandler<RemoveQueuedMessage>(a =>
                HandleRemoveAsync(a.TopicId, a.QueuedId).LogFaults(logger, nameof(RemoveQueuedMessage)))
        ];
    }

    // Taken back into the composer, words and files, to be reworked and sent again as a new message.
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

    private async Task<T?> WithdrawAsync<T>(string topicId, string queuedId, Func<Task<HubResult<T>>> call)
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

        _dispatcher.Dispatch(new QueuedMessageWithdrawn(topicId, queuedId));
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