using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class MessagePipelineTests
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly TopicStreams _topicStreams;
    private readonly MessagePipeline _pipeline;
    private readonly TaskCompletionSource _running = new();

    public MessagePipelineTests()
    {
        _dispatcher = new Dispatcher();
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        _pipeline = new MessagePipeline(
            _dispatcher,
            _messagesStore,
            _topicStreams,
            NullLogger<MessagePipeline>.Instance);
    }

    [Fact]
    public void SubmitUserMessage_DispatchesAddMessage()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault("topic-1");
        messages.ShouldNotBeNull();
        messages.Count.ShouldBe(1);
        messages[0].Role.ShouldBe("user");
        messages[0].Content.ShouldBe("Hello");
        messages[0].SenderId.ShouldBe("user-1");
    }

    [Fact]
    public void SubmitUserMessage_TracksAsPending()
    {
        _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.PendingUserMessages.ShouldBe(1);
    }

    [Fact]
    public void WasSentByThisClient_DistinguishesTrackedFromUnknownCorrelationIds()
    {
        var correlationId = _pipeline.SubmitUserMessage("topic-1", "Hello", "user-1");

        _pipeline.WasSentByThisClient(correlationId).ShouldBeTrue();
        _pipeline.WasSentByThisClient("unknown-id").ShouldBeFalse();
    }

    [Fact]
    public void WasSentByThisClient_ReturnsFalseForNull()
    {
        _pipeline.WasSentByThisClient(null).ShouldBeFalse();
    }

    [Fact]
    public void LoadHistory_TracksFinalizedMessageIds()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "assistant", "Hello", null, null),
            new("msg-2", "assistant", "World", null, null)
        };

        _pipeline.LoadHistory("topic-1", history);

        var snapshot = _pipeline.GetSnapshot("topic-1");
        snapshot.FinalizedCount.ShouldBe(2);
    }

    [Fact]
    public void ClearMessages_ResetsFinalizedState()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("msg-1", "assistant", "Hello", null, null)
        };
        _pipeline.LoadHistory("topic-1", history);
        _pipeline.GetSnapshot("topic-1").FinalizedCount.ShouldBe(1);

        _dispatcher.Dispatch(new ClearMessages("topic-1"));

        _pipeline.GetSnapshot("topic-1").FinalizedCount.ShouldBe(0);
    }

    [Fact]
    public void ResumeFromBuffer_DispatchesMergedMessages()
    {
        var result = new BufferResumeResult(
            [
                new ChatMessageModel { Role = "user", Content = "Q1" },
                new ChatMessageModel { Role = "assistant", Content = "A1" }
            ],
            new ChatMessageModel { Role = "assistant" });

        _pipeline.ResumeFromBuffer(result, "topic-1", null);

        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[0].Content.ShouldBe("Q1");
        messages[1].Content.ShouldBe("A1");
    }

    [Fact]
    public void ResumeFromBuffer_ShowsTheRebuiltContentWhenNoCommittedBubbleOwnsIt()
    {
        // A resume opens the topic stream with the rebuilt message as its accumulator, then
        // replays the buffer into it. What lands in the live buffer must carry the right
        // message id — guarding the interleaved-messageId bubble-loss class of regression.
        var streaming = new ChatMessageModel { Role = "assistant", Content = "Streaming..." };
        GivenAResumedReply("topic-1", streaming, "msg-1");

        _pipeline.ResumeFromBuffer(new BufferResumeResult([], streaming), "topic-1", "msg-1");

        var buffered = _streamingStore.State.StreamingByTopic.GetValueOrDefault("topic-1");
        buffered.ShouldNotBeNull();
        buffered.Content.ShouldBe("Streaming...");
        buffered.CurrentMessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void ResumeFromBuffer_WithNoStreamingContent_ShowsNothing()
    {
        var result = new BufferResumeResult(
            [new ChatMessageModel { Role = "user", Content = "Q1" }],
            new ChatMessageModel { Role = "assistant" });

        _pipeline.ResumeFromBuffer(result, "topic-1", null);

        var messages = _messagesStore.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(1);
        _streamingStore.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    private void GivenAResumedReply(string topicId, ChatMessageModel message, string messageId) =>
        _topicStreams.TryOpen(topicId, message, messageId, _ => _running.Task);
}