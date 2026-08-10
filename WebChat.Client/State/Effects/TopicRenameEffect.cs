using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class TopicRenameEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly ITopicService _topicService;
    private readonly ILogger<TopicRenameEffect> _logger;
    private readonly IDisposable _renameTopicRegistration;

    public TopicRenameEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService,
        ILogger<TopicRenameEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _topicService = topicService;
        _logger = logger;

        _renameTopicRegistration = dispatcher.RegisterHandler<RenameTopic>(action =>
            HandleRenameTopicAsync(action.TopicId, action.Name)
                .LogFaults(_logger, nameof(RenameTopic)));
    }

    public async Task HandleRenameTopicAsync(string topicId, string name)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        var trimmed = name.Trim();

        // A cleared field is someone about to retype, not a request for a nameless conversation.
        if (topic is null || trimmed.Length == 0 || trimmed == topic.Name)
        {
            return;
        }

        var renamed = topic.ToMetadata() with { Name = trimmed };

        // The row takes the new name only once the server has it, the same order the delete path
        // uses: a title that reads as saved while nothing was written is undone by the next load.
        var saved = await _topicService.SaveTopicAsync(renamed);
        if (!saved.IsLive)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        _dispatcher.Dispatch(new UpdateTopic(StoredTopic.FromMetadata(renamed)));
    }

    public void Dispose()
    {
        _renameTopicRegistration.Dispose();
    }
}