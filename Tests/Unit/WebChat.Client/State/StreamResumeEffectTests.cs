using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class StreamResumeEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly TopicStreams _topicStreams;
    private readonly TaskCompletionSource _running = new();
    private readonly FakeStreamResumeService _streamResumeService = new();
    private readonly RecordingLogger<StreamResumeEffect> _logger = new();
    private readonly StreamResumeEffect _effect;

    public StreamResumeEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _topicStreams = new TopicStreams(_dispatcher, _messagesStore);

        _effect = new StreamResumeEffect(
            _dispatcher, _topicsStore, _topicStreams, _streamResumeService, _logger);
    }

    [Fact]
    public async Task RemoteStreamStarted_KnownTopic_ResumesTheStream()
    {
        _dispatcher.Dispatch(new AddTopic(Topic("topic-1")));

        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        await TestChat.Eventually(() => _streamResumeService.ResumedTopicIds.Contains("topic-1"));
        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    // An unknown topic has no row to resume with yet: nothing is marked streaming and nothing
    // is asked of the hub until the row arrives.
    [Fact]
    public void RemoteStreamStarted_UnknownTopic_LeavesTheStreamingStateAlone()
    {
        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();
    }

    // Another user wrote to a topic below this client's cursor: the push names a row not held,
    // and the refresh the same push triggers upserts that row a moment later. The resume waits
    // for the row instead of dying with the lookup, so the indicator and the live reply behave
    // as they do for a loaded row.
    [Fact]
    public async Task RemoteStreamStarted_TopicNotYetLoaded_ResumesWhenItsRowArrives()
    {
        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));
        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();

        _dispatcher.Dispatch(new UpdateTopic(Topic("topic-1")));

        await TestChat.Eventually(() => _streamResumeService.ResumedTopicIds.Contains("topic-1"));
    }

    [Fact]
    public async Task RemoteStreamStarted_ARowCreatedMomentsLater_ResumesOnItsArrival()
    {
        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        _dispatcher.Dispatch(new AddTopic(Topic("topic-1")));

        await TestChat.Eventually(() => _streamResumeService.ResumedTopicIds.Contains("topic-1"));
    }

    // The wait is settled by the first arrival; later copies of the row are ordinary upserts.
    [Fact]
    public async Task RemoteStreamStarted_TheRowArrivingTwice_ResumesOnce()
    {
        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));
        _dispatcher.Dispatch(new UpdateTopic(Topic("topic-1")));
        await TestChat.Eventually(() => _streamResumeService.ResumedTopicIds.Contains("topic-1"));

        _dispatcher.Dispatch(new UpdateTopic(Topic("topic-1")));

        _streamResumeService.ResumedTopicIds.Count.ShouldBe(1);
    }

    // Two pushes back to back for one topic resume it once: the second finds the first still
    // resuming and asks the module, not the store.
    [Fact]
    public void RemoteStreamStarted_TopicAlreadyResuming_DoesNotResumeAgain()
    {
        _dispatcher.Dispatch(new AddTopic(Topic("topic-1")));
        _topicStreams.TryBeginResume("topic-1");

        _dispatcher.Dispatch(new RemoteStreamStarted("topic-1"));

        _streamResumeService.ResumedTopicIds.ShouldBeEmpty();
    }

    private static StoredTopic Topic(string topicId) => new()
    {
        TopicId = topicId,
        ChatId = 123,
        ThreadId = 456,
        AgentId = "agent-1",
        Name = "Test Topic"
    };

    public void Dispose()
    {
        _running.TrySetResult();
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }
}