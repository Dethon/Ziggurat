using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// The share toggle on a person's own topic. The row says shared, or private again, only once the
// server took it: a row that reads as private while the household can still see it is a promise
// nobody kept, and one that reads as shared while nobody can is a surprise later.
public sealed class TopicShareEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly ITopicService _topicService;
    private readonly IDisposable _shareTopicRegistration;

    public TopicShareEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService,
        ILogger<TopicShareEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _topicService = topicService;

        _shareTopicRegistration = dispatcher.RegisterHandler<ShareTopic>(action =>
            HandleShareTopicAsync(action.TopicId, action.Shared)
                .LogFaults(logger, nameof(ShareTopic)));
    }

    public async Task HandleShareTopicAsync(string topicId, bool shared)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null || topic.Shared == shared)
        {
            return;
        }

        var answered = await _topicService.SetTopicSharedAsync(topic.AgentId, topic.ChatId, topic.TopicId, shared);
        if (!answered.IsLive)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        _dispatcher.Dispatch(new UpdateTopic(StoredTopic.FromMetadata(topic.ToMetadata() with { Shared = shared })));
    }

    public void Dispose()
    {
        _shareTopicRegistration.Dispose();
    }
}