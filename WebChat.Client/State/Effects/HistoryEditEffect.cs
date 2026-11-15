using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Helpers;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// A person rewriting a conversation from its page, and every page told it was rewritten. What the page
// shows afterwards is always the history read again, never the page's own guess at what changed: the
// line, the versions and the ids are the store's.
public sealed class HistoryEditEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly TopicStreams _topicStreams;
    private readonly ITopicService _topicService;
    private readonly ILogger<HistoryEditEffect> _logger;
    private readonly IDisposable[] _registrations;

    public HistoryEditEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        TopicStreams topicStreams,
        ITopicService topicService,
        ILogger<HistoryEditEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _topicStreams = topicStreams;
        _topicService = topicService;
        _logger = logger;
        _registrations =
        [
            dispatcher.RegisterHandler<DeleteMessagesFrom>(a =>
                HandleDeleteFromAsync(a.TopicId, a.EntryId).LogFaults(_logger, nameof(DeleteMessagesFrom))),
            dispatcher.RegisterHandler<HistoryChanged>(a =>
                HandleHistoryChangedAsync(a.TopicId).LogFaults(_logger, nameof(HistoryChanged)))
        ];
    }

    public Task HandleDeleteFromAsync(string topicId, string entryId) =>
        RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.DeleteFromAsync(topic.AgentId, topic.TopicId, topic.ChatId, entryId, lastSeen));

    public async Task HandleHistoryChangedAsync(string topicId)
    {
        if (_messagesStore.State.LoadedTopics.Contains(topicId) && !_topicStreams.Snapshot(topicId).HasStream)
        {
            await ReloadAsync(topicId);
        }
    }

    private async Task RewriteAsync(
        string topicId, Func<Models.StoredTopic, string?, Task<HubResult<Nothing>>> rewrite)
    {
        if (_topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId) is not { } topic)
        {
            return;
        }

        try
        {
            if (!(await rewrite(topic, LastSeen(topicId))).IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }
        }
        catch (HubException refused)
        {
            _dispatcher.Dispatch(new ShowError(HubRefusal.MessageOf(refused)));
        }

        // Refused or not, the page shows what the server holds: a refusal for a line that moved on is
        // answered by showing the line as it is.
        await ReloadAsync(topicId);
    }

    private string? LastSeen(string topicId) =>
        _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []).LastOrDefault(m => m.EntryId is not null)?.EntryId;

    private async Task ReloadAsync(string topicId)
    {
        if (_topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId) is not { } topic)
        {
            return;
        }

        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        if (history.IsLive)
        {
            _dispatcher.Dispatch(new MessagesLoaded(topicId, [.. history.Value!.Select(h => h.ToChatMessageModel())]));
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}