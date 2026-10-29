using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ReconnectionEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly SpaceStore _spaceStore;
    private readonly MessagesStore _messagesStore;
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
        _messagesStore = new MessagesStore(_dispatcher);
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

    // The person was reading the archive when the connection dropped. Catch-up re-reads that
    // range, not the ordinary one: the toggle stays visibly on, so ordinary rows underneath it
    // would be another list wearing the archive's clothes.
    [Fact]
    public async Task WhenConnectionReconnected_WhileShowingTheArchive_ReloadsTheArchivedRange()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new ShowArchivedTopics(true));
        var now = DateTimeOffset.UtcNow;
        _mockTopicService
            .Setup(s => s.GetTopicPageAsync("agent-1", "default", null, true))
            .ReturnsAsync(HubResult<TopicPage>.Answered(new TopicPage(
                [new TopicMetadata("topic-a", 1, 1, "agent-1", "Archived", now, null)], null)));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
        _topicsStore.State.Topics.Single().TopicId.ShouldBe("topic-a");
        _mockTopicService.Verify(
            s => s.GetTopicPageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), false),
            Times.Never);
    }

    [Fact]
    public async Task WhenConnectionReconnected_WhileSearching_ReloadsTheSearchRange()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new SearchTopics("abc"));
        var now = DateTimeOffset.UtcNow;
        _mockTopicService
            .Setup(s => s.SearchTopicsAsync("agent-1", "abc", "default", null))
            .ReturnsAsync(HubResult<TopicPage>.Answered(new TopicPage(
                [new TopicMetadata("topic-s", 1, 1, "agent-1", "abc things", now, null)], null)));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
        _topicsStore.State.Topics.Single().TopicId.ShouldBe("topic-s");
        _mockTopicService.Verify(
            s => s.GetTopicPageAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    // The open conversation had been scrolled deep into the list; catch-up replaces the list
    // with its first page, which does not hold that row. The session restart and history reload
    // belong to the selection, not to the row's luck of being on page one.
    [Fact]
    public async Task WhenConnectionReconnected_ASelectedTopicBelowTheFirstPage_StillRestartsItsSession()
    {
        var deep = new StoredTopic
        { TopicId = "topic-deep", AgentId = "agent-1", ChatId = 123, ThreadId = 456, Name = "Deep" };
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new TopicsLoaded([deep]));
        _dispatcher.Dispatch(new SelectTopic("topic-deep"));

        var now = DateTimeOffset.UtcNow;
        _mockTopicService
            .Setup(s => s.GetTopicPageAsync("agent-1", "default", null))
            .ReturnsAsync(HubResult<TopicPage>.Answered(new TopicPage(
                [new TopicMetadata("topic-top", 9, 9, "agent-1", "Top", now, null)], null)));
        _mockSessionService
            .Setup(s => s.StartSessionAsync(It.IsAny<StoredTopic>()))
            .ReturnsAsync(HubResult<bool>.Answered(true));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _mockSessionService.Invocations.Any(
            i => i.Method.Name == nameof(IChatSessionService.StartSessionAsync)));

        _mockSessionService.Verify(
            s => s.StartSessionAsync(It.Is<StoredTopic>(t => t.TopicId == "topic-deep")),
            Times.Once);
        _mockTopicService.Verify(s => s.GetHistoryAsync("agent-1", 123, 456), Times.Once);
    }

    // The reload replaces every bubble in the open conversation. A picture that was sent
    // before the connection dropped is part of that history, and it came back to the bubbles
    // on a cold load — so it must come back on this one too, or backgrounding the app on a
    // phone quietly strips the thumbnails until a full refresh.
    [Fact]
    public async Task WhenConnectionReconnected_TheReloadedHistoryKeepsItsAttachments()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 123, ThreadId = 456, Name = "Test Topic" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic(topic.TopicId));

        var picture = new AttachmentReference
        { Id = "att-1", FileName = "cat.png", MediaType = "image/png", SizeBytes = 1234 };
        _mockTopicService
            .Setup(s => s.GetHistoryAsync("agent-1", 123, 456))
            .ReturnsAsync(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered(
            [
                new ChatHistoryMessage("m-1", "user", "look", "u-1", DateTimeOffset.UnixEpoch, [picture])
            ]));

        CreateEffect();

        _dispatcher.Dispatch(new ConnectionConnected());
        _dispatcher.Dispatch(new ConnectionReconnecting());
        _dispatcher.Dispatch(new ConnectionReconnected());

        await TestChat.Eventually(() => _messagesStore.State.MessagesByTopic.ContainsKey("topic-1"));

        var reloaded = _messagesStore.State.MessagesByTopic["topic-1"].Single();
        reloaded.Attachments.ShouldNotBeNull();
        reloaded.Attachments.Single().ShouldBe(picture);
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
        _messagesStore.Dispose();
    }
}