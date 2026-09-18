using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryExtractionWorkerTests
{
    private readonly Mock<IMemoryExtractor> _extractor = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryExtractionOptions _options = new();
    private readonly MemoryExtractionWorker _worker;

    private static MemoryAnchor Anchor(int persistedMessageCount) =>
        MemoryAnchor.TakenBeforeCurrentTurnIsPersisted(persistedMessageCount);

    public MemoryExtractionWorkerTests()
    {
        _worker = new MemoryExtractionWorker(
            _queue,
            _extractor.Object,
            _embeddingService.Object,
            _store.Object,
            _threadStateStore.Object,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            NullLogger<MemoryExtractionWorker>.Instance,
            _options);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithNovelCandidate_StoresMemory()
    {
        var candidate = new ExtractionCandidate(
            Content: "User works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: ["work", "company"],
            Context: "Mentioned during introduction");

        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-1"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Hello, I work at Contoso")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w =>
                    w.Count == 1 && w[0].Text == "Hello, I work at Contoso"),
                "user1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(candidate.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        _store
            .Setup(s => s.SearchAsync("user1", null, embedding,
                It.Is<IEnumerable<MemoryCategory>>(c => c.Contains(MemoryCategory.Fact)),
                null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);

        var request = new MemoryExtractionRequest("user1", "thread-key-1", Anchor(0), "conv_1", null)
        {
            FallbackContent = "Hello, I work at Contoso"
        };

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m =>
                m.UserId == "user1" &&
                m.Content == "User works at Contoso" &&
                m.Category == MemoryCategory.Fact &&
                m.Importance == 0.8 &&
                m.Confidence == 0.9 &&
                m.Source != null &&
                m.Source.ConversationId == "conv_1" &&
                m.Id.StartsWith("mem_")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithDuplicateCandidate_SkipsStore()
    {
        var candidate = new ExtractionCandidate(
            Content: "User works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: [],
            Context: null);

        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-2"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "I work at Contoso")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        var existingMemory = new MemoryEntry
        {
            Id = "mem_existing",
            UserId = "user1",
            Category = MemoryCategory.Fact,
            Content = "Works at Contoso",
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        _store
            .Setup(s => s.SearchAsync("user1", null, embedding,
                It.IsAny<IEnumerable<MemoryCategory>>(),
                null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(existingMemory, 0.92)]);

        var request = new MemoryExtractionRequest("user1", "thread-key-2", Anchor(0), null, null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_PublishesExtractionMetric()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-3"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Some message")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt => published = evt);

        var request = new MemoryExtractionRequest("user2", "thread-key-3", Anchor(0), null, null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryExtractionEvent>();
        var extractionEvent = (MemoryExtractionEvent)published;
        extractionEvent.UserId.ShouldBe("user2");
        extractionEvent.CandidateCount.ShouldBe(0);
        extractionEvent.StoredCount.ShouldBe(0);
        extractionEvent.Outcome.ShouldBe(MemoryExtractionOutcomes.Empty);
        extractionEvent.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithCandidates_PublishesExtracted()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-6"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "I work at Contoso")]);
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ExtractionCandidate("Works at Contoso", MemoryCategory.Fact, 0.8, 0.9, [], null)]);
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0.1f]);
        _store
            .Setup(s => s.SearchAsync("user1", null, It.IsAny<float[]>(), It.IsAny<IEnumerable<MemoryCategory>>(), null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);

        var published = new List<MetricEvent>();
        _metricsPublisher.Setup(p => p.Publish(It.IsAny<MetricEvent>())).Callback<MetricEvent>(published.Add);

        await _worker.ProcessRequestAsync(new MemoryExtractionRequest("user1", "thread-key-6", Anchor(1), null, null), CancellationToken.None);

        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Extracted);
        evt.CandidateCount.ShouldBe(1);
        evt.StoredCount.ShouldBe(1);
    }

    // A retry exhaustion used to publish the same zero as a turn with nothing in it, so "found
    // nothing" and "broke" were one number on the page.
    [Fact]
    public async Task ProcessRequestAsync_WhenEveryAttemptFails_PublishesFailed()
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("transient"));

        var published = new List<MetricEvent>();
        _metricsPublisher.Setup(p => p.Publish(It.IsAny<MetricEvent>())).Callback<MetricEvent>(published.Add);

        var request = new MemoryExtractionRequest("user1", null, Anchor(0), "conv_1", null)
        {
            FallbackContent = "I work at Contoso"
        };

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Failed);
        evt.CandidateCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_SkipsExtraction_WhenAgentDoesNotHaveMemoryFeature()
    {
        _agentDefinitionProvider.Setup(p => p.GetById("agent-no-memory"))
            .Returns(new AgentDefinition
            {
                Id = "agent-no-memory", Name = "NoMem", Model = "test",
                McpServerEndpoints = [], EnabledFeatures = []
            });

        var request = new MemoryExtractionRequest("user1", "any-key", Anchor(0), "conv_1", "agent-no-memory");

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _metricsPublisher.Verify(
            p => p.Publish(It.IsAny<MemoryExtractionEvent>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenExtractorFails_PublishesErrorEvent()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-5"))
            .ReturnsAsync([new ChatMessage(ChatRole.User, "Some message")]);

        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var published = new List<MetricEvent>();
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(published.Add);

        var request = new MemoryExtractionRequest("user3", "thread-key-5", Anchor(0), null, null)
        {
            FallbackContent = "Some message"
        };

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        var errorEvent = published.OfType<ErrorEvent>().ShouldHaveSingleItem();
        errorEvent.Service.ShouldBe("memory");
        errorEvent.ErrorType.ShouldBe(nameof(HttpRequestException));
        errorEvent.Message.ShouldContain("Connection refused");
        // And the memory page sees a failed turn beside it, not nothing.
        published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem().Outcome.ShouldBe(MemoryExtractionOutcomes.Failed);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithAnEmptyWindow_DropsRequestAndPublishesZeroMetric()
    {
        // What the window comes out as for a thread that is gone belongs to
        // ExtractionWindowTests; this is what the worker does when it comes out empty.
        _threadStateStore.Setup(s => s.GetMessagesAsync("gone"))
            .ReturnsAsync((ChatMessage[]?)null);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt => published = evt);

        var request = new MemoryExtractionRequest("user1", "gone", Anchor(0), "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryExtractionEvent>();
        var evt = (MemoryExtractionEvent)published;
        evt.CandidateCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Empty);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithNullThreadStateKey_RetriesOnTransientFailure()
    {
        var callCount = 0;
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ChatMessage> _, string _, CancellationToken _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    throw new HttpRequestException("transient");
                }

                return [];
            });

        var request = new MemoryExtractionRequest("user1", null, Anchor(0), "conv_1", null)
        {
            FallbackContent = "I work at Contoso"
        };

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _threadStateStore.Verify(s => s.GetMessagesAsync(It.IsAny<string>()), Times.Never);
        callCount.ShouldBe(3);
    }

}