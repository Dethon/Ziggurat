using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class ReconnectionEffect : IDisposable
{
    private readonly IDisposable _subscription;
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly ILogger<ReconnectionEffect> _logger;

    public ReconnectionEffect(
        ConnectionStore connectionStore,
        TopicsStore topicsStore,
        SpaceStore spaceStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService,
        Dispatcher dispatcher,
        ITopicService topicService,
        ILogger<ReconnectionEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _logger = logger;

        _subscription = connectionStore.BecameLiveAgain.Subscribe(
            _ => HandleReconnectedAsync(topicsStore, spaceStore, sessionService, streamResumeService)
                .LogFaults(logger, "became live again"));
    }

    private async Task HandleReconnectedAsync(
        TopicsStore topicsStore,
        SpaceStore spaceStore,
        IChatSessionService sessionService,
        IStreamResumeService streamResumeService)
    {
        // Re-fetch topics from server to pick up changes made while disconnected
        var agentId = topicsStore.State.SelectedAgentId;
        if (agentId is not null)
        {
            var spaceSlug = spaceStore.State.CurrentSlug;

            // The first page, not everything that was held. A bump that happened while the
            // client was not live is covered by exactly this: paging only ever fetches
            // backwards, so becoming live starts the list again from the top.
            var firstPage = await _topicService.GetTopicPageAsync(agentId, spaceSlug);

            // Catch-up can land in the next interruption. Storing a not-live answer would
            // empty the sidebar the recovery exists to refill.
            if (firstPage.IsLive)
            {
                var topics = firstPage.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
                _dispatcher.Dispatch(new TopicsLoaded(topics, firstPage.Value.NextCursor));

                // The page says which replies are in flight, so recovery resumes those and asks
                // about nothing else.
                TopicPageStreams.ResumeReported(
                    topics, firstPage.Value.LiveTopicIds, streamResumeService, _logger);
            }
        }

        var currentState = topicsStore.State;

        var tasks = new List<Task>();

        if (currentState.SelectedTopicId is not null)
        {
            var selectedTopic = currentState.Topics
                .FirstOrDefault(t => t.TopicId == currentState.SelectedTopicId);

            if (selectedTopic is not null)
            {
                tasks.Add(ReloadTopicHistoryAsync(selectedTopic));
                tasks.Add(sessionService.StartSessionAsync(selectedTopic));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task ReloadTopicHistoryAsync(StoredTopic topic)
    {
        var history = await _topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
        if (!history.IsLive)
        {
            return;
        }

        var messages = history.Value!.Select(h => h.ToChatMessageModel()).ToList();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
    }

    public void Dispose()
    {
        _subscription.Dispose();
    }
}