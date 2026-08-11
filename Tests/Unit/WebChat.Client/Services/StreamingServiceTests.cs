using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class StreamingServiceTests : IDisposable
{
    private readonly FakeChatMessagingService _messagingService = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ToastStore _toastStore;
    private readonly ApprovalStore _approvalStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly AgentSettingsStore _agentSettingsStore;
    private readonly FakeTopicService _topicService = new();
    private readonly TopicStreams _topicStreams;
    private readonly StreamingService _service;

    public StreamingServiceTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _approvalStore = new ApprovalStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _agentSettingsStore = new AgentSettingsStore(_dispatcher);
        _topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        _service = new StreamingService(
            _messagingService,
            _dispatcher,
            _topicService,
            _topicsStore,
            _messagesStore,
            _agentSettingsStore,
            new ComposerStore(_dispatcher),
            _topicStreams);
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _toastStore.Dispose();
        _approvalStore.Dispose();
        _userIdentityStore.Dispose();
        _agentSettingsStore.Dispose();
    }

    private StoredTopic CreateTopic(string? topicId = null, string agentId = "test-agent")
    {
        var topic = new StoredTopic
        {
            TopicId = topicId ?? Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = agentId,
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
        _dispatcher.Dispatch(new AddTopic(topic));
        return topic;
    }

    private IReadOnlyList<ChatMessageModel> MessagesFor(string topicId)
        => _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId) ?? [];

    // The tracked entry points return once the stream is open, not once it has finished, so a
    // test that wants to read what the reply left behind has to wait for the topic to stop
    // having one.
    private async Task SendAndDrainAsync(StoredTopic topic, string message = "test")
    {
        await _service.SendMessageAsync(topic, message);
        await DrainAsync(topic);
    }

    private async Task<bool> ResumeAndDrainAsync(
        StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId)
    {
        var lease = _topicStreams.TryBeginResume(topic.TopicId);
        if (lease is null)
        {
            return false;
        }

        _service.TryShowResumedStream(lease, streamingMessage, startMessageId);
        var started = await _service.TryReadResumedStreamAsync(lease, topic);
        if (!started)
        {
            lease.Complete();
        }

        await DrainAsync(topic);
        return started;
    }

    // The stream's own ending, not a five-second look for its after-effects: a lease that is
    // still open hands back a task that finishes when it ends, and a topic with no stream left
    // has already ended.
    private Task DrainAsync(StoredTopic topic) =>
        _topicStreams.Snapshot(topic.TopicId).Completion ?? Task.CompletedTask;

    public enum ExceptionKind
    {
        OperationCanceled,
        TaskCanceled,
        EmptyMessage,
        InvalidOperation,
        ContainsOperationCanceledText
    }

    public enum ErrorChunkKind
    {
        OperationCanceledText,
        TaskCanceledText,
        OperationWasCanceledText,
        NonTransient
    }

    #region SendMessageAsync stream body tests

    [Fact]
    public async Task SendMessageAsync_WithContent_AccumulatesInStreamingMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueContent("Hello", " world");

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Hello world");
    }

    [Fact]
    public async Task SendMessageAsync_OnComplete_StopsStreamingAndPersistsTopic()
    {
        // Merges three originals: OnComplete_StopsStreaming, OnComplete_UpdatesTopicTimestamp,
        // OnComplete_CallsTopicService. All share a single-content arrange; we assert all
        // three observables in one pass to avoid variant explosion.
        var topic = CreateTopic();
        topic.LastMessageAt = null;
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueContent("Done");

        await SendAndDrainAsync(topic);

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
        _topicService.SavedTopics.Count.ShouldBe(1);
        _topicService.SavedTopics[0].LastMessageAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task SendMessageAsync_WithError_StopsStreaming()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueError("Something went wrong");

        await SendAndDrainAsync(topic);

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task SendMessageAsync_WithApprovalRequest_DispatchesShowApproval()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var approval = new ToolApprovalRequestMessage("approval-1", []);
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "After approval", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages.ShouldContain(m => m.Content == "After approval");
    }

    [Fact]
    public async Task SendMessageAsync_WithReasoning_AccumulatesCorrectly()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Reasoning = "Thinking", MessageId = messageId },
            new ChatStreamMessage { Content = "Answer", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldBe("Thinking");
        messages[0].Content.ShouldBe("Answer");
    }

    [Fact]
    public async Task SendMessageAsync_AccumulatesToolCalls()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var messageId = Guid.NewGuid().ToString();
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ToolCalls = "tool_1", MessageId = messageId },
            new ChatStreamMessage { ToolCalls = "tool_2", MessageId = messageId },
            new ChatStreamMessage { Content = "Result", MessageId = messageId },
            new ChatStreamMessage { IsComplete = true, MessageId = messageId }
        );

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages[0].ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public async Task SendMessageAsync_InterleavedMessageIds_PreservesAllContentPerMessage()
    {
        // Distinct test — exercises the backend race where a later message's chunk arrives
        // between an earlier message's chunks (see project_webchat_interleaved_messageid_bubble_loss).
        // Kept separate from MultiTurn to preserve the race-specific arrangement verbatim.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "First part ", MessageId = "msg-1" },
            new ChatStreamMessage { ToolCalls = "tool_a", MessageId = "msg-2" },
            new ChatStreamMessage { Content = "second part", MessageId = "msg-1" },
            new ChatStreamMessage { Content = "msg2 text", MessageId = "msg-2" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        var msg1 = messages.FirstOrDefault(m => m.MessageId == "msg-1");
        var msg2 = messages.FirstOrDefault(m => m.MessageId == "msg-2");

        msg1.ShouldNotBeNull();
        msg1.Content.ShouldBe("First part second part");
        msg2.ShouldNotBeNull();
        msg2.Content.ShouldBe("msg2 text");
        msg2.ToolCalls.ShouldBe("tool_a");
    }

    public enum TurnShape
    {
        MultiTurnContent,
        ReasoningOnlyThenContent,
        ToolCallsOnlyThenContent,
        EmptyOnly
    }

    public static TheoryData<TurnShape> TurnShapes => new()
    {
        TurnShape.MultiTurnContent,
        TurnShape.ReasoningOnlyThenContent,
        TurnShape.ToolCallsOnlyThenContent,
        TurnShape.EmptyOnly,
    };

    [Theory]
    [MemberData(nameof(TurnShapes))]
    public async Task SendMessageAsync_FinalizesEachTurn(TurnShape shape)
    {
        // Merges 4 originals: MultiTurn_SeparatesTurns, ReasoningOnlyTurn_FinalizesToStore,
        // ToolCallsOnlyTurn_FinalizesToStore, WithEmptyMessage_DoesNotAddToHistory.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        switch (shape)
        {
            case TurnShape.MultiTurnContent:
                _messagingService.EnqueueMessages(
                    new ChatStreamMessage { Content = "First turn", MessageId = "msg-1" },
                    new ChatStreamMessage { Content = "Second turn", MessageId = "msg-2" },
                    new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });
                break;
            case TurnShape.ReasoningOnlyThenContent:
                _messagingService.EnqueueMessages(
                    new ChatStreamMessage { Reasoning = "Thinking step", MessageId = "msg-1" },
                    new ChatStreamMessage { Content = "Final answer", MessageId = "msg-2" },
                    new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });
                break;
            case TurnShape.ToolCallsOnlyThenContent:
                _messagingService.EnqueueMessages(
                    new ChatStreamMessage { ToolCalls = "search(\"query\")", MessageId = "msg-1" },
                    new ChatStreamMessage { Content = "Found results", MessageId = "msg-2" },
                    new ChatStreamMessage { IsComplete = true, MessageId = "msg-2" });
                break;
            case TurnShape.EmptyOnly:
                _messagingService.EnqueueMessages(
                    new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });
                break;
        }

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        switch (shape)
        {
            case TurnShape.MultiTurnContent:
                messages.Count.ShouldBe(2);
                messages[0].Content.ShouldBe("First turn");
                messages[1].Content.ShouldBe("Second turn");
                break;
            case TurnShape.ReasoningOnlyThenContent:
                messages.Count.ShouldBe(2);
                messages[0].Reasoning.ShouldBe("Thinking step");
                messages[0].Content.ShouldBeEmpty();
                messages[1].Content.ShouldBe("Final answer");
                break;
            case TurnShape.ToolCallsOnlyThenContent:
                messages.Count.ShouldBe(2);
                messages[0].ToolCalls.ShouldBe("search(\"query\")");
                messages[0].Content.ShouldBeEmpty();
                messages[1].Content.ShouldBe("Found results");
                break;
            case TurnShape.EmptyOnly:
                messages.ShouldBeEmpty();
                break;
        }
    }

    [Theory]
    [InlineData(ExceptionKind.OperationCanceled, false, null)]
    [InlineData(ExceptionKind.TaskCanceled, false, null)]
    [InlineData(ExceptionKind.EmptyMessage, true, null)]
    [InlineData(ExceptionKind.InvalidOperation, true, "Something went wrong")]
    [InlineData(ExceptionKind.ContainsOperationCanceledText, true, null)]
    public async Task SendMessageAsync_WithException_ClassifiesError(
        ExceptionKind kind, bool expectErrorMessage, string? expectedContent)
    {
        // Merges 5 originals: WithOperationCanceledException, WithTaskCanceledException,
        // WithEmptyMessageException, WithAnyException, WithOperationCanceledMessageException.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.SetExceptionToThrow(ExceptionFor(kind));

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        if (expectErrorMessage)
        {
            if (expectedContent is not null)
            {
                messages.ShouldContain(m => m.IsError && m.Content == expectedContent);
            }
            else
            {
                messages.ShouldContain(m => m.IsError);
            }
        }
        else
        {
            messages.ShouldNotContain(m => m.IsError);
        }
    }

    [Theory]
    [InlineData(ErrorChunkKind.OperationCanceledText, false, false, null)]
    [InlineData(ErrorChunkKind.TaskCanceledText, false, false, null)]
    [InlineData(ErrorChunkKind.OperationWasCanceledText, false, false, null)]
    [InlineData(ErrorChunkKind.NonTransient, true, true, "Connection reset by peer")]
    public async Task SendMessageAsync_WithErrorChunk_ClassifiesErrorAndToast(
        ErrorChunkKind kind, bool expectErrorMessage, bool expectToast, string? expectedContent)
    {
        // Merges 6 originals: WithOperationCanceledErrorChunk, WithTaskCanceledErrorChunk,
        // WithOperationWasCanceledErrorChunk, WithNonTransientErrorChunk_AddsInlineErrorMessage,
        // WithNonTransientErrorChunk_ShowsToast, WithTransientErrorChunk_DoesNotShowToast.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueError(ErrorTextFor(kind));

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        if (expectErrorMessage)
        {
            messages.ShouldContain(m => m.IsError && m.Content == expectedContent);
        }
        else
        {
            messages.ShouldNotContain(m => m.IsError);
        }

        if (expectToast)
        {
            _toastStore.State.Toasts.Count.ShouldBe(1);
            _toastStore.State.Toasts[0].Message.ShouldBe(expectedContent);
        }
        else
        {
            _toastStore.State.Toasts.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task SendMessageAsync_MessageIdCommittedByAnEarlierStream_UpdatesTheExistingMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "first", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });
        await SendAndDrainAsync(topic);

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "first and more", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });
        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("first and more");
    }

    [Fact]
    public async Task SendMessageAsync_MessageIdPresentFromHistory_UpdatesTheExistingMessage()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "partial", MessageId = "msg-1" }
        ]));
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "partial and the rest", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" });

        await SendAndDrainAsync(topic);

        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("partial and the rest");
    }

    #endregion

    #region SendMessageAsync entry point tests

    // Announce marks a topic streaming before the stream body runs; a fault inside that body
    // must still leave the topic markable as not-streaming, or a bug earlier in the chunk
    // stream would strand the topic looking permanently busy.
    [Fact]
    public async Task SendMessageAsync_ExceptionMidStream_LeavesNoActiveStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.SetExceptionToThrow(new InvalidOperationException("mid-stream fault"));

        await _service.SendMessageAsync(topic, "test");

        _topicStreams.Snapshot(topic.TopicId).HasStream.ShouldBeFalse();
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task SendMessageAsync_WithNoActiveStream_CreatesNewStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.EnqueueContent("Response");

        await _service.SendMessageAsync(topic, "test");

        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
    }

    // The store keeps a buffer only for a topic it knows is streaming, so a send has to announce
    // the stream before the first chunk is processed or the reply never reaches the screen.
    [Fact]
    public async Task SendMessageAsync_WhileTheReplyArrives_TheChunksReachTheStreamingBuffer()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var buffered = new List<string?>();
        using var subscription = _streamingStore.StateObservable.Subscribe(state =>
            buffered.Add(state.StreamingByTopic.GetValueOrDefault(topic.TopicId)?.Content));

        _messagingService.EnqueueContent("Hello");

        await _service.SendMessageAsync(topic, "test");
        await DrainAsync(topic);

        buffered.ShouldContain("Hello");
    }

    [Fact]
    public async Task SendMessageAsync_SettingsDifferFromDefaults_PassesConfigPatch()
    {
        var agent = new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "luna", DefaultReasoningEffort: "low");
        _dispatcher.Dispatch(new SetAgents([agent]));
        var topic = CreateTopic(agentId: "jack");
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        _messagingService.EnqueueContent("Response");

        await _service.SendMessageAsync(topic, "hello");

        _messagingService.LastConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public async Task SendMessageAsync_SettingsMatchDefaults_PassesNullPatch()
    {
        var agent = new AgentCatalogEntry("jack", "Jack", null, DefaultModel: "luna", DefaultReasoningEffort: "low");
        _dispatcher.Dispatch(new SetAgents([agent]));
        var topic = CreateTopic(agentId: "jack");
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.EnqueueContent("Response");

        await _service.SendMessageAsync(topic, "hello");

        _messagingService.LastConfigPatch.ShouldBeNull();
    }

    #endregion

    #region TryStartResumeStreamAsync stream body tests

    [Fact]
    public async Task TryStartResumeStreamAsync_OnlyUpdatesTimestampIfNewContent()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, [
            new ChatMessageModel { Role = "assistant", Content = "Known" }
        ]));
        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Known" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        _topicService.SavedTopics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryStartResumeStreamAsync_WithNewContent_UpdatesTimestamp()
    {
        var topic = CreateTopic();
        topic.LastMessageAt = new DateTime(2024, 1, 1);
        _dispatcher.Dispatch(new AddTopic(topic));
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "New content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        _topicService.SavedTopics.Count.ShouldBe(1);
        _topicService.SavedTopics[0].LastMessageAt.ShouldNotBeNull();
        _topicService.SavedTopics[0].LastMessageAt!.Value.ShouldBeGreaterThan(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task TryStartResumeStreamAsync_WithApprovalRequest_DispatchesShowApproval()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var approval = new ToolApprovalRequestMessage("approval-1", []);
        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { ApprovalRequest = approval, MessageId = "msg-1" },
            new ChatStreamMessage { Content = "Done", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        var messages = MessagesFor(topic.TopicId);
        messages.ShouldContain(m => m.Content == "Done");
    }

    [Theory]
    [InlineData(ExceptionKind.OperationCanceled, false, null)]
    [InlineData(ExceptionKind.TaskCanceled, false, null)]
    [InlineData(ExceptionKind.EmptyMessage, true, null)]
    [InlineData(ExceptionKind.InvalidOperation, true, "Something went wrong")]
    [InlineData(ExceptionKind.ContainsOperationCanceledText, true, null)]
    public async Task TryStartResumeStreamAsync_WithException_ClassifiesError(
        ExceptionKind kind, bool expectErrorMessage, string? expectedContent)
    {
        // Merges 5 originals matching the send exception cluster, but for resume.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.SetExceptionToThrow(ExceptionFor(kind));

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        var messages = MessagesFor(topic.TopicId);
        if (expectErrorMessage)
        {
            if (expectedContent is not null)
            {
                messages.ShouldContain(m => m.IsError && m.Content == expectedContent);
            }
            else
            {
                messages.ShouldContain(m => m.IsError);
            }
        }
        else
        {
            messages.ShouldNotContain(m => m.IsError);
        }
    }

    [Theory]
    [InlineData(ErrorChunkKind.OperationCanceledText, false, null)]
    [InlineData(ErrorChunkKind.TaskCanceledText, false, null)]
    [InlineData(ErrorChunkKind.OperationWasCanceledText, false, null)]
    [InlineData(ErrorChunkKind.NonTransient, true, "Connection reset by peer")]
    public async Task TryStartResumeStreamAsync_WithErrorChunk_ClassifiesError(
        ErrorChunkKind kind, bool expectErrorMessage, string? expectedContent)
    {
        // Merges 4 originals matching the send error chunk cluster, but for resume.
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Partial" };
        _messagingService.EnqueueError(ErrorTextFor(kind));

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        var messages = MessagesFor(topic.TopicId);
        if (expectErrorMessage)
        {
            messages.ShouldContain(m => m.IsError && m.Content == expectedContent);
        }
        else
        {
            messages.ShouldNotContain(m => m.IsError);
        }
    }

    #endregion

    #region Resume and stream-state entry point tests

    [Fact]
    public async Task TryStartResumeStreamAsync_WithNoActiveStream_ReturnsTrue()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        var existingMessage = new ChatMessageModel { Role = "assistant" };
        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = "Resumed", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        var result = await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        result.ShouldBeTrue();
        _streamingStore.State.StreamingTopics.Contains(topic.TopicId).ShouldBeFalse();
    }

    [Fact]
    public async Task TryStartResumeStreamAsync_ATopicAlreadyStreaming_IsRefusedALease()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.SetBlockUntilComplete(true);
        _messagingService.EnqueueContent("First response");
        var firstTask = _service.SendMessageAsync(topic, "first");

        _topicStreams.TryBeginResume(topic.TopicId).ShouldBeNull();

        _messagingService.UnblockCompletion();
        await firstTask;
    }

    // A resume takes no lock on purpose, so it can claim an idle topic while a send is still
    // waiting for its answer. The reply it is rebuilding is the one in flight, so the send that
    // set out to open a stream over an idle topic must not end it.
    [Fact]
    public async Task SendMessageAsync_AResumeClaimedTheTopicDuringTheRoundTrip_LeavesTheResumedReplyAlone()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        _messagingService.HoldTheSendAnswer();
        _messagingService.EnqueueContent("the send's reply");
        var send = _service.SendMessageAsync(topic, "hello");

        var resumed = _topicStreams.TryBeginResume(topic.TopicId)!;
        var running = new TaskCompletionSource();
        _topicStreams.TryShowResumed(
            resumed,
            new ChatMessageModel { Role = "assistant", Content = "half written" },
            "msg-1").ShouldBeTrue();
        _topicStreams.Read(resumed, _ => running.Task);
        _messagingService.LetTheSendAnswer();
        await send;

        resumed.Append(new ChatStreamMessage { Content = " and the rest", MessageId = "msg-1" })
            .Message.Content.ShouldBe("half written and the rest");
        resumed.Completion.IsCompleted.ShouldBeFalse();
        running.SetResult();
    }

    [Fact]
    public async Task SendMessageAsync_WhileTheReplyRuns_TheTopicHasAStream()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

        _messagingService.SetBlockUntilComplete(true);
        _messagingService.EnqueueContent("Response");
        var streamTask = _service.SendMessageAsync(topic, "test");

        _topicStreams.Snapshot(topic.TopicId).IsStreaming.ShouldBeTrue();

        _messagingService.UnblockCompletion();
        await streamTask;
    }

    [Fact]
    public async Task TryStartResumeStreamAsync_PreservesTheContentAlreadyStreamed()
    {
        var topic = CreateTopic();
        _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        var existingMessage = new ChatMessageModel { Role = "assistant", Content = "Existing" };

        _messagingService.EnqueueMessages(
            new ChatStreamMessage { Content = " more content", MessageId = "msg-1" },
            new ChatStreamMessage { IsComplete = true, MessageId = "msg-1" }
        );

        await ResumeAndDrainAsync(topic, existingMessage, "msg-1");

        var messages = MessagesFor(topic.TopicId);
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldContain("Existing");
        messages[0].Content.ShouldContain("more content");
    }

    #endregion

    private static Exception ExceptionFor(ExceptionKind kind) => kind switch
    {
        ExceptionKind.OperationCanceled => new OperationCanceledException(),
        ExceptionKind.TaskCanceled => new TaskCanceledException(),
        ExceptionKind.EmptyMessage => new Exception(""),
        ExceptionKind.InvalidOperation => new InvalidOperationException("Something went wrong"),
        ExceptionKind.ContainsOperationCanceledText => new Exception("OperationCanceled"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ErrorTextFor(ErrorChunkKind kind) => kind switch
    {
        ErrorChunkKind.OperationCanceledText => "OperationCanceled",
        ErrorChunkKind.TaskCanceledText => "TaskCanceled",
        ErrorChunkKind.OperationWasCanceledText => "The operation was canceled.",
        ErrorChunkKind.NonTransient => "Connection reset by peer",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}