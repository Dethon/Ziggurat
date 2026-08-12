using Domain.DTOs.WebChat;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ReconnectionEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly SpaceStore _spaceStore;
    private readonly Mock<IChatSessionService> _mockSessionService;
    private readonly Mock<IStreamResumeService> _mockStreamResumeService;
    private readonly Mock<ITopicService> _mockTopicService;
    private readonly RecordingLogger<ReconnectionEffect> _logger = new();
    private ReconnectionEffect? _sut;

    public ReconnectionEffectTests()
    {
        _dispatcher = new Dispatcher();
        _connectionStore = new ConnectionStore(_dispatcher);
        _topicsStore = new TopicsStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _mockSessionService = new Mock<IChatSessionService>();
        _mockStreamResumeService = new Mock<IStreamResumeService>();
        _mockTopicService = new Mock<ITopicService>();

        _mockTopicService
            .Setup(s => s.GetHistoryAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered([]));
    }

    private void CreateEffect()
    {
        _sut = new ReconnectionEffect(
            _connectionStore,
            _topicsStore,
            _spaceStore,
            _mockSessionService.Object,
            _mockStreamResumeService.Object,
            _dispatcher,
            _mockTopicService.Object,
            _logger);
    }

    // The catch-up task is abandoned by construction, so a fault inside it — a server that
    // drops the topics call mid-recovery — must at least be logged rather than vanishing.
    [Fact]
    public async Task WhenTheCatchUpFaults_TheFaultIsLoggedRatherThanDiscarded()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _mockTopicService
            .Setup(s => s.GetTopicPageAsync("agent-1", "default", null))
            .ThrowsAsync(new InvalidOperationException("catch-up failed"));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("catch-up failed");
    }

    [Fact]
    public async Task WhenConnectionReconnected_StartsSessionForSelectedTopic()
    {
        var topic = new StoredTopic { TopicId = "topic-1", Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        _mockSessionService
            .Setup(s => s.StartSessionAsync(It.IsAny<StoredTopic>()))
            .ReturnsAsync(HubResult<bool>.Answered(true));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await Task.Delay(50); // Allow async handler to complete

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    // The sweep over every held topic is gone: the page says which replies are in flight, so
    // recovery resumes exactly those and asks about nothing else.
    public async Task WhenConnectionReconnected_ResumesOnlyTheStreamsThePageReported()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        var now = DateTimeOffset.UtcNow;
        _mockTopicService
            .Setup(s => s.GetTopicPageAsync("agent-1", "default", null))
            .ReturnsAsync(HubResult<TopicPage>.Answered(new TopicPage(
                [
                    new TopicMetadata("topic-1", 1, 1, "agent-1", "Topic 1", now, null),
                    new TopicMetadata("topic-2", 2, 2, "agent-1", "Topic 2", now, null)
                ],
                null,
                ["topic-2"])));

        _mockStreamResumeService
            .Setup(s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()))
            .Returns(Task.CompletedTask);

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 2);

        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-2")),
            Times.Once);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-1")),
            Times.Never);
    }

    [Fact]
    public void WhenConnectionReconnecting_DoesNotTriggerYet()
    {
        var topic = new StoredTopic { TopicId = "topic-1", Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        CreateEffect();

        // Only reconnecting, not yet reconnected
        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.IsAny<StoredTopic>()),
            Times.Never);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenTheEpochDoesNotAdvance_DoesNotReloadAgain()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 123, ThreadId = 456, Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionConnected());
        await Task.Delay(50);

        // Status churn that leaves the epoch where it is must not reload a second time.
        _dispatcher.Dispatch(new ConnectionClosed("hub dropped"));
        _dispatcher.Dispatch(new ConnectionConnecting());

        await Task.Delay(50);

        _mockTopicService.Verify(
            s => s.GetHistoryAsync("agent-1", 123, 456),
            Times.Once);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromStore()
    {
        CreateEffect();
        _sut!.Dispose();

        // After dispose, reconnection should not trigger callbacks
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await Task.Delay(50); // Give time for any (incorrectly) triggered callbacks

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.IsAny<StoredTopic>()),
            Times.Never);
        _mockStreamResumeService.Verify(
            s => s.TryResumeStreamAsync(It.IsAny<StoredTopic>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenConnectionReconnected_RefetchesTopicsFromServer()
    {
        var existingTopic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 123, ThreadId = 456, Name = "Existing" };
        _dispatcher.Dispatch(new TopicsLoaded([existingTopic]));
        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        // Server now has an additional topic that was created while disconnected
        var now = DateTimeOffset.UtcNow;
        var serverTopics = new List<TopicMetadata>
        {
            new("topic-1", 123, 456, "agent-1", "Existing", now, null),
            new("topic-2", 789, 101, "agent-1", "New Topic", now, null)
        };
        _mockTopicService
            .Setup(s => s.GetTopicPageAsync("agent-1", "default", null))
            .ReturnsAsync(HubResult<TopicPage>.Answered(new TopicPage(serverTopics, null)));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 2);

        _mockTopicService.Verify(s => s.GetTopicPageAsync("agent-1", "default", null), Times.Once);
        _topicsStore.State.Topics.ShouldContain(t => t.TopicId == "topic-2");
    }

    public void Dispose()
    {
        _sut?.Dispose();
        _connectionStore.Dispose();
        _topicsStore.Dispose();
        _spaceStore.Dispose();
    }
}