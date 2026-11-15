using Domain.DTOs.Channel;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Helpers;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
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
            dispatcher.RegisterHandler<EditAndRerun>(a =>
                HandleEditAndRerunAsync(a.TopicId, a.EntryId, a.Text).LogFaults(_logger, nameof(EditAndRerun))),
            dispatcher.RegisterHandler<EditMessageInPlace>(a =>
                HandleEditInPlaceAsync(a.TopicId, a.EntryId, a.Text).LogFaults(_logger, nameof(EditMessageInPlace))),
            dispatcher.RegisterHandler<RerollLastReply>(a =>
                HandleRerollAsync(a.TopicId, a.ConfigPatch).LogFaults(_logger, nameof(RerollLastReply))),
            dispatcher.RegisterHandler<SwitchMessageVersion>(a =>
                HandleSwitchVersionAsync(a.TopicId, a.Slot, a.Index).LogFaults(_logger, nameof(SwitchMessageVersion))),
            dispatcher.RegisterHandler<HistoryChanged>(a =>
                HandleHistoryChangedAsync(a.TopicId).LogFaults(_logger, nameof(HistoryChanged)))
        ];
    }

    public Task<bool> HandleDeleteFromAsync(string topicId, string entryId) =>
        RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.DeleteFromAsync(topic.AgentId, topic.TopicId, topic.ChatId, entryId, lastSeen));

    // The message's own files go with the edited words; the composer may hold something else entirely.
    public async Task HandleEditAndRerunAsync(string topicId, string entryId, string text)
    {
        var original = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, [])
            .FirstOrDefault(m => m.EntryId == entryId);
        var putAside = await RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.PutAsideFromAsync(topic.AgentId, topic.TopicId, topic.ChatId, entryId, lastSeen));
        if (putAside)
        {
            _dispatcher.Dispatch(new SendMessage(topicId, text, original?.Attachments ?? []));
        }
    }

    public Task<bool> HandleEditInPlaceAsync(string topicId, string entryId, string text) =>
        RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.EditInPlaceAsync(topic.AgentId, topic.TopicId, topic.ChatId, entryId, text, lastSeen));

    // The last person's message is answered again: its reply is put aside, then the message goes as itself.
    public async Task HandleRerollAsync(string topicId, AgentConfigPatch? configPatch)
    {
        var asked = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, [])
            .LastOrDefault(m => m.Role == "user");
        if (asked?.EntryId is not { } entryId)
        {
            return;
        }

        var putAside = await RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.PutAsideReplyAsync(topic.AgentId, topic.TopicId, topic.ChatId, entryId, lastSeen));

        // By the name the history gives it now: a conversation from before entry ids was stamped by the
        // rewrite, and the legacy name the page held names nothing any more.
        var named = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, [])
            .LastOrDefault(m => m.Role == "user")?.EntryId;
        if (putAside && named is not null)
        {
            _dispatcher.Dispatch(new SendRerun(topicId, asked.Content, asked.Attachments ?? [], named, configPatch));
        }
    }

    public Task<bool> HandleSwitchVersionAsync(string topicId, string slot, int index) =>
        RewriteAsync(topicId, (topic, lastSeen) =>
            _topicService.SwitchVersionAsync(topic.AgentId, topic.TopicId, topic.ChatId, slot, index, lastSeen));

    public async Task HandleHistoryChangedAsync(string topicId)
    {
        if (_messagesStore.State.LoadedTopics.Contains(topicId) && !_topicStreams.Snapshot(topicId).HasStream)
        {
            await ReloadAsync(topicId);
        }
    }

    // Whether the rewrite was made.
    private async Task<bool> RewriteAsync(
        string topicId, Func<Models.StoredTopic, string?, Task<HubResult<Nothing>>> rewrite)
    {
        if (_topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId) is not { } topic)
        {
            return false;
        }

        var made = false;
        try
        {
            if (!(await rewrite(topic, LastSeen(topicId))).IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return false;
            }

            made = true;
        }
        catch (HubException refused)
        {
            _dispatcher.Dispatch(new ShowError(HubRefusal.MessageOf(refused)));
        }

        // Refused or not, the page shows what the server holds: a refusal for a line that moved on is
        // answered by showing the line as it is, and a rewrite before a turn is shown before the turn.
        await ReloadAsync(topicId);
        return made;
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