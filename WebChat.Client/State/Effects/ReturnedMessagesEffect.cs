using System.Reactive.Linq;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Toast;
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
    private readonly IDisposable _unshared;
    private readonly IDisposable _live;

    // Said where the words land, which is not where they were queued.
    public const string UnsharedToast =
        "A conversation is no longer shared with you, so what you had queued there is back in your message box.";

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
            // Whichever page takes the words, the one that drew a send as sent takes it off: it was never
            // taken, so left there it would read as sent and be in the composer too.
            if (a.CorrelationIds is { Count: > 0 } sent)
            {
                _dispatcher.Dispatch(new TakeOffSentMessages(a.TopicId, sent));
            }

            if (_topics.State.SelectedTopicId == a.TopicId)
            {
                TakeAsync(a.TopicId).LogFaults(_logger, nameof(QueueReturned));
            }
        });

        // A page that was not showing the topic when told, or not live then, finds them as it opens it or
        // comes back.
        _shown = ShownTopicCatchUp.Subscribe(dispatcher, topics, connection,
            (topicId, moment) => TakeAsync(topicId).LogFaults(_logger, moment));

        // What unsharing handed this person is no topic's to wait for: taken as soon as told, and by every
        // page as it becomes live, the first time too, so a person with no page open then finds it on the
        // next one.
        _unshared = dispatcher.RegisterHandler<QueueUnshared>(a =>
        {
            if (a.CorrelationIds is { Count: > 0 } sent)
            {
                _dispatcher.Dispatch(new TakeOffSentMessages(a.TopicId, sent));
            }

            TakeUnsharedAsync(a.TopicId).LogFaults(_logger, nameof(QueueUnshared));
        });
        _live = connection.StateObservable
            .Where(state => state.Status == ConnectionStatus.Connected)
            .Select(state => state.Epoch)
            .DistinctUntilChanged()
            .Subscribe(_ => TakeUnsharedAsync(unsharedTopicId: null).LogFaults(_logger, "unshared messages"));
    }

    // Into whatever composer is on screen, the words alone: the files were added to the unshared topic's
    // conversation, and no other would take them (TopicRefusals.NotYourFiles). Not that topic's composer,
    // should it still be on screen, since its person may no longer write there.
    private async Task TakeUnsharedAsync(string? unsharedTopicId)
    {
        IReadOnlyList<ReturnedMessage> taken;
        try
        {
            if (await _messagingService.TakeUnsharedAsync() is not { IsLive: true, Value: { Count: > 0 } answered })
            {
                return;
            }

            taken = answered;
        }
        catch (HubException refused)
        {
            _logger.LogInformation("Unshared messages not taken: {Reason}", refused.Message);
            return;
        }

        var onScreen = _topics.State.SelectedTopicId is { } selected && selected != unsharedTopicId ? selected : "";
        _dispatcher.Dispatch(new ShowError(UnsharedToast));
        _dispatcher.Dispatch(new ReturnToComposer(onScreen, [.. taken.Select(m => m with { Attachments = [] })]));
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

        if (waiting.Select(m => m.CorrelationId).OfType<string>().ToList() is { Count: > 0 } drawn)
        {
            _dispatcher.Dispatch(new TakeOffSentMessages(topicId, drawn));
        }

        _dispatcher.Dispatch(new ReturnToComposer(topicId, waiting));
    }

    public void Dispose()
    {
        _returned.Dispose();
        _shown.Dispose();
        _unshared.Dispose();
        _live.Dispose();
    }
}