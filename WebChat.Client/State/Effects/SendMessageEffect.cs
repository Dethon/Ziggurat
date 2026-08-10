using Domain.Conversations;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class SendMessageEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamingService _streamingService;
    private readonly TopicStreams _topicStreams;
    private readonly ITopicService _topicService;
    private readonly IChatMessagingService _messagingService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly IMessagePipeline _pipeline;
    private readonly ComposerStore _composerStore;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<SendMessageEffect> _logger;
    private readonly IDisposable _sendMessageRegistration;
    private readonly IDisposable _cancelStreamingRegistration;
    private readonly IDisposable _retryLastMessageRegistration;

    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        IStreamingService streamingService,
        TopicStreams topicStreams,
        ITopicService topicService,
        IChatMessagingService messagingService,
        UserIdentityStore userIdentityStore,
        IMessagePipeline pipeline,
        ComposerStore composerStore,
        SpaceStore spaceStore,
        ILogger<SendMessageEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _streamingService = streamingService;
        _topicStreams = topicStreams;
        _topicService = topicService;
        _messagingService = messagingService;
        _userIdentityStore = userIdentityStore;
        _pipeline = pipeline;
        _composerStore = composerStore;
        _spaceStore = spaceStore;
        _logger = logger;

        _sendMessageRegistration = dispatcher.RegisterHandler<SendMessage>(action =>
            HandleSendMessageAsync(action).LogFaults(_logger, nameof(SendMessage)));
        _cancelStreamingRegistration = dispatcher.RegisterHandler<CancelStreaming>(action =>
            HandleCancelStreamingAsync(action.TopicId).LogFaults(_logger, nameof(CancelStreaming)));
        _retryLastMessageRegistration = dispatcher.RegisterHandler<RetryLastMessage>(HandleRetryLastMessage);
    }

    private async Task HandleCancelStreamingAsync(string topicId)
    {
        var cancelled = await _messagingService.CancelTopicAsync(topicId);

        // Marking the reply stopped when the stop never reached the server would surprise the
        // user the moment it carries on.
        if (!cancelled.IsLive)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        // The server closes the stream when it is asked to cancel a topic, so the chunk loop
        // ends by itself. Ending the lease here keeps the text that already arrived and takes
        // the topic out of streaming at once; the drained loop's own ending changes nothing.
        _topicStreams.End(topicId);
    }

    private async Task HandleSendMessageAsync(SendMessage action)
    {
        try
        {
            await SendAsync(action);
        }
        catch
        {
            // The message is already rendered locally, so a fault here means it never reached
            // the server. Same feedback as the not-live branches; the rethrow is what LogFaults
            // observes.
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            throw;
        }
    }

    private async Task SendAsync(SendMessage action)
    {
        var state = _topicsStore.State;
        StoredTopic topic;

        // Read before the send, which is what clears the composer: the bubble the person sees
        // has to carry what they attached. A retry brings its own, because the message it is
        // re-sending emptied the composer when it first went out.
        var attached = action.Attachments
            ?? ComposerSelectors.References(
                ComposerSelectors.Ready(_composerStore.State.For(action.TopicId)))
            ?? [];

        if (string.IsNullOrEmpty(action.TopicId))
        {
            var topicName = TopicName(action.Message, attached);
            var identity = ConversationIdGenerator.Create();
            topic = new StoredTopic
            {
                TopicId = identity.TopicId,
                ChatId = identity.ChatId,
                ThreadId = identity.ThreadId,
                AgentId = state.SelectedAgentId!,
                Name = topicName,
                CreatedAt = DateTime.UtcNow,
                SpaceSlug = _spaceStore.State.CurrentSlug
            };

            var started = await _sessionService.StartSessionAsync(topic);

            // Three outcomes, not two. A server that refuses is live and has answered, and
            // stays as silent as it is today; a call that could not be made says so once.
            if (!started.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            if (!started.Value)
            {
                return;
            }

            _dispatcher.Dispatch(new AddTopic(topic));
            _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
            _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

            // No early return, unlike the other user actions: the conversation is already on
            // screen, so the send still runs and its own failure toast dedupes into this one.
            var saved = await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
            if (!saved.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            }
        }
        else
        {
            topic = state.Topics.First(t => t.TopicId == action.TopicId);
            RenameFromOpeningText(topic, action.Message, attached);

            if (_sessionService.CurrentTopic?.TopicId != topic.TopicId)
            {
                var started = await _sessionService.StartSessionAsync(topic);
                if (!started.IsLive)
                {
                    _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                    return;
                }
            }
        }

        // Close off the bubble the agent is writing before the user's own message goes in.
        // Nothing to close on a topic with no reply in flight.
        _topicStreams.FinalizeCurrent(topic.TopicId);

        // Submit user message through pipeline (handles correlation tracking and AddMessage dispatch)
        var identityState = _userIdentityStore.State;
        var currentUser = identityState.AvailableUsers
            .FirstOrDefault(u => u.Id == identityState.SelectedUserId);

        var correlationId = _pipeline.SubmitUserMessage(
            topic.TopicId, action.Message, currentUser?.Id, attached.Count == 0 ? null : attached);

        // Delegate to streaming service (handles stream reuse internally). Awaited so a fault
        // opening the send lands in the catch above; the call returns once the stream is open,
        // not when the reply completes.
        await _streamingService.SendMessageAsync(
            topic, action.Message, correlationId, action.Attachments);
    }

    // Picking a file with nothing selected starts the conversation there and then, before a word
    // has been typed, so it is named after the file. The opening message is what the person meant
    // to call it, and it renames the conversation the way the header field does — the file's name
    // stands only when the message is nothing but files.
    //
    // Bounded to the opening turn by two conditions, not one: the marker is gone the moment the
    // rename lands, and an empty transcript keeps a send that races a history load from renaming
    // a conversation already under way.
    private void RenameFromOpeningText(
        StoredTopic topic, string message, IReadOnlyList<AttachmentReference> attached)
    {
        var opening = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, []).Count == 0;

        if (topic.NameFromFile && opening && !string.IsNullOrWhiteSpace(message))
        {
            _dispatcher.Dispatch(new RenameTopic(topic.TopicId, TopicName(message, attached)));
        }
    }

    // A message with attachments and no text is a normal thing to send, so the conversation is
    // named after the first file rather than after nothing.
    private static string TopicName(string message, IReadOnlyList<AttachmentReference> attached)
    {
        var source = string.IsNullOrWhiteSpace(message)
            ? attached.FirstOrDefault()?.FileName ?? "New conversation"
            : message;
        return source.Length > 50 ? source[..50] + "..." : source;
    }

    private void HandleRetryLastMessage(RetryLastMessage action)
    {
        _dispatcher.Dispatch(new RemoveTrailingErrors(action.TopicId));

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(action.TopicId, []);
        var lastUserMessage = messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMessage is not null)
        {
            // With its files. A message can now be nothing but attachments, so re-sending the
            // text alone would ask the model about a picture it was never given.
            _dispatcher.Dispatch(new SendMessage(
                action.TopicId, lastUserMessage.Content, lastUserMessage.Attachments));
        }
    }

    public void Dispose()
    {
        _sendMessageRegistration.Dispose();
        _cancelStreamingRegistration.Dispose();
        _retryLastMessageRegistration.Dispose();
    }
}