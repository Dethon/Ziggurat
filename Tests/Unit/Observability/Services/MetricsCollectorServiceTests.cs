using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsCollectorServiceTests
{
    private static readonly DateTimeOffset _fixedTimestamp = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
    private const string FixedDate = "2026-03-15";

    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IHubContext<MetricsHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly MetricsCollectorService _sut;

    public MetricsCollectorServiceTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _hubClients.Setup(c => c.All).Returns(_clientProxy.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _sut = new MetricsCollectorService(
            _redis.Object,
            _hubContext.Object,
            NullLogger<MetricsCollectorService>.Instance);
    }

    public sealed record HashIncrementCase(
        string Name,
        MetricEvent Event,
        IReadOnlyList<(string Field, long Value)> ExpectedIncrements)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<HashIncrementCase> HashIncrementCases => new()
    {
        new HashIncrementCase(
            "TokenUsage",
            new TokenUsageEvent
            {
                Sender = "alice",
                Model = "gpt-4",
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 0.0025m,
                Timestamp = _fixedTimestamp
            },
            [
                ("tokens:input", 100),
                ("tokens:output", 50),
                ("tokens:cost", 25),
                ("tokens:byUser:alice", 150),
                ("tokens:byModel:gpt-4", 150)
            ]),
        new HashIncrementCase(
            "MemoryRecall",
            new MemoryRecallEvent
            {
                DurationMs = 250,
                MemoryCount = 5,
                UserId = "alice",
                Timestamp = _fixedTimestamp
            },
            [
                ("memory:recalls", 1),
                ("memory:recallDuration", 250),
                ("memory:recallMemories", 5),
                ("memory:byUser:alice", 1)
            ]),
        new HashIncrementCase(
            "MemoryExtraction",
            new MemoryExtractionEvent
            {
                DurationMs = 1500,
                CandidateCount = 8,
                StoredCount = 3,
                UserId = "bob",
                Timestamp = _fixedTimestamp
            },
            [
                ("memory:extractions", 1),
                ("memory:extractionDuration", 1500),
                ("memory:candidates", 8),
                ("memory:stored", 3),
                ("memory:byUser:bob", 1)
            ]),
        new HashIncrementCase(
            "MemoryDreaming",
            new MemoryDreamingEvent
            {
                MergedCount = 7,
                DecayedCount = 3,
                ProfileRegenerated = true,
                UserId = "alice",
                Timestamp = _fixedTimestamp
            },
            [
                ("memory:dreamings", 1),
                ("memory:merged", 7),
                ("memory:decayed", 3),
                ("memory:profileRegens", 1)
            ]),
        new HashIncrementCase(
            "Latency",
            new LatencyEvent
            {
                Stage = LatencyStage.LlmTotal,
                DurationMs = 1500,
                Model = "anthropic/claude",
                Timestamp = _fixedTimestamp
            },
            [
                ("latency:LlmTotal:count", 1),
                ("latency:LlmTotal:totalMs", 1500)
            ]),
        new HashIncrementCase(
            "Voice",
            new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                SatelliteId = "kitchen-01",
                DurationMs = 250,
                Timestamp = _fixedTimestamp
            },
            [
                ("voice:SttLatencyMs:count", 1),
                ("voice:SttLatencyMs:totalMs", 250)
            ]),
        new HashIncrementCase(
            "SkillPreload",
            new SkillPreloadEvent
            {
                Outcome = SkillPreloadOutcomes.Preloaded,
                Skills = ["home-assistant"],
                DurationMs = 380,
                Timestamp = _fixedTimestamp
            },
            [
                ("skills:preloaded:count", 1),
                ("skills:latency:count", 1),
                ("skills:latency:totalMs", 380)
            ])
    };

    [Theory]
    [MemberData(nameof(HashIncrementCases))]
    public async Task ProcessEventAsync_IncrementsExpectedDailyTotalsHashFields(HashIncrementCase @case)
    {
        await _sut.ProcessEventAsync(@case.Event, _db.Object);

        foreach (var (field, value) in @case.ExpectedIncrements)
        {
            _db.Verify(d => d.HashIncrementAsync(
                $"metrics:totals:{FixedDate}", field, value, It.IsAny<CommandFlags>()), Times.Once);
        }
    }

    public sealed record SortedSetCase(
        string Name,
        MetricEvent Event,
        string ExpectedKey,
        string ExpectedPayloadFragment)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<SortedSetCase> SortedSetCases => new()
    {
        new SortedSetCase(
            "TokenUsage",
            new TokenUsageEvent
            {
                Sender = "alice",
                Model = "gpt-4",
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 0.01m,
                Timestamp = _fixedTimestamp
            },
            $"metrics:tokens:{FixedDate}",
            "\"sender\":\"alice\""),
        new SortedSetCase(
            "ScheduleExecution",
            new ScheduleExecutionEvent
            {
                ScheduleId = "sched-1",
                Prompt = "Do something",
                DurationMs = 5000,
                Success = true,
                Timestamp = _fixedTimestamp
            },
            $"metrics:schedules:{FixedDate}",
            string.Empty),
        new SortedSetCase(
            "MemoryRecall",
            new MemoryRecallEvent
            {
                DurationMs = 250,
                MemoryCount = 5,
                UserId = "alice",
                Timestamp = _fixedTimestamp
            },
            $"metrics:memory-recall:{FixedDate}",
            "\"userId\":\"alice\""),
        new SortedSetCase(
            "MemoryExtraction",
            new MemoryExtractionEvent
            {
                DurationMs = 1500,
                CandidateCount = 8,
                StoredCount = 3,
                UserId = "bob",
                Timestamp = _fixedTimestamp
            },
            $"metrics:memory-extraction:{FixedDate}",
            "\"userId\":\"bob\""),
        new SortedSetCase(
            "MemoryDreaming",
            new MemoryDreamingEvent
            {
                MergedCount = 7,
                DecayedCount = 3,
                ProfileRegenerated = true,
                UserId = "alice",
                Timestamp = _fixedTimestamp
            },
            $"metrics:memory-dreaming:{FixedDate}",
            "\"userId\":\"alice\""),
        new SortedSetCase(
            "Latency",
            new LatencyEvent
            {
                Stage = LatencyStage.LlmTotal,
                DurationMs = 1500,
                Model = "anthropic/claude",
                Timestamp = _fixedTimestamp
            },
            $"metrics:latency:{FixedDate}",
            "\"type\":\"latency\""),
        new SortedSetCase(
            "Voice",
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
                SatelliteId = "kitchen-01",
                Timestamp = _fixedTimestamp
            },
            $"metrics:voice:{FixedDate}",
            "\"satelliteId\":\"kitchen-01\""),
        new SortedSetCase(
            "SkillPreload",
            new SkillPreloadEvent
            {
                Outcome = SkillPreloadOutcomes.Deadline,
                Timestamp = _fixedTimestamp
            },
            $"metrics:skills:{FixedDate}",
            "\"outcome\":\"deadline\"")
    };

    [Theory]
    [MemberData(nameof(SortedSetCases))]
    public async Task ProcessEventAsync_AddsSortedSetEntryForDay(SortedSetCase @case)
    {
        await _sut.ProcessEventAsync(@case.Event, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            @case.ExpectedKey,
            It.Is<RedisValue>(v => string.IsNullOrEmpty(@case.ExpectedPayloadFragment)
                || v.ToString().Contains(@case.ExpectedPayloadFragment)),
            @case.Event.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    public sealed record SignalRForwardCase(string Name, MetricEvent Event, string ExpectedMethod)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<SignalRForwardCase> SignalRForwardCases => new()
    {
        new SignalRForwardCase(
            "TokenUsage",
            new TokenUsageEvent
            {
                Sender = "alice",
                Model = "gpt-4",
                InputTokens = 100,
                OutputTokens = 50,
                Cost = 0.01m
            },
            "OnTokenUsage"),
        new SignalRForwardCase(
            "MemoryRecall",
            new MemoryRecallEvent
            {
                DurationMs = 250,
                MemoryCount = 5,
                UserId = "alice"
            },
            "OnMemoryRecall"),
        new SignalRForwardCase(
            "MemoryExtraction",
            new MemoryExtractionEvent
            {
                DurationMs = 1500,
                CandidateCount = 8,
                StoredCount = 3,
                UserId = "bob"
            },
            "OnMemoryExtraction"),
        new SignalRForwardCase(
            "MemoryDreaming",
            new MemoryDreamingEvent
            {
                MergedCount = 7,
                DecayedCount = 3,
                ProfileRegenerated = true,
                UserId = "alice"
            },
            "OnMemoryDreaming"),
        new SignalRForwardCase(
            "Latency",
            new LatencyEvent { Stage = LatencyStage.MemoryRecall, DurationMs = 42 },
            "OnLatency"),
        new SignalRForwardCase(
            "Voice",
            new VoiceEvent { Metric = VoiceMetric.UtteranceTranscribed, SatelliteId = "kitchen-01" },
            "OnVoice"),
        new SignalRForwardCase(
            "SkillPreload",
            new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.Abstained },
            "OnSkillPreload")
    };

    [Theory]
    [MemberData(nameof(SignalRForwardCases))]
    public async Task ProcessEventAsync_ForwardsEventToSignalR(SignalRForwardCase @case)
    {
        await _sut.ProcessEventAsync(@case.Event, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            @case.ExpectedMethod,
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], @case.Event)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_Heartbeat_UpdatesRedisAndForwardsToSignalR()
    {
        var evt = new HeartbeatEvent
        {
            Service = "agent-1",
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "StringSetAsync"
                && i.Arguments[0].ToString() == "metrics:health:agent-1")
            .ShouldBe(1);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnHealthUpdate",
            It.Is<object[]>(args => args.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_Heartbeat_ScoresServiceOnTheSeenRosterAndSkipsTheLegacySet()
    {
        var evt = new HeartbeatEvent
        {
            Service = "agent-1",
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "SortedSetAddAsync"
                && i.Arguments[0].ToString() == "metrics:health:seen"
                && i.Arguments[1].ToString() == "agent-1"
                && Math.Abs((double)i.Arguments[2] - _fixedTimestamp.ToUnixTimeSeconds()) < 1)
            .ShouldBe(1);

        _db.Invocations.ShouldNotContain(i => i.Method.Name == "SetAddAsync");
    }

    [Fact]
    public async Task CheckHealthAsync_DropsServicesNotSeenWithinRetention()
    {
        var now = _fixedTimestamp;
        var sut = new MetricsCollectorService(
            _redis.Object,
            _hubContext.Object,
            NullLogger<MetricsCollectorService>.Instance,
            new FakeTimeProvider(now));

        _db.Setup(d => d.SortedSetRangeByRankAsync(
                "metrics:health:seen", 0, -1, Order.Ascending, CommandFlags.None))
            .ReturnsAsync([(RedisValue)"lemonade"]);

        await sut.CheckHealthAsync();

        var cutoff = now.Subtract(TimeSpan.FromDays(7)).ToUnixTimeSeconds();
        _db.Invocations
            .Count(i => i.Method.Name == "SortedSetRemoveRangeByScoreAsync"
                && i.Arguments[0].ToString() == "metrics:health:seen"
                && double.IsNegativeInfinity((double)i.Arguments[1])
                && Math.Abs((double)i.Arguments[2] - cutoff) < 1)
            .ShouldBe(1);

        _db.Invocations.ShouldNotContain(i => i.Method.Name == "SetMembersAsync");
    }

    [Fact]
    public async Task DropLegacyRosterAsync_DeletesTheNeverPrunedKnownSet()
    {
        await _sut.DropLegacyRosterAsync(_db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "KeyDeleteAsync"
                && i.Arguments[0].ToString() == "metrics:health:known")
            .ShouldBe(1);
    }

    [Fact]
    public async Task ProcessEvent_Error_StoresAndForwardsCorrectly()
    {
        var evt = new ErrorEvent
        {
            Service = "agent",
            ErrorType = "NullRef",
            Message = "Object reference not set",
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Invocations
            .Count(i => i.Method.Name == "ListLeftPushAsync"
                && i.Arguments[0].ToString() == "metrics:errors:recent"
                && i.Arguments[1].ToString()!.Contains("\"errorType\":\"NullRef\""))
            .ShouldBe(1);

        _db.Verify(d => d.ListTrimAsync(
            "metrics:errors:recent", 0, 99, It.IsAny<CommandFlags>()), Times.Once);

        _db.Verify(d => d.SortedSetAddAsync(
            $"metrics:errors:{FixedDate}",
            It.IsAny<RedisValue>(),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Success_IncrementsCountAndByNameOnly()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = true,
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            $"metrics:totals:{FixedDate}", "tools:count", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            $"metrics:totals:{FixedDate}", "tools:byName:search", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            $"metrics:totals:{FixedDate}", "tools:errors", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
        _db.Verify(d => d.SortedSetAddAsync(
            It.Is<RedisKey>(k => k.ToString().StartsWith("metrics:errors:")),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task ProcessEventAsync_ToolCall_Failure_IncrementsErrorsAndCreatesErrorEvent()
    {
        var evt = new ToolCallEvent
        {
            ToolName = "search",
            DurationMs = 150,
            Success = false,
            Error = "timeout",
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            $"metrics:totals:{FixedDate}", "tools:errors", 1, It.IsAny<CommandFlags>()), Times.Once);

        _db.Verify(d => d.SortedSetAddAsync(
            $"metrics:errors:{FixedDate}",
            It.Is<RedisValue>(v => v.ToString().Contains("\"errorType\":\"search\"")
                && v.ToString().Contains("\"message\":\"timeout\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnError",
            It.Is<object[]>(args => args.Length == 1 && args[0] is ErrorEvent),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_MemoryDreaming_NoProfileRegen_DoesNotIncrementRegens()
    {
        var evt = new MemoryDreamingEvent
        {
            MergedCount = 4,
            DecayedCount = 1,
            ProfileRegenerated = false,
            UserId = "bob",
            Timestamp = _fixedTimestamp
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.HashIncrementAsync(
            $"metrics:totals:{FixedDate}", "memory:profileRegens", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
    }
}