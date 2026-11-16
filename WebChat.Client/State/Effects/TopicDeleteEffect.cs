using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicDeleteEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicStreams _topicStreams;
    private readonly IChatMessagingService _messagingService;
    private readonly ITopicService _topicService;
    private readonly IMessagePipeline _pipeline;
    private readonly ILogger<TopicDeleteEffect> _logger;
    private readonly IDisposable _removeTopicRegistration;

    public TopicDeleteEffect(
        Dispatcher dispatcher,
        TopicStreams topicStreams,
        IChatMessagingService messagingService,
        ITopicService topicService,
        IMessagePipeline pipeline,
        ILogger<TopicDeleteEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicStreams = topicStreams;
        _messagingService = messagingService;
        _topicService = topicService;
        _pipeline = pipeline;
        _logger = logger;

        _removeTopicRegistration = dispatcher.RegisterHandler<RemoveTopic>(action =>
            HandleRemoveTopicAsync(action.TopicId, action.AgentId, action.ChatId, action.ThreadId)
                .LogFaults(_logger, nameof(RemoveTopic)));
    }

    public async Task HandleRemoveTopicAsync(
        string topicId, string? agentId = null, long? chatId = null, long? threadId = null)
    {
        // AgentId/ChatId/ThreadId present means the user asked for this delete. A server
        // notification carries none of them, because the server already deleted the topic.
        var userInitiated = agentId is not null && chatId.HasValue && threadId.HasValue;

        if (_topicStreams.Snapshot(topicId).HasStream)
        {
            // Only the person deleting their own topic stops its reply. A removal the server told of —
            // a topic deleted elsewhere, whose reply the delete already stopped, or one its owner made
            // private, whose reply is still theirs — only stops this page reading it: asking the server
            // to cancel was refused for a topic this person may no longer see, and the removal never
            // happened, the reply still growing on their screen.
            if (userInitiated && !(await _messagingService.CancelTopicAsync(topicId)).IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            _topicStreams.End(topicId);
        }

        // Delete from server only if AgentId/ChatId/ThreadId provided (client-initiated delete)
        // When server sends delete notification, these are null (already deleted server-side)
        if (agentId is not null && chatId.HasValue && threadId.HasValue)
        {
            var deleted = await _topicService.DeleteTopicAsync(agentId, topicId, chatId.Value, threadId.Value);
            if (!deleted.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }
        }

        _dispatcher.Dispatch(new TopicRemoved(topicId));

        // Clear cached messages so re-created topics reload from server; the same action drops
        // the topic's finalized message ids, which is all the pipeline ever tracked.
        _dispatcher.Dispatch(new ClearMessages(topicId));

        // A deleted conversation has nothing pending. Approvals are held per conversation, so
        // this takes only its own with it whatever the user has selected by now.
        _dispatcher.Dispatch(new TopicApprovalsReconciled(topicId, StillPending: null));
    }

    public void Dispose()
    {
        _removeTopicRegistration.Dispose();
    }
}