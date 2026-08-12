using Domain.DTOs.WebChat;
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
    private readonly IStreamResumeService _streamResumeService;
    private readonly ILogger<TopicPagingEffect> _logger;
    private readonly IDisposable _loadMoreRegistration;
    private readonly IDisposable _showArchivedRegistration;
    private readonly IDisposable _searchRegistration;
    private readonly IDisposable _refreshRegistration;
    private int _fetching;

    public TopicPagingEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService,
        SpaceStore spaceStore,
        IStreamResumeService streamResumeService,
        ILogger<TopicPagingEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _topicService = topicService;
        _spaceStore = spaceStore;
        _streamResumeService = streamResumeService;
        _logger = logger;

        _loadMoreRegistration = dispatcher.RegisterHandler<LoadMoreTopics>(
            _ => LoadNextPageAsync().LogFaults(_logger, nameof(LoadMoreTopics)));
        _showArchivedRegistration = dispatcher.RegisterHandler<ShowArchivedTopics>(
            _ => LoadFirstPageAsync().LogFaults(_logger, nameof(ShowArchivedTopics)));
        _searchRegistration = dispatcher.RegisterHandler<SearchTopics>(
            _ => LoadFirstPageAsync().LogFaults(_logger, nameof(SearchTopics)));
        _refreshRegistration = dispatcher.RegisterHandler<RefreshTopicList>(
            _ => RefreshTopAsync().LogFaults(_logger, nameof(RefreshTopicList)));
    }

    // New activity, merged in rather than replacing the list. Only the ordinary list is refreshed
    // this way: a search and the archive are answers to a question the person asked, and rows
    // arriving into them unasked would be a different list than the one they are reading.
    public async Task RefreshTopAsync()
    {
        var state = _topicsStore.State;
        if (state.SelectedAgentId is null
            || state.ShowingArchived
            || !string.IsNullOrWhiteSpace(state.SearchQuery))
        {
            return;
        }

        var page = await _topicService.GetTopicPageAsync(
            state.SelectedAgentId, _spaceStore.State.CurrentSlug);

        if (!page.IsLive)
        {
            return;
        }

        // Upserted one by one, so a row already held gains the new copy in place instead of a
        // second row, and the cursor the person has paged down to survives.
        page.Value!.Topics
            .Select(StoredTopic.FromMetadata)
            .ToList()
            .ForEach(topic => _dispatcher.Dispatch(new UpdateTopic(topic)));
    }

    // Switching between the ordinary list and the archive reads the other range of the same
    // index from the top. Paged exactly like the ordinary list, because it is the same query.
    public async Task LoadFirstPageAsync()
    {
        var state = _topicsStore.State;
        if (state.SelectedAgentId is null)
        {
            return;
        }

        var page = await FetchAsync(state, cursor: null);

        if (!page.IsLive)
        {
            return;
        }

        var topics = page.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
        _dispatcher.Dispatch(new TopicsLoaded(topics, page.Value.NextCursor));
        TopicPageStreams.ResumeReported(topics, page.Value.LiveTopicIds, _streamResumeService, _logger);
    }

    // Which call a page fetch is depends on what the list currently is: a search, the archive,
    // or the ordinary list. Paged the same way whichever it is.
    private Task<HubResult<TopicPage>> FetchAsync(TopicsState state, string? cursor) =>
        string.IsNullOrWhiteSpace(state.SearchQuery)
            ? _topicService.GetTopicPageAsync(
                state.SelectedAgentId!, _spaceStore.State.CurrentSlug, cursor, state.ShowingArchived)
            : _topicService.SearchTopicsAsync(
                state.SelectedAgentId!, state.SearchQuery, _spaceStore.State.CurrentSlug, cursor);

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
            var page = await FetchAsync(state, state.Paging.Cursor);

            // Not live is not an empty page. Storing it as one would end the range and leave the
            // rest of the list unreachable until the next agent change.
            if (!page.IsLive)
            {
                return;
            }

            var topics = page.Value!.Topics.Select(StoredTopic.FromMetadata).ToList();
            _dispatcher.Dispatch(new TopicsPageAppended(topics, page.Value.NextCursor));

            // A reply in flight on a topic further down the list is reported when that page is
            // loaded, which is the only moment the client learns the row exists at all.
            TopicPageStreams.ResumeReported(topics, page.Value.LiveTopicIds, _streamResumeService, _logger);
        }
        finally
        {
            Volatile.Write(ref _fetching, 0);
        }
    }

    public void Dispose()
    {
        _loadMoreRegistration.Dispose();
        _showArchivedRegistration.Dispose();
        _searchRegistration.Dispose();
        _refreshRegistration.Dispose();
    }
}