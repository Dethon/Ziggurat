using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A chat command is not a turn, so a group whose first message is one runs no turn and builds
// nothing: no agent, no thread read, no MCP connection, no delivery target and no minted
// conversation. Clearing a conversation that has no live group is routine after a restart.
public class ChatMonitorConversationGroupTests
{
    [Fact]
    public async Task Monitor_GroupOpenedByAClearCommand_ConstructsNoAgentAndStillWipesTheThread()
    {
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var threadResolver = new ChatThreadResolver(stateStore.Object);
        var agentKey = new AgentKey("conv-1");
        var channel = MonitorTestMocks.CreateChannel(
            messages: MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/clear"));
        var agentFactory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        agentFactory.Created.ShouldBeEmpty();
        stateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }

    [Fact]
    public async Task Monitor_GroupOpenedByAClearCommandCarryingReplyTo_ResolvesNoTargetsAndMintsNothing()
    {
        // Resolving this message's targets would mint a WebChat conversation for a command
        // nobody will ever write a reply into.
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "/clear",
            Sender = "fran",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null)]
        });
        var agentFactory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        signalr.CreatedConversations.ShouldBeEmpty();
        agentFactory.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Monitor_TurnAfterAClearCommand_IsRoutedToTheChannelThatSentIt()
    {
        // The clear ends the group, so the message typed next opens a fresh one and is its
        // first turn. This is the case the deleted message-index invariant used to cover: a
        // command torn-down group must not leave its anchors — here the voice satellite that
        // sent the clear — pointing at the next turn's reply.
        var voice = new FakeChannelConnection { ChannelId = "voice" };
        var webchat = new FakeChannelConnection { ChannelId = "webchat" };
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(() =>
        {
            // Written from inside the delete so the ordering needs no gate: by the time the
            // clear reaches its persisted state, the group it ended is already complete.
            webchat.WriteMessage(MonitorTestMocks.CreateChannelMessage(
                conversationId: "7:42", content: "and now the news", channelId: "webchat", agentId: "jonas"));
            webchat.Complete();
            voice.Complete();
            return Task.CompletedTask;
        });
        var threadResolver = new ChatThreadResolver(stateStore.Object);
        voice.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", content: "/clear", channelId: "voice", agentId: "jonas"));

        var monitor = new ChatMonitor(
            [voice, webchat],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        webchat.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
        voice.SentReplies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Monitor_SecondTurnEqualToTheAnchorMessage_IsStillANewTurn()
    {
        // "The turn the anchors came from" is answered by identity, not by value. ChannelMessage
        // is a record, so two fires with the same content and the same ReplyTo list are equal —
        // and if the group compared them by equality the second turn would count as the anchor,
        // keep its Minted marker and skip the announce, leaving its reply with no live stream.
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var replyTo = new[] { new ReplyTarget("signalr", null) };
        var first = ScheduleFire("Check stalled torrents") with { ReplyTo = replyTo };
        var second = ScheduleFire("Check stalled torrents") with { ReplyTo = replyTo };
        first.ShouldBe(second);
        first.ShouldNotBeSameAs(second);
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", first, second);

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        signalr.CreatedConversations.Count.ShouldBe(2);
        signalr.CreatedConversations[0].ExistingConversationId.ShouldBeNull();
        signalr.CreatedConversations[1].ExistingConversationId.ShouldBe("minted-signalr");
    }

    [Fact]
    public async Task Monitor_FirstTurnFailsToEstablish_LogsEndsTheGroupAndTheNextMessageStartsAFreshOne()
    {
        // Establishing the group happens inside the turn loop now, so a state store that is
        // down has to end the group there and then: log the failure, complete the group and
        // dispose the agent. The channel stays open on purpose: a group that waited for its
        // message stream to end would hold the agent open with it — and the next message for
        // the same conversation must open a fresh group and be answered, not queue silently
        // into the dead one until restart.
        var channel = new FakeChannelConnection();
        var fakeAgent = new FakeAiAgent { RestoreExceptionToThrow = new HttpRequestException("state store down") };
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = monitor.Monitor(cts.Token);
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage());

        await fakeAgent.DisposeSignaled.Task.WaitAsync(cts.Token);
        fakeAgent.DisposeCalls.ShouldBe(1);
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        // The store recovered; the next message opens a fresh group and is answered.
        fakeAgent.RestoreExceptionToThrow = null;
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "again"));
        channel.Complete();
        await run;

        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_FirstTurnSetupTimesOut_EndsTheGroupAndTheNextMessageStartsAFreshOne()
    {
        // A transport timeout — the HttpClient one behind CreateConversationAsync or behind the
        // thread restore — surfaces as a TaskCanceledException, which is an
        // OperationCanceledException nobody asked for. Only a /cancel or /clear has already
        // ended this group; a timeout has to take the failure path like any other setup
        // failure, or the group stays registered and every later message queues into it unread.
        var channel = new FakeChannelConnection();
        var fakeAgent = new FakeAiAgent
        {
            RestoreExceptionToThrow = new TaskCanceledException("the request timed out")
        };
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = monitor.Monitor(cts.Token);
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage());

        await fakeAgent.DisposeSignaled.Task.WaitAsync(cts.Token);
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<TaskCanceledException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        // The timeout was transient; the next message opens a fresh group and is answered.
        fakeAgent.RestoreExceptionToThrow = null;
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "again"));
        channel.Complete();
        await run;

        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Group_LaterTurnFailsToSetUp_LogsAndCompletesTheGroup()
    {
        // The group is already established, so this is not the first turn — and a setup failure
        // on it leaks the same way: swallowed by the stream merge, the group stays registered
        // and later messages queue into it unread. It ends the group too.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        await using var group = new ConversationGroup(
            new AgentKey("conv-1"),
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            new FailingRecallHook(2, new HttpRequestException("memory store down")),
            logger.Object);
        var grouping = new FakeGrouping(new AgentKey("conv-1"));
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage()));
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage(content: "and again")));
        var completed = false;

        // The grouping is never completed by the producer: ending it is the group's own job.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in group.RunAsync(
            grouping,
            () =>
            {
                completed = true;
                grouping.Complete();
            },
            cts.Token))
        { }

        completed.ShouldBeTrue();
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Group_FailingAfterACancelReplacedItsContext_LeavesTheSuccessorsContextAlone()
    {
        // A /cancel completes this group's grouping while it is still inside its establish path
        // — that path runs on the group token, not the turn one, so it keeps going. By the time
        // it fails, the user's next message has opened a fresh group with a fresh context under
        // the same key. Ending this group must not take that one down with it.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        var agentKey = new AgentKey("conv-1");
        var restoreReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeAgent = new FakeAiAgent
        {
            RestoreExceptionToThrow = new HttpRequestException("state store down"),
            RestoreGate = async () =>
            {
                restoreReached.TrySetResult();
                await releaseRestore.Task;
            }
        };
        await using var group = new ConversationGroup(
            agentKey,
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);
        var grouping = new FakeGrouping(agentKey);
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage()));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(async () =>
        {
            await foreach (var _ in group.RunAsync(grouping, grouping.Complete, cts.Token))
            { }
        }, cts.Token);

        await restoreReached.Task.WaitAsync(cts.Token);
        // The /cancel: it addresses the context this group is running on.
        threadResolver.Cancel(agentKey, threadResolver.Resolve(agentKey));
        // The successor group resolves a fresh one under the same key.
        var successorContext = threadResolver.Resolve(agentKey);
        releaseRestore.TrySetResult();
        await run;

        successorContext.Cts.IsCancellationRequested.ShouldBeFalse();
        threadResolver.AgentKeys.ShouldContain(agentKey);
    }

    [Fact]
    public async Task Group_TurnBufferedBehindACancel_IsDrainedAndNamedAtTeardown()
    {
        // The /cancel tears the group down while "left behind" is already routed into this
        // group's message channel. Wherever the race leaves it — read by the pump into the
        // pending queue, or still buffered in the grouping — the teardown must name it,
        // exactly like a turn queued behind a running one.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        await using var group = new ConversationGroup(
            new AgentKey("conv-1"),
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);
        var grouping = new FakeGrouping(new AgentKey("conv-1"));
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage(content: "/cancel")));
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage(content: "left behind")));
        grouping.Complete();

        await foreach (var _ in group.RunAsync(grouping, grouping.Complete, CancellationToken.None))
        { }

        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Group_ThreadResolverDisposedAtShutdown_LogsAndCompletesTheGroup()
    {
        // Resolving the context is the first thing the group does, before any guard. If the DI
        // container disposed the resolver at shutdown, the throw would leave the stream merge
        // swallowing it, the grouping never completed and every later message for this
        // conversation queued into a group nobody reads. It ends the group instead.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        threadResolver.Dispose();
        await using var group = new ConversationGroup(
            new AgentKey("conv-1"),
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);
        var grouping = new FakeGrouping(new AgentKey("conv-1"));
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage()));
        var completed = false;

        await foreach (var _ in group.RunAsync(grouping, () => completed = true, CancellationToken.None))
        { }

        completed.ShouldBeTrue();
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<ObjectDisposedException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_WarmupIsTheOnlyThingThatFailed_LogsThatFailureOnce()
    {
        // The turn waits on the warmup, so its failure arrives as the turn's setup failure and
        // is logged there. Observing the abandoned warmup afterwards must not report the same
        // exception a second time under a different name.
        var fakeAgent = new FakeAiAgent { WarmupExceptionToThrow = new HttpRequestException("MCP connect failed") };
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage());
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Group_MessagePumpFaults_LogsAndCompletesTheGroup()
    {
        // The pump folds its own failures into the pending channel, and reading them out
        // rethrows past both per-turn catches and out of RunAsync, where the stream merge
        // swallows it and the group is left registered and unread. A wire message that
        // deserialized with no content is enough to get there: parsing it for a command throws.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        var agentKey = new AgentKey("conv-1");
        await using var group = new ConversationGroup(
            agentKey,
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);
        var grouping = new FakeGrouping(agentKey);
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage() with { Content = null! }));
        var completed = false;

        // The grouping is never completed by the producer: ending it is the group's own job.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var _ in group.RunAsync(
            grouping,
            () =>
            {
                completed = true;
                grouping.Complete();
            },
            cts.Token))
        { }

        completed.ShouldBeTrue();
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    // Fails the nth recall, so a test can pick which turn's setup goes down.
    private sealed class FailingRecallHook(int failingCall, Exception exception) : IMemoryRecallHook
    {
        private int _calls;

        public Task EnrichAsync(
            Microsoft.Extensions.AI.ChatMessage message,
            string userId,
            string? conversationId,
            string? agentId,
            Microsoft.Agents.AI.AgentSession thread,
            CancellationToken ct)
        {
            return Interlocked.Increment(ref _calls) == failingCall
                ? Task.FromException(exception)
                : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Group_ContextDisposedBeforeItIsUsed_LogsAndCompletesTheGroup()
    {
        // Resolving the context is guarded, but registering the completion callback on it and
        // linking its token are not, and both throw on a context that was disposed in between —
        // the resolver being disposed at shutdown while this group resolves. Thrown out of
        // RunAsync they leak the group exactly like a failed resolve, so they belong under the
        // same guard.
        var logger = new Mock<ILogger<ChatMonitor>>();
        var channel = new FakeChannelConnection();
        var threadResolver = new ChatThreadResolver();
        var agentKey = new AgentKey("conv-1");
        // Disposing the context does not take it out of the resolver, so this group resolves a
        // dead one.
        threadResolver.Resolve(agentKey).Dispose();
        await using var group = new ConversationGroup(
            agentKey,
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            new DeliveryTargetResolver([channel], logger.Object),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);
        var grouping = new FakeGrouping(agentKey);
        grouping.Write((channel, MonitorTestMocks.CreateChannelMessage()));
        var completed = false;

        await foreach (var _ in group.RunAsync(grouping, () => completed = true, CancellationToken.None))
        { }

        completed.ShouldBeTrue();
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<ObjectDisposedException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    private sealed class FakeGrouping(AgentKey key)
        : IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)>
    {
        private readonly System.Threading.Channels.Channel<(IChannelConnection Channel, ChannelMessage Message)> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<(IChannelConnection Channel, ChannelMessage Message)>();

        public AgentKey Key => key;

        public void Complete() => _channel.Writer.TryComplete();

        public IEnumerable<(IChannelConnection Channel, ChannelMessage Message)> DrainPending()
        {
            while (_channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }

        public void Write((IChannelConnection Channel, ChannelMessage Message) item) => _channel.Writer.TryWrite(item);

        public IAsyncEnumerator<(IChannelConnection Channel, ChannelMessage Message)> GetAsyncEnumerator(
            CancellationToken ct = default)
        {
            return _channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        }
    }

    private static ChannelMessage ScheduleFire(string content)
    {
        return new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = content,
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "sched-1"),
            ReplyTo = [new ReplyTarget("signalr", null)]
        };
    }
}