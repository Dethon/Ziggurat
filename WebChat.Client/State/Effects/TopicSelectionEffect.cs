using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using TopicReadState = WebChat.Client.State.Topics.TopicReadState;

namespace WebChat.Client.State.Effects;

public sealed class TopicSelectionEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly ITopicService _topicService;
    private readonly IStreamResumeService _streamResumeService;
    private readonly IMessagePipeline _pipeline;
    private readonly ILogger<TopicSelectionEffect> _logger;
    private readonly IDisposable _selectTopicRegistration;

    public TopicSelectionEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        ITopicService topicService,
        IStreamResumeService streamResumeService,
        IMessagePipeline pipeline,
        ILogger<TopicSelectionEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _topicService = topicService;
        _streamResumeService = streamResumeService;
        _pipeline = pipeline;
        _logger = logger;

        _selectTopicRegistration = dispatcher.RegisterHandler<SelectTopic>(action =>
        {
            if (action.TopicId is not null)
            {
                HandleSelectTopicAsync(action.TopicId).LogFaults(_logger, nameof(SelectTopic));
            }
        });
    }

    public async Task HandleSelectTopicAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return;
        }

        var hasMessages = _messagesStore.State.MessagesByTopic.ContainsKey(topicId);
        if (!hasMessages)
        {
            var session = await _sessionService.StartSessionAsync(topic);

            // The user tapped this conversation, so a session that could not be started says so
            // once (ADR-0004). Carrying on would open a thread with no history and no session
            // behind it — a blank conversation that answers nothing typed into it.
            if (!session.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);

            // Re-check after async work - SendMessageEffect might have added messages
            var currentMessages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
            if (history.IsLive && currentMessages.Count == 0)
            {
                _pipeline.LoadHistory(topicId, history.Value!);
            }
        }

        await MarkTopicAsReadAsync(topic);

        // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
        // awaiting the conversation.
        _streamResumeService.TryResumeStreamAsync(topic).LogFaults(_logger, "stream resume");
    }

    private Task MarkTopicAsReadAsync(StoredTopic topic) =>
        TopicReadState.MarkReadAsync(topic, _dispatcher, _topicService);

    public void Dispose()
    {
        _selectTopicRegistration.Dispose();
    }
}