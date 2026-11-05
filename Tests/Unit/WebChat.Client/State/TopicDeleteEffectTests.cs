using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicDeleteEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ApprovalStore _approvalStore;
    private readonly ToastStore _toastStore;
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly FakeTopicService _topicService;
    private readonly RecordingLogger<TopicDeleteEffect> _logger = new();
    private readonly TopicStreams _topicStreams;
    private readonly TaskCompletionSource _running = new();
    private readonly TopicDeleteEffect _effect;

    public TopicDeleteEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, new TopicStreams(_dispatcher, _messagesStore),
            NullLogger<MessagePipeline>.Instance);

        _topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        _effect = new TopicDeleteEffect(
            _dispatcher,
            _topicStreams,
            _messagingService,
            _topicService,
            pipeline,
            _logger);
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_ClientInitiated_DeletesOnTheServerAndClearsMessages()
    {
        GivenTopicWithMessages("topic-1");

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _topicService.DeletedTopicIds.ShouldBe(["topic-1"]);
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_ServerNotification_DoesNotDeleteAgain()
    {
        GivenTopicWithMessages("topic-1");

        await _effect.HandleRemoveTopicAsync("topic-1");

        _topicService.DeletedTopicIds.ShouldBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_TopicIsStreaming_CancelsTheStreamFirst()
    {
        GivenTopicWithMessages("topic-1");
        GivenAReplyInFlight("topic-1").Append(new ChatStreamMessage { Content = "partial", MessageId = "m-2" });

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _messagingService.CancelledTopics.ShouldBe(["topic-1"]);
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    // The owner made the topic private while its reply streamed on another member's page: the page may
    // no longer see it and drops it, and stops reading the reply — it never asks the server to stop a
    // reply that is not its own (the hub refused, "No such conversation", the removal threw and the
    // page kept the topic and its reply growing).
    [Fact]
    public async Task HandleRemoveTopicAsync_ServerNotificationOnAStreamingTopic_StopsReadingWithoutCancellingTheReply()
    {
        GivenTopicWithMessages("topic-1");
        GivenAReplyInFlight("topic-1");

        await _effect.HandleRemoveTopicAsync("topic-1");

        _messagingService.CancelledTopics.ShouldBeEmpty();
        _topicsStore.State.Topics.ShouldBeEmpty();
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    // The server already deleted the topic; the notification only reports it. A cancel that cannot
    // be made is best-effort cleanup, not a failed user action — the row must still leave, and no
    // toast may blame the user for something they never did.
    [Fact]
    public async Task HandleRemoveTopicAsync_ServerDeletedAStreamingTopicWhileNotLive_RemovesTheRowWithoutAToast()
    {
        GivenTopicWithMessages("topic-1");
        GivenAReplyInFlight("topic-1");
        _messagingService.NotLive = true;

        await _effect.HandleRemoveTopicAsync("topic-1");

        _topicsStore.State.Topics.ShouldBeEmpty();
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
        _toastStore.State.Toasts.ShouldBeEmpty();
    }

    // The user asked for this one, so a cancel that cannot be made keeps its answer: the row stays
    // and the toast says the delete did not go through.
    [Fact]
    public async Task HandleRemoveTopicAsync_UserDeletesAStreamingTopicWhileNotLive_KeepsTheRowAndShowsTheToast()
    {
        GivenTopicWithMessages("topic-1");
        GivenAReplyInFlight("topic-1");
        _messagingService.NotLive = true;

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _topicsStore.State.Topics.ShouldNotBeEmpty();
        _toastStore.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_SelectedTopic_ClearsThePendingApproval()
    {
        GivenTopicWithMessages("topic-1");
        _dispatcher.Dispatch(new SelectTopic("topic-1"));
        _dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _approvalStore.State.CurrentRequest.ShouldBeNull();
    }

    [Fact]
    public async Task HandleRemoveTopicAsync_OtherTopicSelected_LeavesThePendingApproval()
    {
        GivenTopicWithMessages("topic-1");
        _dispatcher.Dispatch(new SelectTopic("topic-2"));
        _dispatcher.Dispatch(new ShowApproval("topic-2", new ToolApprovalRequestMessage("approval-1", [])));

        await _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);

        _approvalStore.State.CurrentRequest.ShouldNotBeNull();
    }

    // The user can switch conversations while the delete's round trip is in flight. Whether
    // the pending approval belongs to the deleted conversation is decided by the selection at
    // dispatch time, not the one from before the awaits — or the other topic's prompt is wiped.
    [Fact]
    public async Task HandleRemoveTopicAsync_UserSwitchedTopicsDuringTheDelete_LeavesTheOtherTopicsApproval()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));
        _topicService.DeleteGate = new TaskCompletionSource();

        var delete = _effect.HandleRemoveTopicAsync("topic-1", "agent-1", chatId: 10, threadId: 20);
        _dispatcher.Dispatch(new SelectTopic("topic-2"));
        _dispatcher.Dispatch(new ShowApproval("topic-2", new ToolApprovalRequestMessage("approval-1", [])));
        _topicService.DeleteGate.SetResult();
        await delete;

        _approvalStore.State.CurrentRequest.ShouldNotBeNull();
    }

    [Fact]
    public async Task Dispatch_RemoveTopic_RunsTheSameWork()
    {
        GivenTopicWithMessages("topic-1");

        _dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => _topicService.DeletedTopicIds.Contains("topic-1"));
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task Dispatch_RemoveTopic_FaultIsLoggedRatherThanDiscarded()
    {
        GivenTopicWithMessages("topic-1");
        _topicService.ThrowOnDeleteTopic = new InvalidOperationException("delete rejected");

        _dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("delete rejected");
    }

    [Fact]
    public async Task Disposed_StopsHandlingRemoveTopic()
    {
        GivenTopicWithMessages("topic-1");
        _effect.Dispose();

        _dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await Task.Delay(50);
        _topicService.DeletedTopicIds.ShouldBeEmpty();
    }

    private static StoredTopic Topic(string topicId) => new()
    {
        TopicId = topicId,
        ChatId = 10,
        ThreadId = 20,
        AgentId = "agent-1",
        Name = "Topic"
    };

    private StreamLease GivenAReplyInFlight(string topicId) =>
        _topicStreams.TryOpen(
            topicId, new ChatMessageModel { Role = "assistant" }, null, _ => _running.Task)!;

    private void GivenTopicWithMessages(string topicId)
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic(topicId)]));
        _dispatcher.Dispatch(new MessagesLoaded(topicId, [
            new ChatMessageModel { Role = "assistant", Content = "hello", MessageId = "m-1" }
        ]));
        _calls.Reset();
    }

    public void Dispose()
    {
        _running.TrySetResult();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _approvalStore.Dispose();
        _toastStore.Dispose();
    }
}