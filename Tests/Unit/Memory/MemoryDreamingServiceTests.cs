using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryDreamingServiceTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IMemoryConsolidator> _consolidator = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly MemoryDreamingOptions _options = new();
    private readonly MemoryDreamingService _service;
    private readonly IReadOnlyCollection<string> _warnings;

    private static readonly DateTimeOffset _now = new(2026, 3, 28, 3, 0, 0, TimeSpan.Zero);

    public MemoryDreamingServiceTests()
    {
        _store
            .Setup(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry m, CancellationToken _) => m);

        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _consolidator
            .Setup(c => c.SynthesizeProfileAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalityProfile
            {
                UserId = "user1",
                Summary = "Test profile",
                LastUpdated = _now
            });

        _store
            .Setup(s => s.SaveProfileAsync(It.IsAny<PersonalityProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile p, CancellationToken _) => p);

        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()));

        var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        _warnings = logs.Messages;

        _service = new MemoryDreamingService(
            _store.Object,
            _consolidator.Object,
            _embeddingService.Object,
            _metricsPublisher.Object,
            Mock.Of<ICronValidator>(),
            LoggerFactory.Create(builder => builder.AddProvider(logs)).CreateLogger<MemoryDreamingService>(),
            _options);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_ExecutesMergeThenDecayThenReflect()
    {
        var callOrder = new List<string>();

        var memories = new List<MemoryEntry>
        {
            MakeMemory("m1", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5))
        };

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("consolidate"))
            .ReturnsAsync([]);

        _consolidator
            .Setup(c => c.SynthesizeProfileAsync("user1", It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("synthesize"))
            .ReturnsAsync(new PersonalityProfile
            {
                UserId = "user1",
                Summary = "Test",
                LastUpdated = _now
            });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        callOrder.Count.ShouldBe(2);
        callOrder[0].ShouldBe("consolidate");
        callOrder[1].ShouldBe("synthesize");
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecaysOldUnaccesedMemories()
    {
        var oldFact = MakeMemory("old_fact", "user1", MemoryCategory.Fact, 0.8,
            _now.AddDays(-60), _now.AddDays(-60));
        var recentFact = MakeMemory("recent_fact", "user1", MemoryCategory.Fact, 0.8,
            _now.AddDays(-5), _now.AddDays(-5));
        var oldInstruction = MakeMemory("old_instr", "user1", MemoryCategory.Instruction, 0.8,
            _now.AddDays(-60), _now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldFact, recentFact, oldInstruction });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        // Old fact should be decayed: 0.8 * 0.9 = 0.72
        _store.Verify(s => s.UpdateImportanceAsync("user1", "old_fact", 0.72, It.IsAny<CancellationToken>()), Times.Once);

        // Recent fact should NOT be decayed
        _store.Verify(s => s.UpdateImportanceAsync("user1", "recent_fact", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);

        // Old instruction should NOT be decayed (exempt category)
        _store.Verify(s => s.UpdateImportanceAsync("user1", "old_instr", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecayRespectsFloor()
    {
        // importance 0.05 * 0.9 = 0.045 < floor 0.1 → NOT decayed
        var lowImportance = MakeMemory("low", "user1", MemoryCategory.Fact, 0.05,
            _now.AddDays(-60), _now.AddDays(-60));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { lowImportance });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.UpdateImportanceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_AppliesMergeDecisions()
    {
        var m1 = MakeMemory("m1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m2 = MakeMemory("m2", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1, m2 });

        var mergeDecision = new MergeDecision(
            SourceIds: ["m1", "m2"],
            Action: MergeAction.Merge,
            MergedContent: "Combined fact about user",
            Category: MemoryCategory.Fact,
            Importance: 0.85,
            Tags: ["merged"]);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mergeDecision])
            .ReturnsAsync([]);

        var embedding = new float[] { 0.1f, 0.2f };
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync("Combined fact about user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m =>
                m.Content == "Combined fact about user" &&
                m.Category == MemoryCategory.Fact &&
                m.Importance == 0.85),
            It.IsAny<CancellationToken>()), Times.Once);

        _store.Verify(s => s.DeleteAsync("user1", "m1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync("user1", "m2", It.IsAny<CancellationToken>()), Times.Once);
    }

    // The ids in a decision came back through a language model, which was given them and asked to
    // repeat them. It mostly does, and sometimes returns "Mem_3" for "mem_3" — the right memory,
    // typed differently. Matched exactly, both source ids miss, the decision falls under the
    // two-source guard and is dropped without a word: the pass reports fewer merges than the model
    // decided and nothing says why. Ids are mem_{Guid:N}, so two of them cannot differ by case
    // alone and there is nothing to lose by ignoring it.
    [Fact]
    public async Task RunDreamingForUserAsync_MergeSourceIdsInAnotherCase_StillMergesThem()
    {
        var m1 = MakeMemory("mem_1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m2 = MakeMemory("mem_2", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1, m2 });

        var mergeDecision = new MergeDecision(
            SourceIds: ["Mem_1", "MEM_2"],
            Action: MergeAction.Merge,
            MergedContent: "Combined fact about user",
            Category: MemoryCategory.Fact,
            Importance: 0.85,
            Tags: ["merged"]);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([mergeDecision])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(
            It.Is<MemoryEntry>(m => m.Content == "Combined fact about user"),
            It.IsAny<CancellationToken>()), Times.Once);

        // The stored id, not the one the model typed: the store is asked to delete by key, and a
        // key it does not hold deletes nothing while the merged memory it duplicates lives on.
        _store.Verify(s => s.DeleteAsync("user1", "mem_1", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync("user1", "mem_2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_SupersededSourceIdInAnotherCase_DeletesTheStoredOne()
    {
        var oldMemory = MakeMemory("mem_old", "user1", MemoryCategory.Fact, 0.5, _now.AddDays(-30), _now.AddDays(-30));
        var newMemory = MakeMemory("mem_new", "user1", MemoryCategory.Fact, 0.9, _now.AddDays(-1), _now.AddDays(-1));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldMemory, newMemory });

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MergeDecision(SourceIds: ["Mem_Old", "Mem_New"], Action: MergeAction.SupersedeOlder)])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.DeleteAsync("user1", "mem_old", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.DeleteAsync("user1", "mem_new", It.IsAny<CancellationToken>()), Times.Never);
    }

    // A source id that names no memory at all is still dropped: the guard exists because a model
    // can invent one, and ignoring case is not the same as accepting anything.
    [Fact]
    public async Task RunDreamingForUserAsync_MergeSourceIdThatNamesNoMemory_IsStillDropped()
    {
        var m1 = MakeMemory("mem_1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1 });

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MergeDecision(
                SourceIds: ["mem_1", "mem_invented"],
                Action: MergeAction.Merge,
                MergedContent: "Combined fact about user",
                Category: MemoryCategory.Fact,
                Importance: 0.85,
                Tags: [])])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.StoreAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.DeleteAsync("user1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Dropping it is right; dropping it without a word is what made the same thing happen to the
    // wrongly-cased ids for as long as it did. A pass that consolidates less than the model asked
    // for should say which id it could not place.
    [Fact]
    public async Task RunDreamingForUserAsync_ADecisionDroppedForUnknownIds_SaysWhich()
    {
        var m1 = MakeMemory("mem_1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1 });

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MergeDecision(
                SourceIds: ["mem_1", "mem_invented"],
                Action: MergeAction.Merge,
                MergedContent: "Combined fact about user",
                Category: MemoryCategory.Fact,
                Importance: 0.85,
                Tags: [])])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _warnings.ShouldContain(message => message.Contains("mem_invented", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunDreamingForUserAsync_SupersedeOlder_DeletesOldMemory()
    {
        var oldMemory = MakeMemory("old", "user1", MemoryCategory.Fact, 0.5, _now.AddDays(-30), _now.AddDays(-30));
        var newMemory = MakeMemory("new", "user1", MemoryCategory.Fact, 0.9, _now.AddDays(-1), _now.AddDays(-1));

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { oldMemory, newMemory });

        var supersedeDecision = new MergeDecision(
            SourceIds: ["old", "new"],
            Action: MergeAction.SupersedeOlder);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([supersedeDecision])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.DeleteAsync("user1", "old", It.IsAny<CancellationToken>()), Times.Once);

        _store.Verify(s => s.DeleteAsync("user1", "new", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_LoopsMergePassUntilConsolidatorReturnsEmpty()
    {
        var m1 = MakeMemory("m1", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m2 = MakeMemory("m2", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));
        var m3 = MakeMemory("m3", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10));

        // After first merge, m1+m2 are deleted and a new memory appears.
        // Second consolidation pass has the chance to merge the survivor with m3.
        var afterFirstMerge = new List<MemoryEntry>
        {
            m3,
            MakeMemory("merged1", "user1", MemoryCategory.Fact, 0.85, _now, _now)
        };
        var afterSecondMerge = new List<MemoryEntry>
        {
            MakeMemory("merged2", "user1", MemoryCategory.Fact, 0.9, _now, _now)
        };

        _store
            .SetupSequence(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry> { m1, m2, m3 })
            .ReturnsAsync(afterFirstMerge)
            .ReturnsAsync(afterSecondMerge)
            .ReturnsAsync(afterSecondMerge);

        _consolidator
            .SetupSequence(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MergeDecision(["m1", "m2"], MergeAction.Merge, "merged", MemoryCategory.Fact, 0.85, [])
            ])
            .ReturnsAsync([
                new MergeDecision(["merged1", "m3"], MergeAction.Merge, "merged again", MemoryCategory.Fact, 0.9, [])
            ])
            .ReturnsAsync([]);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _consolidator.Verify(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "merged"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.StoreAsync(It.Is<MemoryEntry>(m => m.Content == "merged again"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_MergeLoopIsBoundedByMaxPasses()
    {
        var memories = new List<MemoryEntry>
        {
            MakeMemory("a", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10)),
            MakeMemory("b", "user1", MemoryCategory.Fact, 0.7, _now.AddDays(-10), _now.AddDays(-10))
        };

        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        // Consolidator never returns empty — only the bound should stop the loop.
        // Use fresh (valid) source IDs on every pass by referencing the ones the store returns.
        _consolidator
            .Setup(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntry> list, CancellationToken _) =>
                list.Count >= 2
                    ? [new MergeDecision([list[0].Id, list[1].Id], MergeAction.Merge, "x", MemoryCategory.Fact, 0.8, [])]
                    : []);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _consolidator.Verify(c => c.ConsolidateAsync(It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()),
            Times.AtMost(_options.MaxMergePasses));
    }

    [Fact]
    public async Task RunDreamingForUserAsync_PublishesDreamingMetric()
    {
        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                MakeMemory("m1", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5))
            });

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt => published = evt);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryDreamingEvent>();
        var dreamingEvent = (MemoryDreamingEvent)published;
        dreamingEvent.UserId.ShouldBe("user1");
        dreamingEvent.ProfileRegenerated.ShouldBeTrue();
        dreamingEvent.ProfileRemoved.ShouldBeFalse();
    }

    [Fact]
    public async Task RunDreamingForUserAsync_NoMemories_RemovesProfileInsteadOfSynthesizing()
    {
        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
        _store
            .Setup(s => s.DeleteProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.DeleteProfileAsync("user1", It.IsAny<CancellationToken>()), Times.Once);
        _consolidator.Verify(
            c => c.SynthesizeProfileAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<MemoryEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(s => s.SaveProfileAsync(It.IsAny<PersonalityProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_NoMemories_RecordsProfileRemovalOnEvent()
    {
        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
        _store
            .Setup(s => s.DeleteProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.Publish(It.IsAny<MetricEvent>()))
            .Callback<MetricEvent>(evt => published = evt);

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        var dreamingEvent = published.ShouldBeOfType<MemoryDreamingEvent>();
        dreamingEvent.UserId.ShouldBe("user1");
        dreamingEvent.ProfileRemoved.ShouldBeTrue();
        dreamingEvent.ProfileRegenerated.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_UserHoldingOnlyAProfile_IsVisitedByTheRunAndCleared()
    {
        // The enumeration was the defect: a profile-only user never appeared in the
        // run's user list. Drives the real run loop, not RunDreamingForUserAsync.
        var cron = new Mock<ICronValidator>();
        cron.SetupSequence(c => c.GetNextOccurrence(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeZoneInfo>()))
            .Returns(DateTime.UtcNow.AddMilliseconds(-1))
            .Returns((DateTime?)null);

        _store
            .Setup(s => s.GetAllUserIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "orphan" });
        _store
            .Setup(s => s.GetByUserIdAsync("orphan", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());
        _store
            .Setup(s => s.DeleteProfileAsync("orphan", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new MemoryDreamingService(
            _store.Object,
            _consolidator.Object,
            _embeddingService.Object,
            _metricsPublisher.Object,
            cron.Object,
            NullLogger<MemoryDreamingService>.Instance,
            _options);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        _store.Verify(s => s.DeleteProfileAsync("orphan", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunDreamingForUserAsync_SomeMemoriesRemain_KeepsProfile()
    {
        _store
            .Setup(s => s.GetByUserIdAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                MakeMemory("survivor", "user1", MemoryCategory.Fact, 0.8, _now.AddDays(-5), _now.AddDays(-5))
            });

        await _service.RunDreamingForUserAsync("user1", _now, CancellationToken.None);

        _store.Verify(s => s.DeleteProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SaveProfileAsync(It.IsAny<PersonalityProfile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static MemoryEntry MakeMemory(
        string id, string userId, MemoryCategory category, double importance,
        DateTimeOffset createdAt, DateTimeOffset lastAccessedAt) =>
        new()
        {
            Id = id,
            UserId = userId,
            Category = category,
            Content = $"Memory {id}",
            Importance = importance,
            Confidence = 0.9,
            CreatedAt = createdAt,
            LastAccessedAt = lastAccessedAt
        };
}