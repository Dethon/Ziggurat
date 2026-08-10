using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public class StreamingStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly StreamingStore _store;

    public StreamingStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new StreamingStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void StreamStarted_AddsTopicToStreamingTopics()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        _store.State.StreamingTopics.ShouldContain("topic-1");
    }

    [Fact]
    public void StreamChunk_ReplacesAllContentTypes()
    {
        // The service accumulates and sends full values in each chunk
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", "Thinking ", "tool1()", "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello World", "Thinking about this", "tool1()\ntool2()", "msg-1"));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.Content.ShouldBe("Hello World");
        content.Reasoning.ShouldBe("Thinking about this");
        content.ToolCalls.ShouldBe("tool1()\ntool2()");
    }

    [Fact]
    public void StreamChunk_UpdatesCurrentMessageId()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));

        _store.State.StreamingByTopic["topic-1"].CurrentMessageId.ShouldBe("msg-1");

        _dispatcher.Dispatch(new StreamChunk("topic-1", " World", null, null, "msg-2"));

        _store.State.StreamingByTopic["topic-1"].CurrentMessageId.ShouldBe("msg-2");
    }

    [Fact]
    public void StreamCompleted_ClearsStreamingState()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _store.State.StreamingByTopic.ShouldNotContainKey("topic-1");
        _store.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public void MultipleTopics_StreamIndependently()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamStarted("topic-2"));

        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-2", "World", null, null, "msg-2"));

        _store.State.StreamingByTopic["topic-1"].Content.ShouldBe("Hello");
        _store.State.StreamingByTopic["topic-2"].Content.ShouldBe("World");

        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _store.State.StreamingByTopic.ShouldNotContainKey("topic-1");
        _store.State.StreamingByTopic.ShouldContainKey("topic-2");
        _store.State.StreamingByTopic["topic-2"].Content.ShouldBe("World");
    }

    // A chunk for a topic with no reply in flight used to be dropped here, because six writers
    // could emit one. TopicStreams is the only writer now and answers that itself, so the
    // guard is gone; TopicStreamFlowTests holds what a user would notice if it came back.

    [Fact]
    public void StreamChunk_HandlesNullMessageIdGracefully()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", " World", null, null, null));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.CurrentMessageId.ShouldBe("msg-1"); // Preserved from previous
    }
}