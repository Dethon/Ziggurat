using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicPagingEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly SpaceStore _spaceStore;
    private readonly FakeTopicService _topicService;
    private readonly FakeStreamResumeService _streamResumeService = new();
    private readonly TopicPagingEffect _effect;

    public TopicPagingEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _topicService = new FakeTopicService(_calls) { PageSize = 2 };

        _effect = new TopicPagingEffect(
            _dispatcher,
            _topicsStore,
            _topicService,
            _spaceStore,
            _streamResumeService,
            new RecordingLogger<TopicPagingEffect>());
    }

    [Fact]
    public async Task ReachingTheEndOfTheList_FetchesThePageBelowTheCursor()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();

        await _effect.LoadNextPageAsync();

        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["topic-3", "topic-2", "topic-1", "topic-0"]);
    }

    [Fact]
    public async Task ReachingTheEndOfTheLastPage_AsksForNothingFurther()
    {
        // Three rows against a page of two, so the second page is short and ends the range.
        SeedTopics(3);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();
        await _effect.LoadNextPageAsync();

        _topicsStore.State.Paging.HasMore.ShouldBeFalse();
        var asked = _calls.Calls.Count(c => c.StartsWith("topics:"));

        await _effect.LoadNextPageAsync();

        _calls.Calls.Count(c => c.StartsWith("topics:")).ShouldBe(asked);
    }

    // Scrolling asks repeatedly while the person keeps moving. Two fetches for the same rows
    // would append the same page twice.
    [Fact]
    public async Task TwoAsksWhileOneIsInFlight_FetchOnePage()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();

        await Task.WhenAll(_effect.LoadNextPageAsync(), _effect.LoadNextPageAsync());

        _topicsStore.State.Topics.Select(t => t.TopicId)
            .ShouldBe(["topic-3", "topic-2", "topic-1", "topic-0"]);
    }

    // Not live is not the end of the range: storing it as one would leave the rest of the list
    // unreachable until the next agent change.
    [Fact]
    public async Task APageThatCouldNotBeFetched_LeavesTheCursorWhereItWas()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();
        var cursor = _topicsStore.State.Paging.Cursor;
        _topicService.NotLive = true;

        await _effect.LoadNextPageAsync();

        _topicsStore.State.Paging.Cursor.ShouldBe(cursor);
        _topicsStore.State.Paging.HasMore.ShouldBeTrue();
    }

    // The archive is the same query over the range below the cutoff, so it opens and pages like
    // the ordinary list and nothing about any topic is written to put it there.
    [Fact]
    public async Task ShowingTheArchive_ReadsTheOtherRangeFromTheTop()
    {
        SeedTopics(4);
        _topicService.ArchivedTopicIds.Add("topic-0");
        _topicService.ArchivedTopicIds.Add("topic-1");
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();

        _dispatcher.Dispatch(new ShowArchivedTopics(true));
        await _effect.LoadFirstPageAsync();

        _topicsStore.State.ShowingArchived.ShouldBeTrue();
        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["topic-1", "topic-0"]);
    }

    [Fact]
    public async Task PagingTheArchive_KeepsAskingForTheArchivedRange()
    {
        SeedTopics(4);
        Enumerable.Range(0, 4).ToList().ForEach(i => _topicService.ArchivedTopicIds.Add($"topic-{i}"));
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new ShowArchivedTopics(true));
        await _effect.LoadFirstPageAsync();

        await _effect.LoadNextPageAsync();

        _topicsStore.State.Topics.Select(t => t.TopicId)
            .ShouldBe(["topic-3", "topic-2", "topic-1", "topic-0"]);
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
    }

    // The client-side filter over loaded rows is gone: past the first page it could only ever
    // find what this client happened to hold.
    [Fact]
    public async Task Searching_AsksTheServerAndReplacesTheList()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        await LoadFirstPageAsync();

        _dispatcher.Dispatch(new SearchTopics("Topic 0"));
        await _effect.LoadFirstPageAsync();

        _calls.Calls.ShouldContain("search:Topic 0");
        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["topic-0"]);
    }

    [Fact]
    public async Task PagingASearch_KeepsAskingTheSearchCall()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new SearchTopics("Topic"));
        await _effect.LoadFirstPageAsync();

        await _effect.LoadNextPageAsync();

        _topicsStore.State.Topics.Select(t => t.TopicId)
            .ShouldBe(["topic-3", "topic-2", "topic-1", "topic-0"]);
        _calls.Calls.ShouldNotContain(call => call.StartsWith("topics:"));
    }

    // An emptied search box is the ordinary list again, not a search for nothing.
    [Fact]
    public async Task ClearingTheSearch_ReadsTheOrdinaryListAgain()
    {
        SeedTopics(4);
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new SearchTopics("Topic 0"));
        await _effect.LoadFirstPageAsync();

        _dispatcher.Dispatch(new SearchTopics(""));
        await _effect.LoadFirstPageAsync();

        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["topic-3", "topic-2"]);
    }

    private void SeedTopics(int count)
    {
        var start = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        foreach (var i in Enumerable.Range(0, count))
        {
            _topicService.SeedTopic(new global::Domain.DTOs.WebChat.TopicMetadata(
                $"topic-{i}", 100 + i, 0, "agent-1", $"Topic {i}", start, start.AddMinutes(i)));
        }
    }

    private async Task LoadFirstPageAsync()
    {
        var page = await _topicService.GetTopicPageAsync("agent-1");
        _dispatcher.Dispatch(new TopicsLoaded(
            page.Value!.Topics.Select(global::WebChat.Client.Models.StoredTopic.FromMetadata).ToList(),
            page.Value.NextCursor));
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _spaceStore.Dispose();
    }
}