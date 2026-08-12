using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Memory;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using MetricsDTOs = Domain.DTOs.Metrics;

namespace Tests.Unit.Memory;

public class MemoryRecallHookTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryRecallHook _hook;

    private static readonly float[] _testEmbedding = Enumerable.Range(0, 1536).Select(i => (float)i / 1536).ToArray();

    public enum ExtractionFallbackCause
    {
        ThreadStoreThrows,
        NoStateKey,
    }

    public MemoryRecallHookTests()
    {
        // Most cases here are about a user who has memories; the unremembered path has its
        // own tests below and sets this back to false.
        _store.Setup(s => s.HasAnyMemoriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _hook = CreateHook(new MemoryRecallOptions());
    }

    private MemoryRecallHook CreateHook(MemoryRecallOptions options) =>
        new(
            _store.Object,
            _embeddingService.Object,
            _threadStateStore.Object,
            _queue,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            Mock.Of<ILogger<MemoryRecallHook>>(),
            options);

    private static AgentSession CreateSessionWithStateKey(string stateKey)
    {
        var session = new Mock<AgentSession>().Object;
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, stateKey);
        return session;
    }

    [Fact]
    public async Task EnrichAsync_AttachesMemoryContextToMessage()
    {
        var message = new ChatMessage(ChatRole.User, "Hello, I need help");
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Prefers concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.92)
        };
        var profile = new PersonalityProfile
        {
            UserId = "user1", Summary = "Brief communicator", LastUpdated = DateTimeOffset.UtcNow
        };

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync("user1", null, _testEmbedding, null, null, null, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.Count.ShouldBe(1);
        context.Profile.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnrichAsync_NoMemoriesAndNoProfile_AttachesNothingAndProceeds()
    {
        var message = new ChatMessage(ChatRole.User, "Hello again");

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync("user1", null, _testEmbedding, null, null, null, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        message.GetMemoryContext().ShouldBeNull();
        _metricsPublisher.Verify(p => p.Publish(It.IsAny<MetricsDTOs.ErrorEvent>()), Times.Never);
        _metricsPublisher.Verify(p => p.Publish(It.IsAny<MemoryRecallEvent>()), Times.Once);
    }

    [Fact]
    public async Task EnrichAsync_WhenEmbeddingFails_ProceedsWithoutMemory()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API down"));

        await _hook.EnrichAsync(message, "user1", null, null, session, CancellationToken.None);

        message.GetMemoryContext().ShouldBeNull();
        _metricsPublisher.Verify(
            p => p.Publish(It.Is<MetricsDTOs.ErrorEvent>(e => e.Service == MemoryRecallHook.EmbeddingErrorService)),
            Times.Once);
    }

    [Fact]
    public async Task EnrichAsync_WhenTheEmbeddingServerHangs_IsTreatedAsAnOutageNotACancelledTurn()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        // What HttpClient throws when its own timeout expires. A hung server is the likeliest
        // way the embedding call fails, and it must not read as the caller cancelling the turn.
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timed out", new TimeoutException()));

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        _metricsPublisher.Verify(
            p => p.Publish(It.Is<MetricsDTOs.ErrorEvent>(e => e.Service == MemoryRecallHook.EmbeddingErrorService)),
            Times.Once);

        _queue.Complete();
        var request = await _queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.UserId.ShouldBe("user1");
    }

    [Fact]
    public async Task EnrichAsync_WhenTheTurnIsCancelled_DoesNotReportAnEmbeddingOutage()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, cts.Token);

        _metricsPublisher.Verify(
            p => p.Publish(It.Is<MetricsDTOs.ErrorEvent>(e => e.Service == MemoryRecallHook.EmbeddingErrorService)),
            Times.Never);
    }

    [Fact]
    public async Task EnrichAsync_WhenEmbeddingFails_StillAttachesAProfileAndStillEnqueuesExtraction()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalityProfile
            {
                UserId = "user1", Summary = "Brief communicator", LastUpdated = DateTimeOffset.UtcNow
            });

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        // The outage costs the turn its recalled memories and nothing else. Carrying the
        // enqueue out with it would mean everything said during the outage is dropped for
        // good — there is no retry and nothing to replay.
        _queue.Complete();
        var request = await _queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.UserId.ShouldBe("user1");

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Profile.ShouldNotBeNull();
        context.Memories.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnrichAsync_TimesTheEmbeddingCallSeparatelyFromTheRecallStage()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var latencies = CaptureLatencyEvents();

        await _hook.EnrichAsync(message, "user1", "conv1", null, session, CancellationToken.None);

        var embedding = latencies.SingleOrDefault(e => e.Stage == LatencyStage.MemoryEmbedding);
        embedding.ShouldNotBeNull();
        embedding.ConversationId.ShouldBe("conv1");
        embedding.DurationMs.ShouldBeGreaterThanOrEqualTo(0);

        latencies.ShouldContain(e => e.Stage == LatencyStage.MemoryRecall);
    }

    [Fact]
    public async Task EnrichAsync_UnrememberedUser_IsStillEnqueuedForExtraction()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var session = CreateSessionWithStateKey("state-test");
        GiveTheUserNoMemories();

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        // Skipping this would mean a user with no memories could never acquire any, so the
        // skip would latch on and never release.
        _queue.Complete();
        var request = await _queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.UserId.ShouldBe("user1");
        request.FallbackContent.ShouldBe("Hello");
    }

    [Fact]
    public async Task EnrichAsync_EmptinessIsReadFromStorageOnEveryTurn_NotCached()
    {
        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var stored = false;
        _store.Setup(s => s.HasAnyMemoriesAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);

        await _hook.EnrichAsync(new ChatMessage(ChatRole.User, "first"), "user1", "c", null, session, CancellationToken.None);
        _embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Their first memory lands, and the very next turn searches.
        stored = true;
        await _hook.EnrichAsync(new ChatMessage(ChatRole.User, "second"), "user1", "c", null, session, CancellationToken.None);
        _embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // Their memories are all removed, and the turn after that skips again.
        stored = false;
        await _hook.EnrichAsync(new ChatMessage(ChatRole.User, "third"), "user1", "c", null, session, CancellationToken.None);
        _embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void GiveTheUserNoMemories()
    {
        _store.Setup(s => s.HasAnyMemoriesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);
    }

    [Fact]
    public async Task EnrichAsync_WhenEmbeddingFails_StillTimesTheEmbeddingCall()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API down"));

        var latencies = CaptureLatencyEvents();

        await _hook.EnrichAsync(message, "user1", "conv1", null, session, CancellationToken.None);

        latencies.ShouldContain(e => e.Stage == LatencyStage.MemoryEmbedding);
    }

    [Fact]
    public async Task EnrichAsync_AsksWhetherTheUserIsRememberedWhileTheThreadIsFetched()
    {
        var session = CreateSessionWithStateKey("state-test");
        var asked = new TaskCompletionSource();
        var overlapped = false;

        _store.Setup(s => s.HasAnyMemoriesAsync("user1", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                asked.TrySetResult();
                return Task.FromResult(false);
            });

        // Reports whether the question was already in flight while the thread was being read.
        // Both are Redis round trips and neither needs the other's answer, so running them in
        // series adds one to the blocking path of every turn.
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test"))
            .Returns(async () =>
            {
                var finished = await Task.WhenAny(asked.Task, Task.Delay(TimeSpan.FromSeconds(1)));
                overlapped = finished == asked.Task;
                return 0L;
            });
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        await _hook.EnrichAsync(
            new ChatMessage(ChatRole.User, "Hello"), "user1", "conv_1", null, session, CancellationToken.None);

        overlapped.ShouldBeTrue();
    }

    private List<LatencyEvent> CaptureLatencyEvents()
    {
        var captured = new List<LatencyEvent>();
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<LatencyEvent>()))
            .Callback<MetricEvent>(e =>
            {
                if (e is LatencyEvent latency)
                {
                    captured.Add(latency);
                }
            });
        return captured;
    }

    [Fact]
    public async Task EnrichAsync_SkipsRecall_WhenMemoryDisabled()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        const string agentId = "agent-no-memory";
        _agentDefinitionProvider.Setup(p => p.GetById(agentId))
            .Returns(new AgentDefinition
            {
                Id = agentId, Name = agentId, Model = "test",
                McpServerEndpoints = [], EnabledFeatures = []
            });

        var session = new Mock<AgentSession>().Object;

        await _hook.EnrichAsync(message, "user1", "conv_1", agentId, session, CancellationToken.None);

        message.GetMemoryContext().ShouldBeNull();
        _embeddingService.Verify(
            e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _metricsPublisher.Verify(
            p => p.Publish(It.IsAny<MetricsDTOs.MemoryRecallEvent>()),
            Times.Never);
    }

    [Fact]
    public async Task EnrichAsync_UpdatesAccessTimestampsForReturnedMemories()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Fact,
                Content = "Works at Contoso", Importance = 0.8, Confidence = 0.7,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.9)
        };

        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync(0L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>()))
            .ReturnsAsync((ChatMessage[]?)null);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        await _hook.EnrichAsync(message, "user1", null, null, session, CancellationToken.None);

        // Fire-and-forget, give it a moment
        await Task.Delay(50);

        _store.Verify(s => s.UpdateAccessAsync("user1", "mem_1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnrichAsync_BuildsRecallWindowFromLastUserMessages()
    {
        var currentMessage = new ChatMessage(ChatRole.User, "and surf?");
        var session = CreateSessionWithStateKey("state-window");

        var persisted = new ChatMessage[]
        {
            new(ChatRole.User, "beaches near Lisbon?"),
            new(ChatRole.Assistant, "Cascais, Guincho..."),
            new(ChatRole.User, "which has the best surf?"),
            new(ChatRole.Assistant, "Guincho is famous..."),
            new(ChatRole.User, "and for beginners?")
        };
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-window")).ReturnsAsync((long)persisted.Length);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-window", It.IsAny<int>()))
            .ReturnsAsync(persisted);

        string? capturedEmbeddingInput = null;
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((text, _) => capturedEmbeddingInput = text)
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(currentMessage, "user1", "conv_1", null, session, CancellationToken.None);

        capturedEmbeddingInput.ShouldNotBeNull();
        // Default WindowUserTurns=3: last 2 user messages from persisted + current.
        capturedEmbeddingInput.ShouldContain("which has the best surf?");
        capturedEmbeddingInput.ShouldContain("and for beginners?");
        capturedEmbeddingInput.ShouldContain("and surf?");
        capturedEmbeddingInput.ShouldNotContain("beaches near Lisbon?");
        capturedEmbeddingInput.ShouldNotContain("Cascais");
        capturedEmbeddingInput.ShouldNotContain("Guincho is famous");
    }

    [Fact]
    public async Task EnrichAsync_EnqueuesExtractionWithAnchorEqualToPersistedCount()
    {
        var message = new ChatMessage(ChatRole.User, "current");
        var session = CreateSessionWithStateKey("state-anchor");

        var persisted = new ChatMessage[]
        {
            new(ChatRole.User, "m0"),
            new(ChatRole.Assistant, "m1"),
            new(ChatRole.User, "m2"),
            new(ChatRole.Assistant, "m3")
        };
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-anchor")).ReturnsAsync((long)persisted.Length);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-anchor", It.IsAny<int>()))
            .ReturnsAsync(persisted);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            item.UserId.ShouldBe("user1");
            item.ThreadStateKey.ShouldBe("state-anchor");
            item.Anchor.PersistedMessageCount.ShouldBe(4);
            item.ConversationId.ShouldBe("conv_1");
            break;
        }
    }

    [Theory]
    [InlineData(ExtractionFallbackCause.ThreadStoreThrows)]
    [InlineData(ExtractionFallbackCause.NoStateKey)]
    public async Task EnrichAsync_WhenThreadContextUnavailable_StillEnqueuesExtractionWithFallback(ExtractionFallbackCause cause)
    {
        var message = new ChatMessage(ChatRole.User, "hello");
        AgentSession session;
        string? expectedStateKey;
        switch (cause)
        {
            case ExtractionFallbackCause.ThreadStoreThrows:
                session = CreateSessionWithStateKey("state-broken");
                expectedStateKey = "state-broken";
                _threadStateStore.Setup(s => s.GetMessageCountAsync("state-broken"))
                    .ThrowsAsync(new InvalidOperationException("redis down"));
                _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-broken", It.IsAny<int>()))
                    .ThrowsAsync(new InvalidOperationException("redis down"));
                break;
            case ExtractionFallbackCause.NoStateKey:
                session = new Mock<AgentSession>().Object;
                expectedStateKey = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause));
        }

        string? capturedEmbeddingInput = null;
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((text, _) => capturedEmbeddingInput = text)
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        if (cause == ExtractionFallbackCause.ThreadStoreThrows)
        {
            capturedEmbeddingInput.ShouldBe("hello");
        }
        else
        {
            _threadStateStore.Verify(s => s.GetMessageCountAsync(It.IsAny<string>()), Times.Never);
            _threadStateStore.Verify(s => s.GetTailMessagesAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            item.UserId.ShouldBe("user1");
            item.ThreadStateKey.ShouldBe(expectedStateKey);
            item.FallbackContent.ShouldBe("hello");
            if (cause == ExtractionFallbackCause.NoStateKey)
            {
                item.Anchor.PersistedMessageCount.ShouldBe(0);
            }
            break;
        }
    }

    [Fact]
    public async Task EnrichAsync_LongThread_AnchorsExtractionAtFullThreadLengthUsingTailRead()
    {
        // 500 messages persisted but only a bounded tail fetched: the extraction anchor
        // must still point at the real end of the thread, and the full-list read must not run.
        var session = CreateSessionWithStateKey("state-tail");
        var tail = Enumerable.Range(0, 200)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"))
            .ToArray();
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-tail")).ReturnsAsync(500L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-tail", It.IsAny<int>())).ReturnsAsync(tail);
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _store.Setup(s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        var message = new ChatMessage(ChatRole.User, "current question");
        await _hook.EnrichAsync(message, "user-1", "conv-1", null, session, CancellationToken.None);

        _queue.Complete();
        var request = await _queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.Anchor.PersistedMessageCount.ShouldBe(500);
        _threadStateStore.Verify(s => s.GetMessagesAsync(It.IsAny<string>()), Times.Never);
    }
}