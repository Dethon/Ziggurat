using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Judgments;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Judgments;

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
        // No judge configured: every judgment is absent and the worker behaves as it always has.
        _worker = Worker(StubJudge.Absent(AbsenceReason.Unconfigured));
    }

    private MemoryExtractionWorker Worker(IJudge judge, MemoryJudgmentSettings? settings = null) =>
        new(
            _queue,
            _extractor.Object,
            _embeddingService.Object,
            _store.Object,
            _threadStateStore.Object,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            new MemoryJudge(judge, settings ?? new MemoryJudgmentSettings(), new FakeTimeProvider(), _metricsPublisher.Object),
            NullLogger<MemoryExtractionWorker>.Instance,
            _options);

    private static readonly (string, double)[] _nothingLasting = [("fact", 0.05), ("preference", 0.05), ("instruction", 0.05)];

    private List<MetricEvent> Recording()
    {
        var published = new List<MetricEvent>();
        _metricsPublisher.Setup(p => p.Publish(It.IsAny<MetricEvent>())).Callback<MetricEvent>(published.Add);
        return published;
    }

    private static MemoryExtractionRequest Current(string text) =>
        new("user1", null, Anchor(0), "conv_1", null) { FallbackContent = text };

    [Fact]
    public async Task ProcessRequestAsync_WhenTheGateFindsNothingLasting_DoesNotExtractAndSaysGated()
    {
        var published = Recording();
        var worker = Worker(StubJudge.Nouls(_nothingLasting));

        await worker.ProcessRequestAsync(Current("hola, ¿qué tal?"), CancellationToken.None);

        _extractor.Verify(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Gated);
        evt.CandidateCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenAnyAnswerIsAboveTheBar_ExtractsAsBefore()
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var published = Recording();
        var worker = Worker(StubJudge.Nouls(("fact", 0.05), ("preference", 0.05), ("instruction", 0.9)));

        await worker.ProcessRequestAsync(Current("a partir de ahora háblame de tú"), CancellationToken.None);

        _extractor.Verify(e => e.ExtractAsync(
            It.Is<IReadOnlyList<ChatMessage>>(w => w.Count == 1 && w[0].Text == "a partir de ahora háblame de tú"),
            "user1", It.IsAny<CancellationToken>()), Times.Once);
        published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem().Outcome.ShouldBe(MemoryExtractionOutcomes.Empty);
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenTheJudgeIsAbsent_ExtractsAsBefore()
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var worker = Worker(StubJudge.Absent(AbsenceReason.Deadline));

        await worker.ProcessRequestAsync(Current("hola"), CancellationToken.None);

        _extractor.Verify(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithJudgmentsDisabled_AsksNothing()
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var judge = StubJudge.Nouls(_nothingLasting);
        var worker = Worker(judge, new MemoryJudgmentSettings { Enabled = false });

        await worker.ProcessRequestAsync(Current("hola"), CancellationToken.None);

        judge.Requests.ShouldBeEmpty();
        _extractor.Verify(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // The state Jev reads is the window as fields: the text turns as context, the current message
    // as current, and no tool content anywhere — the window left it out before the judge saw it.
    [Fact]
    public async Task ProcessRequestAsync_SendsTheWindowAsFields_WithNoToolContent()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-7"))
            .ReturnsAsync([
                new ChatMessage(ChatRole.User, "¿qué tiempo hace?"),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "get_weather")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "SECRET-FETCHED-TEXT")]),
                new ChatMessage(ChatRole.Assistant, "Sol y 24 grados.")
            ]);
        var judge = StubJudge.Nouls(_nothingLasting);
        var worker = Worker(judge);

        var request = new MemoryExtractionRequest("user1", "thread-key-7", Anchor(4), "conv_1", null) { FallbackContent = "gracias" };
        await worker.ProcessRequestAsync(request, CancellationToken.None);

        var state = judge.Requests.ShouldHaveSingleItem().State;
        state["current"]!.GetValue<string>().ShouldBe("gracias");
        state["context"]!.AsArray().Select(n => n!["text"]!.GetValue<string>()).ShouldBe(["¿qué tiempo hace?", "Sol y 24 grados."]);
        state.ToJsonString().ShouldNotContain("SECRET-FETCHED-TEXT");
    }

    // Shutdown is not a failed extraction. The host's stop token unwinds the worker, and publishing
    // an error and a `failed` turn for it puts noise on the error page and the memory page on
    // every deploy — the same reason MemoryDreamingService excludes it from its own catch.
    [Fact]
    public async Task ProcessRequestAsync_WhenTheTurnIsCancelled_PublishesNothing_AndPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var published = Recording();
        var worker = Worker(StubJudge.Absent(AbsenceReason.Unconfigured));

        await Should.ThrowAsync<OperationCanceledException>(
            () => worker.ProcessRequestAsync(Current("hola"), cancelled.Token));

        published.ShouldBeEmpty();
    }

    // A failure after some candidates were stored reported zeros, so the Memory page showed
    // "failed, 0 candidates, 0 stored" for a turn that wrote two memories.
    [Fact]
    public async Task ProcessRequestAsync_WhenOneCandidateFailsAfterOthersStored_ReportsWhatWasStored()
    {
        ExtractorReturns(Fact("Trabaja en Globex"), Fact("Vive en Madrid"));
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync("Vive en Madrid", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("embedding unreachable"));
        var published = Recording();
        var worker = Worker(Verifying(_ => Verified(0.9)));

        await worker.ProcessRequestAsync(Current("trabajo en Globex y vivo en Madrid"), CancellationToken.None);

        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Failed);
        evt.CandidateCount.ShouldBe(2);
        evt.StoredCount.ShouldBe(1);
    }

    private static readonly (string, double)[] _somethingLasting = [("fact", 0.9), ("preference", 0.05), ("instruction", 0.05)];

    private static JudgmentOutcome Verified(double supported, double aboutUser = 0.9, double durable = 0.9, double notAQuestion = 0.9) =>
        StubJudge.Answered(("supported", supported), ("about_user", aboutUser), ("durable", durable), ("not_a_question", notAQuestion));

    // Gate: something lasting; verify: by the candidate's text.
    private static StubJudge Verifying(Func<string, JudgmentOutcome> byCandidate) =>
        new(request => request.State["candidate"] is { } candidate
            ? byCandidate(candidate.GetValue<string>())
            : StubJudge.Answered(_somethingLasting));

    private void ExtractorReturns(params ExtractionCandidate[] candidates)
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0.1f]);
        _store
            .Setup(s => s.SearchAsync("user1", null, It.IsAny<float[]>(), It.IsAny<IEnumerable<MemoryCategory>>(), null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);
    }

    private static ExtractionCandidate Fact(string content) => new(content, MemoryCategory.Fact, 0.5, 0.9, [], null);

    [Fact]
    public async Task ProcessRequestAsync_ACandidateTheCheckDrops_IsNotStored_AndTheEventCountsIt()
    {
        ExtractorReturns(Fact("Preguntó por el tiempo"), Fact("Trabaja en Globex"));
        var published = Recording();
        var worker = Worker(Verifying(c => c == "Trabaja en Globex" ? Verified(0.9) : Verified(0.9, durable: 0.1)));

        await worker.ProcessRequestAsync(Current("trabajo en Globex, ¿qué tiempo hace?"), CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "Trabaja en Globex"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "Preguntó por el tiempo"), It.IsAny<CancellationToken>()), Times.Never);
        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.CandidateCount.ShouldBe(2);
        evt.DroppedCount.ShouldBe(1);
        evt.StoredCount.ShouldBe(1);
        evt.Outcome.ShouldBe(MemoryExtractionOutcomes.Extracted);
        published.OfType<MemoryJudgmentEvent>().Single(j => j.Dropped == true).Candidate.ShouldBe("Preguntó por el tiempo");
    }

    [Fact]
    public async Task ProcessRequestAsync_AnAbsentAnswerForOneCandidate_StoresItAndLeavesTheOthersToTheirOwn()
    {
        ExtractorReturns(Fact("absent"), Fact("dropped"), Fact("kept"));
        var worker = Worker(Verifying(c => c switch
        {
            "absent" => new JudgmentOutcome.Absent(AbsenceReason.Error),
            "dropped" => Verified(0.1),
            _ => Verified(0.9)
        }));

        await worker.ProcessRequestAsync(Current("hola"), CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "absent"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "kept"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "dropped"), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The dedup is unchanged and runs after: a kept candidate that duplicates a stored memory is
    // still not written, and StoredCount keeps meaning what was written.
    [Fact]
    public async Task ProcessRequestAsync_TheDedupStillRunsAfterTheCheck()
    {
        ExtractorReturns(Fact("Trabaja en Globex"));
        _store
            .Setup(s => s.SearchAsync("user1", null, It.IsAny<float[]>(), It.IsAny<IEnumerable<MemoryCategory>>(), null, null, 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(new MemoryEntry
            {
                Id = "mem_existing", UserId = "user1", Category = MemoryCategory.Fact, Content = "Works at Globex",
                Importance = 0.8, Confidence = 0.9, CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.95)]);
        var published = Recording();
        var worker = Worker(Verifying(_ => Verified(0.9)));

        await worker.ProcessRequestAsync(Current("trabajo en Globex"), CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        var evt = published.OfType<MemoryExtractionEvent>().ShouldHaveSingleItem();
        evt.DroppedCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_JudgesTheCandidatesConcurrently()
    {
        ExtractorReturns(Fact("a"), Fact("b"), Fact("c"), Fact("d"), Fact("e"));
        // The gate is one call; then five verifications, each held until all five are in flight.
        var gate = new StubJudge(_ => StubJudge.Answered(_somethingLasting));
        var verifier = new StubJudge(_ => Verified(0.9)).HoldingUntil(5);
        var worker = Worker(new SplitJudge(gate, verifier));

        await worker.ProcessRequestAsync(Current("hola"), CancellationToken.None);

        verifier.MaxInFlight.ShouldBe(5);
        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
    }

    // Routes a request with a candidate to one judge and the rest to another.
    private sealed class SplitJudge(IJudge gate, IJudge verifier) : IJudge
    {
        public Task<JudgmentOutcome> JudgeAsync(JudgmentRequest request, CancellationToken deadline) =>
            (request.State["candidate"] is null ? gate : verifier).JudgeAsync(request, deadline);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessRequestAsync_PublishesAJudgmentEvent_ForAnAnsweredAndForAnAbsentCall(bool answered)
    {
        _extractor
            .Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var published = Recording();
        var worker = Worker(answered ? StubJudge.Nouls(_nothingLasting) : StubJudge.Absent(AbsenceReason.Error));

        await worker.ProcessRequestAsync(Current("hola"), CancellationToken.None);

        var evt = published.OfType<MemoryJudgmentEvent>().ShouldHaveSingleItem();
        evt.Kind.ShouldBe(MemoryJudgmentKinds.Gate);
        evt.Answered.ShouldBe(answered);
        evt.UserId.ShouldBe("user1");
        evt.ConversationId.ShouldBe("conv_1");
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