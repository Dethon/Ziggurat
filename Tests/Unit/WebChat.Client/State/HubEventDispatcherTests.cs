using Domain.DTOs.WebChat;
using Moq;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class HubEventDispatcherTests : IDisposable
{
    private readonly Mock<IDispatcher> _mockDispatcher;
    private readonly Dispatcher _realDispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly TopicStreams _topicStreams;
    private readonly TaskCompletionSource _running = new();
    private readonly HubEventDispatcher _sut;

    public HubEventDispatcherTests()
    {
        _mockDispatcher = new Mock<IDispatcher>();
        _realDispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_realDispatcher);
        _messagesStore = new MessagesStore(_realDispatcher);
        _streamingStore = new StreamingStore(_realDispatcher);
        // The module writes into the real stores so a push can be asserted by what it left
        // behind; the mock stays on the dispatcher the type under test uses directly.
        _topicStreams = new TopicStreams(_realDispatcher, _messagesStore);
        var mockPipeline = new Mock<IMessagePipeline>();
        _sut = new HubEventDispatcher(
            _mockDispatcher.Object,
            _topicsStore,
            _topicStreams,
            mockPipeline.Object);
    }

    public void Dispose()
    {
        _running.TrySetResult();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }

    private static TopicMetadata CreateTopicMetadata(string topicId = "topic-1") =>
        new(topicId, 123L, 456L, "agent-1", "Test Topic", DateTimeOffset.UtcNow, null);

    [Fact]
    public void HandleTopicChanged_Created_DispatchesAddTopic()
    {
        var metadata = CreateTopicMetadata();
        var notification = new TopicChangedNotification(TopicChangeType.Created, "topic-1", metadata);

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<AddTopic>(a => a.Topic.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleTopicChanged_Updated_DispatchesUpdateTopic()
    {
        var metadata = CreateTopicMetadata();
        var notification = new TopicChangedNotification(TopicChangeType.Updated, "topic-1", metadata);

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<UpdateTopic>(a => a.Topic.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    public void HandleTopicChanged_Deleted_DispatchesRemoveTopic()
    {
        var notification = new TopicChangedNotification(TopicChangeType.Deleted, "topic-1");

        _sut.HandleTopicChanged(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<RemoveTopic>(a => a.TopicId == "topic-1")),
            Times.Once);
    }

    [Fact]
    // Whether the start is resumed or merely marked is StreamResumeEffect's decision; the
    // dispatcher only reports what the server pushed.
    public void HandleStreamStarted_KnownTopic_StillOnlyDispatches()
    {
        var topic = new StoredTopic
        {
            TopicId = "topic-1",
            ChatId = 123,
            ThreadId = 456,
            AgentId = "agent-1",
            Name = "Test Topic"
        };
        _realDispatcher.Dispatch(new AddTopic(topic));

        var notification = new StreamStartedNotification("topic-1");

        _sut.HandleStreamStarted(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<RemoteStreamStarted>(a => a.TopicId == "topic-1")),
            Times.Once);
        _mockDispatcher.Verify(d => d.Dispatch(It.IsAny<StreamStarted>()), Times.Never);
    }

    [Fact]
    public void HandleApprovalResolved_DispatchesApprovalResolved()
    {
        var notification = new ApprovalResolvedNotification("topic-1", "approval-123");

        _sut.HandleApprovalResolved(notification);

        _mockDispatcher.Verify(
            d => d.Dispatch(It.Is<ApprovalResolved>(a => a.ApprovalId == "approval-123")),
            Times.Once);
    }

    // Taking the prompt off screen is all the push does. What the approval let through comes down
    // the topic's stream with the rest of the reply, so this must add nothing of its own.
    [Fact]
    public void HandleApprovalResolved_AddsNothingToTheReplyInFlight()
    {
        GivenAReplyInFlight("topic-1", "msg-being-written");

        _sut.HandleApprovalResolved(new ApprovalResolvedNotification("topic-1", "approval-123"));

        _streamingStore.State.StreamingByTopic["topic-1"].HasContent.ShouldBeFalse();
    }

    private void GivenAReplyInFlight(string topicId, string? currentMessageId = null) =>
        _topicStreams.TryOpen(
            topicId, new ChatMessageModel { Role = "assistant" }, currentMessageId, _ => _running.Task);
}