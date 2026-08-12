using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// Fetching the page below the cursor, and nothing else. Scrolling asks repeatedly while the
// person keeps moving, so a fetch already in flight swallows the next ask rather than opening a
// second one for the same rows.
public sealed class TopicPagingEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly ITopicService _topicService;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<TopicPagingEffect> _logger;
    private readonly IDisposable _loadMoreRegistration;
    private int _fetching;

    public TopicPagingEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService,
        SpaceStore spaceStore,
        ILogger<TopicPagingEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _topicService = topicService;
        _spaceStore = spaceStore;
        _logger = logger;

        _loadMoreRegistration = dispatcher.RegisterHandler<LoadMoreTopics>(
            _ => LoadNextPageAsync().LogFaults(_logger, nameof(LoadMoreTopics)));
    }

    public async Task LoadNextPageAsync()
    {
        var state = _topicsStore.State;
        if (state.SelectedAgentId is null || !state.Paging.HasMore || state.Paging.Cursor is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _fetching, 1) == 1)
        {
            return;
        }

        try
        {
            var page = await _topicService.GetTopicPageAsync(
                state.SelectedAgentId, _spaceStore.State.CurrentSlug, state.Paging.Cursor);

            // Not live is not an empty page. Storing it as one would end the range and leave the
            // rest of the list unreachable until the next agent change.
            if (!page.IsLive)
            {
                return;
            }

            _dispatcher.Dispatch(new TopicsPageAppended(
                page.Value!.Topics.Select(StoredTopic.FromMetadata).ToList(), page.Value.NextCursor));
        }
        finally
        {
            Volatile.Write(ref _fetching, 0);
        }
    }

    public void Dispose() => _loadMoreRegistration.Dispose();
}