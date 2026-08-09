using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

internal sealed class FakeAiAgent : DisposableAgent
{
    public Exception? ExceptionToThrow { get; init; }
    public Exception? RestoreExceptionToThrow { get; set; }
    public int DisposeCalls;
    public AgentResponseUpdate[] UpdatesToYield { get; init; } = [];

    public int WarmupCalls;
    public TaskCompletionSource WarmupSignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TimeSpan WarmupDelay { get; init; }
    public TimeSpan TurnDelay { get; init; }
    public Func<CancellationToken, Task>? TurnGate { get; init; }
    public ConcurrentQueue<string> Events { get; } = new();
    public ConcurrentQueue<string> RestoredSessionKeys { get; } = new();
    public ConcurrentQueue<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = new();

    public Exception? WarmupExceptionToThrow { get; init; }

    public override async Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
    {
        Interlocked.Increment(ref WarmupCalls);
        if (WarmupExceptionToThrow is not null)
        {
            throw WarmupExceptionToThrow;
        }

        if (WarmupDelay > TimeSpan.Zero)
        {
            await Task.Delay(WarmupDelay, ct);
        }

        Events.Enqueue("warmup");
        WarmupSignaled.TrySetResult();
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
    }

    public Func<Task>? RestoreGate { get; init; }

    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedThread,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (RestoreGate is not null)
        {
            await RestoreGate();
        }

        if (RestoreExceptionToThrow is not null)
        {
            throw RestoreExceptionToThrow;
        }

        if (serializedThread.ValueKind == JsonValueKind.String && serializedThread.GetString() is { } key)
        {
            RestoredSessionKeys.Enqueue(key);
        }
        return new FakeAgentThread();
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResponse());
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        Events.Enqueue("run");
        ReceivedMessages.Enqueue(messages.ToList());
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        if (TurnGate is not null)
        {
            await TurnGate(cancellationToken);
        }

        if (TurnDelay > TimeSpan.Zero)
        {
            await Task.Delay(TurnDelay, cancellationToken);
        }

        foreach (var update in UpdatesToYield)
        {
            yield return update;
        }

        // The real agent persists the turn to the thread as part of running it, so the fake
        // does too — a recall hook reading the thread mid-build must not see this yet.
        if (thread is FakeAgentThread fakeThread)
        {
            fakeThread.PersistedMessages.AddRange(messages);
        }

        Events.Enqueue("run-complete");
    }

    public TaskCompletionSource DisposeSignaled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCalls);
        DisposeSignaled.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeThreadSessionAsync(AgentSession thread)
    {
        return ValueTask.CompletedTask;
    }

    internal sealed class FakeAgentThread : AgentSession
    {
        public List<ChatMessage> PersistedMessages { get; } = [];
    }
}

internal sealed class FakeAgentFactory(DisposableAgent agent) : IAgentFactory
{
    public List<(AgentKey Key, IToolApprovalHandler ApprovalHandler)> Created { get; } = [];

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        Created.Add((agentKey, approvalHandler));
        return agent;
    }

    public DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string conversationId, string[] whitelistPatterns, string userId)
        => throw new NotImplementedException();
}

internal sealed class FakeChannelConnection : IChannelConnection
{
    private readonly Channel<ChannelMessage> _channel = System.Threading.Channels.Channel.CreateUnbounded<ChannelMessage>();

    public string ChannelId { get; init; } = "test-channel";

    public bool AttachOnly { get; init; }

    public string? ConversationIdToReturn { get; init; }

    public Func<Task>? ReplyGate { get; init; }

    public Action<SendReplyParams>? OnReply { get; init; }

    public IAsyncEnumerable<ChannelMessage> Messages => _channel.Reader.ReadAllAsync();

    public List<SendReplyParams> SentReplies { get; } = [];

    public List<(string AgentId, string TopicName, string Sender, string? InitialPrompt, string? ExistingConversationId)> CreatedConversations { get; } = [];

    public List<(string ConversationId, IReadOnlyList<ToolApprovalRequest> Requests)> NotifyAutoApprovedCalls { get; } = [];

    public async Task SendReplyAsync(SendReplyParams reply, CancellationToken ct)
    {
        if (ReplyGate is not null)
        {
            await ReplyGate();
        }

        SentReplies.Add(reply);
        OnReply?.Invoke(reply);
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.FromResult(new ToolApprovalResult());

    public Dictionary<string, byte[]> Attachments { get; } = [];

    public Task<byte[]?> FetchAttachmentAsync(string attachmentId, CancellationToken ct)
        => Task.FromResult(Attachments.GetValueOrDefault(attachmentId));

    public Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        NotifyAutoApprovedCalls.Add((conversationId, requests));
        return Task.CompletedTask;
    }

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, string? initialPrompt, string? address, string? existingConversationId, CancellationToken ct)
    {
        CreatedConversations.Add((agentId, topicName, sender, initialPrompt, existingConversationId));
        return Task.FromResult(ConversationIdToReturn);
    }

    public void WriteMessage(ChannelMessage message) => _channel.Writer.TryWrite(message);

    public void Complete() => _channel.Writer.TryComplete();
}

internal static class MonitorTestMocks
{
    public static ChannelMessage CreateChannelMessage(
        string conversationId = "conv-1",
        string content = "Hello",
        string sender = "test",
        string channelId = "test-channel",
        string? agentId = null)
    {
        return new ChannelMessage
        {
            ConversationId = conversationId,
            Content = content,
            Sender = sender,
            ChannelId = channelId,
            AgentId = agentId
        };
    }

    public static FakeChannelConnection CreateChannel(string channelId = "test-channel", params ChannelMessage[] messages)
    {
        var channel = new FakeChannelConnection { ChannelId = channelId };
        foreach (var msg in messages)
        {
            channel.WriteMessage(msg);
        }
        channel.Complete();
        return channel;
    }

    public static FakeAiAgent CreateAgent()
    {
        return new FakeAiAgent();
    }

    public static FakeAgentFactory CreateAgentFactory(FakeAiAgent agent)
    {
        return new FakeAgentFactory(agent);
    }

    public static ChatThreadResolver CreateThreadResolver()
    {
        return new ChatThreadResolver();
    }
}

public class ChatMonitorTests
{
    [Fact]
    public async Task Monitor_SingleMessage_SendsStreamCompleteReply()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - at minimum a StreamComplete reply should be sent
        channel.SentReplies.ShouldContain(r =>
            r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_VoiceMessageWithSatelliteId_SetsSatelliteIdOnUserMessage()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = new ChannelMessage
        {
            ConversationId = "conv-1",
            Content = "lights on",
            Sender = "household",
            ChannelId = "voice",
            SatelliteId = "kitchen-01"
        };
        var channel = MonitorTestMocks.CreateChannel(channelId: "voice", messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - the satellite id rides on the ChatMessage handed to the agent
        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        userMessage.GetSatelliteId().ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task Monitor_SingleMessage_WarmsUpSessionOncePerConversation()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        // Act
        await monitor.Monitor(CancellationToken.None);
        var done = await Task.WhenAny(fakeAgent.WarmupSignaled.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        // Assert - the session is warmed up exactly once for the conversation
        done.ShouldBe(fakeAgent.WarmupSignaled.Task);
        fakeAgent.WarmupCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_AwaitsWarmupBeforeStreaming_Deterministically()
    {
        // Arrange - slow warmup; if it were fire-and-forget, streaming would start first
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent { WarmupDelay = TimeSpan.FromMilliseconds(150) };
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - warmup completes before the first streaming turn starts
        fakeAgent.Events.ToArray().ShouldBe(["warmup", "run", "run-complete"]);
    }

    [Fact]
    public async Task Monitor_MultipleChannels_RoutesRepliesToOriginatingChannel()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var msg1 = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", channelId: "ch-1");
        var msg2 = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-2", channelId: "ch-2");
        var channel1 = MonitorTestMocks.CreateChannel("ch-1", msg1);
        var channel2 = MonitorTestMocks.CreateChannel("ch-2", msg2);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel1, channel2],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - each channel should receive replies for its own conversation
        channel1.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-1");
        channel2.SentReplies.ShouldAllBe(r => r.ConversationId == "conv-2");
    }

    [Fact]
    public async Task Monitor_SecondMessageFromDifferentChannelInSameConversation_RepliesToThatChannel()
    {
        // A voice-started conversation is mirrored into WebChat under the SAME
        // ConversationId, so both channels share AgentKey(ConversationId, AgentId) and
        // the WebChat message joins the existing voice group. The reply to a message
        // must follow the channel that actually sent it: when the user types into the
        // WebChat conversation, the answer belongs in WebChat, not spoken on the voice
        // satellite that originally opened it (which would also leave WebChat stuck
        // "streaming" because it never receives the terminal StreamComplete).
        var threadResolver = MonitorTestMocks.CreateThreadResolver();

        var completes = 0;
        var firstSpoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void track(SendReplyParams r)
        {
            if (r.ContentType != ReplyContentType.StreamComplete)
            {
                return;
            }

            var n = Interlocked.Increment(ref completes);
            if (n == 1)
            {
                firstSpoken.TrySetResult();
            }
            else if (n == 2)
            {
                secondDelivered.TrySetResult();
            }
        }

        var voice = new FakeChannelConnection { ChannelId = "voice", OnReply = track };
        var webchat = new FakeChannelConnection { ChannelId = "webchat", OnReply = track };
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [voice, webchat],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = monitor.Monitor(cts.Token);

        voice.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "voice", agentId: "jonas"));
        await firstSpoken.Task.WaitAsync(cts.Token);

        webchat.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", channelId: "webchat", agentId: "jonas"));
        await secondDelivered.Task.WaitAsync(cts.Token);

        voice.Complete();
        webchat.Complete();
        await run;

        // The second reply must be routed to WebChat ONLY — not also spoken on the voice satellite
        // that opened the conversation. Counting terminal StreamCompletes per channel proves both
        // the routing (each turn went to its own origin) and the exclusion (no duplicate fan-out):
        // voice received turn 1's completion only, webchat received turn 2's completion only.
        voice.SentReplies.Count(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete).ShouldBe(1);
        webchat.SentReplies.Count(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete).ShouldBe(1);
    }

    [Fact]
    public async Task Monitor_WithCancelCommand_CancelsWithoutWipingThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var agentKey = new AgentKey("conv-1");
        var message = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/cancel");
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // Pre-resolve a context so we can verify cancellation
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled but thread state should NOT be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(It.IsAny<AgentKey>()), Times.Never);
    }

    [Fact]
    public async Task Monitor_AgentStreamThrows_PublishesErrorEvent()
    {
        // Arrange
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent { ExceptionToThrow = new HttpRequestException("422 Unprocessable Entity") };
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();
        var metricsPublisher = new Mock<IMetricsPublisher>();

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            metricsPublisher.Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert
        metricsPublisher.Verify(p => p.Publish(
            It.Is<ErrorEvent>(e =>
                e.Service == "agent" &&
                e.ErrorType == nameof(HttpRequestException) &&
                e.Message.Contains("422 Unprocessable Entity"))), Times.Once);
    }

    [Fact]
    public async Task Monitor_ScheduledFireWithReplyTo_BindsApprovalHandlerToDeliveryTarget()
    {
        // For a scheduled fire the origin channel (mcp-scheduling) auto-approves
        // every tool call silently. If the approval handler is bound to the origin,
        // the delivery target (WebChat) never sees the tool calls and the user
        // can't tell what the scheduled run did. The handler must instead bind to
        // the first delivery target so tool-call notifications surface there.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var fire = MonitorTestMocks.CreateChannelMessage(
            conversationId: "fire-1", channelId: "scheduling", agentId: "jonas") with
        {
            ReplyTo = [new ReplyTarget("signalr", null)]
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", fire);
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var created = agentFactory.Created.ShouldHaveSingleItem();
        created.ApprovalHandler.ShouldBeSameAs(signalr);
        created.Key.ConversationId.ShouldBe("minted-signalr");
    }

    [Fact]
    public void FromMessage_DownloadOrigin_ReturnsNull()
    {
        var msg = MonitorTestMocks.CreateChannelMessage(conversationId: "c", channelId: "library", agentId: "jack")
            with
        { Origin = new MessageOrigin(MessageOriginKind.Download, null) };
        ScheduleExecutionEvent.FromMessage(msg, 100, true, null).ShouldBeNull();
    }

    [Fact]
    public async Task Monitor_WithClearCommand_CleansUpAndWipesThread()
    {
        // Arrange
        var mockStateStore = new Mock<IThreadStateStore>();
        mockStateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var threadResolver = new ChatThreadResolver(mockStateStore.Object);
        var agentKey = new AgentKey("conv-1");
        var message = MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/clear");
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var logger = new Mock<ILogger<ChatMonitor>>();

        // Pre-resolve a context so we can verify cleanup
        var context = threadResolver.Resolve(agentKey);

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        // Act
        await monitor.Monitor(CancellationToken.None);

        // Assert - CTS should be canceled AND thread state should be deleted
        context.Cts.IsCancellationRequested.ShouldBeTrue();
        mockStateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }

    [Fact]
    public async Task Monitor_ClearWhenPersistedDeleteFails_LogsTheFailureAndStillEndsTheGroup()
    {
        // A /clear that cannot reach the state store must not pass silently: the live thread
        // is gone, the persisted one survives and returns on the next message, and the only
        // trace of that mismatch is this log line.
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>()))
            .ThrowsAsync(new HttpRequestException("state store down"));
        var threadResolver = new ChatThreadResolver(stateStore.Object);
        var context = threadResolver.Resolve(new AgentKey("conv-1"));
        var channel = MonitorTestMocks.CreateChannel(
            messages: MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/clear"));
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        context.Cts.IsCancellationRequested.ShouldBeTrue();
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    // The next three tests state the trade in one place: turns within a conversation
    // serialise, while conversations and delivery fan-out stay concurrent.
    [Fact]
    public async Task Monitor_TwoMessagesInOneConversation_RunsTurnsSequentially()
    {
        // Each turn takes time, so overlapping turns interleave the run/run-complete
        // events. Sequential turns produce one closed window per message.
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "first"),
                MonitorTestMocks.CreateChannelMessage(content: "second")
            ]);
        var fakeAgent = new FakeAiAgent { TurnDelay = TimeSpan.FromMilliseconds(100) };

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.Events.ToArray()
            .ShouldBe(["warmup", "run", "run-complete", "run", "run-complete"]);
    }

    [Fact]
    public async Task Monitor_CancelArrivingDuringATurn_CancelsTheRunningTurn()
    {
        // /cancel is how the WebChat stop button reaches the monitor (channel/cancel becomes a
        // message with this content). Serialising turns must not queue it behind the turn it
        // exists to stop: the turn here never ends on its own.
        var channel = new FakeChannelConnection();
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "a long one"));
        // The cancel is written from inside the turn, so it strictly arrives while that turn
        // is running rather than racing it at startup.
        var fakeAgent = new FakeAiAgent
        {
            TurnGate = ct =>
            {
                channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "/cancel"));
                channel.Complete();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        fakeAgent.Events.ToArray().ShouldBe(["warmup", "run"]);
    }

    [Fact]
    public async Task Monitor_TurnQueuedBehindARunningTurn_RunsAfterIt()
    {
        // The second message arrives strictly while the first turn is running — written from
        // inside that turn — so it queues, and runs only once the first turn has completed.
        var channel = new FakeChannelConnection();
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "first"));
        var wroteSecond = 0;
        var fakeAgent = new FakeAiAgent
        {
            TurnGate = _ =>
            {
                if (Interlocked.Exchange(ref wroteSecond, 1) == 0)
                {
                    channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "second"));
                    channel.Complete();
                }

                return Task.CompletedTask;
            }
        };

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        fakeAgent.Events.ToArray()
            .ShouldBe(["warmup", "run", "run-complete", "run", "run-complete"]);
        fakeAgent.ReceivedMessages.SelectMany(m => m).Select(m => m.Text)
            .ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Monitor_ClearArrivingWithATurnQueued_DropsTheQueuedTurnAndLogsTheDrop()
    {
        // Commands dispatch ahead of queued turns, so a /clear behind a queued "do X" wipes
        // the state and ends the group before "do X" runs. That immediacy is the point — the
        // user is discarding the thread the queued turn would have written into — but the
        // dropped turn must not vanish silently: the teardown acknowledges it in the log.
        var channel = new FakeChannelConnection();
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "a long one"));
        var fakeAgent = new FakeAiAgent
        {
            TurnGate = ct =>
            {
                // Written from inside the running turn, so both strictly arrive while it runs:
                // "do X" queues behind it, then the clear tears the group down.
                channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "do X"));
                channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "/clear"));
                channel.Complete();
                return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            new ChatThreadResolver(stateStore.Object),
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // The running turn was cancelled, the queued one never ran, and the thread was wiped.
        fakeAgent.Events.ToArray().ShouldBe(["warmup", "run"]);
        stateStore.Verify(s => s.DeleteAsync(new AgentKey("conv-1")), Times.Once);
        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_CancelDuringTurnSetup_SurfacesAWarmupFault()
    {
        // A /cancel lands while the turn is still setting up (blocked in memory recall).
        // Setup exits on the cancellation, but the warmup it started fire-and-forget had
        // already failed for a real reason — that failure must be logged, not become an
        // unobserved task exception.
        var channel = new FakeChannelConnection();
        var fakeAgent = new FakeAiAgent { WarmupExceptionToThrow = new HttpRequestException("MCP connect failed") };
        var recallHook = new Mock<IMemoryRecallHook>();
        recallHook
            .Setup(h => h.EnrichAsync(It.IsAny<ChatMessage>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<AgentSession>(), It.IsAny<CancellationToken>()))
            .Returns(async (ChatMessage _, string _, string? _, string? _, AgentSession _, CancellationToken ct) =>
            {
                channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "/cancel"));
                channel.Complete();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recallHook.Object,
            logger.Object);

        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage());
        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_TurnSetupFailsAfterWarmupStarted_SurfacesTheWarmupFaultToo()
    {
        // Turn setup fails for its own reason (memory recall down); that is already logged
        // and ends the group. The warmup started before it carries a distinct failure of
        // its own, which must not go to the grave unobserved.
        var fakeAgent = new FakeAiAgent { WarmupExceptionToThrow = new HttpRequestException("MCP connect failed") };
        var recallHook = new Mock<IMemoryRecallHook>();
        recallHook
            .Setup(h => h.EnrichAsync(It.IsAny<ChatMessage>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<AgentSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("recall down"));
        var channel = MonitorTestMocks.CreateChannel(messages: MonitorTestMocks.CreateChannelMessage());
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recallHook.Object,
            logger.Object);

        await monitor.Monitor(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Monitor_MessagesInDifferentConversations_RunTurnsConcurrently()
    {
        // Both turns block until BOTH have started. Concurrent conversations release the
        // gate; serialising across conversations would leave the first turn waiting.
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var timedOut = false;
        var fakeAgent = new FakeAiAgent
        {
            TurnGate = async _ =>
            {
                if (Interlocked.Increment(ref started) >= 2)
                {
                    bothStarted.TrySetResult();
                }

                try
                {
                    await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }
            }
        };
        var channel1 = MonitorTestMocks.CreateChannel(
            "ch-1", MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", channelId: "ch-1"));
        var channel2 = MonitorTestMocks.CreateChannel(
            "ch-2", MonitorTestMocks.CreateChannelMessage(conversationId: "conv-2", channelId: "ch-2"));

        var monitor = new ChatMonitor(
            [channel1, channel2],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        timedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task Monitor_MultiTargetFanOut_DeliversToTargetsConcurrently()
    {
        // Both fan-out targets block until BOTH have been called. Concurrent delivery
        // passes; sequential delivery times out target A, which then misses the reply.
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Func<Task> gate = async () =>
        {
            if (Interlocked.Increment(ref started) >= 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        };
        var targetA = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "conv-a", ReplyGate = gate };
        var targetB = new FakeChannelConnection { ChannelId = "telegram", ConversationIdToReturn = "conv-b", ReplyGate = gate };
        var origin = new FakeChannelConnection { ChannelId = "scheduling" };
        origin.WriteMessage(new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "scheduled prompt",
            Sender = "scheduler",
            ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", null)]
        });
        origin.Complete();
        targetA.Complete();
        targetB.Complete();
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [origin, targetA, targetB],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        targetA.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
        targetB.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
    }

    [Fact]
    public async Task Monitor_AgentStreamsContent_PublishesFirstReplyLatencyOnce()
    {
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent
        {
            UpdatesToYield =
            [
                new AgentResponseUpdate { Contents = [new TextContent("hello")] },
                new AgentResponseUpdate { Contents = [new TextContent("world")] }
            ]
        };
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            metrics.Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        var firstReply = published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .ShouldHaveSingleItem();
        firstReply.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        firstReply.ConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task Monitor_AgentYieldsNoContent_DoesNotPublishFirstReply()
    {
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            metrics.Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        // Guard against a vacuous pass: the run must have actually completed a turn.
        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
        published.OfType<LatencyEvent>().ShouldNotContain(e => e.Stage == LatencyStage.FirstReply);
    }

    [Fact]
    public async Task Monitor_DownloadOriginIntoExistingConversation_AnnouncesTurnStartBeforeFirstReply()
    {
        // A download alert delivers into an EXISTING WebChat conversation. The channel
        // must receive the turn-start announce (create_conversation with the existing id
        // and the alert text as initialPrompt) BEFORE the first reply chunk, so its
        // stream exists when chunks arrive.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        FakeChannelConnection? signalrRef = null;
        bool? announcedAtFirstReply = null;
        var signalr = new FakeChannelConnection
        {
            ChannelId = "signalr",
            OnReply = _ => announcedAtFirstReply ??= signalrRef!.CreatedConversations.Count == 1
        };
        signalrRef = signalr;
        signalr.Complete();
        var library = MonitorTestMocks.CreateChannel("library", new ChannelMessage
        {
            ConversationId = "7:42",
            Content = "[download-complete] film.mkv",
            Sender = "fran",
            ChannelId = "library",
            AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            ReplyTo = [new ReplyTarget("signalr", "7:42")]
        });
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [library, signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        var announce = signalr.CreatedConversations.ShouldHaveSingleItem();
        announce.ExistingConversationId.ShouldBe("7:42");
        announce.InitialPrompt.ShouldBe("[download-complete] film.mkv");
        announce.Sender.ShouldBe("fran");
        announcedAtFirstReply.ShouldNotBeNull();
        announcedAtFirstReply.ShouldBe(true);
        signalr.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_UserMessageWithoutOrigin_DoesNotAnnounce()
    {
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        channel.CreatedConversations.ShouldBeEmpty();
        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
    }

    [Fact]
    public async Task Monitor_ScheduleOriginMintingConversation_DoesNotAnnounceMintedTarget()
    {
        // The group-opening message's create_conversation already set up the stream;
        // a second (announce) call would double-increment the pending count and wedge
        // the stream open. Exactly ONE CreatedConversations entry must exist: the mint.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Check stalled torrents",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "sched-1"),
            ReplyTo = [new ReplyTarget("signalr", null)]
        });
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        var mint = signalr.CreatedConversations.ShouldHaveSingleItem();
        mint.ExistingConversationId.ShouldBeNull();
        signalr.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }
}