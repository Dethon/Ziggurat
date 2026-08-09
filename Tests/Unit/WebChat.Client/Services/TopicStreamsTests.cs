using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class TopicStreamsTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly TopicStreams _streams;
    private readonly TaskCompletionSource _running = new();

    public TopicStreamsTests()
    {
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _streams = new TopicStreams(_dispatcher, _messagesStore);
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
    }

    public void Dispose()
    {
        _running.TrySetResult();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }

    private StreamLease Open(string topicId = "topic-1", string? currentMessageId = null) =>
        _streams.TryOpen(topicId, Assistant(), currentMessageId, _ => _running.Task)!;

    private static ChatMessageModel Assistant(string content = "") =>
        new() { Role = "assistant", Content = content };

    private static ChatStreamMessage Chunk(string content, string? messageId = "msg-1") =>
        new() { Content = content, MessageId = messageId };

    private IReadOnlyList<ChatMessageModel> Messages(string topicId = "topic-1") =>
        _messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId) ?? [];

    private StreamingContent? Buffer(string topicId = "topic-1") =>
        _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);

    #region Opening

    [Fact]
    public void TryOpen_ATopicWithNoStream_HandsBackALease()
    {
        var lease = _streams.TryOpen("topic-1", Assistant(), null, _ => _running.Task);

        lease.ShouldNotBeNull();
        _streams.Snapshot("topic-1").IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void TryOpen_ATopicThatAlreadyHasAStream_RefusesAndLeavesTheFirstLeaseWorking()
    {
        var first = Open();

        var second = _streams.TryOpen("topic-1", Assistant(), null, _ => _running.Task);

        second.ShouldBeNull();
        first.Append(Chunk("still mine")).Message.Content.ShouldBe("still mine");
    }

    [Fact]
    public void TryOpen_TwoTopics_EachGetsItsOwnStream()
    {
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", []));

        Open().Append(Chunk("one"));
        Open("topic-2").Append(Chunk("two"));

        Buffer()!.Content.ShouldBe("one");
        Buffer("topic-2")!.Content.ShouldBe("two");
    }

    #endregion

    #region A lease that still holds its topic

    [Fact]
    public void Append_TheLeaseHoldsTheTopic_AccumulatesAndReturnsTheMessage()
    {
        var lease = Open();

        lease.Append(Chunk("Hello"));
        var append = lease.Append(Chunk(" world"));

        append.Message.Content.ShouldBe("Hello world");
        append.IsNew.ShouldBeTrue();
        Buffer()!.Content.ShouldBe("Hello world");
    }

    [Fact]
    public void Append_AChunkThatAddsNothing_IsNotNew()
    {
        var lease = Open();

        var append = lease.Append(new ChatStreamMessage { MessageId = "msg-1", IsComplete = true });

        append.IsNew.ShouldBeFalse();
    }

    [Fact]
    public void AppendToCommittedMessage_TheLeaseHoldsTheTopic_AccumulatesWithoutTouchingTheBuffer()
    {
        var lease = Open();

        var append = lease.AppendToCommittedMessage(Chunk("out of sight"));

        append.Message.Content.ShouldBe("out of sight");
        Buffer()!.HasContent.ShouldBeFalse();
    }

    [Fact]
    public void StartMessage_TheLeaseHoldsTheTopic_CommitsTheOldMessageAndResetsTheBuffer()
    {
        var lease = Open();
        lease.Append(Chunk("first turn"));

        var committed = lease.StartMessage("msg-2");

        committed!.Content.ShouldBe("first turn");
        Messages().Count.ShouldBe(1);
        Messages()[0].Content.ShouldBe("first turn");
        Buffer()!.HasContent.ShouldBeFalse();
        lease.CurrentMessageId.ShouldBe("msg-2");
    }

    [Fact]
    public void StartMessage_AMessageThisStreamWroteBefore_ContinuesIt()
    {
        var lease = Open();
        lease.Append(Chunk("first part ", "msg-1"));
        var stashed = lease.StartMessage("msg-2")!;
        lease.Append(Chunk("other message", "msg-2"));

        lease.StartMessage("msg-1", stashed);
        var append = lease.Append(Chunk("second part", "msg-1"));

        append.Message.Content.ShouldBe("first part second part");
    }

    [Fact]
    public void Complete_TheLeaseHoldsTheTopic_CommitsTheTextAndEndsTheStream()
    {
        var lease = Open();
        lease.Append(Chunk("the whole answer"));

        lease.Complete();

        Messages().Single().Content.ShouldBe("the whole answer");
        _streams.Snapshot("topic-1").HasStream.ShouldBeFalse();
        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public async Task Complete_TheLeaseHoldsTheTopic_FinishesTheLeasesCompletion()
    {
        var lease = Open();

        lease.Complete();

        await lease.Completion;
    }

    // A tool call arrives on the topic's stream like everything else in the reply, and the message
    // it names is the message it belongs to — the same statement the rest of the stream makes.
    [Fact]
    public void Append_AToolCallForAMessageOfItsOwn_ShowsItInThatMessage()
    {
        var lease = Open();
        lease.Append(Chunk("writing", "msg-1"));

        lease.StartMessage("msg-2");
        lease.Append(new ChatStreamMessage { ToolCalls = "search()", MessageId = "msg-2" });
        lease.Complete();

        Messages().Count.ShouldBe(2);
        Messages()[0].Content.ShouldBe("writing");
        Messages()[0].ToolCalls.ShouldBeNullOrEmpty();
        Messages()[1].ToolCalls.ShouldBe("search()");
    }

    #endregion

    #region A lease that no longer holds its topic

    [Fact]
    public void Append_AStaleLease_DoesNothing()
    {
        var stale = Open();
        stale.Complete();
        var current = Open();
        current.Append(Chunk("the newer reply"));

        var append = stale.Append(Chunk("from the old stream"));

        append.IsNew.ShouldBeFalse();
        Buffer()!.Content.ShouldBe("the newer reply");
    }

    [Fact]
    public void StartMessage_AStaleLease_DoesNothing()
    {
        var stale = Open();
        stale.Complete();
        var current = Open();
        current.Append(Chunk("the newer reply"));

        stale.StartMessage("msg-9").ShouldBeNull();

        Buffer()!.Content.ShouldBe("the newer reply");
        current.CurrentMessageId.ShouldBe("msg-1");
    }

    // The late-cleanup bug the forget-by-value rule used to catch: a stream ends after the user
    // has sent again, and its ending must not clear the state of the stream that replaced it.
    [Fact]
    public void Complete_AStaleLease_LeavesTheStreamThatHoldsTheTopicAlone()
    {
        var stale = Open();
        stale.Complete();
        var current = Open();
        current.Append(Chunk("the newer reply"));

        stale.Complete();

        _streams.Snapshot("topic-1").IsStreaming.ShouldBeTrue();
        _streamingStore.State.StreamingTopics.ShouldContain("topic-1");
        Buffer()!.Content.ShouldBe("the newer reply");
    }

    [Fact]
    public async Task Completion_TheTopicKeyedEndRanInstead_StillFinishes()
    {
        var lease = Open();

        _streams.End("topic-1");

        await lease.Completion;
    }

    #endregion

    #region Verbs keyed by topic, for callers holding no lease

    [Fact]
    public void FinalizeCurrent_ATopicWithNoStream_LeavesNothingBehind()
    {
        _streams.FinalizeCurrent("topic-1");

        Messages().ShouldBeEmpty();
        Buffer().ShouldBeNull();
    }

    [Fact]
    public void FinalizeCurrent_ATopicThatIsStreaming_CommitsTheTextAndClearsTheBuffer()
    {
        var lease = Open();
        lease.Append(Chunk("half an answer"));

        _streams.FinalizeCurrent("topic-1");

        Messages().Single().Content.ShouldBe("half an answer");
        Buffer()!.HasContent.ShouldBeFalse();
        _streams.Snapshot("topic-1").IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void End_ATopicWithNoStream_LeavesNothingBehind()
    {
        _streams.End("topic-1");

        Messages().ShouldBeEmpty();
        Buffer().ShouldBeNull();
    }

    // The stop button: what already arrived is kept, and the topic stops being busy.
    [Fact]
    public void End_ATopicThatIsStreaming_CommitsWhatArrivedAndEndsTheStream()
    {
        var lease = Open();
        lease.Append(Chunk("as far as it got"));

        _streams.End("topic-1");

        Messages().Single().Content.ShouldBe("as far as it got");
        _streams.Snapshot("topic-1").HasStream.ShouldBeFalse();
        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
        lease.Append(Chunk(" and more")).IsNew.ShouldBeFalse();
    }

    #endregion

    #region Resuming

    [Fact]
    public void TryBeginResume_ATopicWithNoStream_HandsBackALeaseAndMarksItResuming()
    {
        var lease = _streams.TryBeginResume("topic-1");

        lease.ShouldNotBeNull();
        _streams.Snapshot("topic-1").IsResuming.ShouldBeTrue();
        _streams.Snapshot("topic-1").IsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void TryBeginResume_ATopicAlreadyResuming_Refuses()
    {
        _streams.TryBeginResume("topic-1");

        _streams.TryBeginResume("topic-1").ShouldBeNull();
    }

    [Fact]
    public void TryBeginResume_ATopicAlreadyStreaming_Refuses()
    {
        Open();

        _streams.TryBeginResume("topic-1").ShouldBeNull();
    }

    [Fact]
    public void TryOpen_ATopicThatIsResuming_Refuses()
    {
        _streams.TryBeginResume("topic-1");

        _streams.TryOpen("topic-1", Assistant(), null, _ => _running.Task).ShouldBeNull();
    }

    [Fact]
    public void TryShowResumed_AResumingLease_UpgradesTheSameRecordInPlace()
    {
        var lease = _streams.TryBeginResume("topic-1")!;

        var upgraded = _streams.TryShowResumed(lease, Assistant("already written"), "msg-1");

        upgraded.ShouldBeTrue();
        _streams.Snapshot("topic-1").IsStreaming.ShouldBeTrue();
        lease.Append(Chunk(" and the rest", "msg-1")).Message.Content
            .ShouldBe("already written and the rest");
    }

    // Showing comes before the wire is open, so the topic is streaming with nothing reading it
    // yet. That window is the reply's own: what it wrote is on screen and no second stream can
    // claim the topic.
    [Fact]
    public void TryShowResumed_BeforeTheWireIsRead_HoldsTheTopicAndShowsTheReply()
    {
        var lease = _streams.TryBeginResume("topic-1")!;

        _streams.TryShowResumed(lease, Assistant("already written"), "msg-1");
        _streams.PublishCurrent("topic-1");

        _streams.TryOpen("topic-1", Assistant(), null, _ => _running.Task).ShouldBeNull();
        _streamingStore.State.StreamingByTopic["topic-1"].Content.ShouldBe("already written");
    }

    [Fact]
    public void TryShowResumed_AStaleLease_Refuses()
    {
        var stale = _streams.TryBeginResume("topic-1")!;
        stale.Complete();
        Open();

        _streams.TryShowResumed(stale, Assistant(), "msg-1").ShouldBeFalse();
    }

    [Fact]
    public void Complete_AResumeThatFoundNothing_LeavesTheTopicIdle()
    {
        var lease = _streams.TryBeginResume("topic-1")!;

        lease.Complete();

        _streams.Snapshot("topic-1").HasStream.ShouldBeFalse();
        _streamingStore.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    #endregion

    #region The record

    [Fact]
    public void Snapshot_AStreamingTopic_CarriesTheTaskTheMessageAndTheMessageId()
    {
        var lease = Open(currentMessageId: "msg-1");
        lease.Append(Chunk("partway", "msg-1"));

        var snapshot = _streams.Snapshot("topic-1");

        snapshot.Phase.ShouldBe(TopicStreamPhase.Streaming);
        snapshot.Stream.ShouldBe(_running.Task);
        snapshot.Message!.Content.ShouldBe("partway");
        snapshot.CurrentMessageId.ShouldBe("msg-1");
    }

    [Fact]
    public void Snapshot_ATopicWithNoStream_SaysSo()
    {
        var snapshot = _streams.Snapshot("topic-1");

        snapshot.Phase.ShouldBe(TopicStreamPhase.None);
        snapshot.HasStream.ShouldBeFalse();
        snapshot.Stream.ShouldBeNull();
        snapshot.Message.ShouldBeNull();
    }

    // The projection follows the record: one started, one chunk carrying the whole accumulated
    // message, one completion.
    [Fact]
    public void AStreamFromStartToEnd_PublishesTheProjectionForRendering()
    {
        var seen = new List<IAction>();
        _dispatcher.RegisterCatchAll(seen.Add);

        var lease = Open();
        lease.Append(Chunk("Hi"));
        lease.Append(Chunk(" there"));
        lease.Complete();

        seen.OfType<StreamStarted>().Count().ShouldBe(1);
        seen.OfType<StreamChunk>().Select(c => c.Content).ShouldBe(["Hi", "Hi there"]);
        seen.OfType<StreamCompleted>().Count().ShouldBe(1);
    }

    #endregion
}