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
    private ITimer? _searchDebounce;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);
    private int _fetching;
    private TopicRange? _firstPageInFlight;

    // A delete is not a range change, so the range stamp cannot see it: a page read before the
    // delete committed and landing after would resurrect the row the server has already
    // confirmed gone. Each removal is numbered so a landing page can drop exactly the rows
    // deleted since it was asked for — a later answer that still carries the topic is the
    // server's own word that it exists.
    private long _removals;
    private readonly Dictionary<string, long> _removedAt = [];
    private readonly IDisposable _removedRegistration;

    public TopicPagingEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService,
        SpaceStore spaceStore,
        IStreamResumeService streamResumeService,
        TimeProvider timeProvider,
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
        // A search is a hub call now, so one is not made on every keystroke: the pause is what
        // turns a typed word into one search rather than one per letter. The store already holds
        // the latest query, so the fetch that finally runs asks for what was last typed.
        _searchRegistration = dispatcher.RegisterHandler<SearchTopics>(_ =>
        {
            _searchDebounce?.Dispose();
            _searchDebounce = timeProvider.CreateTimer(
                _ => LoadFirstPageAsync().LogFaults(_logger, nameof(SearchTopics)),
                null, SearchDebounce, Timeout.InfiniteTimeSpan);
        });
        _refreshRegistration = dispatcher.RegisterHandler<RefreshTopicList>(
            _ => RefreshTopAsync().LogFaults(_logger, nameof(RefreshTopicList)));
        _removedRegistration = dispatcher.RegisterHandler<TopicRemoved>(
            action => _removedAt[action.TopicId] = ++_removals);
    }

    // New activity, merged in rather than replacing the list. Only the ordinary list is refreshed
    // this way: a search and the archive are answers to a question the person asked, and rows
    // arriving into them unasked would be a different list than the one they are reading.
    public async Task RefreshTopAsync()
    {
        var range = CurrentRange();
        if (range is null || range.Archived || !string.IsNullOrWhiteSpace(range.SearchQuery))
        {
            return;
        }

        var asked = _removals;
        var page = await range.FetchPageAsync(_topicService, cursor: null);

        if (!page.IsLive || range != CurrentRange())
        {
            return;
        }

        // Upserted one by one, so a row already held gains the new copy in place instead of a
        // second row, and the cursor the person has paged down to survives.
        page.Value!.Topics
            .Select(StoredTopic.FromMetadata)
            .Where(topic => !RemovedSince(topic, asked))
            .ToList()
            .ForEach(topic => _dispatcher.Dispatch(new UpdateTopic(topic)));
    }

    // Switching between the ordinary list and the archive reads the other range of the same
    // index from the top. Paged exactly like the ordinary list, because it is the same query.
    public async Task LoadFirstPageAsync()
    {
        var range = CurrentRange();
        if (range is null || range == _firstPageInFlight)
        {
            return;
        }

        // Held per range rather than as one flag: swallowing a new range's ask behind an old
        // range's fetch would leave the new list never loaded, since the old answer is dropped.
        _firstPageInFlight = range;
        try
        {
            var asked = _removals;
            var page = await range.FetchPageAsync(_topicService, cursor: null);

            if (!page.IsLive || range != CurrentRange())
            {
                return;
            }

            var topics = page.Value!.Topics
                .Select(StoredTopic.FromMetadata)
                .Where(topic => !RemovedSince(topic, asked))
                .ToList();
            _dispatcher.Dispatch(new TopicsLoaded(topics, page.Value.NextCursor));
            TopicPageStreams.ResumeReported(topics, page.Value.LiveTopicIds, _streamResumeService, _logger);
        }
        finally
        {
            if (_firstPageInFlight == range)
            {
                _firstPageInFlight = null;
            }
        }
    }

    public async Task LoadNextPageAsync()
    {
        var state = _topicsStore.State;
        var range = CurrentRange();
        var cursor = state.Paging.Cursor;
        if (range is null || !state.Paging.HasMore || cursor is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _fetching, 1) == 1)
        {
            return;
        }

        try
        {
            var asked = _removals;
            var page = await range.FetchPageAsync(_topicService, cursor);

            // Not live is not an empty page. Storing it as one would end the range and leave the
            // rest of the list unreachable until the next agent change.
            // A live answer still lands only on the list that asked for it: same range, and a
            // list whose cursor is still the one this page was fetched below — a first page
            // replacing the list mid-flight makes this a continuation of nothing.
            if (!page.IsLive || range != CurrentRange() || _topicsStore.State.Paging.Cursor != cursor)
            {
                return;
            }

            var topics = page.Value!.Topics
                .Select(StoredTopic.FromMetadata)
                .Where(topic => !RemovedSince(topic, asked))
                .ToList();
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

    // What the list currently is, read fresh: comparing a fetch's stamp against this after the
    // await is what tells a stale answer from a current one.
    private TopicRange? CurrentRange() =>
        TopicRange.Of(_topicsStore.State, _spaceStore.State.CurrentSlug);

    private bool RemovedSince(StoredTopic topic, long asked) =>
        _removedAt.TryGetValue(topic.TopicId, out var removal) && removal > asked;

    public void Dispose()
    {
        _loadMoreRegistration.Dispose();
        _showArchivedRegistration.Dispose();
        _searchRegistration.Dispose();
        _refreshRegistration.Dispose();
        _removedRegistration.Dispose();
        _searchDebounce?.Dispose();
    }
}